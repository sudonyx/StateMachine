using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate12ConcernTests
{
    private const string TrustedKey = "gate12-test-ingestion-key";

    [Fact]
    public async Task Concerns_progress_independently_coordinate_offer_release_and_replay_after_restart()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate12-{Guid.NewGuid():N}.db");
        var providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-gate12-providers-{Guid.NewGuid():N}");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", providerDirectory);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
        string instanceId;
        string v4InstanceId;
        string v5InstanceId;
        try
        {
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                AddTrustedKey(client);
                foreach (var filename in new[] { "orion-v4.json", "orion-v5.json", "orion-v6.json" })
                {
                    var definition = await File.ReadAllTextAsync(
                        Path.Combine(AppContext.BaseDirectory, "definitions", filename));
                    Assert.Equal(HttpStatusCode.OK,
                        (await client.PostAsync("/api/definitions", Json(definition))).StatusCode);
                }

                v4InstanceId = $"orion-v4-{Guid.NewGuid():N}";
                v5InstanceId = $"orion-v5-{Guid.NewGuid():N}";
                instanceId = $"orion-v6-{Guid.NewGuid():N}";
                await CreateInstance(client, v4InstanceId, "4.0", null);
                await CreateInstance(client, v5InstanceId, "5.0", null);
                await CreateInstance(client, instanceId, "6.0",
                    new { checksComplete = true, eligibilityDecision = "Eligible" });

                using var initial = await ReadJson(client, $"/api/instances/{instanceId}/concerns");
                Assert.Equal("Draft", initial.RootElement.GetProperty("parentState").GetString());
                Assert.Equal("Pending", ConcernState(initial.RootElement, "documentCompleteness"));
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "assetAvailability"));

                using var submitted = await Command(client, instanceId, "submit", "SubmitApplication", 0);
                Assert.Equal("Accepted", AttemptStatus(submitted.RootElement));
                Assert.Equal(1, submitted.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());

                using var blockedAssessment = await Command(
                    client, instanceId, "assessment-before-documents", "CompleteEligibilityAssessment", 1);
                Assert.Equal("Rejected", AttemptStatus(blockedAssessment.RootElement));
                Assert.Equal("guards-disabled",
                    blockedAssessment.RootElement.GetProperty("attempt").GetProperty("reasonCode").GetString());

                await RegisterOperation(client, instanceId, "documents-first",
                    "DocumentsVerified", "document-service", 1);
                var documentObservation = new
                {
                    messageId = "documents-first",
                    type = "DocumentsVerified",
                    expectedRevision = 1,
                    correlationId = "documents-first",
                    source = "document-service",
                    evidence = new { documentSetDigest = "sha256:document-set-v1" }
                };
                using var documentsComplete = await PostJson(
                    client, $"/api/instances/{instanceId}/observations", documentObservation);
                Assert.Equal("Accepted", AttemptStatus(documentsComplete.RootElement));
                Assert.Equal(JsonValueKind.Null,
                    documentsComplete.RootElement.GetProperty("transition").ValueKind);
                Assert.Equal("Complete", await GetConcernState(client, instanceId, "documentCompleteness"));
                using var duplicateDocument = await PostJson(
                    client, $"/api/instances/{instanceId}/observations", documentObservation);
                Assert.Equal("Duplicate", duplicateDocument.RootElement.GetProperty("message").GetProperty("status").GetString());
                var documentHistory = await client.GetFromJsonAsync<JsonElement[]>(
                    $"/api/instances/{instanceId}/concerns/history");
                Assert.NotNull(documentHistory);
                Assert.Single(documentHistory);

                using var assessment = await Command(client, instanceId, "assessment", "CompleteEligibilityAssessment", 1);
                Assert.Equal("Accepted", AttemptStatus(assessment.RootElement));
                Assert.Equal("Approved", assessment.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
                using var reservationRequested = await Command(
                    client, instanceId, "reservation-request", "RequestAssetReservation", 2);
                Assert.Equal("Accepted", AttemptStatus(reservationRequested.RootElement));
                Assert.Equal("Pending", await GetConcernState(client, instanceId, "assetAvailability"));

                await RegisterOperation(client, instanceId, "offer-document",
                    "OfferDocumentGenerated", "offer-document-service", 2);
                using var offerGenerated = await PostJson(client, $"/api/instances/{instanceId}/observations", new
                {
                    messageId = "offer-document-generated",
                    type = "OfferDocumentGenerated",
                    expectedRevision = 2,
                    correlationId = "offer-document",
                    source = "offer-document-service",
                    evidence = new { documentId = "offer-1", offerDigest = "sha256:offer-v1" }
                });
                Assert.Equal("Accepted", AttemptStatus(offerGenerated.RootElement));
                Assert.Equal("OfferReady",
                    offerGenerated.RootElement.GetProperty("instance").GetProperty("currentState").GetString());

                using var blockedRelease = await Command(client, instanceId, "release-too-early", "ReleaseOffer", 3);
                Assert.Equal("Rejected", AttemptStatus(blockedRelease.RootElement));
                var releaseFindings = blockedRelease.RootElement.GetProperty("attempt").GetProperty("guardFindings");
                Assert.Contains(releaseFindings.EnumerateArray(), finding =>
                    finding.GetProperty("status").GetString() == "Disabled" &&
                    finding.GetProperty("explanation").GetString()!.Contains("assetAvailability", StringComparison.Ordinal));

                await RegisterOperation(client, instanceId, "asset-reservation",
                    "EquipmentReserved", "asset-allocation-service", 3);
                using var assetReserved = await PostJson(client, $"/api/instances/{instanceId}/observations", new
                {
                    messageId = "asset-reserved",
                    type = "EquipmentReserved",
                    expectedRevision = 3,
                    correlationId = "asset-reservation",
                    source = "asset-allocation-service",
                    evidence = new { reservationId = "reservation-1", assetId = "asset-1" }
                });
                Assert.Equal("Accepted", AttemptStatus(assetReserved.RootElement));
                Assert.Equal(3, assetReserved.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
                Assert.Equal("Reserved", await GetConcernState(client, instanceId, "assetAvailability"));

                using var released = await Command(client, instanceId, "release-offer", "ReleaseOffer", 3);
                Assert.Equal("Accepted", AttemptStatus(released.RootElement));
                Assert.Equal("AwaitingOfferAcceptance",
                    released.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
                using var concernReplay = await ReadJson(client, $"/api/instances/{instanceId}/concerns/replay");
                Assert.True(
                    concernReplay.RootElement.GetProperty("outcome").GetString() == "Consistent",
                    concernReplay.RootElement.GetRawText());
                Assert.Equal("Complete", ReplayState(concernReplay.RootElement, "documentCompleteness"));
                Assert.Equal("Reserved", ReplayState(concernReplay.RootElement, "assetAvailability"));
                using var parentReplay = await ReadJson(client, $"/api/instances/{instanceId}/replay");
                Assert.True(
                    parentReplay.RootElement.GetProperty("stateReconstructionOutcome").GetString() == "Consistent",
                    parentReplay.RootElement.GetRawText());
                using var decisionReplay = await ReadJson(
                    client, $"/api/instances/{instanceId}/replay/decision-verification");
                Assert.Equal("Verified",
                    decisionReplay.RootElement.GetProperty("decisionVerificationOutcome").GetString());

                using var openApi = await ReadJson(client, "/openapi/v1.json");
                Assert.Contains("/api/instances/{id}/concerns", openApi.RootElement.GetRawText(), StringComparison.Ordinal);
                Assert.Contains("/api/instances/{id}/concerns/replay", openApi.RootElement.GetRawText(), StringComparison.Ordinal);
            }

            using (var restartedFactory = new WebApplicationFactory<Program>())
            using (var restartedClient = restartedFactory.CreateClient())
            {
                using var afterRestart = await ReadJson(
                    restartedClient, $"/api/instances/{instanceId}/concerns/replay");
                Assert.True(
                    afterRestart.RootElement.GetProperty("outcome").GetString() == "Consistent",
                    afterRestart.RootElement.GetRawText());
                using var oldV4Replay = await ReadJson(restartedClient, $"/api/instances/{v4InstanceId}/replay");
                using var oldV5Replay = await ReadJson(restartedClient, $"/api/instances/{v5InstanceId}/replay");
                Assert.Equal("4.0", oldV4Replay.RootElement.GetProperty("definitionVersion").GetString());
                Assert.Equal("5.0", oldV5Replay.RootElement.GetProperty("definitionVersion").GetString());
                Assert.Equal("Consistent", oldV4Replay.RootElement.GetProperty("stateReconstructionOutcome").GetString());
                Assert.Equal("Consistent", oldV5Replay.RootElement.GetProperty("stateReconstructionOutcome").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", previousProviderDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", previousKey);
            SqliteConnection.ClearAllPools();
            DeleteDatabase(database);
            if (Directory.Exists(providerDirectory))
            {
                Directory.Delete(providerDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cancellation_terminates_local_concerns_and_refuses_a_late_observation()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate12-cancel-{Guid.NewGuid():N}.db");
        var providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-gate12-cancel-providers-{Guid.NewGuid():N}");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", providerDirectory);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            AddTrustedKey(client);
            await RegisterV6(client);
            var instanceId = $"cancel-{Guid.NewGuid():N}";
            await CreateInstance(client, instanceId, "6.0", null);
            await RegisterOperation(client, instanceId, "pending-doc-check",
                "DocumentsVerified", "document-service", 0);

            using var cancellation = await Command(client, instanceId, "cancel", "CancelApplication", 0);
            Assert.Equal("Accepted", AttemptStatus(cancellation.RootElement));
            using var states = await ReadJson(client, $"/api/instances/{instanceId}/concerns");
            Assert.Equal("Cancelled", states.RootElement.GetProperty("parentState").GetString());
            Assert.Equal("Cancelled", ConcernState(states.RootElement, "documentCompleteness"));
            Assert.Equal("Cancelled", ConcernState(states.RootElement, "assetAvailability"));
            using var history = await ReadJson(client, $"/api/instances/{instanceId}/concerns/history");
            Assert.Equal(2, history.RootElement.GetArrayLength());
            Assert.All(history.RootElement.EnumerateArray(), entry =>
                Assert.Equal(cancellation.RootElement.GetProperty("attempt").GetProperty("id").GetString(),
                    entry.GetProperty("attemptId").GetString()));

            using var lateObservation = await PostJson(client, $"/api/instances/{instanceId}/observations", new
            {
                messageId = "late-documents",
                type = "DocumentsVerified",
                expectedRevision = 0,
                correlationId = "pending-doc-check",
                source = "document-service",
                evidence = new { documentSetDigest = "sha256:late-documents" }
            });
            Assert.Equal("Stale", lateObservation.RootElement.GetProperty("message").GetProperty("status").GetString());
            Assert.Equal("terminal-instance",
                lateObservation.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());
            using var replay = await ReadJson(client, $"/api/instances/{instanceId}/concerns/replay");
            Assert.Equal("Consistent", replay.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", previousProviderDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", previousKey);
            SqliteConnection.ClearAllPools();
            DeleteDatabase(database);
            if (Directory.Exists(providerDirectory))
            {
                Directory.Delete(providerDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Two_hosts_arbitrate_simultaneous_inputs_to_different_concerns_and_cancellation()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate12-race-{Guid.NewGuid():N}.db");
        var providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-gate12-race-providers-{Guid.NewGuid():N}");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", providerDirectory);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
        try
        {
            using var firstFactory = new WebApplicationFactory<Program>();
            using var secondFactory = new WebApplicationFactory<Program>();
            using var first = firstFactory.CreateClient();
            using var second = secondFactory.CreateClient();
            AddTrustedKey(first);
            AddTrustedKey(second);
            await RegisterV6(first);

            var coordinatedId = $"concerns-race-{Guid.NewGuid():N}";
            await CreateInstance(first, coordinatedId, "6.0",
                new { checksComplete = true, eligibilityDecision = "Eligible" });
            using var submitted = await Command(first, coordinatedId, "submit", "SubmitApplication", 0);
            await RegisterOperation(first, coordinatedId, "initial-documents",
                "DocumentsVerified", "document-service", 1);
            using var documentsComplete = await PostJson(first, $"/api/instances/{coordinatedId}/observations", new
            {
                messageId = "initial-documents",
                type = "DocumentsVerified",
                expectedRevision = 1,
                correlationId = "initial-documents",
                source = "document-service",
                evidence = new { documentSetDigest = "sha256:initial" }
            });
            using var approved = await Command(first, coordinatedId, "assess", "CompleteEligibilityAssessment", 1);
            using var requested = await Command(first, coordinatedId, "reserve", "RequestAssetReservation", 2);
            Assert.Equal("Accepted", AttemptStatus(approved.RootElement));
            Assert.Equal("Accepted", AttemptStatus(requested.RootElement));
            await RegisterOperation(first, coordinatedId, "invalidate-documents",
                "DocumentsInvalidated", "document-service", 2);
            await RegisterOperation(first, coordinatedId, "confirm-asset",
                "EquipmentReserved", "asset-allocation-service", 2);

            var documentTask = PostJson(first, $"/api/instances/{coordinatedId}/observations", new
            {
                messageId = "invalidate-documents",
                type = "DocumentsInvalidated",
                expectedRevision = 2,
                correlationId = "invalidate-documents",
                source = "document-service",
                evidence = new { verificationDigest = "sha256:invalid", invalidReason = "replacement-required" }
            });
            var assetTask = PostJson(second, $"/api/instances/{coordinatedId}/observations", new
            {
                messageId = "confirm-asset",
                type = "EquipmentReserved",
                expectedRevision = 2,
                correlationId = "confirm-asset",
                source = "asset-allocation-service",
                evidence = new { reservationId = "reservation-race", assetId = "asset-race" }
            });
            using var documentResult = await documentTask;
            using var assetResult = await assetTask;
            Assert.Equal("Accepted", AttemptStatus(documentResult.RootElement));
            Assert.Equal("Accepted", AttemptStatus(assetResult.RootElement));
            using var vector = await ReadJson(first, $"/api/instances/{coordinatedId}/concerns");
            Assert.Equal("Invalid", ConcernState(vector.RootElement, "documentCompleteness"));
            Assert.Equal("Reserved", ConcernState(vector.RootElement, "assetAvailability"));
            Assert.Equal(2, vector.RootElement.GetProperty("parentRevision").GetInt64());
            using var replay = await ReadJson(first, $"/api/instances/{coordinatedId}/concerns/replay");
            Assert.Equal("Consistent", replay.RootElement.GetProperty("outcome").GetString());

            var cancellationId = $"cancel-race-{Guid.NewGuid():N}";
            await CreateInstance(first, cancellationId, "6.0", null);
            await RegisterOperation(first, cancellationId, "cancel-race-documents",
                "DocumentsVerified", "document-service", 0);
            var cancellationTask = Command(first, cancellationId, "cancel-race", "CancelApplication", 0);
            var observationTask = PostJson(second, $"/api/instances/{cancellationId}/observations", new
            {
                messageId = "cancel-race-documents",
                type = "DocumentsVerified",
                expectedRevision = 0,
                correlationId = "cancel-race-documents",
                source = "document-service",
                evidence = new { documentSetDigest = "sha256:cancel-race" }
            });
            using var cancellationResult = await cancellationTask;
            using var observationResult = await observationTask;
            Assert.Equal("Accepted", AttemptStatus(cancellationResult.RootElement));
            var observationStatus = observationResult.RootElement.GetProperty("message").GetProperty("status").GetString();
            Assert.Contains(observationStatus, new[] { "Accepted", "Stale" });
            using var cancelledStates = await ReadJson(first, $"/api/instances/{cancellationId}/concerns");
            Assert.Equal("Cancelled", cancelledStates.RootElement.GetProperty("parentState").GetString());
            Assert.Equal("Cancelled", ConcernState(cancelledStates.RootElement, "documentCompleteness"));
            using var cancellationReplay = await ReadJson(first, $"/api/instances/{cancellationId}/concerns/replay");
            Assert.Equal("Consistent", cancellationReplay.RootElement.GetProperty("outcome").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", previousProviderDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", previousKey);
            SqliteConnection.ClearAllPools();
            DeleteDatabase(database);
            if (Directory.Exists(providerDirectory))
            {
                Directory.Delete(providerDirectory, recursive: true);
            }
        }
    }

    private static async Task RegisterV6(HttpClient client)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v6.json"));
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("/api/definitions", Json(definition))).StatusCode);
    }

    private static async Task CreateInstance(HttpClient client, string id, string version, object? facts)
    {
        var response = await client.PostAsJsonAsync("/api/instances", new
        {
            id,
            definitionId = "orion-equipment-lease",
            definitionVersion = version,
            facts
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task RegisterOperation(
        HttpClient client,
        string instanceId,
        string operationId,
        string observationType,
        string sourceIdentity,
        long revision)
    {
        var response = await client.PostAsJsonAsync($"/api/instances/{instanceId}/expected-operations", new
        {
            operationId,
            observationType,
            sourceIdentity,
            expectedRevision = revision
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static Task<JsonDocument> Command(
        HttpClient client, string instanceId, string messageId, string type, long revision) =>
        PostJson(client, $"/api/instances/{instanceId}/commands", new
        {
            messageId,
            type,
            expectedRevision = revision
        });

    private static async Task<JsonDocument> PostJson(HttpClient client, string route, object request)
    {
        var response = await client.PostAsJsonAsync(route, request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> ReadJson(HttpClient client, string route)
    {
        var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> GetConcernState(HttpClient client, string instanceId, string concernId)
    {
        using var response = await ReadJson(client, $"/api/instances/{instanceId}/concerns");
        return ConcernState(response.RootElement, concernId);
    }

    private static string? ConcernState(JsonElement inspection, string concernId) =>
        inspection.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("currentState").GetString();

    private static string? ReplayState(JsonElement replay, string concernId) =>
        replay.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("reconstructedState").GetString();

    private static string? AttemptStatus(JsonElement response) =>
        response.GetProperty("attempt").GetProperty("status").GetString();

    private static void AddTrustedKey(HttpClient client) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-State-Machine-Trusted-Ingestion-Key", TrustedKey);

    private static StringContent Json(string value) =>
        new(value, System.Text.Encoding.UTF8, "application/json");

    private static void DeleteDatabase(string database)
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = database + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
