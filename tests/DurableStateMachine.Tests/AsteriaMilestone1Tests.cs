using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class AsteriaMilestone1Tests
{
    [Fact]
    public async Task Registered_definition_supports_local_progress_concern_history_and_safe_cancellation()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-asteria-m1-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            var definition = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "definitions", "asteria-release-governance-v1.json"));

            var registration = await client.PostAsync("/api/definitions", Json(definition));
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);

            var invalidDefinition = definition.Replace(
                "\"languageVersion\": \"1.3\"",
                "\"languageVersion\": \"1.4\"",
                StringComparison.Ordinal);
            var invalidRegistration = await client.PostAsync("/api/definitions", Json(invalidDefinition));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidRegistration.StatusCode);

            var incompleteId = $"asteria-incomplete-{Guid.NewGuid():N}";
            await CreateInstance(client, incompleteId, new Dictionary<string, object>
            {
                ["releaseId"] = "AST-TEST-INCOMPLETE"
            });
            using (var incompleteSubmission = await Command(
                client, incompleteId, "submit-incomplete", "SubmitRelease", 0))
            {
                Assert.Equal("Unresolved", AttemptStatus(incompleteSubmission.RootElement));
                Assert.Equal("unresolved-guard", ReasonCode(incompleteSubmission.RootElement));
                Assert.Equal("Draft", CurrentState(incompleteSubmission.RootElement));
            }

            var instanceId = $"asteria-release-{Guid.NewGuid():N}";
            await CreateInstance(client, instanceId, CompleteDraftFacts());
            using (var initial = await ReadJson(client, $"/api/instances/{instanceId}/concerns"))
            {
                Assert.Equal("Draft", initial.RootElement.GetProperty("parentState").GetString());
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "FunctionalVerification"));
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "SecurityVerification"));
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "ReleaseReviewApproval"));
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "EmergencyApproval"));
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "DeploymentExecution"));
                Assert.Equal("NotRequested", ConcernState(initial.RootElement, "CancellationIntent"));
            }

            using (var submitted = await Command(client, instanceId, "submit", "SubmitRelease", 0))
            {
                Assert.Equal("Accepted", AttemptStatus(submitted.RootElement));
                Assert.Equal("UnderAssessment", CurrentState(submitted.RootElement));
            }

            var parentHistory = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{instanceId}/history");
            Assert.NotNull(parentHistory);
            Assert.Single(parentHistory);
            using (var parentReplay = await ReadJson(client, $"/api/instances/{instanceId}/replay"))
            {
                Assert.Equal("Consistent", parentReplay.RootElement.GetProperty("outcome").GetString());
            }

            using (var blockedDeployment = await Command(
                client, instanceId, "prepare-deployment-too-early", "PrepareDeploymentRequest", 1))
            {
                Assert.Equal("Rejected", AttemptStatus(blockedDeployment.RootElement));
                Assert.Equal("guards-disabled", ReasonCode(blockedDeployment.RootElement));
            }

            using (var functional = await Command(
                client, instanceId, "functional-request", "RequestFunctionalVerification", 1))
            {
                Assert.Equal("Accepted", AttemptStatus(functional.RootElement));
            }

            using (var duplicateFunctional = await Command(
                client, instanceId, "functional-request", "RequestFunctionalVerification", 1))
            {
                Assert.Equal("Duplicate", MessageStatus(duplicateFunctional.RootElement));
            }

            using (var security = await Command(
                client, instanceId, "security-request", "RequestSecurityVerification", 1))
            {
                Assert.Equal("Accepted", AttemptStatus(security.RootElement));
            }

            using (var releaseReview = await Command(
                client, instanceId, "release-review-request", "RequestReleaseReview", 1))
            {
                Assert.Equal("Accepted", AttemptStatus(releaseReview.RootElement));
            }

            using (var concerns = await ReadJson(client, $"/api/instances/{instanceId}/concerns"))
            {
                Assert.Equal(1, concerns.RootElement.GetProperty("parentRevision").GetInt64());
                Assert.Equal("Requested", ConcernState(concerns.RootElement, "FunctionalVerification"));
                Assert.Equal("Requested", ConcernState(concerns.RootElement, "SecurityVerification"));
                Assert.Equal("Requested", ConcernState(concerns.RootElement, "ReleaseReviewApproval"));
                Assert.Equal("NotRequested", ConcernState(concerns.RootElement, "EmergencyApproval"));
                Assert.Equal("NotRequested", ConcernState(concerns.RootElement, "DeploymentExecution"));
            }

            var concernHistory = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{instanceId}/concerns/history");
            Assert.NotNull(concernHistory);
            Assert.Equal(3, concernHistory.Length);

            using (var replay = await ReadJson(client, $"/api/instances/{instanceId}/concerns/replay"))
            {
                Assert.Equal("Consistent", replay.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("Requested", ReplayedConcernState(replay.RootElement, "FunctionalVerification"));
                Assert.Equal("Requested", ReplayedConcernState(replay.RootElement, "SecurityVerification"));
                Assert.Equal("Requested", ReplayedConcernState(replay.RootElement, "ReleaseReviewApproval"));
                Assert.Equal("NotRequested", ReplayedConcernState(replay.RootElement, "EmergencyApproval"));
            }

            var cancellationId = $"asteria-cancel-{Guid.NewGuid():N}";
            await CreateInstance(client, cancellationId, new Dictionary<string, object>());
            using (var blockedCancellation = await Command(
                client, cancellationId, "cancel-before-intent", "CancelRelease", 0))
            {
                Assert.Equal("Rejected", AttemptStatus(blockedCancellation.RootElement));
                Assert.Equal("guards-disabled", ReasonCode(blockedCancellation.RootElement));
            }

            using (var cancellationIntent = await Command(
                client, cancellationId, "record-cancellation", "RequestCancellation", 0))
            {
                Assert.Equal("Accepted", AttemptStatus(cancellationIntent.RootElement));
            }

            using (var cancelled = await Command(
                client, cancellationId, "cancel-with-intent", "CancelRelease", 0))
            {
                Assert.Equal("Accepted", AttemptStatus(cancelled.RootElement));
                Assert.Equal("Cancelled", CurrentState(cancelled.RootElement));
            }

            using (var cancelledConcerns = await ReadJson(client, $"/api/instances/{cancellationId}/concerns"))
            {
                Assert.Equal("Requested", ConcernState(cancelledConcerns.RootElement, "CancellationIntent"));
                Assert.Equal("NotRequested", ConcernState(cancelledConcerns.RootElement, "DeploymentExecution"));
            }

            using (var lateConcernCommand = await Command(
                client, cancellationId, "late-functional-request", "RequestFunctionalVerification", 1))
            {
                Assert.Equal("Rejected", MessageStatus(lateConcernCommand.RootElement));
                Assert.Equal("Cancelled", CurrentState(lateConcernCommand.RootElement));
            }

            using (var cancellationReplay = await ReadJson(
                client, $"/api/instances/{cancellationId}/concerns/replay"))
            {
                Assert.Equal("Consistent", cancellationReplay.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("Requested", ReplayedConcernState(cancellationReplay.RootElement, "CancellationIntent"));
                Assert.Equal("NotRequested", ReplayedConcernState(cancellationReplay.RootElement, "DeploymentExecution"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            SqliteConnection.ClearAllPools();
            DeleteDatabase(database);
        }
    }

    private static Dictionary<string, object> CompleteDraftFacts() => new()
    {
        ["releaseId"] = "AST-TEST-001",
        ["assessmentGeneration"] = 1,
        ["materialDigest"] = "sha256:synthetic-fixture",
        ["product"] = "AsteriaPortal",
        ["targetVersion"] = "3.0.0",
        ["environment"] = "staging",
        ["releaseCategory"] = "Standard",
        ["changeSize"] = "Small",
        ["authenticationChange"] = false,
        ["customerDataImpact"] = false,
        ["releaseSummary"] = "Synthetic local test fixture",
        ["plannedDeploymentAt"] = "2027-02-15T14:00:00Z",
        ["releaseOwner"] = "synthetic-owner"
    };

    private static async Task CreateInstance(HttpClient client, string id, Dictionary<string, object> facts)
    {
        var response = await client.PostAsJsonAsync("/api/instances", new
        {
            id,
            definitionId = "asteria-release-governance",
            definitionVersion = "1.0",
            facts
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

    private static string? AttemptStatus(JsonElement response) =>
        response.GetProperty("attempt").GetProperty("status").GetString();

    private static string? MessageStatus(JsonElement response) =>
        response.GetProperty("message").GetProperty("status").GetString();

    private static string? ReasonCode(JsonElement response) =>
        response.GetProperty("attempt").GetProperty("reasonCode").GetString();

    private static string? CurrentState(JsonElement response) =>
        response.GetProperty("instance").GetProperty("currentState").GetString();

    private static string? ConcernState(JsonElement inspection, string concernId) =>
        inspection.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("currentState").GetString();

    private static string? ReplayedConcernState(JsonElement replay, string concernId) =>
        replay.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("reconstructedState").GetString();

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
