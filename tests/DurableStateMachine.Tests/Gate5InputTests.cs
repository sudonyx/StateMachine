using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate5InputTests
{
    private const string TrustedKey = "gate5-test-ingestion-key";
    private const string Definition = """
        {
          "languageVersion": "1.1",
          "id": "gate5-test",
          "version": "1",
          "initialState": "Draft",
          "states": [
            { "id": "Draft" },
            { "id": "Active" },
            { "id": "Cancelled", "terminal": true },
            { "id": "Done", "terminal": true }
          ],
          "events": ["Request", "Cancel", "Complete"],
          "inputPolicy": {
            "commands": [
              { "name": "StartWork", "event": "Request" },
              { "name": "CancelWork", "event": "Cancel" }
            ],
            "observations": [
              { "name": "WorkCompleted", "event": "Complete", "requiredEvidence": ["resultDigest"] }
            ]
          },
          "transitions": [
            { "id": "request", "source": "Draft", "event": "Request", "target": "Active" },
            { "id": "cancel-draft", "source": "Draft", "event": "Cancel", "target": "Cancelled" },
            { "id": "cancel-active", "source": "Active", "event": "Cancel", "target": "Cancelled" },
            { "id": "complete", "source": "Active", "event": "Complete", "target": "Done" }
          ]
        }
        """;

    [Fact]
    public async Task Commands_observations_and_legacy_events_enforce_the_input_boundary()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate5-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
        var instanceId = $"gate5-{Guid.NewGuid():N}";
        var operationId = $"operation-{Guid.NewGuid():N}";
        try
        {
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);

                Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
                {
                    id = instanceId,
                    definitionId = "gate5-test",
                    definitionVersion = "1",
                    facts = new { }
                })).StatusCode);

                client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
                var unauthorizedSeed = await client.PostAsJsonAsync($"/api/instances/{instanceId}/expected-operations", new
                {
                    operationId,
                    observationType = "WorkCompleted",
                    sourceIdentity = "synthetic-worker"
                });
                Assert.Equal(HttpStatusCode.Forbidden, unauthorizedSeed.StatusCode);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);

                var bypass = await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Request" });
                Assert.Equal(HttpStatusCode.Forbidden, bypass.StatusCode);
                using (var bypassBody = JsonDocument.Parse(await bypass.Content.ReadAsStringAsync()))
                {
                    Assert.Equal("legacy-event-endpoint-restricted",
                        bypassBody.RootElement.GetProperty("code").GetString());
                }

                var command = new
                {
                    deliveryId = "transport-command-1",
                    messageId = "command-1",
                    operationId = "business-command-1",
                    type = "StartWork",
                    expectedRevision = 0,
                    payload = new { comment = "start", state = "Done" }
                };
                var repeatedDelivery = new
                {
                    deliveryId = "transport-command-2",
                    messageId = command.messageId,
                    operationId = command.operationId,
                    type = command.type,
                    expectedRevision = command.expectedRevision,
                    payload = command.payload
                };
                var duplicateDeliveries = await Task.WhenAll(
                    client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", command),
                    client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", repeatedDelivery));
                var results = await Task.WhenAll(duplicateDeliveries.Select(async response =>
                {
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    return (body.RootElement.GetProperty("message").GetProperty("status").GetString(),
                        body.RootElement.GetProperty("instance").GetProperty("revision").GetInt64(),
                        body.RootElement.GetProperty("instance").GetProperty("currentState").GetString(),
                        body.RootElement.GetProperty("message").GetProperty("deliveryId").GetString(),
                        body.RootElement.GetProperty("message").GetProperty("operationId").GetString());
                }));
                Assert.Single(results, result => result.Item1 == "Accepted");
                Assert.Single(results, result => result.Item1 == "Duplicate");
                Assert.All(results, result => Assert.Equal(1, result.Item2));
                Assert.All(results, result => Assert.Equal("Active", result.Item3));
                Assert.Contains(results, result => result.Item4 == "transport-command-1");
                Assert.Contains(results, result => result.Item4 == "transport-command-2");
                Assert.All(results, result => Assert.Equal("business-command-1", result.Item5));

                using (var conflicting = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/commands", new
                    {
                        deliveryId = "transport-command-conflict",
                        messageId = "command-1",
                        operationId = "business-command-1",
                        type = "StartWork",
                        expectedRevision = 0,
                        payload = new { comment = "changed" }
                    })).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("IdentityConflict", conflicting.RootElement.GetProperty("message").GetProperty("status").GetString());
                }

                using (var repeatedAction = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/commands", new
                    {
                        messageId = "command-2",
                        type = "StartWork",
                        expectedRevision = 1
                    })).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Rejected", repeatedAction.RootElement.GetProperty("message").GetProperty("status").GetString());
                    Assert.Null(repeatedAction.RootElement.GetProperty("message").GetProperty("acceptedEvent").GetString());
                    Assert.Equal("no-applicable-transition", repeatedAction.RootElement.GetProperty("attempt").GetProperty("reasonCode").GetString());
                }

                using (var staleCommand = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/commands", new
                    {
                        messageId = "stale-command",
                        type = "CancelWork",
                        expectedRevision = 0
                    })).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Stale", staleCommand.RootElement.GetProperty("message").GetProperty("status").GetString());
                }

                client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
                using (var untrusted = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "forged", "missing-op", 1, "synthetic-worker", new { resultDigest = "fake" }, DateTimeOffset.Parse("2026-01-01T00:00:00Z")))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Untrusted", untrusted.RootElement.GetProperty("message").GetProperty("status").GetString());
                    Assert.Equal("untrusted-ingestion-boundary", untrusted.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());
                }
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);

                var seeded = await client.PostAsJsonAsync($"/api/instances/{instanceId}/expected-operations", new
                {
                    operationId,
                    observationType = "WorkCompleted",
                    sourceIdentity = "synthetic-worker",
                    expectedRevision = 1
                });
                Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

                var malformedFacts = await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "malformed-facts", operationId, 1, "synthetic-worker",
                        new { resultDigest = "x" }, null, new { fact = new[] { "not-a-scalar" } }));
                Assert.Equal(HttpStatusCode.BadRequest, malformedFacts.StatusCode);
                var recordedMessages = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages");
                Assert.NotNull(recordedMessages);
                Assert.Contains(recordedMessages, message =>
                    message.GetProperty("messageId").GetString() == "malformed-facts" &&
                    message.GetProperty("status").GetString() == "Malformed" &&
                    message.GetProperty("reasonCode").GetString() == "malformed-input");

                using (var unknownOperation = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "unknown", "not-expected", 1, "synthetic-worker", new { resultDigest = "x" }, null))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Unexpected", unknownOperation.RootElement.GetProperty("message").GetProperty("status").GetString());
                }
                using (var spoofedSource = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "spoofed", operationId, 1, "unlisted-source", new { resultDigest = "x" }, null))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Untrusted", spoofedSource.RootElement.GetProperty("message").GetProperty("status").GetString());
                    Assert.Equal("source-identity-mismatch", spoofedSource.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());
                }
                using (var missingEvidence = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "missing-evidence", operationId, 1, "synthetic-worker", new { }, null))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Malformed", missingEvidence.RootElement.GetProperty("message").GetProperty("status").GetString());
                }

                var occurredAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
                await using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
                {
                    await connection.OpenAsync();
                    await using var triggerCommand = connection.CreateCommand();
                    triggerCommand.CommandText = """
                        CREATE TRIGGER fail_gate5_history_insert
                        BEFORE INSERT ON TransitionHistory
                        BEGIN
                            SELECT RAISE(ABORT, 'injected Gate 5 history failure');
                        END;
                        """;
                    await triggerCommand.ExecuteNonQueryAsync();
                }
                var failedCommit = await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "delivery-atomic", operationId, 1, "synthetic-worker",
                        new { resultDigest = "sha256:atomic" }, occurredAt));
                Assert.Equal(HttpStatusCode.InternalServerError, failedCommit.StatusCode);
                var unchanged = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
                Assert.Equal("Active", unchanged.GetProperty("currentState").GetString());
                Assert.Equal(1, unchanged.GetProperty("revision").GetInt64());
                await using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
                {
                    await connection.OpenAsync();
                    await using var dropCommand = connection.CreateCommand();
                    dropCommand.CommandText = "DROP TRIGGER fail_gate5_history_insert";
                    await dropCommand.ExecuteNonQueryAsync();
                }

                var acceptedObservation = await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "delivery-1", operationId, 1, "synthetic-worker",
                        new { resultDigest = "sha256:abc" }, occurredAt));
                Assert.Equal(HttpStatusCode.OK, acceptedObservation.StatusCode);
                using (var body = JsonDocument.Parse(await acceptedObservation.Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Accepted", body.RootElement.GetProperty("message").GetProperty("status").GetString());
                    Assert.Equal("Complete", body.RootElement.GetProperty("message").GetProperty("acceptedEvent").GetString());
                    Assert.Equal("trusted-ingestion-key", body.RootElement.GetProperty("message").GetProperty("authority").GetString());
                    Assert.Equal("transport-delivery-1", body.RootElement.GetProperty("message").GetProperty("deliveryId").GetString());
                    Assert.Equal("observation-delivery-1", body.RootElement.GetProperty("message").GetProperty("observationId").GetString());
                    Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("message").GetProperty("operationId").ValueKind);
                    Assert.Equal("Done", body.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
                    Assert.Equal("sha256:abc", body.RootElement.GetProperty("message").GetProperty("requestEnvelope")
                        .GetProperty("evidence").GetProperty("resultDigest").GetString());
                }

                using (var duplicateResult = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "delivery-1", operationId, 1, "synthetic-worker",
                        new { resultDigest = "sha256:abc" }, occurredAt,
                        deliveryId: "transport-delivery-1-retry"))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Duplicate", duplicateResult.RootElement.GetProperty("message").GetProperty("status").GetString());
                    Assert.Equal("duplicate-message", duplicateResult.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());
                    Assert.Equal("transport-delivery-1-retry",
                        duplicateResult.RootElement.GetProperty("message").GetProperty("deliveryId").GetString());
                    Assert.Equal("observation-delivery-1",
                        duplicateResult.RootElement.GetProperty("message").GetProperty("observationId").GetString());
                }
                using (var duplicateResult = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "delivery-2", operationId, 1, "synthetic-worker",
                        new { resultDigest = "sha256:abc" }, occurredAt))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Duplicate", duplicateResult.RootElement.GetProperty("message").GetProperty("status").GetString());
                    Assert.Equal("duplicate-operation-result", duplicateResult.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());
                }
                using (var conflictingResult = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/observations", Observation(
                        "delivery-3", operationId, 1, "synthetic-worker",
                        new { resultDigest = "sha256:different" }, occurredAt))
                    ).Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Conflicting", conflictingResult.RootElement.GetProperty("message").GetProperty("status").GetString());
                }

                var messages = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/inbound-messages");
                Assert.NotNull(messages);
                Assert.Contains(messages, message => message.GetProperty("inputKind").GetString() == "LegacyEvent");
                Assert.Contains(messages, message => message.GetProperty("reasonCode").GetString() == "source-identity-mismatch");
                var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
                Assert.NotNull(history);
                Assert.Equal(2, history.Length);
            }

            using (var restartedFactory = new WebApplicationFactory<Program>())
            using (var restartedClient = restartedFactory.CreateClient())
            {
                restartedClient.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
                var duplicateAfterRestartResponse = await restartedClient.PostAsJsonAsync(
                    $"/api/instances/{instanceId}/commands", new
                    {
                        deliveryId = "transport-command-after-restart",
                        messageId = "command-1",
                        operationId = "business-command-1",
                        type = "StartWork",
                        expectedRevision = 0,
                        payload = new { comment = "start", state = "Done" }
                    });
                var duplicateAfterRestartJson = await duplicateAfterRestartResponse.Content.ReadAsStringAsync();
                using var duplicateAfterRestart = JsonDocument.Parse(duplicateAfterRestartJson);
                Assert.Equal("Duplicate", duplicateAfterRestart.RootElement.GetProperty("message").GetProperty("status").GetString());
                Assert.Equal("Done", (await restartedClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}"))
                    .GetProperty("currentState").GetString());
            }
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
    public async Task Late_observation_after_cancellation_is_inspectable_but_cannot_reactivate_instance()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate5-late-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
            const string id = "late-result";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id,
                definitionId = "gate5-test",
                definitionVersion = "1",
                facts = new { }
            })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(
                $"/api/instances/{id}/expected-operations", new
                {
                    operationId = "late-operation",
                    observationType = "WorkCompleted",
                    sourceIdentity = "synthetic-worker",
                    expectedRevision = 0
                })).StatusCode);

            using (var cancellation = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                $"/api/instances/{id}/commands", new
                {
                    messageId = "cancel-before-result",
                    type = "CancelWork",
                    expectedRevision = 0
                })).Content.ReadAsStringAsync()))
            {
                Assert.Equal("Accepted", cancellation.RootElement.GetProperty("message").GetProperty("status").GetString());
                Assert.Equal("Cancelled", cancellation.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
            }

            using var lateObservation = JsonDocument.Parse(await (await client.PostAsJsonAsync(
                $"/api/instances/{id}/observations", Observation(
                    "late-delivery", "late-operation", 0, "synthetic-worker", new { resultDigest = "late" }, null)))
                .Content.ReadAsStringAsync());
            Assert.Equal("Stale", lateObservation.RootElement.GetProperty("message").GetProperty("status").GetString());
            Assert.Equal("terminal-instance", lateObservation.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());
            Assert.Equal(JsonValueKind.Null, lateObservation.RootElement.GetProperty("attempt").ValueKind);
            var current = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{id}");
            Assert.Equal("Cancelled", current.GetProperty("currentState").GetString());
            Assert.Equal(1, current.GetProperty("revision").GetInt64());
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

    private static object Observation(
        string messageId,
        string operationId,
        long revision,
        string source,
        object evidence,
        DateTimeOffset? occurredAt,
        object? facts = null,
        string? deliveryId = null) => new
    {
        deliveryId = deliveryId ?? $"transport-{messageId}",
        messageId,
        observationId = $"observation-{messageId}",
        type = "WorkCompleted",
        expectedRevision = revision,
        correlationId = operationId,
        source,
        trusted = true,
        occurredAt,
        evidence,
        facts
    };

    private static StringContent Json(string value) =>
        new(value, System.Text.Encoding.UTF8, "application/json");
}

[CollectionDefinition("API environment", DisableParallelization = true)]
public sealed class ApiEnvironmentCollection { }
