namespace DurableStateMachine.Api;

internal sealed class ConfiguredTestTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    private ConfiguredTestTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public static TimeProvider Create(IWebHostEnvironment environment, string databasePath)
    {
        var configuredTime = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TEST_CLOCK_UTC");
        if (string.IsNullOrWhiteSpace(configuredTime))
        {
            return TimeProvider.System;
        }
        if (!environment.IsDevelopment()
            || !string.Equals(
                Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS"),
                "true",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY")))
        {
            throw new InvalidOperationException(
                "A configured test clock requires Development, enabled test controls, and a trusted-ingestion key.");
        }

        if (!DateTimeOffset.TryParse(
                configuredTime,
                global::System.Globalization.CultureInfo.InvariantCulture,
                global::System.Globalization.DateTimeStyles.RoundtripKind,
                out var utcNow))
        {
            throw new InvalidOperationException(
                "DURABLE_STATE_MACHINE_TEST_CLOCK_UTC must be a round-trip ISO-8601 timestamp.");
        }
        var configuredDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE");
        if (string.IsNullOrWhiteSpace(configuredDatabase)
            || !Path.IsPathFullyQualified(configuredDatabase)
            || !Path.IsPathFullyQualified(databasePath)
            || !string.Equals(
                Path.GetFullPath(configuredDatabase),
                Path.GetFullPath(databasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A test clock requires DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE to exactly match the explicitly configured absolute database path.");
        }

        var fullDatabasePath = Path.GetFullPath(databasePath);
        var markerPath = fullDatabasePath + ".test-clock-enabled";
        if (File.Exists(fullDatabasePath) && !File.Exists(markerPath))
        {
            throw new InvalidOperationException(
                "Refusing to apply the test clock to an existing database without its isolated test-clock marker.");
        }
        if (!File.Exists(markerPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullDatabasePath)!);
            using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(marker);
            writer.WriteLine($"gate15-test-clock:{fullDatabasePath}");
        }

        return new ConfiguredTestTimeProvider(utcNow);
    }
}
