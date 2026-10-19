using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Api;
using DurableStateMachine.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate8TimerTests
{
    private static readonly string DefinitionPath =
        Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v4.json");

    [Fact]
    public async Task Document_follow_up_deadlines_are_versioned_durable_and_recovered_after_restarts()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        var definition = await File.ReadAllTextAsync(DefinitionPath);
        var changedDefinition = definition
            .Replace("\"version\": \"4.0\"", "\"version\": \"4.1\"", StringComparison.Ordinal)
            .Replace("\"duration\": \"P10D\"", "\"duration\": \"P5D\"", StringComparison.Ordinal);
        var oldInstance = $"gate8-old-{Guid.NewGuid():N}";
        var newInstance = $"gate8-new-{Guid.NewGuid():N}";
        DateTimeOffset oldDeadline;
        DateTimeOffset newDeadline;

        using (var factory = environment.CreateFactory())
        using (var client = environment.CreateClient(factory))
        {
            await Register(client, definition);
            await CreateInstance(client, oldInstance, "4.0", documentsComplete: false);
            var submission = await SubmitCommand(client, oldInstance, "SubmitApplication", 0,
                new { documentsComplete = false });
            Assert.Equal("AwaitingDocuments", submission.GetProperty("instance").GetProperty("currentState").GetString());
            var oldTimer = Assert.Single(await GetTimers(client, oldInstance));
            oldDeadline = oldTimer.GetProperty("dueAt").GetDateTimeOffset();
            Assert.Equal(clock.GetUtcNow().AddDays(10), oldDeadline);
            Assert.Equal(clock.GetUtcNow(), oldTimer.GetProperty("calculatedAt").GetDateTimeOffset());
            Assert.Equal("4.0", oldTimer.GetProperty("definitionVersion").GetString());
            Assert.Equal(1, oldTimer.GetProperty("originRevision").GetInt64());

            await Register(client, changedDefinition);
            await CreateInstance(client, newInstance, "4.1", documentsComplete: false);
            await SubmitCommand(client, newInstance, "SubmitApplication", 0,
                new { documentsComplete = false });
            var newTimer = Assert.Single(await GetTimers(client, newInstance));
            newDeadline = newTimer.GetProperty("dueAt").GetDateTimeOffset();
            Assert.Equal(clock.GetUtcNow().AddDays(5), newDeadline);
            Assert.NotEqual(oldDeadline, newDeadline);
            Assert.Empty(await ProcessDueTimers(client));
            clock.SetUtcNow(clock.GetUtcNow().AddDays(-2));
            Assert.Empty(await ProcessDueTimers(client));
        }

        clock.SetUtcNow(new DateTimeOffset(2026, 1, 11, 10, 0, 0, TimeSpan.Zero));
        using (var restarted = environment.CreateFactory())
        using (var client = environment.CreateClient(restarted))
        {
            var processed = await ProcessDueTimers(client);
            Assert.Equal(2, processed.Length);
            Assert.All(processed, result =>
                Assert.Equal("Fired", result.GetProperty("timer").GetProperty("status").GetString()));

            var recoveredOldTimer = Assert.Single(await GetTimers(client, oldInstance));
            var recoveredNewTimer = Assert.Single(await GetTimers(client, newInstance));
            Assert.Equal(oldDeadline, recoveredOldTimer.GetProperty("dueAt").GetDateTimeOffset());
            Assert.Equal(newDeadline, recoveredNewTimer.GetProperty("dueAt").GetDateTimeOffset());
            Assert.Equal("4.0", recoveredOldTimer.GetProperty("definitionVersion").GetString());
            Assert.Equal("4.1", recoveredNewTimer.GetProperty("definitionVersion").GetString());
            Assert.Equal("AwaitingDocuments",
                (await GetInstance(client, oldInstance)).GetProperty("currentState").GetString());
            Assert.Equal(2, (await GetInstance(client, oldInstance)).GetProperty("revision").GetInt64());
            Assert.Equal(clock.GetUtcNow(), recoveredOldTimer.GetProperty("processingEvidence")
                .GetProperty("processingEvidence").GetProperty("currentTime").GetDateTimeOffset());

            var effects = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{oldInstance}/effects");
            Assert.NotNull(effects);
            var effect = Assert.Single(effects);
            Assert.Equal("Pending", effect.GetProperty("status").GetString());
            Assert.True(effect.GetProperty("harnessExecutionId").ValueKind == JsonValueKind.Null);
        }

        using (var restartedAgain = environment.CreateFactory())
        using (var client = environment.CreateClient(restartedAgain))
        {
            Assert.Empty(await ProcessDueTimers(client));
            Assert.Equal(2, (await GetInstance(client, oldInstance)).GetProperty("revision").GetInt64());
            Assert.Equal(2, (await GetInstance(client, newInstance)).GetProperty("revision").GetInt64());
        }
    }

    [Fact]
    public async Task Acceptance_before_deadline_cancels_timers_and_at_deadline_expiry_wins()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        using var factory = environment.CreateFactory();
        using var client = environment.CreateClient(factory);
        await Register(client, await File.ReadAllTextAsync(DefinitionPath));

        var acceptedInstance = $"gate8-accepted-{Guid.NewGuid():N}";
        await CreateOffer(client, acceptedInstance);
        var acceptedTimers = await GetTimers(client, acceptedInstance);
        Assert.Equal(2, acceptedTimers.Length);
        clock.Advance(TimeSpan.FromDays(6) + TimeSpan.FromHours(23));
        var accepted = await SubmitCommand(client, acceptedInstance, "AcceptOffer", 2,
            new { offerValidAtEvaluation = true, offerEvaluationTime = clock.GetUtcNow().ToString("O") });
        Assert.Equal("AssetReservationPending",
            accepted.GetProperty("instance").GetProperty("currentState").GetString());
        Assert.All(await GetTimers(client, acceptedInstance), timer =>
            Assert.Equal("Cancelled", timer.GetProperty("status").GetString()));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Empty(await ProcessDueTimers(client));
        Assert.Equal("AssetReservationPending",
            (await GetInstance(client, acceptedInstance)).GetProperty("currentState").GetString());

        var expiredInstance = $"gate8-expired-{Guid.NewGuid():N}";
        await CreateOffer(client, expiredInstance);
        var expiryTimers = await GetTimers(client, expiredInstance);
        var expiry = expiryTimers.Single(timer =>
            timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry");
        var reminder = expiryTimers.Single(timer =>
            timer.GetProperty("timerDeclarationId").GetString() == "offer-reminder");
        var exactDeadline = expiry.GetProperty("dueAt").GetDateTimeOffset();
        clock.SetUtcNow(exactDeadline);

        var refusedAcceptance = await SubmitCommand(client, expiredInstance, "AcceptOffer", 2,
            new { offerValidAtEvaluation = true, offerEvaluationTime = exactDeadline.ToString("O") });
        Assert.Equal("Rejected", refusedAcceptance.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("deadline-expired",
            refusedAcceptance.GetProperty("message").GetProperty("reasonCode").GetString());

        var fired = await ProcessDueTimers(client);
        Assert.Equal(2, fired.Length);
        var resultingTimers = await GetTimers(client, expiredInstance);
        Assert.Equal("Fired", resultingTimers.Single(timer =>
                timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry")
            .GetProperty("status").GetString());
        var staleReminder = resultingTimers.Single(timer =>
            timer.GetProperty("id").GetString() == reminder.GetProperty("id").GetString());
        Assert.Equal("Stale", staleReminder.GetProperty("status").GetString());
        Assert.Null(staleReminder.GetProperty("actionOperationId").GetString());
        Assert.Equal("OfferExpired",
            (await GetInstance(client, expiredInstance)).GetProperty("currentState").GetString());
        Assert.Empty(await ProcessDueTimers(client));
        var history = await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{expiredInstance}/history");
        Assert.NotNull(history);
        Assert.Contains(history, item => item.GetProperty("event").GetString() == "OfferExpired");
        Assert.DoesNotContain(history, item => item.GetProperty("event").GetString() == "AcceptOffer");
    }

    [Fact]
    public async Task Offer_reminder_creates_a_durable_harness_intent_without_invoking_a_provider()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        using var factory = environment.CreateFactory();
        using var client = environment.CreateClient(factory);
        await Register(client, await File.ReadAllTextAsync(DefinitionPath));
        var instanceId = $"gate8-reminder-{Guid.NewGuid():N}";
        await CreateOffer(client, instanceId);
        var timers = await GetTimers(client, instanceId);
        var reminder = timers.Single(timer =>
            timer.GetProperty("timerDeclarationId").GetString() == "offer-reminder");
        var expiry = timers.Single(timer =>
            timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry");
        Assert.Equal(clock.GetUtcNow().AddDays(6), reminder.GetProperty("dueAt").GetDateTimeOffset());
        Assert.Equal(clock.GetUtcNow().AddDays(7), expiry.GetProperty("dueAt").GetDateTimeOffset());

        client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
        using (var unauthorized = await client.PostAsync(
                   "/api/test-controls/timers/process-due?limit=10", content: null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        }
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-State-Machine-Trusted-Ingestion-Key", TimerApiEnvironment.TestKey);
        using (var forged = await client.PostAsJsonAsync(
                   $"/api/instances/{instanceId}/events", new { @event = "OfferExpired" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        }

        clock.Advance(TimeSpan.FromDays(6));
        var firedReminder = Assert.Single(await ProcessDueTimers(client));
        Assert.Equal("offer-reminder",
            firedReminder.GetProperty("timer").GetProperty("timerDeclarationId").GetString());
        var timerId = firedReminder.GetProperty("timer").GetProperty("id").GetString();
        var persistedReminder = (await GetTimers(client, instanceId)).Single(timer =>
            timer.GetProperty("id").GetString() == timerId);
        Assert.Equal("Fired", persistedReminder.GetProperty("status").GetString());
        var operationId = persistedReminder.GetProperty("actionOperationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(operationId));

        using var effectsResponse = await client.GetAsync($"/api/instances/{instanceId}/effects");
        effectsResponse.EnsureSuccessStatusCode();
        using var effectsJson = JsonDocument.Parse(await effectsResponse.Content.ReadAsStringAsync());
        var effect = Assert.Single(effectsJson.RootElement.EnumerateArray());
        Assert.Equal(operationId, effect.GetProperty("operationId").GetString());
        Assert.Equal("Pending", effect.GetProperty("status").GetString());
        Assert.Null(effect.GetProperty("harnessExecutionId").GetString());
        Assert.Empty(await ProcessDueTimers(client));
        Assert.Equal("AwaitingOfferAcceptance",
            (await GetInstance(client, instanceId)).GetProperty("currentState").GetString());

        clock.Advance(TimeSpan.FromDays(1));
        Assert.Single(await ProcessDueTimers(client));
        Assert.Equal("OfferExpired",
            (await GetInstance(client, instanceId)).GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Simultaneous_acceptance_and_due_expiry_cannot_commit_conflicting_transitions()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        using var factory = environment.CreateFactory();
        using var client = environment.CreateClient(factory);
        await Register(client, await File.ReadAllTextAsync(DefinitionPath));
        var instanceId = $"gate8-race-{Guid.NewGuid():N}";
        await CreateOffer(client, instanceId);
        var expiry = (await GetTimers(client, instanceId)).Single(timer =>
            timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry");
        clock.SetUtcNow(expiry.GetProperty("dueAt").GetDateTimeOffset());

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptanceTask = Task.Run(async () =>
        {
            await start.Task;
            return await SendCommand(client, instanceId, "AcceptOffer", 2,
                new { offerValidAtEvaluation = true, offerEvaluationTime = clock.GetUtcNow().ToString("O") });
        });
        var timerTask = Task.Run(async () =>
        {
            await start.Task;
            return await ProcessDueTimers(client);
        });
        start.SetResult();
        using var acceptance = await acceptanceTask;
        Assert.Equal(HttpStatusCode.OK, acceptance.StatusCode);
        using var acceptanceJson = JsonDocument.Parse(await acceptance.Content.ReadAsStringAsync());
        Assert.NotEqual("Accepted",
            acceptanceJson.RootElement.GetProperty("message").GetProperty("status").GetString());
        var timerResults = await timerTask;
        Assert.Equal(2, timerResults.Length);

        var current = await GetInstance(client, instanceId);
        Assert.Equal("OfferExpired", current.GetProperty("currentState").GetString());
        Assert.Equal(3, current.GetProperty("revision").GetInt64());
        var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
        Assert.NotNull(history);
        Assert.Contains(history, item => item.GetProperty("event").GetString() == "OfferExpired");
        Assert.DoesNotContain(history, item => item.GetProperty("event").GetString() == "AcceptOffer");
    }

    [Fact]
    public async Task Receiving_documents_cancels_follow_up_and_terminal_cancellation_cannot_be_reversed()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        using var factory = environment.CreateFactory();
        using var client = environment.CreateClient(factory);
        await Register(client, await File.ReadAllTextAsync(DefinitionPath));

        var completed = $"gate8-docs-complete-{Guid.NewGuid():N}";
        await CreateInstance(client, completed, "4.0", documentsComplete: false);
        await SubmitCommand(client, completed, "SubmitApplication", 0,
            new { documentsComplete = false });
        await SubmitCommand(client, completed, "ReceiveDocuments", 1, new { documentsComplete = true });
        Assert.Equal("Cancelled", (await GetTimers(client, completed))[0].GetProperty("status").GetString());

        var cancelled = $"gate8-cancelled-{Guid.NewGuid():N}";
        await CreateInstance(client, cancelled, "4.0", documentsComplete: false);
        await SubmitCommand(client, cancelled, "SubmitApplication", 0,
            new { documentsComplete = false });
        await SubmitCommand(client, cancelled, "CancelApplication", 1);
        Assert.Equal("Cancelled", (await GetTimers(client, cancelled))[0].GetProperty("status").GetString());
        clock.Advance(TimeSpan.FromDays(11));
        Assert.Empty(await ProcessDueTimers(client));
        Assert.Equal("Cancelled", (await GetInstance(client, cancelled)).GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Failed_atomic_timer_insert_rolls_back_offer_state_and_history()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        using var factory = environment.CreateFactory();
        using var client = environment.CreateClient(factory);
        await Register(client, await File.ReadAllTextAsync(DefinitionPath));
        var instanceId = $"gate8-rollback-{Guid.NewGuid():N}";
        await CreateOfferReadyForIssue(client, instanceId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MachineDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_gate8_timer_insert
                BEFORE INSERT ON Timers
                BEGIN
                    SELECT RAISE(ABORT, 'injected timer persistence failure');
                END;
                """);
        }

        var response = await SendCommand(client, instanceId, "IssueOffer", 1);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("AssessmentReady",
            (await GetInstance(client, instanceId)).GetProperty("currentState").GetString());
        Assert.Empty(await GetTimers(client, instanceId));
        var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
        Assert.NotNull(history);
        Assert.DoesNotContain(history, item => item.GetProperty("transitionId").GetString() == "issue-offer-after-eligibility");
    }

    [Fact]
    public async Task Failed_atomic_timer_cancellation_rolls_back_acceptance_and_preserves_pending_rows()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        using var environment = new TimerApiEnvironment(clock);
        using var factory = environment.CreateFactory();
        using var client = environment.CreateClient(factory);
        await Register(client, await File.ReadAllTextAsync(DefinitionPath));
        var instanceId = $"gate8-cancel-rollback-{Guid.NewGuid():N}";
        await CreateOffer(client, instanceId);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MachineDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_gate8_timer_cancel
                BEFORE UPDATE ON Timers
                WHEN NEW.Status = 'Cancelled'
                BEGIN
                    SELECT RAISE(ABORT, 'injected timer cancellation failure');
                END;
                """);
        }

        using var response = await SendCommand(client, instanceId, "AcceptOffer", 2,
            new { offerValidAtEvaluation = true, offerEvaluationTime = clock.GetUtcNow().ToString("O") });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var current = await GetInstance(client, instanceId);
        Assert.Equal("AwaitingOfferAcceptance", current.GetProperty("currentState").GetString());
        Assert.Equal(2, current.GetProperty("revision").GetInt64());
        Assert.All(await GetTimers(client, instanceId), timer =>
            Assert.Equal("Pending", timer.GetProperty("status").GetString()));
        var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
        Assert.NotNull(history);
        Assert.DoesNotContain(history, item => item.GetProperty("event").GetString() == "AcceptOffer");
    }

    private static async Task CreateOffer(HttpClient client, string instanceId)
    {
        await CreateOfferReadyForIssue(client, instanceId);
        await SubmitCommand(client, instanceId, "IssueOffer", 1);
    }

    private static async Task CreateOfferReadyForIssue(HttpClient client, string instanceId)
    {
        var response = await client.PostAsJsonAsync("/api/instances", new
        {
            id = instanceId,
            definitionId = "orion-equipment-lease",
            definitionVersion = "4.0",
            facts = new
            {
                documentsComplete = true,
                riskScore = 75,
                equipmentValue = 50000,
                checksComplete = true,
                eligibilityDecision = "Eligible",
                cancellationPermitted = true
            }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await SubmitCommand(client, instanceId, "SubmitApplication", 0);
    }

    private static async Task CreateInstance(
        HttpClient client,
        string instanceId,
        string version,
        bool documentsComplete)
    {
        var response = await client.PostAsJsonAsync("/api/instances", new
        {
            id = instanceId,
            definitionId = "orion-equipment-lease",
            definitionVersion = version,
            facts = InstanceFacts(documentsComplete)
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Dictionary<string, object?> InstanceFacts(bool documentsComplete)
    {
        var facts = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["riskScore"] = 75,
            ["equipmentValue"] = 50000,
            ["checksComplete"] = true,
            ["eligibilityDecision"] = "Eligible",
            ["cancellationPermitted"] = true
        };
        if (documentsComplete)
        {
            facts["documentsComplete"] = true;
        }
        return facts;
    }

    private static async Task<JsonElement> SubmitCommand(
        HttpClient client,
        string instanceId,
        string type,
        long expectedRevision,
        object? facts = null)
    {
        using var response = await SendCommand(client, instanceId, type, expectedRevision, facts);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static Task<HttpResponseMessage> SendCommand(
        HttpClient client,
        string instanceId,
        string type,
        long expectedRevision,
        object? facts = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["messageId"] = $"gate8-{Guid.NewGuid():N}",
            ["type"] = type,
            ["expectedRevision"] = expectedRevision
        };
        if (facts is not null)
        {
            request["facts"] = facts;
        }
        return client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", request);
    }

    private static async Task Register(HttpClient client, string definition)
    {
        using var response = await client.PostAsync("/api/definitions",
            new StringContent(definition, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> GetInstance(HttpClient client, string instanceId)
    {
        using var response = await client.GetAsync($"/api/instances/{instanceId}");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement[]> GetTimers(HttpClient client, string instanceId) =>
        await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/timers") ?? [];

    private static async Task<JsonElement[]> ProcessDueTimers(HttpClient client)
    {
        using var response = await client.PostAsync("/api/test-controls/timers/process-due?limit=100", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private sealed class TimerApiEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dsm-gate8-{Guid.NewGuid():N}.db");
        internal const string TestKey = "gate8-test-only-local-key";

        public TimerApiEnvironment(MutableTimeProvider clock)
        {
            Clock = clock;
            Set("DURABLE_STATE_MACHINE_DB", _databasePath);
            Set("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", "true");
            Set("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TestKey);
            Set("Harness__Capabilities__customer-notification__ProviderIdentity", "synthetic-notification-provider");
            Set("Harness__Capabilities__customer-notification__ProviderId", "00000000-0000-0000-0000-000000000101");
            Set("Harness__Capabilities__customer-notification__ApiDescriptionVersionId", "00000000-0000-0000-0000-000000000102");
            Set("Harness__Capabilities__customer-notification__OperationKey", "POST:/notifications");
        }

        public MutableTimeProvider Clock { get; }

        public WebApplicationFactory<Program> CreateFactory() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton<TimeProvider>(Clock);
                }));

        public HttpClient CreateClient(WebApplicationFactory<Program> factory)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-State-Machine-Trusted-Ingestion-Key", TestKey);
            return client;
        }

        private void Set(string name, string value)
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (key, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);

        public void SetUtcNow(DateTimeOffset value) => _now = value.ToUniversalTime();
    }
}
