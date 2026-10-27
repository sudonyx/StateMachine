using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.AsteriaClient;

public sealed class AsteriaAssessmentCoordinator(
    AsteriaReleaseRegistry registry,
    AsteriaStateMachineClient stateMachine,
    AsteriaDecisionApiClient decisionApi,
    string definitionJson,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public async Task<AsteriaReleaseSnapshot> CreateAndAssessAsync(
        string releaseId,
        AsteriaReleaseMaterial material,
        CancellationToken cancellationToken = default)
    {
        _ = await PrepareAsync(releaseId, material, cancellationToken);
        return await ResumeAsync(releaseId, cancellationToken);
    }

    public async Task<AsteriaReleaseSnapshot> PrepareAsync(
        string releaseId,
        AsteriaReleaseMaterial material,
        CancellationToken cancellationToken = default)
    {
        var (definitionId, definitionVersion) = ReadDefinitionIdentity(definitionJson);
        _ = await registry.CreateReleaseAsync(
            releaseId, material, _timeProvider.GetUtcNow(), definitionId, definitionVersion, cancellationToken);
        return await WithLeaseAsync(releaseId, async (leaseToken, phaseToken) =>
        {
            var release = await registry.GetReleaseAsync(releaseId, phaseToken)
                ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
            await AssertAdmissionAsync(release, leaseToken, phaseToken);
            if (release.Status == "Accepted")
            {
                return release;
            }
            await stateMachine.EnsureDefinitionAsync(definitionJson, phaseToken);
            await AssertAdmissionAsync(release, leaseToken, phaseToken);
            var instance = await stateMachine.EnsureInstanceAsync(release, phaseToken);
            if (release.Work.Status == "Registered")
            {
                await registry.AdvanceStatusAsync(
                    releaseId, "WorkflowCreated", _timeProvider.GetUtcNow(), phaseToken);
            }

            await AssertAdmissionAsync(release, leaseToken, phaseToken);
            instance = await stateMachine.EnsureSubmittedAsync(release, phaseToken);
            if (release.Work.ParentRevision is null)
            {
                await registry.RecordParentRevisionAsync(
                    releaseId, instance.Revision, _timeProvider.GetUtcNow(), phaseToken);
                await registry.UpdateRecoveryMetadataAsync(
                    releaseId, Metadata("submission-reconciled", instance.Revision), _timeProvider.GetUtcNow(), phaseToken);
            }
            else if (release.Work.ParentRevision.Value != instance.Revision)
            {
                throw new InvalidOperationException(
                    $"Asteria parent revision changed from the recorded assessment parent " +
                    $"{release.Work.ParentRevision.Value} to {instance.Revision}.");
            }

            return await ReloadAsync(releaseId, phaseToken);
        }, cancellationToken);
    }

    public async Task<AsteriaReleaseSnapshot> ResumeAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var release = await registry.GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");

        return await WithLeaseAsync(releaseId, async (leaseToken, phaseToken) =>
        {
            var currentRelease = await registry.GetReleaseAsync(releaseId, phaseToken)
                ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' disappeared.");
            await AssertAdmissionAsync(currentRelease, leaseToken, phaseToken);
            if (currentRelease.Status == "Accepted")
            {
                return currentRelease;
            }

            await stateMachine.EnsureDefinitionAsync(definitionJson, phaseToken);
            await AssertAdmissionAsync(currentRelease, leaseToken, phaseToken);
            var instance = await stateMachine.EnsureInstanceAsync(currentRelease, phaseToken);
            if (currentRelease.Work.Status == "Registered")
            {
                await registry.AdvanceStatusAsync(
                    releaseId, "WorkflowCreated", _timeProvider.GetUtcNow(), phaseToken);
                await registry.UpdateRecoveryMetadataAsync(
                    releaseId, Metadata("workflow-instance-inspected"), _timeProvider.GetUtcNow(), phaseToken);
                currentRelease = await ReloadAsync(releaseId, phaseToken);
            }

            var recoveryAfterObservationSubmission =
                currentRelease.Work.Status is "ObservationSubmitted" or "Accepted";
            if (!recoveryAfterObservationSubmission)
            {
                await AssertAdmissionAsync(currentRelease, leaseToken, phaseToken);
                instance = await stateMachine.EnsureSubmittedAsync(currentRelease, phaseToken);
                if (currentRelease.Work.ParentRevision is null)
                {
                    await registry.RecordParentRevisionAsync(
                        releaseId, instance.Revision, _timeProvider.GetUtcNow(), phaseToken);
                    await registry.UpdateRecoveryMetadataAsync(
                        releaseId, Metadata("submission-reconciled", instance.Revision), _timeProvider.GetUtcNow(), phaseToken);
                    currentRelease = await ReloadAsync(releaseId, phaseToken);
                }
                else if (currentRelease.Work.ParentRevision.Value != instance.Revision)
                {
                    throw new InvalidOperationException(
                        $"Asteria parent revision changed from the recorded assessment parent " +
                        $"{currentRelease.Work.ParentRevision.Value} to {instance.Revision}.");
                }

                await AssertAdmissionAsync(currentRelease, leaseToken, phaseToken);
                _ = await stateMachine.EnsureExpectedOperationAsync(
                    currentRelease, currentRelease.Work.ParentRevision!.Value, phaseToken);
                await registry.AdvanceStatusAsync(
                    releaseId, "OperationRegistered", _timeProvider.GetUtcNow(), phaseToken);
                await registry.UpdateRecoveryMetadataAsync(
                    releaseId, Metadata("expected-operation-inspected", currentRelease.Work.ParentRevision), _timeProvider.GetUtcNow(), phaseToken);
                currentRelease = await ReloadAsync(releaseId, phaseToken);

                var (requestJson, fingerprint) = AsteriaDecisionApiClient.CreateCanonicalRequest(currentRelease);
                await registry.RecordDecisionRequestAsync(
                    releaseId, fingerprint, requestJson, _timeProvider.GetUtcNow(), phaseToken);
                currentRelease = await ReloadAsync(releaseId, phaseToken);

                if (currentRelease.Work.OriginalResponseJson is null)
                {
                    await AssertAdmissionAsync(currentRelease, leaseToken, phaseToken);
                    var originalResponse = await decisionApi.AssessAsync(
                        currentRelease.Work.DecisionIdempotencyKey,
                        currentRelease.Work.DecisionRequestJson
                            ?? throw new InvalidDataException("The Decision API request was not persisted."),
                        phaseToken);
                    var validated = AsteriaDecisionApiClient.ValidateResponse(originalResponse, currentRelease);
                    var status = validated.EvaluationId is null ? "NoApplicablePolicy" : "DecisionReceived";
                    await registry.RecordDecisionResponseAsync(
                        releaseId,
                        validated.EvaluationId,
                        validated.SelectedPolicyVersionsJson,
                        validated.OriginalResponseJson,
                        validated.OriginalEvidenceJson,
                        status,
                        _timeProvider.GetUtcNow(),
                        phaseToken);
                    await registry.UpdateRecoveryMetadataAsync(
                        releaseId, Metadata("decision-response-durably-recorded", status: status), _timeProvider.GetUtcNow(), phaseToken);
                }
            }

            currentRelease = await ReloadAsync(releaseId, phaseToken);
            if (currentRelease.Work.OriginalResponseJson is null)
            {
                throw new InvalidDataException("Asteria assessment recovery has no durable original Decision API response.");
            }

            var assessment = AsteriaDecisionApiClient.ValidateResponse(currentRelease.Work.OriginalResponseJson, currentRelease);
            if (assessment.EvaluationId != currentRelease.Work.EvaluationId ||
                !string.Equals(assessment.SelectedPolicyVersionsJson, currentRelease.Work.SelectedPolicyVersionsJson, StringComparison.Ordinal) ||
                !string.Equals(assessment.OriginalEvidenceJson, currentRelease.Work.OriginalEvidenceJson, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Stored Asteria decision evidence does not match the original response.");
            }

            if (currentRelease.Work.Status is not ("ObservationSubmitted" or "Accepted"))
            {
                await registry.AdvanceStatusAsync(
                    releaseId, "ObservationSubmitted", _timeProvider.GetUtcNow(), phaseToken);
                await registry.UpdateRecoveryMetadataAsync(
                    releaseId, Metadata("observation-submission-started", currentRelease.Work.ParentRevision, assessment.Outcome),
                    _timeProvider.GetUtcNow(), phaseToken);
                currentRelease = await ReloadAsync(releaseId, phaseToken);
            }

            if (currentRelease.Work.ParentRevision is null)
            {
                throw new InvalidDataException("Asteria assessment work has no durable parent revision.");
            }

            await AssertAdmissionAsync(currentRelease, leaseToken, phaseToken);
            _ = await stateMachine.TryAcceptObservationAsync(currentRelease, assessment, phaseToken);
            await registry.AdvanceStatusAsync(
                releaseId, "Accepted", _timeProvider.GetUtcNow(), phaseToken);
            await registry.UpdateRecoveryMetadataAsync(
                releaseId, Metadata("observation-accepted-and-acknowledged", currentRelease.Work.ParentRevision, assessment.Outcome),
                _timeProvider.GetUtcNow(), phaseToken);
            return await ReloadAsync(releaseId, phaseToken);
        }, cancellationToken);
    }

    private async Task<T> WithLeaseAsync<T>(
        string releaseId,
        Func<string, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid().ToString("N");
        if (!await registry.AcquireLeaseAsync(
                releaseId, leaseToken, _timeProvider.GetUtcNow(), LeaseDuration, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Asteria release '{releaseId}' is currently being processed by another coordinator.");
        }

        using var heartbeatStop = new CancellationTokenSource();
        using var leaseLost = new CancellationTokenSource();
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, leaseLost.Token);
        var heartbeat = MaintainLeaseAsync(releaseId, leaseToken, heartbeatStop.Token, leaseLost);
        try
        {
            var result = await action(leaseToken, phaseCancellation.Token);
            phaseCancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
            await registry.ReleaseLeaseAsync(releaseId, leaseToken, CancellationToken.None);
        }
    }

    private async Task MaintainLeaseAsync(
        string releaseId,
        string leaseToken,
        CancellationToken stoppingToken,
        CancellationTokenSource leaseLost)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromTicks(LeaseDuration.Ticks / 3));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (!await registry.RenewLeaseAsync(
                            releaseId, leaseToken, _timeProvider.GetUtcNow(), LeaseDuration, stoppingToken))
                    {
                        leaseLost.Cancel();
                        return;
                    }
                }
                catch (SqliteException)
                {
                    leaseLost.Cancel();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<AsteriaReleaseSnapshot> ReloadAsync(
        string releaseId,
        CancellationToken cancellationToken) =>
        await registry.GetReleaseAsync(releaseId, cancellationToken)
        ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' disappeared during processing.");

    private async Task AssertAdmissionAsync(
        AsteriaReleaseSnapshot release,
        string leaseToken,
        CancellationToken cancellationToken) =>
        await registry.AssertCoordinatorAdmissionAsync(
            release.ReleaseId,
            release.AssessmentGeneration,
            leaseToken,
            _timeProvider.GetUtcNow(),
            cancellationToken);

    private static (string DefinitionId, string DefinitionVersion) ReadDefinitionIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var definitionId = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var definitionVersion = root.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(definitionId) || string.IsNullOrWhiteSpace(definitionVersion))
        {
            throw new InvalidDataException("Asteria definition must declare an ID and version.");
        }
        return (definitionId, definitionVersion);
    }

    private string Metadata(string lastAction, long? parentRevision = null, string? outcome = null, string? status = null) =>
        JsonSerializer.Serialize(new
        {
            lastAction,
            parentRevision,
            outcome,
            status,
            coordinatorObservedAt = _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
}
