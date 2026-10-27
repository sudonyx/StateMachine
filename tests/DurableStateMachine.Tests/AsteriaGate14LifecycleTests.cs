extern alias AsteriaSyntheticProvider;

using DurableStateMachine.Core;
using DurableStateMachine.AsteriaClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DurableStateMachine.Tests;

public sealed class AsteriaGate14LifecycleTests
{
    [Theory]
    [InlineData("functional", "FunctionalVerification", "verifyFunctionalRelease")]
    [InlineData("security", "SecurityVerification", "verifySecurityRelease")]
    public async Task Synthetic_verification_providers_publish_and_return_concern_bound_evidence(
        string mode,
        string concernId,
        string operationId)
    {
        using var factory = new SyntheticProviderFactory(mode, Path.Combine(
            Path.GetTempPath(), $"asteria-verification-{Guid.NewGuid():N}.db"));
        using var client = factory.CreateClient();
        using var openApi = await client.GetFromJsonAsync<JsonDocument>("/openapi/v1.json");
        Assert.NotNull(openApi);
        var operation = openApi.RootElement.GetProperty("paths")
            .GetProperty("/verification").GetProperty("post");
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());

        var request = new
        {
            releaseId = "AST-SYNTHETIC-" + mode,
            assessmentGeneration = 3,
            materialDigest = "sha256:" + new string('b', 64),
            workflowInstanceId = Guid.NewGuid().ToString(),
            concernId,
            activityId = Guid.NewGuid().ToString(),
            expectedOperationId = Guid.NewGuid().ToString()
        };
        using var response = await client.PostAsJsonAsync("/verification", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(result);
        Assert.Equal(request.releaseId, result.RootElement.GetProperty("releaseId").GetString());
        Assert.Equal(request.assessmentGeneration, result.RootElement.GetProperty("assessmentGeneration").GetInt32());
        Assert.Equal(request.materialDigest, result.RootElement.GetProperty("materialDigest").GetString());
        Assert.Equal(concernId, result.RootElement.GetProperty("concernId").GetString());
        Assert.Equal("Passed", result.RootElement.GetProperty("result").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.RootElement.GetProperty("evidenceId").GetString()));

