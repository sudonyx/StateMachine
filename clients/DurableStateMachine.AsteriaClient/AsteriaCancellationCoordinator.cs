using System.Security.Cryptography;
using System.Text;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaCancellationResult(
    string Disposition,
    string ParentState,
    string CancellationIntentState);

public sealed class AsteriaCancellationCoordinator(
    AsteriaReleaseRegistry releaseRegistry,
    AsteriaActivityLedger activityLedger,
    AsteriaStateMachineClient stateMachine)
{
    public async Task<AsteriaCancellationResult> CancelAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var release = await releaseRegistry.GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
        if (release.RegistryState != "Operational" ||
            release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.Generation != release.AssessmentGeneration ||
            release.Work.MaterialDigest != release.MaterialDigest ||
            release.Work.WorkflowInstanceId != release.WorkflowInstanceId)
        {
            throw new InvalidOperationException("Asteria cancellation requires the current accepted assessment generation.");
        }
        var instance = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance is missing.");
        var concerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue("CancellationIntent", out var cancellation))
        {
            throw new InvalidDataException("Asteria workflow has no cancellation-intent concern.");
        }
        if (instance.CurrentState == "Completed")
        {
            throw new InvalidOperationException("Asteria release is already Completed; cancellation is too late.");
        }

        var deploymentWorkId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0Deployment", "work");
        var deployment = await activityLedger.GetAsync(deploymentWorkId, cancellationToken);
        var dispatchMayHaveBegun =
            instance.CurrentState is "DeploymentInProgress" or "DeploymentFailed" or
                "DeploymentUnknown" or "PostDeploymentVerification" or "HealthFailed" or "HealthUnresolved" ||
            deployment?.Status is "DispatchPending" or "Dispatched" or "ResponsePersisted" or
                "ObservationSubmitted" or "Accepted" or "OutcomeUnknown";
        if (dispatchMayHaveBegun)
        {
            _ = await stateMachine.RequestCancellationIntentAsync(
                release,
                StableId($"{release.ReleaseId}\0{release.AssessmentGeneration}\0Cancellation", "intent"),
                cancellationToken);
            var current = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
                ?? throw new InvalidDataException("Asteria workflow disappeared after cancellation intent.");
            var currentConcerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
            return new AsteriaCancellationResult(
                "CancellationIntentRecorded; external deployment is not cancelled or rolled back.",
                current.CurrentState,
                currentConcerns["CancellationIntent"].CurrentState);
        }

        if (instance.CurrentState != "Cancelled")
        {
            _ = await stateMachine.RequestCancellationIntentAsync(
                release,
                StableId($"{release.ReleaseId}\0{release.AssessmentGeneration}\0Cancellation", "intent"),
                cancellationToken);
        }
        var cancelled = await stateMachine.CancelBeforeDispatchAsync(
            release,
            StableId($"{release.ReleaseId}\0{release.AssessmentGeneration}\0Cancellation", "before-dispatch"),
            cancellationToken);
        var cancelledConcerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        return new AsteriaCancellationResult(
            "CancelledBeforeExternalDispatch",
            cancelled.CurrentState,
            cancelledConcerns["CancellationIntent"].CurrentState);
    }

    private static string StableId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));
}
