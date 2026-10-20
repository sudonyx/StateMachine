using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using DurableStateMachine.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class RecoveryReplayTests
{
    private const string Definition = """
        {
          "languageVersion": "1.0",
          "id": "replay-test",
          "version": "7",
          "initialState": "Draft",
          "states": [{ "id": "Draft" }, { "id": "Done", "terminal": true }],
          "events": ["Complete"],
          "transitions": [
            { "id": "complete", "source": "Draft", "event": "Complete", "target": "Done" }
          ]
        }
        """;
    private const string DefinitionWithObservation = """
        {
          "languageVersion": "1.1",
          "id": "replay-observation-test",
          "version": "1",
          "initialState": "Draft",
          "states": [{ "id": "Draft" }, { "id": "Done", "terminal": true }],
          "events": ["Complete"],
          "inputPolicy": {
            "commands": [],
            "observations": [{ "name": "WorkCompleted", "event": "Complete", "requiredEvidence": [] }]
          },
          "transitions": [
            { "id": "complete", "source": "Draft", "event": "Complete", "target": "Done" }
          ]
        }
        """;
    private const string TimedDefinition = """
        {
          "languageVersion": "1.2",
          "id": "recovery-timer-test",
          "version": "1",
          "initialState": "Open",
          "states": [{ "id": "Open" }, { "id": "Expired", "terminal": true }],
          "events": ["Start", "DeadlineReached"],
          "inputPolicy": {
            "commands": [{ "name": "Start", "event": "Start" }],
            "observations": []
          },
          "transitions": [
            { "id": "start", "source": "Open", "event": "Start", "target": "Open" },
            { "id": "expire", "source": "Open", "event": "DeadlineReached", "target": "Expired" }
          ],
          "timers": [{
            "id": "deadline",
            "onTransition": "start",
            "duration": "P1D",
            "event": "DeadlineReached",
            "whileState": "Open",
            "cancelOnTransitions": [],
            "blockedEvents": [],
            "invalidatedByDueTimers": []
          }]
        }
        """;

    [Fact]
    public async Task Replay_and_recovery_reconstruct_history_without_mutating_it()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-replay-{Guid.NewGuid():N}.db");
        var providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-replay-providers-{Guid.NewGuid():N}");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", providerDirectory);
        try
        {
            var instanceId = $"replay-{Guid.NewGuid():N}";
            var externalCalls = new CountingExternalHandler();
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient<HarnessExecutionClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => externalCalls)));
            using var client = factory.CreateClient();

            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
            var newerDefinition = Definition
                .Replace("\"version\": \"7\"", "\"version\": \"8\"", StringComparison.Ordinal)
                .Replace("\"initialState\": \"Draft\"", "\"initialState\": \"Done\"", StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync("/api/definitions", Json(newerDefinition))).StatusCode);
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync("/api/instances", new
                {
                    id = instanceId,
                    definitionId = "replay-test",
                    definitionVersion = "7"
                })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" })).StatusCode);

            var historyBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
            var attemptsBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/attempts");
            var receiptsBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages");
            var timersBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/timers");
            var effectsBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/effects");
            var expectedBefore = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/expected-operations");
            var instanceBefore = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
            var replay = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/replay");
            Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
            Assert.Equal("Draft", replay.GetProperty("initialStateFromBoundDefinition").GetString());
            Assert.Equal("7", replay.GetProperty("definitionVersion").GetString());
            Assert.Equal("Done", replay.GetProperty("reconstructedFinalState").GetString());
            Assert.Equal(1, replay.GetProperty("reconstructedRevision").GetInt64());
            Assert.Equal(2, replay.GetProperty("historicalAttemptsExamined").GetInt32());
            Assert.Equal(1, replay.GetProperty("acceptedTransitionsExamined").GetInt32());

            var decisionVerification = await client.GetFromJsonAsync<JsonElement>(
                $"/api/instances/{instanceId}/replay/decision-verification");
            Assert.Equal("Verified", decisionVerification.GetProperty("decisionVerificationOutcome").GetString());

            var recoveryFirst = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/recovery");
            var recoverySecond = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/recovery");
            Assert.Equal("ConsistentNoOutstandingWork", recoveryFirst.GetProperty("classification").GetString());
            Assert.Equal(recoveryFirst.GetProperty("currentRevision").GetInt64(),
                recoverySecond.GetProperty("currentRevision").GetInt64());
            Assert.Equal("Consistent", recoverySecond.GetProperty("replay").GetProperty("outcome").GetString());
            var historyAfter = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
            Assert.Equal(historyBefore?.Select(item => item.GetProperty("id").GetString()),
                historyAfter?.Select(item => item.GetProperty("id").GetString()));
            Assert.Equal(attemptsBefore?.Select(item => item.GetRawText()), (
                await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/attempts"))?.Select(item => item.GetRawText()));
            Assert.Equal(receiptsBefore?.Select(item => item.GetRawText()), (
                await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages"))?.Select(item => item.GetRawText()));
            Assert.Equal(timersBefore?.Select(item => item.GetRawText()), (
                await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/timers"))?.Select(item => item.GetRawText()));
            Assert.Equal(effectsBefore?.Select(item => item.GetRawText()), (
                await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/effects"))?.Select(item => item.GetRawText()));
            Assert.Equal(expectedBefore?.Select(item => item.GetRawText()), (
                await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/expected-operations"))?.Select(item => item.GetRawText()));
            Assert.Equal(instanceBefore.GetRawText(),
                (await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}")).GetRawText());
            Assert.Equal(0, externalCalls.Count);
            Assert.Empty(Directory.Exists(providerDirectory)
                ? Directory.GetFiles(providerDirectory, "*.db")
                : []);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", previousProviderDirectory);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
            if (Directory.Exists(providerDirectory))
            {
                Directory.Delete(providerDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Replay_reports_projection_mismatch_without_repairing_it()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-replay-corrupt-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            var instanceId = $"replay-corrupt-{Guid.NewGuid():N}";
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "replay-test",
                        definitionVersion = "7"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" })).StatusCode);
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Instances SET CurrentState = 'Draft', Revision = 9 WHERE Id = $id";
                command.Parameters.AddWithValue("$id", instanceId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            using (var restartedFactory = new WebApplicationFactory<Program>())
            using (var restartedClient = restartedFactory.CreateClient())
            {
                var replay = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/replay");
                Assert.Equal("Inconsistent", replay.GetProperty("outcome").GetString());
                Assert.Equal("Done", replay.GetProperty("reconstructedFinalState").GetString());
                Assert.Equal("Draft", replay.GetProperty("recordedCurrentState").GetString());
                Assert.Equal(9, replay.GetProperty("recordedRevision").GetInt64());
                Assert.Contains(replay.GetProperty("integrityFindings").EnumerateArray(),
                    finding => finding.GetProperty("code").GetString() == "projection-revision-mismatch");
                Assert.Contains(replay.GetProperty("integrityFindings").EnumerateArray(),
                    finding => finding.GetProperty("code").GetString() == "projection-state-mismatch");

                var recorded = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
                Assert.Equal("Draft", recorded.GetProperty("currentState").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Replay_detects_revision_gaps_in_accepted_history()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-replay-gap-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            var instanceId = $"replay-gap-{Guid.NewGuid():N}";
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "replay-test",
                        definitionVersion = "7"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" })).StatusCode);
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE TransitionHistory SET RevisionBefore = 3, RevisionAfter = 4 WHERE InstanceId = $id";
                command.Parameters.AddWithValue("$id", instanceId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            using var restartedFactory = new WebApplicationFactory<Program>();
            using var restartedClient = restartedFactory.CreateClient();
            var replay = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/replay");
            Assert.Equal("Inconsistent", replay.GetProperty("outcome").GetString());
            Assert.Contains(replay.GetProperty("integrityFindings").EnumerateArray(),
                finding => finding.GetProperty("code").GetString() == "revision-gap-or-invalid-sequence");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Decision_verification_reports_legacy_missing_evidence_as_insufficient()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-replay-evidence-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            var instanceId = $"replay-evidence-{Guid.NewGuid():N}";
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "replay-test",
                        definitionVersion = "7"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" })).StatusCode);
            }

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE InputAttempts SET EvaluationEvidenceJson = '{}', EvaluationEvidenceDigest = '' WHERE InstanceId = $id";
                command.Parameters.AddWithValue("$id", instanceId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            using var restartedFactory = new WebApplicationFactory<Program>();
            using var restartedClient = restartedFactory.CreateClient();
            var replay = await restartedClient.GetFromJsonAsync<JsonElement>(
                $"/api/instances/{instanceId}/replay/decision-verification");
            Assert.Equal("InsufficientEvidence", replay.GetProperty("outcome").GetString());
            Assert.Equal("Consistent", replay.GetProperty("stateReconstructionOutcome").GetString());
            Assert.Equal("InsufficientEvidence",
                replay.GetProperty("decisionVerificationOutcome").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Recovery_reports_overdue_timer_without_processing_it()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-recovery-timer-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            var instanceId = $"recovery-timer-{Guid.NewGuid():N}";
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync("/api/definitions", Json(TimedDefinition))).StatusCode);
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync("/api/instances", new
                {
                    id = instanceId,
                    definitionId = "recovery-timer-test",
                    definitionVersion = "1"
                })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
                {
                    messageId = "start-message",
                    type = "Start",
                    expectedRevision = 0
                })).StatusCode);

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Timers SET DueAt = $dueAt WHERE InstanceId = $id";
                command.Parameters.AddWithValue("$dueAt", DateTimeOffset.UtcNow.AddMinutes(-1).UtcTicks);
                command.Parameters.AddWithValue("$id", instanceId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var assessment = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/recovery");
            Assert.Equal("ConsistentPendingLocalWork", assessment.GetProperty("classification").GetString());
            var timer = Assert.Single(assessment.GetProperty("timers").EnumerateArray());
            Assert.Equal("Pending", timer.GetProperty("status").GetString());
            Assert.True(timer.GetProperty("overdue").GetBoolean());

            var instance = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
            Assert.Equal("Open", instance.GetProperty("currentState").GetString());
            Assert.Equal(1, instance.GetProperty("revision").GetInt64());
            var timersAfterAssessment = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{instanceId}/timers");
            Assert.Equal("Pending", Assert.Single(timersAfterAssessment!).GetProperty("status").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Recovery_reports_expected_external_work_without_submitting_it()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-recovery-pending-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        const string trustedKey = "recovery-test-key";
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", trustedKey);
        try
        {
            var instanceId = $"recovery-pending-{Guid.NewGuid():N}";
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync("/api/definitions", Json(DefinitionWithObservation))).StatusCode);
            Assert.Equal(HttpStatusCode.Created,
                (await client.PostAsJsonAsync("/api/instances", new
                {
                    id = instanceId,
                    definitionId = "replay-observation-test",
                    definitionVersion = "1"
                })).StatusCode);

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/instances/{instanceId}/expected-operations")
            {
                Content = JsonSerializerContent(new
                {
                    operationId = "external-op-1",
                    observationType = "WorkCompleted",
                    sourceIdentity = "test-provider",
                    expectedRevision = 0
                })
            };
            request.Headers.Add("X-State-Machine-Trusted-Ingestion-Key", trustedKey);
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);

            var assessment = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/recovery");
            Assert.Equal("ConsistentPendingExternalWork", assessment.GetProperty("classification").GetString());
            Assert.Contains(assessment.GetProperty("expectedOperations").EnumerateArray(),
                operation => operation.GetProperty("operationId").GetString() == "external-op-1");
            Assert.Empty(assessment.GetProperty("outstandingEffects").EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", previousKey);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Durable_intake_survives_restart_and_resumes_with_original_identity_and_revision()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-intake-recovery-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousControls = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", "true");
        try
        {
            var instanceId = $"intake-recovery-{Guid.NewGuid():N}";
            string receiptId;
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync("/api/definitions", Json(TimedDefinition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "recovery-timer-test",
                        definitionVersion = "1"
                    })).StatusCode);

                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync($"/api/test-controls/inputs/{instanceId}/BeforeIntakeCommit",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable,
                    (await client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
                    {
                        messageId = "not-durable",
                        type = "Start",
                        expectedRevision = 0
                    })).StatusCode);
                Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages") ?? []);

                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync($"/api/test-controls/inputs/{instanceId}/AfterIntakeCommitBeforeEvaluation",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable,
                    (await client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
                    {
                        messageId = "recover-me",
                        type = "Start",
                        expectedRevision = 0,
                        payload = new { operation = "original" }
                    })).StatusCode);
                var pending = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages");
                var intake = Assert.Single(pending!);
                receiptId = intake.GetProperty("id").GetString()!;
                Assert.Equal("Received", intake.GetProperty("status").GetString());
                Assert.Equal("recover-me", intake.GetProperty("messageId").GetString());
                var assessment = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/recovery");
                Assert.Equal("InsufficientEvidenceForSafeContinuation",
                    assessment.GetProperty("classification").GetString());
            }

            using (var restartedFactory = new WebApplicationFactory<Program>())
            using (var restartedClient = restartedFactory.CreateClient())
            {
                var resumed = await restartedClient.PostAsync(
                    $"/api/instances/{instanceId}/inbound-messages/{receiptId}/resume",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
                using var body = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
                Assert.Equal("Accepted", body.RootElement.GetProperty("message").GetProperty("status").GetString());
                Assert.Equal("recover-me", body.RootElement.GetProperty("message").GetProperty("messageId").GetString());
                Assert.Equal(1, body.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());

                long expectedRevision = 1;
                foreach (var point in new[] { "DuringGuardEvaluation", "BeforeFinalPersistence", "DuringProjectionUpdate" })
                {
                    Assert.Equal(HttpStatusCode.OK,
                        (await restartedClient.PostAsync($"/api/test-controls/inputs/{instanceId}/{point}",
                            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                    var messageId = $"resume-{point}";
                    Assert.Equal(HttpStatusCode.ServiceUnavailable,
                        (await restartedClient.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
                        {
                            messageId,
                            type = "Start",
                            expectedRevision
                        })).StatusCode);
                    var pendingReceipts = await restartedClient.GetFromJsonAsync<JsonElement[]>(
                        $"/api/instances/{instanceId}/inbound-messages");
                    var pendingReceipt = Assert.Single(pendingReceipts!, item =>
                        item.GetProperty("messageId").GetString() == messageId);
                    Assert.Equal("Received", pendingReceipt.GetProperty("status").GetString());
                    Assert.Equal(HttpStatusCode.OK,
                        (await restartedClient.PostAsync(
                            $"/api/instances/{instanceId}/inbound-messages/{pendingReceipt.GetProperty("id").GetString()}/resume",
                            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                    expectedRevision++;
                }

                var histories = await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
                Assert.Equal(4, histories!.Length);
                var attempts = await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/attempts");
                Assert.Equal(4, attempts!.Length);
                var receipts = await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages");
                Assert.Equal(4, receipts!.Length);
                Assert.Equal(HttpStatusCode.Conflict,
                    (await restartedClient.PostAsync(
                        $"/api/instances/{instanceId}/inbound-messages/{receiptId}/resume",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", previousControls);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Independent_hosts_cannot_accept_one_intake_twice()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-intake-race-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousControls = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", "true");
        try
        {
            var instanceId = $"intake-race-{Guid.NewGuid():N}";
            string receiptId;
            using (var sourceFactory = new WebApplicationFactory<Program>())
            using (var sourceClient = sourceFactory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK,
                    (await sourceClient.PostAsync("/api/definitions", Json(TimedDefinition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await sourceClient.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "recovery-timer-test",
                        definitionVersion = "1"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await sourceClient.PostAsync($"/api/test-controls/inputs/{instanceId}/AfterIntakeCommitBeforeEvaluation",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable,
                    (await sourceClient.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
                    {
                        messageId = "contended-input",
                        type = "Start",
                        expectedRevision = 0
                    })).StatusCode);
                var receipts = await sourceClient.GetFromJsonAsync<JsonElement[]>(
                    $"/api/instances/{instanceId}/inbound-messages");
                receiptId = Assert.Single(receipts!).GetProperty("id").GetString()!;
            }

            using var firstFactory = new WebApplicationFactory<Program>();
            using var secondFactory = new WebApplicationFactory<Program>();
            using var firstClient = firstFactory.CreateClient();
            using var secondClient = secondFactory.CreateClient();
            var route = $"/api/instances/{instanceId}/inbound-messages/{receiptId}/resume";
            var results = await Task.WhenAll(
                firstClient.PostAsync(route, new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
                secondClient.PostAsync(route, new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
            Assert.Contains(results, response => response.StatusCode == HttpStatusCode.OK);
            Assert.All(results, response =>
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            var history = await firstClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
            Assert.Single(history!);
            var instance = await firstClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
            Assert.Equal(1, instance.GetProperty("revision").GetInt64());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", previousControls);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Resume_rechecks_original_revision_and_persisted_observation_authority()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-intake-authority-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousControls = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        const string trustedKey = "gate10-resume-test-key";
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", "true");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", trustedKey);
        try
        {
            var commandInstance = $"stale-intake-{Guid.NewGuid():N}";
            var observationInstance = $"trusted-intake-{Guid.NewGuid():N}";
            string staleReceiptId;
            string observationReceiptId;
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(TimedDefinition))).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync("/api/definitions", Json(DefinitionWithObservation))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = commandInstance,
                        definitionId = "recovery-timer-test",
                        definitionVersion = "1"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = observationInstance,
                        definitionId = "replay-observation-test",
                        definitionVersion = "1"
                    })).StatusCode);

                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync($"/api/test-controls/inputs/{commandInstance}/AfterIntakeCommitBeforeEvaluation",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable,
                    (await client.PostAsJsonAsync($"/api/instances/{commandInstance}/commands", new
                    {
                        messageId = "older-command",
                        type = "Start",
                        expectedRevision = 0
                    })).StatusCode);
                var commandReceipts = await client.GetFromJsonAsync<JsonElement[]>(
                    $"/api/instances/{commandInstance}/inbound-messages");
                staleReceiptId = Assert.Single(commandReceipts!).GetProperty("id").GetString()!;
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsJsonAsync($"/api/instances/{commandInstance}/commands", new
                    {
                        messageId = "newer-command",
                        type = "Start",
                        expectedRevision = 0
                    })).StatusCode);

                using var seedOperation = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/instances/{observationInstance}/expected-operations")
                {
                    Content = JsonSerializerContent(new
                    {
                        operationId = "worker-operation",
                        observationType = "WorkCompleted",
                        sourceIdentity = "test-provider",
                        expectedRevision = 0
                    })
                };
                seedOperation.Headers.Add("X-State-Machine-Trusted-Ingestion-Key", trustedKey);
                Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(seedOperation)).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync($"/api/test-controls/inputs/{observationInstance}/AfterIntakeCommitBeforeEvaluation",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                using var observation = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/instances/{observationInstance}/observations")
                {
                    Content = JsonSerializerContent(new
                    {
                        messageId = "provider-result",
                        type = "WorkCompleted",
                        expectedRevision = 0,
                        correlationId = "worker-operation",
                        source = "test-provider",
                        evidence = new { resultDigest = "provider-result-digest" }
                    })
                };
                observation.Headers.Add("X-State-Machine-Trusted-Ingestion-Key", trustedKey);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(observation)).StatusCode);
                var observationReceipts = await client.GetFromJsonAsync<JsonElement[]>(
                    $"/api/instances/{observationInstance}/inbound-messages");
                observationReceiptId = Assert.Single(observationReceipts!).GetProperty("id").GetString()!;
                Assert.Equal("trusted-ingestion-key",
                    observationReceipts![0].GetProperty("authority").GetString());
            }

            using (var restartedFactory = new WebApplicationFactory<Program>())
            using (var restartedClient = restartedFactory.CreateClient())
            {
                using var staleResponse = await restartedClient.PostAsync(
                    $"/api/instances/{commandInstance}/inbound-messages/{staleReceiptId}/resume",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                using var staleBody = JsonDocument.Parse(await staleResponse.Content.ReadAsStringAsync());
                Assert.Equal("Stale", staleBody.RootElement.GetProperty("message").GetProperty("status").GetString());
                Assert.Equal(0, staleBody.RootElement.GetProperty("message").GetProperty("expectedRevision").GetInt64());
                Assert.Equal(1, staleBody.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());

                using var observationResponse = await restartedClient.PostAsync(
                    $"/api/instances/{observationInstance}/inbound-messages/{observationReceiptId}/resume",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, observationResponse.StatusCode);
                using var observationBody = JsonDocument.Parse(await observationResponse.Content.ReadAsStringAsync());
                Assert.Equal("Accepted",
                    observationBody.RootElement.GetProperty("message").GetProperty("status").GetString());
                Assert.Equal(1, observationBody.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", previousControls);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", previousKey);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task Operating_system_process_termination_preserves_a_pending_external_effect()
    {
        const string trustedKey = "gate10-effect-process-test-key";
        const string definition = """
            {
              "languageVersion": "1.1",
              "id": "process-effect-test",
              "version": "1",
              "initialState": "Open",
              "states": [{ "id": "Open" }, { "id": "Done", "terminal": true }],
              "events": ["Complete"],
              "inputPolicy": {
                "commands": [],
                "observations": [{ "name": "OfferDocumentGenerated", "event": "Complete", "requiredEvidence": [] }]
              },
              "transitions": [
                { "id": "complete", "source": "Open", "event": "Complete", "target": "Done" }
              ]
            }
            """;
        var database = Path.Combine(Path.GetTempPath(), $"dsm-process-effect-{Guid.NewGuid():N}.db");
        var providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-process-provider-{Guid.NewGuid():N}");
        var apiAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.Api.dll");
        var instanceId = $"process-effect-{Guid.NewGuid():N}";
        const string operationId = "pending-provider-operation";
        Process? api = null;
        Process? restartedApi = null;
        HttpResponseMessage? interruptedResponse = null;
        try
        {
            var firstPort = GetUnusedPort();
            api = StartApiProcess(apiAssembly, database, firstPort, trustedKey, providerDirectory);
            using (var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{firstPort}") })
            {
                await WaitForApiAsync(client, api);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(definition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "process-effect-test",
                        definitionVersion = "1"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync(
                        $"/api/test-controls/effects/{operationId}/AfterLocalCommitBeforeProvider/pause",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
                using var effectRequest = new HttpRequestMessage(HttpMethod.Post,
                    $"/api/instances/{instanceId}/direct-effects")
                {
                    Content = JsonSerializerContent(new
                    {
                        operationId,
                        capability = "offer-document",
                        customerReference = "synthetic-customer",
                        offerReference = "synthetic-offer"
                    })
                };
                effectRequest.Headers.Add("X-State-Machine-Trusted-Ingestion-Key", trustedKey);
                var interruptedRequest = client.SendAsync(effectRequest);
                await WaitForPauseAsync(client, api,
                    $"/api/test-controls/effects/{operationId}/AfterLocalCommitBeforeProvider/pause-status");
                api.Kill(entireProcessTree: true);
                await api.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    interruptedResponse = await interruptedRequest.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (HttpRequestException)
                {
                }
            }

            var secondPort = GetUnusedPort();
            restartedApi = StartApiProcess(apiAssembly, database, secondPort, trustedKey, providerDirectory);
            using (var restartedClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{secondPort}") })
            {
                await WaitForApiAsync(restartedClient, restartedApi);
                var assessment = await restartedClient.GetFromJsonAsync<JsonElement>(
                    $"/api/instances/{instanceId}/recovery");
                Assert.Equal("ConsistentPendingExternalWork",
                    assessment.GetProperty("classification").GetString());
                Assert.Contains(assessment.GetProperty("outstandingEffects").EnumerateArray(), effect =>
                    effect.GetProperty("operationId").GetString() == operationId &&
                    effect.GetProperty("status").GetString() == "Pending");
                Assert.Contains(assessment.GetProperty("expectedOperations").EnumerateArray(), operation =>
                    operation.GetProperty("operationId").GetString() == operationId &&
                    operation.GetProperty("status").GetString() == "Expected");
                Assert.Empty(Directory.Exists(providerDirectory)
                    ? Directory.GetFiles(providerDirectory, "*.db")
                    : []);
            }
        }
        finally
        {
            interruptedResponse?.Dispose();
            StopOwnedProcess(api);
            StopOwnedProcess(restartedApi);
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, $"{database}-wal", $"{database}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            if (Directory.Exists(providerDirectory))
            {
                Directory.Delete(providerDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("AfterIntakeCommitBeforeEvaluation")]
    [InlineData("BeforeFinalPersistence")]
    public async Task Operating_system_process_termination_preserves_recoverable_input(string point)
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-process-kill-{Guid.NewGuid():N}.db");
        var apiAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.Api.dll");
        Assert.True(File.Exists(apiAssembly), $"Expected API assembly at '{apiAssembly}'.");
        var instanceId = $"process-kill-{Guid.NewGuid():N}";
        var receiptId = string.Empty;
        Process? api = null;
        Process? restartedApi = null;
        HttpResponseMessage? interruptedResponse = null;
        try
        {
            var firstPort = GetUnusedPort();
            api = StartApiProcess(apiAssembly, database, firstPort);
            using (var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{firstPort}") })
            {
                await WaitForApiAsync(client, api);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(TimedDefinition))).StatusCode);
                Assert.Equal(HttpStatusCode.Created,
                    (await client.PostAsJsonAsync("/api/instances", new
                    {
                        id = instanceId,
                        definitionId = "recovery-timer-test",
                        definitionVersion = "1"
                    })).StatusCode);
                Assert.Equal(HttpStatusCode.OK,
                    (await client.PostAsync(
                        $"/api/test-controls/inputs/{instanceId}/{point}/pause",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);

                var interruptedRequest = client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
                {
                    messageId = "process-owned-intake",
                    type = "Start",
                    expectedRevision = 0
                });
                await WaitForPauseAsync(client, api,
                    $"/api/test-controls/inputs/{instanceId}/{point}/pause-status");
                receiptId = await WaitForIntakeAsync(database, instanceId);
                api.Kill(entireProcessTree: true);
                await api.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    interruptedResponse = await interruptedRequest.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (HttpRequestException)
                {
                    // The request connection is expected to end with its owning process.
                }
                Assert.True(api.HasExited);
            }

            var secondPort = GetUnusedPort();
            restartedApi = StartApiProcess(apiAssembly, database, secondPort);
            using (var restartedClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{secondPort}") })
            {
                await WaitForApiAsync(restartedClient, restartedApi);
                var intake = await restartedClient.GetFromJsonAsync<JsonElement>(
                    $"/api/instances/{instanceId}/inbound-messages/{receiptId}");
                Assert.Equal("Received", intake.GetProperty("status").GetString());
                var projection = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
                Assert.Equal("Open", projection.GetProperty("currentState").GetString());
                Assert.Equal(0, projection.GetProperty("revision").GetInt64());
                Assert.Empty(await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history") ?? []);
                using var resume = await restartedClient.PostAsync(
                    $"/api/instances/{instanceId}/inbound-messages/{receiptId}/resume",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.OK, resume.StatusCode);
                using var body = JsonDocument.Parse(await resume.Content.ReadAsStringAsync());
                Assert.Equal("Accepted", body.RootElement.GetProperty("message").GetProperty("status").GetString());
                var history = await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
                Assert.Single(history!);
            }
        }
        finally
        {
            interruptedResponse?.Dispose();
            StopOwnedProcess(api);
            StopOwnedProcess(restartedApi);
            SqliteConnection.ClearAllPools();
            foreach (var path in new[] { database, $"{database}-wal", $"{database}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static StringContent Json(string value) => new(value, System.Text.Encoding.UTF8, "application/json");

    private static StringContent JsonSerializerContent<T>(T value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process StartApiProcess(
        string apiAssembly,
        string database,
        int port,
        string? trustedKey = null,
        string? providerDirectory = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(apiAssembly);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DURABLE_STATE_MACHINE_DB"] = database;
        startInfo.Environment["DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS"] = "true";
        if (trustedKey is not null)
        {
            startInfo.Environment["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = trustedKey;
        }
        if (providerDirectory is not null)
        {
            startInfo.Environment["DURABLE_STATE_MACHINE_PROVIDER_DB_DIR"] = providerDirectory;
        }
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the API child process.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitForApiAsync(HttpClient client, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"API child process exited with code {process.ExitCode}.");
            }
            try
            {
                using var response = await client.GetAsync("/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("The API child process did not become healthy.");
    }

    private static async Task<string> WaitForIntakeAsync(string database, string instanceId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Pooling = false
                }.ToString());
                await connection.OpenAsync(timeout.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT Id FROM InboundMessages
                    WHERE InstanceId = $instanceId AND Status = 'Received'
                    """;
                command.Parameters.AddWithValue("$instanceId", instanceId);
                var receiptId = await command.ExecuteScalarAsync(timeout.Token) as string;
                if (receiptId is not null)
                {
                    return receiptId;
                }
            }
            catch (SqliteException)
            {
            }
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException("The durable intake row did not reach the expected process-kill boundary.");
    }

    private static async Task WaitForPauseAsync(
        HttpClient client,
        Process process,
        string statusPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"API child process exited with code {process.ExitCode}.");
            }
            using var response = await client.GetAsync(statusPath, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (status.RootElement.GetProperty("reached").GetBoolean())
            {
                return;
            }
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException($"The API child process did not reach pause status '{statusPath}'.");
    }

    private static void StopOwnedProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        process.Dispose();
    }

    private sealed class CountingExternalHandler : HttpMessageHandler
    {
        public int Count { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            throw new InvalidOperationException($"Read-only recovery unexpectedly called {request.RequestUri}.");
        }
    }
}