        using var invalid = await client.PostAsJsonAsync(
            "/verification", request with { concernId = concernId == "FunctionalVerification"
                ? "SecurityVerification"
                : "FunctionalVerification" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Synthetic_deployment_and_health_providers_follow_contracts_and_persist_idempotency()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asteria-provider-{Guid.NewGuid():N}.db");
        string? deploymentId = null;
        try
        {
            using (var deploymentFactory = new SyntheticProviderFactory("deployment", path))
            using (var deploymentHttp = deploymentFactory.CreateClient())
            {
                using var openApi = await deploymentHttp.GetFromJsonAsync<JsonDocument>("/openapi/v1.json");
                Assert.NotNull(openApi);
                var operations = openApi.RootElement.GetProperty("paths");
                Assert.True(operations.TryGetProperty("/deployments", out var deployments));
                Assert.True(deployments.TryGetProperty("post", out var post));
                Assert.Equal("requestDeployment", post.GetProperty("operationId").GetString());
                Assert.True(operations.TryGetProperty("/deployments/{deploymentId}", out var byId));
                Assert.Equal("getDeploymentStatus", byId.GetProperty("get").GetProperty("operationId").GetString());
                Assert.True(operations.TryGetProperty("/deployments/by-idempotency-key/{idempotencyKey}", out var byKey));
                Assert.Equal(
                    "lookupDeploymentByIdempotencyKey",
                    byKey.GetProperty("get").GetProperty("operationId").GetString());

                var request = new
                {
                    releaseId = "AST-SYNTHETIC-001",
                    assessmentGeneration = 1,
                    materialDigest = "sha256:" + new string('a', 64),
                    product = "AsteriaPortal",
                    targetVersion = "4.2.0",
                    environment = "staging",
                    idempotencyKey = "ast-synthetic-idempotency-001"
                };
                using var acceptedResponse = await deploymentHttp.PostAsJsonAsync("/deployments", request);
                Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
                var accepted = await acceptedResponse.Content.ReadFromJsonAsync<JsonDocument>();
                Assert.NotNull(accepted);
                deploymentId = accepted.RootElement.GetProperty("deploymentId").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(deploymentId));
                Assert.Equal("Pending", accepted.RootElement.GetProperty("status").GetString());

                using var replayResponse = await deploymentHttp.PostAsJsonAsync("/deployments", request);
                Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
                using var conflictingResponse = await deploymentHttp.PostAsJsonAsync(
                    "/deployments", request with { targetVersion = "4.2.1" });
                Assert.Equal(HttpStatusCode.Conflict, conflictingResponse.StatusCode);

                using var status = await WaitForSyntheticDeploymentAsync(deploymentHttp, deploymentId);
                Assert.Equal("Succeeded", status.RootElement.GetProperty("status").GetString());
                Assert.False(string.IsNullOrWhiteSpace(status.RootElement.GetProperty("evidenceId").GetString()));
            }

            using var healthFactory = new SyntheticProviderFactory("health", path);
            using var healthHttp = healthFactory.CreateClient();
            using var healthOpenApi = await healthHttp.GetFromJsonAsync<JsonDocument>("/openapi/v1.json");
            Assert.NotNull(healthOpenApi);
            var healthOperation = healthOpenApi.RootElement.GetProperty("paths")
                .GetProperty("/health-verifications").GetProperty("get");
            Assert.Equal(
                "verifyDeployedApplicationHealth",
                healthOperation.GetProperty("operationId").GetString());
            var query = "?deploymentId=" + Uri.EscapeDataString(deploymentId!) +
                "&targetVersion=4.2.0&environment=staging";
            using var healthResponse = await healthHttp.GetAsync("/health-verifications" + query);
            Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
            using var health = await healthResponse.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.NotNull(health);
            Assert.Equal("Passed", health.RootElement.GetProperty("result").GetString());
            using var mismatch = await healthHttp.GetAsync(
                "/health-verifications?deploymentId=" + Uri.EscapeDataString(deploymentId) +
                "&targetVersion=4.2.1&environment=staging");
            using var mismatchResult = await mismatch.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.NotNull(mismatchResult);
            Assert.Equal("Failed", mismatchResult.RootElement.GetProperty("result").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }

    [Fact]
    public void V5_definition_loads_with_explicit_deployment_health_and_completion_guards()
    {
        var definition = TestSupport.LoadWorkflow("asteria-release-governance-v5.json");

        Assert.Equal("asteria-release-governance", definition.Id);
        Assert.Equal("5.0", definition.Version);
        Assert.Contains("DeploymentInProgress", definition.States.Keys);
        Assert.Contains("PostDeploymentVerification", definition.States.Keys);
        Assert.True(definition.States["Completed"].IsTerminal);
        Assert.Contains("Deployment", definition.Concerns.Keys);
        Assert.Contains("HealthVerification", definition.Concerns.Keys);
        Assert.Contains(definition.Transitions, transition =>
            transition.Id == "deployment-start-from-ready" &&
            transition.Source == "ReadyForDeployment" &&
            transition.Target == "DeploymentInProgress");
        Assert.Contains(definition.Transitions, transition =>
            transition.Id == "deployment-succeeded" &&
            transition.Source == "DeploymentInProgress" &&
            transition.Target == "PostDeploymentVerification");
        Assert.Contains(definition.Transitions, transition =>
            transition.Id == "health-passed-completes-release" &&
            transition.Source == "PostDeploymentVerification" &&
            transition.Target == "Completed");
    }

    [Fact]
    public async Task Deadline_identity_is_durable_and_conflicting_replay_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"asteria-deadline-{Guid.NewGuid():N}.db");
        try
        {
            await using var registry = new AsteriaReleaseRegistry(path);
            var release = await registry.CreateReleaseAsync(
                "AST-G14-DEADLINE-001",
                new AsteriaReleaseMaterial(
                    "AsteriaPortal",
                    "4.2.0",
                    "production",
                    "Standard",
                    "Medium",
                    false,
                    true,
                    "Controlled deadline test",
                    "release-owner@example.invalid",
                    new DateTimeOffset(2027, 3, 15, 10, 0, 0, TimeSpan.Zero)),
                DateTimeOffset.Parse("2026-10-26T16:00:00Z"),
                "asteria-release-governance",
                "5.0");
            await using var ledger = new AsteriaDeadlineLedger(path);
            var reservation = new AsteriaDeadlineReservation(
                "ast-g14-deadline-verification-001",
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                release.WorkflowInstanceId,
                "VerificationEscalation",
                new DateTimeOffset(2027, 3, 13, 10, 0, 0, TimeSpan.Zero),
                release.Work.WorkId);
            var initial = await ledger.EnsureAsync(
                reservation, DateTimeOffset.Parse("2026-10-26T16:00:00Z"));
            var replay = await ledger.EnsureAsync(
                reservation, DateTimeOffset.Parse("2026-10-27T16:00:00Z"));

            Assert.Equal("Pending", initial.Status);
            Assert.Equal(initial.DeadlineId, replay.DeadlineId);
            Assert.Equal(initial.DueAt, replay.DueAt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.EnsureAsync(
                reservation with { DueAt = reservation.DueAt.AddMinutes(1) },
                DateTimeOffset.Parse("2026-10-27T16:00:00Z")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<JsonDocument> WaitForSyntheticDeploymentAsync(
        HttpClient client,
        string deploymentId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JsonDocument>(
                "/deployments/" + Uri.EscapeDataString(deploymentId));
            Assert.NotNull(status);
            if (status.RootElement.GetProperty("status").GetString() == "Succeeded")
            {
                return status;
            }
            status.Dispose();
            await Task.Delay(50);
        }

        throw new TimeoutException("Synthetic deployment did not reach Succeeded within five seconds.");
    }

    private sealed class SyntheticProviderFactory(string mode, string databasePath)
        : WebApplicationFactory<AsteriaSyntheticProvider::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ASTERIA_SYNTHETIC_PROVIDER_MODE", mode);
            builder.UseSetting("ASTERIA_SYNTHETIC_DEPLOYMENT_DB", databasePath);
            builder.UseSetting("ASTERIA_SYNTHETIC_DEPLOYMENT_DELAY_MS", "10");
        }
    }
}
