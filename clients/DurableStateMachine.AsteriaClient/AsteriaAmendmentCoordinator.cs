using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.AsteriaClient;

public sealed class AsteriaAmendmentCoordinator(
    AsteriaReleaseRegistry registry,
    AsteriaStateMachineClient stateMachine,
    string definitionJson,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly (string DefinitionId, string DefinitionVersion) _definitionIdentity =
        ReadDefinitionIdentity(definitionJson);

    public async Task<AsteriaAmendmentSnapshot> AmendAsync(
        string releaseId,
        string amendmentId,
        int expectedBaseGeneration,
        AsteriaReleaseMaterial material,
        CancellationToken cancellationToken = default)
    {
        if (_definitionIdentity.DefinitionVersion != "3.0")
        {
            throw new InvalidDataException("Asteria amendments require the immutable Asteria definition V3.");
        }

        var oldGeneration = await registry.GetGenerationAsync(
            releaseId, expectedBaseGeneration, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Asteria release '{releaseId}' generation {expectedBaseGeneration} does not exist.");
        var priorAmendment = await registry.GetAmendmentAsync(releaseId, amendmentId, cancellationToken);
        var oldInstance = await stateMachine.InspectGenerationAsync(oldGeneration, cancellationToken);
        var leaseToken = Guid.NewGuid().ToString("N");
        var amendment = await registry.ReserveAmendmentAsync(
            releaseId,
            amendmentId,
            expectedBaseGeneration,
            material,
            oldInstance.CurrentState,
            leaseToken,
            _timeProvider.GetUtcNow(),
            LeaseDuration,
            _definitionIdentity.DefinitionId,
            _definitionIdentity.DefinitionVersion,
            cancellationToken);

        if (amendment.Phase == "NewGenerationActivated")
        {
            return amendment;
        }
        if (priorAmendment is not null)
        {
            leaseToken = Guid.NewGuid().ToString("N");
        }
        if (!await registry.AcquireAmendmentLeaseAsync(
                releaseId, amendmentId, leaseToken, _timeProvider.GetUtcNow(), LeaseDuration, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Asteria amendment '{amendmentId}' is currently being processed by another coordinator.");
        }

        return await WithLeaseAsync(
            releaseId, amendmentId, leaseToken, amendment, cancellationToken);
    }

    private async Task<AsteriaAmendmentSnapshot> WithLeaseAsync(
        string releaseId,
        string amendmentId,
        string leaseToken,
        AsteriaAmendmentSnapshot amendment,
        CancellationToken cancellationToken)
    {
        using var heartbeatStop = new CancellationTokenSource();
        using var leaseLost = new CancellationTokenSource();
        using var phaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, leaseLost.Token);
        var heartbeat = MaintainLeaseAsync(releaseId, leaseToken, heartbeatStop.Token, leaseLost);
        try
        {
            while (amendment.Phase != "NewGenerationActivated")
            {
                await AssertAmendmentLeaseAsync(amendment, leaseToken, phaseCancellation.Token);
                var replacement = await registry.GetGenerationAsync(
                    releaseId, amendment.NewGeneration, phaseCancellation.Token)
                    ?? throw new InvalidDataException("Reserved Asteria generation snapshot is missing.");

                switch (amendment.Phase)
                {
                    case "AmendmentReserved":
                        await stateMachine.EnsureDefinitionAsync(definitionJson, phaseCancellation.Token);
                        await AssertAmendmentLeaseAsync(amendment, leaseToken, phaseCancellation.Token);
                        _ = await stateMachine.EnsureInstanceAsync(replacement, phaseCancellation.Token);
                        await registry.AdvanceAmendmentPhaseAsync(
                            releaseId, amendmentId, leaseToken, "NewInstanceProvisioned",
                            _timeProvider.GetUtcNow(), phaseCancellation.Token);
                        break;

                    case "NewInstanceProvisioned":
                    {
                        var oldGeneration = await registry.GetGenerationAsync(
                            releaseId, amendment.ExpectedBaseGeneration, phaseCancellation.Token)
                            ?? throw new InvalidDataException("Amended Asteria base generation is missing.");
                        await AssertAmendmentLeaseAsync(amendment, leaseToken, phaseCancellation.Token);
                        var currentOldInstance = await stateMachine.InspectGenerationAsync(
                            oldGeneration, phaseCancellation.Token);
                        var attempt = await registry.GetOrCreateSupersessionAttemptAsync(
                            releaseId,
                            amendmentId,
                            currentOldInstance.Revision,
                            leaseToken,
                            _timeProvider.GetUtcNow(),
                            phaseCancellation.Token);
                        var attempts = await registry.ListSupersessionAttemptsAsync(
                            releaseId, amendmentId, phaseCancellation.Token);
                        if (!attempts.Contains(attempt))
                        {
                            throw new InvalidDataException("Durable supersession attempt could not be read back.");
                        }
                        await stateMachine.EnsureSupersededAsync(
                            oldGeneration, replacement, amendment, attempts, phaseCancellation.Token);
                        var oldInstance = await stateMachine.InspectGenerationAsync(
                            oldGeneration, phaseCancellation.Token);
                        if (oldInstance.CurrentState != "Superseded")
                        {
                            throw new InvalidDataException("Asteria old generation was not terminally superseded.");
                        }
                        await registry.AdvanceAmendmentPhaseAsync(
                            releaseId, amendmentId, leaseToken, "OldInstanceSuperseded",
                            _timeProvider.GetUtcNow(), phaseCancellation.Token);
                        break;
                    }

                    case "OldInstanceSuperseded":
                    {
                        var oldGeneration = await registry.GetGenerationAsync(
                            releaseId, amendment.ExpectedBaseGeneration, phaseCancellation.Token)
                            ?? throw new InvalidDataException("Amended Asteria base generation is missing.");
                        await AssertAmendmentLeaseAsync(amendment, leaseToken, phaseCancellation.Token);
                        var attempts = await registry.ListSupersessionAttemptsAsync(
                            releaseId, amendmentId, phaseCancellation.Token);
                        await stateMachine.EnsureSupersededAsync(
                            oldGeneration, replacement, amendment, attempts, phaseCancellation.Token);
                        await registry.ActivateAmendedGenerationAsync(
                            releaseId, amendmentId, leaseToken, _timeProvider.GetUtcNow(), phaseCancellation.Token);
                        break;
                    }

                    default:
                        throw new InvalidDataException($"Unsupported Asteria amendment phase '{amendment.Phase}'.");
                }

                amendment = await registry.GetAmendmentAsync(releaseId, amendmentId, phaseCancellation.Token)
                    ?? throw new InvalidDataException("Asteria amendment reservation disappeared during recovery.");
            }

            var current = await registry.GetReleaseAsync(releaseId, phaseCancellation.Token)
                ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' disappeared after activation.");
            if (current.AssessmentGeneration != amendment.NewGeneration ||
                current.Work.Status != "Registered")
            {
                throw new InvalidDataException("Activated Asteria generation is not the operational current generation.");
            }
            return amendment;
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
            await registry.ReleaseAmendmentLeaseAsync(releaseId, leaseToken, CancellationToken.None);
        }
    }

    private async Task AssertAmendmentLeaseAsync(
        AsteriaAmendmentSnapshot amendment,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        await registry.AssertAmendmentAdmissionAsync(
            amendment.ReleaseId,
            amendment.AmendmentId,
            amendment.ExpectedBaseGeneration,
            leaseToken,
            _timeProvider.GetUtcNow(),
            LeaseDuration,
            cancellationToken);
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
                    if (!await registry.RenewAmendmentLeaseAsync(
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

    private static (string DefinitionId, string DefinitionVersion) ReadDefinitionIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException("Asteria definition must declare an ID and version.");
        }
        return (id, version);
    }
}
