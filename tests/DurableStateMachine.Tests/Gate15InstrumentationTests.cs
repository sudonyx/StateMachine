using System.Diagnostics;
using DurableStateMachine.Api;
using DurableStateMachine.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace DurableStateMachine.Tests;

[CollectionDefinition("Gate 15 environment tests", DisableParallelization = true)]
public sealed class Gate15EnvironmentCollection
{
    public const string Name = "Gate 15 environment tests";
}

[Collection(Gate15EnvironmentCollection.Name)]
public sealed class Gate15InstrumentationTests
{
    private const string ClockVariable = "DURABLE_STATE_MACHINE_TEST_CLOCK_UTC";
    private const string ClockDatabaseVariable = "DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE";
    private const string TestControlsVariable = "DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS";
    private const string TrustedKeyVariable = "DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY";

    [Fact]
    public void Test_clock_defaults_to_system_time_when_not_configured()
    {
        using var variables = new EnvironmentVariables();
        variables.Clear(ClockVariable, ClockDatabaseVariable, TestControlsVariable, TrustedKeyVariable);

        var provider = ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Production"), "relative.db");

        Assert.Same(TimeProvider.System, provider);
    }

    [Fact]
    public void Test_clock_requires_development_test_controls_and_trusted_ingestion()
    {
        using var variables = new EnvironmentVariables();
        variables.Set(ClockVariable, "2026-01-01T00:00:00Z");
        variables.Set(TestControlsVariable, "true");
        variables.Set(TrustedKeyVariable, "local-test-key");

        Assert.Throws<InvalidOperationException>(() =>
            ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Production"), "relative.db"));

        variables.Set(TestControlsVariable, "false");
        Assert.Throws<InvalidOperationException>(() =>
            ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), "relative.db"));

        variables.Set(TestControlsVariable, "true");
        variables.Clear(TrustedKeyVariable);
        Assert.Throws<InvalidOperationException>(() =>
            ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), "relative.db"));

        variables.Set(TrustedKeyVariable, "local-test-key");
        variables.Set(ClockVariable, "not-a-timestamp");
        Assert.Throws<InvalidOperationException>(() =>
            ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), "relative.db"));
    }

    [Fact]
    public void Test_clock_requires_matching_absolute_database_and_isolation_marker()
    {
        using var variables = new EnvironmentVariables();
        variables.Set(ClockVariable, "2026-01-01T00:00:00Z");
        variables.Set(TestControlsVariable, "true");
        variables.Set(TrustedKeyVariable, "local-test-key");

        var directory = Path.Combine(Path.GetTempPath(), $"gate15-clock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var databasePath = Path.Combine(directory, "test.db");
            variables.Set(ClockDatabaseVariable, databasePath);
            Assert.Throws<InvalidOperationException>(() =>
                ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), "relative.db"));
            Assert.Throws<InvalidOperationException>(() =>
                ConfiguredTestTimeProvider.Create(
                    new TestWebHostEnvironment("Development"),
                    Path.Combine(directory, "different.db")));

            File.WriteAllBytes(databasePath, []);
            Assert.Throws<InvalidOperationException>(() =>
                ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), databasePath));

            File.Delete(databasePath);
            var provider = ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), databasePath);
            Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), provider.GetUtcNow());
            Assert.True(File.Exists(databasePath + ".test-clock-enabled"));

            File.WriteAllBytes(databasePath, []);
            Assert.Equal(
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                ConfiguredTestTimeProvider.Create(new TestWebHostEnvironment("Development"), databasePath).GetUtcNow());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Evaluation_metrics_are_bounded_and_aggregated_by_outcome()
    {
        var metrics = new TransitionEvaluationMetrics(enabled: true);
        metrics.Record("Accepted", Stopwatch.Frequency / 2);
        metrics.Record("Rejected", 0);
        metrics.Record("Stale", Stopwatch.Frequency * 2);

        var snapshot = metrics.Snapshot();

        Assert.True(snapshot.Enabled);
        Assert.Equal(1, snapshot.Outcomes["Accepted"].Count);
        Assert.Equal(500, snapshot.Outcomes["Accepted"].MeanMilliseconds, precision: 3);
        Assert.Equal(1, snapshot.Outcomes["Rejected"].Count);
        Assert.Equal(0, snapshot.Outcomes["Rejected"].MeanMilliseconds);
        Assert.Equal(1, snapshot.Outcomes["Other"].Count);
        Assert.Equal(2000, snapshot.Outcomes["Other"].MeanMilliseconds, precision: 3);
        Assert.Equal(snapshot.Outcomes["Accepted"].BucketUpperBoundsMilliseconds.Count + 1,
            snapshot.Outcomes["Accepted"].HistogramBuckets.Count);
        Assert.Equal(1, snapshot.Outcomes["Accepted"].HistogramBuckets[17]);
        Assert.Equal(1, snapshot.Outcomes["Rejected"].HistogramBuckets[0]);
        Assert.Equal(1, snapshot.Outcomes["Other"].HistogramBuckets[^1]);
        Assert.Equal(1, snapshot.Outcomes["Accepted"].HistogramBuckets.Sum());
        Assert.Equal(1, snapshot.Outcomes["Rejected"].HistogramBuckets.Sum());
        Assert.Equal(1, snapshot.Outcomes["Other"].HistogramBuckets.Sum());
    }

    [Fact]
    public void Disabled_evaluation_metrics_do_not_record_samples()
    {
        var metrics = new TransitionEvaluationMetrics(enabled: false);
        metrics.Record("Accepted", Stopwatch.Frequency);

        var snapshot = metrics.Snapshot();

        Assert.False(snapshot.Enabled);
        Assert.All(snapshot.Outcomes.Values, outcome => Assert.Equal(0, outcome.Count));
    }

    private sealed class EnvironmentVariables : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

        public void Set(string name, string value)
        {
            Remember(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Clear(params string[] names)
        {
            foreach (var name in names)
            {
                Remember(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        private void Remember(string name)
        {
            if (!_originalValues.ContainsKey(name))
            {
                _originalValues.Add(name, Environment.GetEnvironmentVariable(name));
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "DurableStateMachine.Tests";
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
