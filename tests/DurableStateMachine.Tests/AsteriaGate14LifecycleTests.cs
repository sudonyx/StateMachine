using DurableStateMachine.Core;
using DurableStateMachine.AsteriaClient;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

public sealed class AsteriaGate14LifecycleTests
{
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
}
