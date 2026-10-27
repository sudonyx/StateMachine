using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Persistence;

public sealed class MachineStore(
    MachineDbContext db,
    InstanceLockRegistry instanceLockRegistry,
    InputFaultPlan inputFaultPlan)
{
    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web);
    private object InstanceLocksSync => instanceLockRegistry.Sync;
    private Dictionary<string, InstanceGate> InstanceLocks => instanceLockRegistry.Locks;

    public async Task<DefinitionRegistration> RegisterDefinitionAsync(string definitionJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var definition = MachineDefinitionLoader.Load(definitionJson);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson)));
        var current = await db.Definitions.FindAsync([definition.Id, definition.Version], cancellationToken);
        if (current is not null)
        {
            if (!string.Equals(current.ContentDigest, digest, StringComparison.Ordinal))
            {
                throw new DefinitionConflictException($"Definition '{definition.Id}@{definition.Version}' is already registered with different content.");
            }
            return new DefinitionRegistration(ToSummary(current), true);
        }

        var record = new DefinitionRecord
        {
            Id = definition.Id,
            Version = definition.Version,
            LanguageVersion = definition.LanguageVersion,
            DefinitionJson = definitionJson,
            ContentDigest = digest,
            RegisteredAt = now
        };
        db.Definitions.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            if (!await DefinitionExistsAsync(definition.Id, definition.Version, cancellationToken))
            {
                throw;
            }
            var winner = await db.Definitions.FindAsync([definition.Id, definition.Version], cancellationToken);
            if (winner is not null && string.Equals(winner.ContentDigest, digest, StringComparison.Ordinal))
            {
                return new DefinitionRegistration(ToSummary(winner), true);
            }
            throw new DefinitionConflictException($"Definition '{definition.Id}@{definition.Version}' was registered concurrently with different content.");
        }

        return new DefinitionRegistration(ToSummary(record), false);
    }

    public async Task<IReadOnlyList<DefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken) =>
        await db.Definitions.AsNoTracking()
            .OrderBy(record => record.Id).ThenBy(record => record.Version)
            .Select(record => new DefinitionSummary(record.Id, record.Version, record.LanguageVersion, record.ContentDigest, record.RegisteredAt))
            .ToListAsync(cancellationToken);

    public async Task<DefinitionDetails> GetDefinitionAsync(string id, string version, CancellationToken cancellationToken)
    {
        var record = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            definition => definition.Id == id && definition.Version == version, cancellationToken)
            ?? throw new ResourceNotFoundException($"Definition '{id}@{version}' does not exist.");
        var parsed = MachineDefinitionLoader.Load(record.DefinitionJson);
        return new DefinitionDetails(
            ToSummary(record),
            parsed.InitialState,
            parsed.States.Values.Select(state => new StateDetails(state.Id, state.IsTerminal)).ToArray(),
            parsed.Events.Order(StringComparer.Ordinal).ToArray(),
            parsed.Transitions.Count,
            record.DefinitionJson);
    }

    public async Task<MachineInstanceDetails> CreateInstanceAsync(
        string id,
        string definitionId,
        string definitionVersion,
        IReadOnlyDictionary<string, FactValue> facts,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Instance ID must be a non-empty string.", nameof(id));
        }
        var definitionRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == definitionId && record.Version == definitionVersion, cancellationToken)
            ?? throw new ResourceNotFoundException($"Definition '{definitionId}@{definitionVersion}' does not exist.");
        var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
        var record = new InstanceRecord
        {
            Id = id,
            DefinitionId = definitionId,
            DefinitionVersion = definitionVersion,
            CurrentState = definition.InitialState,
            FactsJson = SerializeFacts(facts),
            Revision = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
        var concernRecords = definition.Concerns.Values.Select(concern => new ConcernStateRecord
        {
            InstanceId = id,
            ConcernId = concern.Id,
            DefinitionId = definitionId,
            DefinitionVersion = definitionVersion,
            CurrentState = concern.InitialState,
            Revision = 0,
            UpdatedAt = now
        }).ToArray();
        db.Instances.Add(record);
        db.ConcernStates.AddRange(concernRecords);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            foreach (var concernRecord in concernRecords)
            {
                db.Entry(concernRecord).State = EntityState.Detached;
            }
            if (await db.Instances.AsNoTracking().AnyAsync(instance => instance.Id == id, cancellationToken))
            {
                throw new DefinitionConflictException($"Instance '{id}' already exists.");
            }
            throw;
        }
        return ToDetails(record);
    }

    public async Task<ConcernInspection> GetConcernInspectionAsync(string id, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{id}' does not exist.");
        var definitionRecord = await db.Definitions.AsNoTracking().SingleAsync(
            item => item.Id == instance.DefinitionId && item.Version == instance.DefinitionVersion, cancellationToken);
        var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
        var rows = await db.ConcernStates.AsNoTracking()
            .Where(item => item.InstanceId == id)
            .OrderBy(item => item.ConcernId)
            .ToListAsync(cancellationToken);
        var history = await db.ConcernTransitionHistory.AsNoTracking()
            .Where(item => item.InstanceId == id)
            .OrderBy(item => item.RevisionAfter).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var findings = new List<ConcernIntegrityFinding>();
        foreach (var concern in definition.Concerns.Values)
        {
            var row = rows.SingleOrDefault(item => item.ConcernId == concern.Id);
            if (row is null)
            {
                findings.Add(new("missing-concern-projection", concern.Id,
                    "The declared concern has no durable current-state projection."));
            }
            else if (row.DefinitionId != instance.DefinitionId || row.DefinitionVersion != instance.DefinitionVersion)
            {
                findings.Add(new("concern-definition-mismatch", concern.Id,
                    "The concern projection is bound to a different definition epoch."));
            }
            else if (!concern.States.ContainsKey(row.CurrentState))
            {
                findings.Add(new("unknown-concern-projection-state", concern.Id,
                    $"Projected state '{row.CurrentState}' is not declared by this concern."));
            }
        }
        foreach (var row in rows.Where(item => !definition.Concerns.ContainsKey(item.ConcernId)))
        {
            findings.Add(new("undeclared-concern-projection", row.ConcernId,
                "A concern projection exists but is absent from the bound definition."));
        }
        var lastByConcern = history.GroupBy(item => item.ConcernId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.ConcernRevisionAfter).Last(),
                StringComparer.Ordinal);
        var states = rows.Select(row => new ConcernStateDetails(
            row.ConcernId, row.CurrentState, row.Revision, row.UpdatedAt,
            lastByConcern.TryGetValue(row.ConcernId, out var last) ? last.Id : null,
            lastByConcern.TryGetValue(row.ConcernId, out last) ? last.AttemptId : null)).ToArray();
        return new ConcernInspection(id, instance.DefinitionId, instance.DefinitionVersion,
            instance.CurrentState, instance.Revision, states, findings);
    }

    public async Task<IReadOnlyList<ConcernTransitionHistoryRecord>> ListConcernHistoryAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (!await db.Instances.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken))
        {
            throw new ResourceNotFoundException($"Instance '{id}' does not exist.");
        }
        return await db.ConcernTransitionHistory.AsNoTracking()
            .Where(item => item.InstanceId == id)
            .OrderBy(item => item.RevisionAfter).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<ConcernReplayReport> GetConcernReplayAsync(string id, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginReadSnapshotAsync(cancellationToken);
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{id}' does not exist.");
        var definition = await db.Definitions.AsNoTracking().SingleAsync(
            item => item.Id == instance.DefinitionId && item.Version == instance.DefinitionVersion, cancellationToken);
        var migrations = await db.Migrations.AsNoTracking()
            .Where(item => item.InstanceId == id && item.Status == "Committed")
            .ToListAsync(cancellationToken);
        var projections = await db.ConcernStates.AsNoTracking()
            .Where(item => item.InstanceId == id).ToListAsync(cancellationToken);
        var history = await db.ConcernTransitionHistory.AsNoTracking()
            .Where(item => item.InstanceId == id)
            .OrderBy(item => item.OccurredAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var attempts = await db.InputAttempts.AsNoTracking()
            .Where(item => item.InstanceId == id).ToListAsync(cancellationToken);
        var report = migrations.Count != 0
            ? new ConcernReplayReport(id, instance.DefinitionId, instance.DefinitionVersion,
                definition.ContentDigest, "UnsupportedHistoricalFormat", [],
                [new ConcernReplayFinding("concern-definition-migration-unsupported", null,
                    "Historical concern replay across definition migrations is not supported; migration is refused for concern definitions.")],
                history.Select(item => $"concern-history:{item.Id}").ToArray())
            : ConcernHistoricalReplay.Reconstruct(instance, definition, projections, history, attempts);
        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    public async Task<MachineInstanceDetails> GetInstanceAsync(string id, CancellationToken cancellationToken)
    {
        var record = await db.Instances.AsNoTracking().SingleOrDefaultAsync(instance => instance.Id == id, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{id}' does not exist.");
        return ToDetails(record);
    }

    public async Task<HistoricalReplayReport> GetHistoricalReplayAsync(
        string instanceId,
        bool verifyDecisions,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginReadSnapshotAsync(cancellationToken);
        var snapshot = await ReadReplaySnapshotAsync(instanceId, cancellationToken);
        var report = HistoricalReplay.Reconstruct(snapshot.Instance, snapshot.Definitions, snapshot.Migrations,
            snapshot.History, snapshot.Attempts, snapshot.Receipts, snapshot.Timers, verifyDecisions);
        await transaction.CommitAsync(cancellationToken);
        return report;
    }

    public async Task<MigrationEligibility> PreviewMigrationAsync(
        string instanceId,
        MigrationRequest request,
        DefinitionMigrationPlan? plan,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginReadSnapshotAsync(cancellationToken);
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
        var eligibility = await EvaluateMigrationAsync(instance, request, plan, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return eligibility;
    }

    public async Task<MigrationExecutionResult> ExecuteMigrationAsync(
        string instanceId,
        MigrationRequest request,
        DefinitionMigrationPlan? plan,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MigrationId) || request.MigrationId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.AuthorisationReason) || request.ExpectedRevision < 0)
        {
            throw new ArgumentException("A migration ID, authorisation reason, and non-negative expected revision are required.");
        }

        await using var transaction = await BeginImmediateTransactionAsync(cancellationToken);
        var instance = await db.Instances.SingleOrDefaultAsync(
            record => record.Id == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");

        var prior = await db.Migrations.SingleOrDefaultAsync(
            record => record.InstanceId == instanceId && record.MigrationId == request.MigrationId,
            cancellationToken);
        if (prior is not null)
        {
            if (prior.SourceDefinitionId != request.SourceDefinitionId ||
                prior.SourceDefinitionVersion != request.SourceDefinitionVersion ||
                prior.TargetDefinitionId != request.TargetDefinitionId ||
                prior.TargetDefinitionVersion != request.TargetDefinitionVersion ||
                prior.ExpectedRevision != request.ExpectedRevision ||
                prior.AuthorisationReason != request.AuthorisationReason ||
                prior.PlanId != (plan?.Id ?? "unsupported") ||
                prior.PlanVersion != (plan?.Version ?? "0"))
            {
                throw new DefinitionConflictException(
                    $"Migration identity '{request.MigrationId}' was already used with different content.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new MigrationExecutionResult(prior, ToDetails(instance));
        }

        var eligibility = await EvaluateMigrationAsync(instance, request, plan, cancellationToken);
        var record = CreateMigrationRecord(instance, request, plan, eligibility, now);
        var sourceDefinition = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == request.SourceDefinitionId && item.Version == request.SourceDefinitionVersion,
            cancellationToken);
        var targetDefinition = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == request.TargetDefinitionId && item.Version == request.TargetDefinitionVersion,
            cancellationToken);
        record.SourceDefinitionDigest = sourceDefinition is null
            ? string.Empty
            : ComputeDefinitionDigest(sourceDefinition.DefinitionJson);
        record.TargetDefinitionDigest = targetDefinition is null
            ? string.Empty
            : ComputeDefinitionDigest(targetDefinition.DefinitionJson);
        if (eligibility.Status == "Eligible")
        {
            var correspondence = plan!.StateCorrespondences.Single(item => item.SourceState == instance.CurrentState);
            var changed = await db.Instances
                .Where(item => item.Id == instanceId &&
                               item.Revision == request.ExpectedRevision &&
                               item.DefinitionId == request.SourceDefinitionId &&
                               item.DefinitionVersion == request.SourceDefinitionVersion &&
                               item.CurrentState == instance.CurrentState)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.DefinitionId, request.TargetDefinitionId)
                    .SetProperty(item => item.DefinitionVersion, request.TargetDefinitionVersion)
                    .SetProperty(item => item.CurrentState, correspondence.TargetState)
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (changed != 1)
            {
                record.Status = "Refused";
                record.Outcome = "concurrent-instance-change";
                record.Explanation = "The instance changed before the conditional migration update could commit.";
                eligibility = eligibility with
                {
                    Status = "Refused",
                    Findings = [.. eligibility.Findings, record.Explanation]
                };
                record.EligibilityFindingsJson = JsonSerializer.Serialize(eligibility.Findings, StoredJsonOptions);
            }
            else
            {
                record.Status = "Committed";
                record.Outcome = "migration-committed";
                record.Explanation = "The validated definition epoch was committed without changing instance facts, revision, timers, or external work.";
                instance.DefinitionId = request.TargetDefinitionId;
                instance.DefinitionVersion = request.TargetDefinitionVersion;
                instance.CurrentState = correspondence.TargetState;
                instance.UpdatedAt = now;
            }
        }

        db.Migrations.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MigrationExecutionResult(record, ToDetails(instance));
    }

    public async Task<IReadOnlyList<MigrationRecord>> ListMigrationsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.Migrations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.CommittedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<RecoveryAssessment> GetRecoveryAssessmentAsync(
        string instanceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var transaction = await BeginReadSnapshotAsync(cancellationToken);
        var snapshot = await ReadReplaySnapshotAsync(instanceId, cancellationToken);
        var instance = snapshot.Instance;
        var attempts = snapshot.Attempts;
        var receipts = snapshot.Receipts;
        var effects = await db.EffectOperations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.RequestedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        var stagedObservations = await db.StagedObservations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        var effectAttempts = await (
            from attempt in db.EffectAttempts.AsNoTracking()
            join operation in db.EffectOperations.AsNoTracking()
                on attempt.EffectOperationId equals operation.Id
            where operation.InstanceId == instanceId
            orderby attempt.EffectOperationId, attempt.AttemptNumber
            select attempt).ToListAsync(cancellationToken);
        var timers = snapshot.Timers;
        var expectedOperations = await db.ExpectedOperations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ExpectedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);

        var replay = HistoricalReplay.Reconstruct(instance, snapshot.Definitions, snapshot.Migrations,
            snapshot.History, attempts, receipts, timers, verifyDecisions: false);
        var effectAttemptsByOperation = effectAttempts
            .GroupBy(attempt => attempt.EffectOperationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.AttemptNumber).Last(), StringComparer.Ordinal);
        var outstandingEffects = new List<RecoveryEffect>();
        foreach (var effect in effects)
        {
            if (effect.Status is "ObservationAccepted" or "ObservationRefused" or
                "ProviderFailed" or "ProviderRefused" or "HarnessRejected")
            {
                continue;
            }

            effectAttemptsByOperation.TryGetValue(effect.Id, out var latestAttempt);
            var unknown = effect.Status is "OutcomeUnknown" or "AttemptInitiated" or
                "HarnessSucceededResponseMissing" or "HarnessResponseNotUsable" ||
                latestAttempt?.ProviderOutcome == "Unknown";
            var guidance = effect.Capability == "customer-notification"
                ? "Do not automatically retry this notification; inspect the existing outcome and preserve its operation identity."
                : effect.HarnessExecutionId is not null
                    ? "Inspect the existing harness execution; do not submit a replacement during recovery assessment."
                    : "Harness correlation is not durable; do not submit a replacement or infer failure.";
            outstandingEffects.Add(new RecoveryEffect(
                effect.OperationId,
                effect.Capability,
                unknown ? "OutcomeUnknown" : effect.Status,
                effect.HarnessExecutionId,
                latestAttempt?.ProviderOutcome,
                latestAttempt?.ObservationStatus,
                guidance));
        }

        var pendingInputs = receipts
            .Where(receipt => receipt.Status is "Received" or "Pending" or "Processing")
            .Select(receipt => new RecoveryInput(receipt.Id, receipt.Status, receipt.ReasonCode, receipt.AttemptId,
                "Resume this durable input only through its receipt-specific continuation route; its original identity and envelope are retained."))
            .Concat(attempts
                .Where(attempt => attempt.Status is "Unresolved" or "Ambiguous")
                .Select(attempt => new RecoveryInput(attempt.Id, attempt.Status, attempt.ReasonCode, attempt.Id,
                    "Preserve the original attempt and evidence; do not automatically re-evaluate an unresolved or ambiguous input.")))
            .ToArray();
        var unresolvedStagedObservations = stagedObservations
            .Where(result => result.Status == "Received")
            .Select(result => new RecoveryStagedObservation(
                result.Id,
                result.ExpectedOperationId,
                result.WorkflowOperationId,
                result.HarnessExecutionId,
                result.Status,
                result.PayloadDigest,
                "Continue this original staged result through its result-specific route; do not invoke the provider again."))
            .ToArray();
        var recoveryTimers = timers.Select(timer => new RecoveryTimer(
            timer.Id,
            timer.Status,
            timer.DueAt,
            timer.Status == "Pending" && timer.DueAt <= now,
            timer.ResultReason)).ToArray();
        var unresolvedExpected = expectedOperations
            .Where(operation => operation.Status is "Pending" or "Expected")
            .Select(operation => new RecoveryExpectedOperation(
                operation.OperationId, operation.Status, operation.ExpectedRevision, operation.ObservationType))
            .ToArray();
        var reasons = new List<string>();
        var hasPendingReceipt = pendingInputs.Any(input => input.Status is "Received" or "Pending" or "Processing");
        hasPendingReceipt |= unresolvedStagedObservations.Length > 0;
        var hasPendingLocal = recoveryTimers.Any(timer => timer.Status == "Pending");
        var hasUnresolvedInput = pendingInputs.Any(input => input.Status is "Unresolved" or "Ambiguous");
        var hasUnknownExternal = outstandingEffects.Any(effect => effect.Status == "OutcomeUnknown");
        var hasPendingExternal = outstandingEffects.Count > 0 || unresolvedExpected.Length > 0;

        string classification;
        if (replay.Outcome == "Inconsistent")
        {
            classification = "InconsistentHistoryOrProjection";
            reasons.Add("Historical transition evidence does not agree with the durable projection.");
        }
        else if (replay.Outcome is "InsufficientEvidence" or "UnsupportedHistoricalFormat")
        {
            classification = "InsufficientEvidenceForSafeContinuation";
            reasons.Add("The historical record is not sufficient to establish a safe continuation boundary.");
        }
        else if (hasPendingReceipt || hasUnresolvedInput)
        {
            classification = "InsufficientEvidenceForSafeContinuation";
            reasons.Add(hasPendingReceipt
                ? "A durable intake has no completed processing link and requires explicit receipt-specific continuation."
                : "An input evaluation is unresolved or ambiguous and must not be silently repeated.");
        }
        else if (hasUnknownExternal)
        {
            classification = "ConsistentExternalOutcomeUnknown";
            reasons.Add("At least one external operation has an unknown outcome; its original identity must be preserved.");
        }
        else if (hasPendingExternal)
        {
            classification = "ConsistentPendingExternalWork";
            reasons.Add("Expected external work remains unresolved; assessment did not contact or resubmit an external system.");
        }
        else if (hasPendingLocal)
        {
            classification = "ConsistentPendingLocalWork";
            reasons.Add("A durable local input or timer remains pending; assessment did not evaluate inputs or process timers.");
        }
        else
        {
            classification = "ConsistentNoOutstandingWork";
            reasons.Add("The projection agrees with accepted history and no pending local or external work was found.");
        }

        foreach (var timer in recoveryTimers.Where(item => item.Status == "Pending"))
        {
            reasons.Add(timer.Overdue
                ? $"Timer '{timer.TimerId}' is overdue but was not processed."
                : $"Timer '{timer.TimerId}' remains pending until {timer.DueAt:O}.");
        }
        foreach (var effect in outstandingEffects)
        {
            reasons.Add($"Effect '{effect.OperationId}' is {effect.Status}; {effect.RecoveryGuidance}");
        }

        var assessment = new RecoveryAssessment(
            instance.Id,
            classification,
            instance.CurrentState,
            instance.Revision,
            now,
            instance.Revision,
            replay,
            reasons,
            pendingInputs,
            unresolvedStagedObservations,
            outstandingEffects,
            recoveryTimers,
            expectedOperations.Select(operation => new RecoveryExpectedOperation(
                operation.OperationId, operation.Status, operation.ExpectedRevision, operation.ObservationType)).ToArray());
        await transaction.CommitAsync(cancellationToken);
        return assessment;
    }

    public async Task<StagedObservationRecord> StageVerifiedExternalResultAsync(
        string instanceId,
        string workflowOperationId,
        string harnessExecutionId,
        string providerIdentity,
        string capability,
        string observationType,
        string? providerEffectId,
        DateTimeOffset occurredAt,
        JsonElement evidence,
        IReadOnlyList<string> evidenceReferences,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        inputFaultPlan.ThrowIfArmed(instanceId, "BeforeExternalResultStaging");
        if (evidence.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(harnessExecutionId) ||
            string.IsNullOrWhiteSpace(providerIdentity) ||
            string.IsNullOrWhiteSpace(capability) ||
            string.IsNullOrWhiteSpace(observationType))
        {
            throw new InvalidDataException("A staged external result requires verified harness correlation and an object evidence payload.");
        }

        await using var transaction = await BeginImmediateTransactionAsync(cancellationToken);
        var effect = await db.EffectOperations.SingleOrDefaultAsync(
            record => record.InstanceId == instanceId && record.OperationId == workflowOperationId,
            cancellationToken)
            ?? throw new ResourceNotFoundException(
                $"Workflow operation '{workflowOperationId}' does not exist for instance '{instanceId}'.");
        var expected = await db.ExpectedOperations.SingleOrDefaultAsync(
            record => record.InstanceId == instanceId && record.OperationId == workflowOperationId,
            cancellationToken)
            ?? throw new DefinitionConflictException(
                $"Workflow operation '{workflowOperationId}' has no registered expected observation.");
        var instance = await db.Instances.SingleAsync(record => record.Id == instanceId, cancellationToken);
        if (effect.Status != "ProviderSucceeded" ||
            effect.OutcomeConfidence != "ObservedAndValidated" ||
            effect.HarnessExecutionId != harnessExecutionId ||
            effect.ProviderIdentity != providerIdentity ||
            effect.Capability != capability ||
            effect.ObservationType != observationType ||
            effect.ExecutionRevision != expected.ExpectedRevision ||
            expected.SourceIdentity != providerIdentity ||
            expected.ObservationType != observationType ||
            expected.Status != "Expected" ||
            instance.DefinitionId != effect.DefinitionId ||
            instance.DefinitionVersion != effect.DefinitionVersion)
        {
            throw new DefinitionConflictException(
                "The verified result does not match the durable harness execution, effect intent, expected operation, and definition binding.");
        }

        var payloadJson = CanonicalizeJson(JsonSerializer.Serialize(new
        {
            effectId = providerEffectId,
            harnessExecutionId
        }, StoredJsonOptions));
        var evidenceJson = CanonicalizeJson(evidence.GetRawText());
        var referencesJson = JsonSerializer.Serialize(
            evidenceReferences.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StoredJsonOptions);
        var staged = new StagedObservationRecord
        {
            Id = CreateStagedObservationId(instanceId, workflowOperationId, harnessExecutionId),
            InstanceId = instanceId,
            ExpectedOperationId = expected.OperationId,
            WorkflowOperationId = workflowOperationId,
            HarnessExecutionId = harnessExecutionId,
            ProviderIdentity = providerIdentity,
            Capability = capability,
            DefinitionId = effect.DefinitionId,
            DefinitionVersion = effect.DefinitionVersion,
            ExecutionRevision = effect.ExecutionRevision,
            SourceAuthority = "verified-harness-adapter",
            ResultClassification = "Succeeded",
            ObservationType = observationType,
            ProviderEffectId = providerEffectId,
            OccurredAt = occurredAt.ToUniversalTime(),
            PayloadJson = payloadJson,
            EvidenceJson = evidenceJson,
            EvidenceReferencesJson = referencesJson,
            PayloadDigest = string.Empty,
            ReceivedAt = receivedAt.ToUniversalTime(),
            Status = "Received",
            Explanation = "The verified external response is durably staged and awaits Gate 5 observation processing."
        };
        staged.PayloadDigest = ComputeStagedObservationDigest(staged);

        var prior = await db.StagedObservations.SingleOrDefaultAsync(
            record => record.InstanceId == instanceId &&
                record.ExpectedOperationId == workflowOperationId,
            cancellationToken);
        if (prior is not null)
        {
            if (prior.PayloadDigest != staged.PayloadDigest ||
                prior.HarnessExecutionId != harnessExecutionId)
            {
                throw new DefinitionConflictException(
                    $"A conflicting external result is already staged for expected operation '{workflowOperationId}'.");
            }
            await transaction.CommitAsync(cancellationToken);
            return prior;
        }
        var executionConflict = await db.StagedObservations.AsNoTracking().AnyAsync(
            record => record.HarnessExecutionId == harnessExecutionId,
            cancellationToken);
        if (executionConflict)
        {
            throw new DefinitionConflictException(
                $"Harness execution '{harnessExecutionId}' is already bound to a different staged result.");
        }

        db.StagedObservations.Add(staged);
        inputFaultPlan.ThrowIfArmed(instanceId, "DuringExternalResultStagingPersistence");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        inputFaultPlan.ThrowIfArmed(instanceId, "AfterExternalResultStagingCommit");
        await inputFaultPlan.WaitIfPausedAsync(
            instanceId, "AfterExternalResultStagingCommit", cancellationToken);
        return staged;
    }

    public async Task<InboundProcessingResult> ContinueStagedObservationAsync(
        string instanceId,
        string stagedResultId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var staged = await db.StagedObservations.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == stagedResultId && record.InstanceId == instanceId,
            cancellationToken)
            ?? throw new ResourceNotFoundException(
                $"Staged result '{stagedResultId}' does not exist for instance '{instanceId}'.");
        if (staged.Status != "Received")
        {
            if (staged.ReceiptId is null)
            {
                throw new InvalidDataException(
                    $"Final staged result '{stagedResultId}' has no linked observation receipt.");
            }
            return new InboundProcessingResult(
                await GetInboundMessageAsync(instanceId, staged.ReceiptId, cancellationToken), null);
        }
        if (staged.SourceAuthority != "verified-harness-adapter" ||
            staged.ResultClassification != "Succeeded" ||
            ComputeStagedObservationDigest(staged) != staged.PayloadDigest)
        {
            throw new InvalidDataException(
                $"Staged result '{stagedResultId}' failed its authority or immutable payload digest check.");
        }

        var effect = await db.EffectOperations.AsNoTracking().SingleOrDefaultAsync(
            record => record.InstanceId == instanceId &&
                record.OperationId == staged.WorkflowOperationId,
            cancellationToken)
            ?? throw new InvalidDataException("The staged result has no original workflow effect intent.");
        var expected = await db.ExpectedOperations.AsNoTracking().SingleOrDefaultAsync(
            record => record.InstanceId == instanceId &&
                record.OperationId == staged.ExpectedOperationId,
            cancellationToken)
            ?? throw new InvalidDataException("The staged result has no original expected operation.");
        if (effect.HarnessExecutionId != staged.HarnessExecutionId ||
            effect.ExecutionRevision != staged.ExecutionRevision ||
            effect.DefinitionId != staged.DefinitionId ||
            effect.DefinitionVersion != staged.DefinitionVersion ||
            effect.ProviderIdentity != staged.ProviderIdentity ||
            expected.ObservationType != staged.ObservationType ||
            expected.SourceIdentity != staged.ProviderIdentity ||
            expected.ExpectedRevision != staged.ExecutionRevision)
        {
            throw new InvalidDataException(
                $"Staged result '{stagedResultId}' no longer matches its original effect and expected-operation correlation.");
        }

        using var payloadDocument = JsonDocument.Parse(staged.PayloadJson);
        using var evidenceDocument = JsonDocument.Parse(staged.EvidenceJson);
        if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object ||
            evidenceDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Staged result '{stagedResultId}' contains a non-object observation payload.");
        }
        inputFaultPlan.ThrowIfArmed(instanceId, "BeforeStagedObservationEvaluation");
        var payload = payloadDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        var evidence = evidenceDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        return await SubmitInputAsync(
            instanceId,
            "Observation",
            $"harness-effect-result-{staged.WorkflowOperationId}",
            null,
            null,
            $"observation-harness-{staged.WorkflowOperationId}",
            staged.ObservationType,
            staged.ExecutionRevision,
            staged.ExpectedOperationId,
            staged.ProviderIdentity,
            staged.OccurredAt,
            payload,
            facts: null,
            evidence: evidence,
            decisionEvidence: null,
            factsProvenance: null,
            trustedSource: true,
            now,
            cancellationToken,
            stagedResultId: staged.Id);
    }

    public async Task<IReadOnlyList<StagedObservationRecord>> ListStagedObservationsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.StagedObservations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<ReplaySnapshot> ReadReplaySnapshotAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
        var definitions = await db.Definitions.AsNoTracking().ToListAsync(cancellationToken);
        var migrations = await db.Migrations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.SourceRevision).ThenBy(record => record.CommittedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        var history = await db.TransitionHistory.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.RevisionAfter).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        var attempts = await db.InputAttempts.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        var receipts = await db.InboundMessages.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        var timers = await db.Timers.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.DueAt).ThenBy(record => record.Id)
            .ToListAsync(cancellationToken);
        return new ReplaySnapshot(instance, definitions, migrations, history, attempts, receipts, timers);
    }

    private async Task<MigrationEligibility> EvaluateMigrationAsync(
        InstanceRecord instance,
        MigrationRequest request,
        DefinitionMigrationPlan? plan,
        CancellationToken cancellationToken)
    {
        var findings = new List<string>();
        var outstanding = new List<string>();
        var sourceRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == request.SourceDefinitionId && item.Version == request.SourceDefinitionVersion,
            cancellationToken);
        var targetRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == request.TargetDefinitionId && item.Version == request.TargetDefinitionVersion,
            cancellationToken);
        var sourceDigest = sourceRecord is null ? null : ComputeDefinitionDigest(sourceRecord.DefinitionJson);
        var targetDigest = targetRecord is null ? null : ComputeDefinitionDigest(targetRecord.DefinitionJson);

        if (targetRecord is null)
        {
            findings.Add("unknown-target-definition");
        }
        if (plan is null)
        {
            findings.Add("unsupported-source-target-pair");
        }
        else if (plan.SourceDefinitionId != request.SourceDefinitionId ||
                 plan.SourceDefinitionVersion != request.SourceDefinitionVersion ||
                 plan.TargetDefinitionId != request.TargetDefinitionId ||
                 plan.TargetDefinitionVersion != request.TargetDefinitionVersion)
        {
            findings.Add("migration-plan-binding-mismatch");
        }
        if (instance.DefinitionId != request.SourceDefinitionId ||
            instance.DefinitionVersion != request.SourceDefinitionVersion)
        {
            findings.Add(instance.DefinitionId == request.TargetDefinitionId &&
                          instance.DefinitionVersion == request.TargetDefinitionVersion
                ? "instance-already-on-target-version"
                : "source-definition-binding-mismatch");
        }
        if (instance.Revision != request.ExpectedRevision)
        {
            findings.Add("source-instance-revision-changed");
        }
        if (sourceRecord is null)
        {
            findings.Add("missing-historical-source-definition");
        }
        else if (sourceDigest != sourceRecord.ContentDigest)
        {
            findings.Add("source-definition-digest-mismatch");
        }
        if (targetRecord is not null && targetDigest != targetRecord.ContentDigest)
        {
            findings.Add("target-definition-digest-mismatch");
        }

        string? targetState = null;
        if (sourceRecord is not null && targetRecord is not null && plan is not null)
        {
            try
            {
                var sourceDefinition = MachineDefinitionLoader.Load(sourceRecord.DefinitionJson);
                var targetDefinition = MachineDefinitionLoader.Load(targetRecord.DefinitionJson);
                if (sourceDefinition.States.TryGetValue(instance.CurrentState, out var sourceState) &&
                    sourceState.IsTerminal)
                {
                    findings.Add("terminal-instance-migration-forbidden");
                }
                var correspondence = plan.StateCorrespondences.SingleOrDefault(
                    item => item.SourceState == instance.CurrentState);
                if (!plan.SupportedSourceStates.Contains(instance.CurrentState, StringComparer.Ordinal))
                {
                    findings.Add("source-state-not-supported-by-migration-plan");
                }
                if (correspondence is null)
                {
                    findings.Add("source-state-not-mapped");
                }
                else
                {
                    targetState = correspondence.TargetState;
                    if (string.IsNullOrWhiteSpace(correspondence.BusinessMeaning))
                    {
                        findings.Add("state-correspondence-has-no-business-meaning");
                    }
                    if (!sourceDefinition.States.TryGetValue(instance.CurrentState, out sourceState) ||
                        !targetDefinition.States.TryGetValue(correspondence.TargetState, out var mappedTarget))
                    {
                        findings.Add("mapped-state-missing-from-definition");
                    }
                    else
                    {
                        if (sourceState.IsTerminal != mappedTarget.IsTerminal || mappedTarget.IsTerminal)
                        {
                            findings.Add("incompatible-state-lifecycle-semantics");
                        }
                        var oldOutgoing = sourceDefinition.Transitions
                            .Where(item => item.Source == instance.CurrentState)
                            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
                        var newOutgoing = targetDefinition.Transitions
                            .Where(item => item.Source == correspondence.TargetState)
                            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
                        if (oldOutgoing.Length != newOutgoing.Length ||
                            oldOutgoing.Where((item, index) => !SameTransitionSemantics(
                                item, newOutgoing.ElementAtOrDefault(index), plan)).Any())
                        {
                            findings.Add("incompatible-outgoing-transition-semantics");
                        }
                    }
                }
                var facts = DeserializeFacts(instance.FactsJson);
                foreach (var requiredFact in plan.RequiredTargetFacts)
                {
                    if (!facts.ContainsKey(requiredFact))
                    {
                        findings.Add($"unsatisfied-target-fact:{requiredFact}");
                    }
                }
            }
            catch (MachineDefinitionException)
            {
                findings.Add("missing-or-invalid-definition-evidence");
            }
        }

        if (await db.InboundMessages.AsNoTracking().AnyAsync(item =>
                item.InstanceId == instance.Id && item.Status == "Received", cancellationToken))
        {
            outstanding.Add("pending-durable-intake");
        }
        if (await db.StagedObservations.AsNoTracking().AnyAsync(item =>
                item.InstanceId == instance.Id && item.Status == "Received", cancellationToken))
        {
            outstanding.Add("unresolved-staged-external-result");
        }
        if (await db.ExpectedOperations.AsNoTracking().AnyAsync(item =>
                item.InstanceId == instance.Id && item.Status == "Expected", cancellationToken))
        {
            outstanding.Add("pending-expected-operation");
        }
        if (await db.EffectOperations.AsNoTracking().AnyAsync(item => item.InstanceId == instance.Id, cancellationToken))
        {
            outstanding.Add("existing-external-effect-operation");
        }
        if (await db.Timers.AsNoTracking().AnyAsync(item =>
                item.InstanceId == instance.Id && item.Status == "Pending", cancellationToken))
        {
            outstanding.Add("pending-timer-with-definition-specific-semantics");
        }
        findings.AddRange(outstanding);

        return new MigrationEligibility(
            instance.Id,
            findings.Count == 0 ? "Eligible" : "Refused",
            plan?.Id,
            plan?.Version,
            request.SourceDefinitionId,
            request.SourceDefinitionVersion,
            request.TargetDefinitionId,
            request.TargetDefinitionVersion,
            instance.CurrentState,
            targetState,
            instance.Revision,
            findings,
            outstanding);
    }

    private static bool SameTransitionSemantics(
        MachineTransition source,
        MachineTransition? target,
        DefinitionMigrationPlan plan)
    {
        if (target is null || source.Id != target.Id || source.Event != target.Event)
        {
            return false;
        }
        var mappedTarget = plan.StateCorrespondences.SingleOrDefault(
            item => item.SourceState == source.Target)?.TargetState;
        if (mappedTarget is null || mappedTarget != target.Target)
        {
            return false;
        }
        var sourceGuard = source.Guard is null ? null :
            JsonSerializer.Serialize(source.Guard, source.Guard.GetType(), StoredJsonOptions);
        var targetGuard = target.Guard is null ? null :
            JsonSerializer.Serialize(target.Guard, target.Guard.GetType(), StoredJsonOptions);
        return sourceGuard == targetGuard;
    }

    private static MigrationRecord CreateMigrationRecord(
        InstanceRecord instance,
        MigrationRequest request,
        DefinitionMigrationPlan? plan,
        MigrationEligibility eligibility,
        DateTimeOffset now)
    {
        return new MigrationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceId = instance.Id,
            MigrationId = request.MigrationId,
            PlanId = plan?.Id ?? "unsupported",
            PlanVersion = plan?.Version ?? "0",
            SourceDefinitionId = request.SourceDefinitionId,
            SourceDefinitionVersion = request.SourceDefinitionVersion,
            SourceDefinitionDigest = string.Empty,
            TargetDefinitionId = request.TargetDefinitionId,
            TargetDefinitionVersion = request.TargetDefinitionVersion,
            TargetDefinitionDigest = string.Empty,
            SourceState = instance.CurrentState,
            TargetState = eligibility.TargetState ?? instance.CurrentState,
            ExpectedRevision = request.ExpectedRevision,
            SourceRevision = instance.Revision,
            TargetRevision = instance.Revision,
            AuthorisationContext = "configured-controlled-administration",
            AuthorisationReason = request.AuthorisationReason,
            EligibilityFindingsJson = JsonSerializer.Serialize(eligibility.Findings, StoredJsonOptions),
            OutstandingWorkFindingsJson = JsonSerializer.Serialize(eligibility.OutstandingWorkFindings, StoredJsonOptions),
            AmendmentReferencesJson = JsonSerializer.Serialize(plan?.AmendmentReferences ?? [], StoredJsonOptions),
            Status = "Refused",
            Outcome = eligibility.Findings.FirstOrDefault() ?? "migration-not-eligible",
            Explanation = string.Join("; ", eligibility.Findings),
            CommittedAt = now
        };
    }

    private static string ComputeDefinitionDigest(string definitionJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson)));

    public async Task<IReadOnlyList<TimerRecord>> ListTimersAsync(string instanceId, CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.Timers.AsNoTracking().Where(timer => timer.InstanceId == instanceId)
            .OrderBy(timer => timer.DueAt).ThenBy(timer => timer.Id).ToListAsync(cancellationToken);
    }

    public async Task<TimerRecord> GetTimerAsync(
        string instanceId,
        string timerId,
        CancellationToken cancellationToken) =>
        await db.Timers.AsNoTracking().SingleOrDefaultAsync(
            timer => timer.InstanceId == instanceId && timer.Id == timerId, cancellationToken)
        ?? throw new ResourceNotFoundException($"Timer '{timerId}' does not exist for instance '{instanceId}'.");

    public async Task<IReadOnlyList<TimerProcessingResult>> ProcessDueTimersAsync(
        DateTimeOffset now,
        int limit,
        Func<string, string, string, EffectHarnessBinding>? resolveEffectBinding,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Timer processing limit must be between 1 and 500.");
        }
        now = now.ToUniversalTime();
        var timerIds = await db.Timers.AsNoTracking()
            .Where(timer => timer.Status == "Pending" && timer.DueAt <= now)
            .OrderBy(timer => timer.DueAt).ThenBy(timer => timer.Id)
            .Select(timer => new { timer.Id, timer.InstanceId })
            .Take(limit)
            .ToListAsync(cancellationToken);
        var results = new List<TimerProcessingResult>(timerIds.Count);
        foreach (var candidate in timerIds)
        {
            results.Add(await ProcessTimerAsync(candidate.InstanceId, candidate.Id, now,
                resolveEffectBinding, cancellationToken));
        }
        return results;
    }

    private async Task<TimerProcessingResult> ProcessTimerAsync(
        string instanceId,
        string timerId,
        DateTimeOffset now,
        Func<string, string, string, EffectHarnessBinding>? resolveEffectBinding,
        CancellationToken cancellationToken)
    {
        InstanceGate gate;
        lock (InstanceLocksSync)
        {
            if (!InstanceLocks.TryGetValue(instanceId, out gate!))
            {
                gate = new InstanceGate();
                InstanceLocks.Add(instanceId, gate);
            }
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            await using var transaction = await BeginImmediateTransactionAsync(cancellationToken);
            var timer = await db.Timers.SingleOrDefaultAsync(
                record => record.Id == timerId && record.InstanceId == instanceId, cancellationToken)
                ?? throw new ResourceNotFoundException($"Timer '{timerId}' does not exist for instance '{instanceId}'.");
            if (timer.Status != "Pending")
            {
                await transaction.CommitAsync(cancellationToken);
                return new TimerProcessingResult(timer, null, AlreadyProcessed: true);
            }
            if (timer.DueAt > now)
            {
                await transaction.CommitAsync(cancellationToken);
                return new TimerProcessingResult(timer, null, AlreadyProcessed: false);
            }

            var instance = await db.Instances.SingleOrDefaultAsync(
                record => record.Id == instanceId, cancellationToken)
                ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
            var definitionRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
                record => record.Id == timer.DefinitionId && record.Version == timer.DefinitionVersion,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    $"The registered definition for timer '{timerId}' is missing.");
            var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
            var declaration = definition.Timers.SingleOrDefault(item => item.Id == timer.TimerDeclarationId);
            var processingEvidence = new
            {
                timerId = timer.Id,
                currentTime = now,
                dueAt = timer.DueAt,
                currentRevision = instance.Revision,
                currentState = instance.CurrentState,
                definitionId = instance.DefinitionId,
                definitionVersion = instance.DefinitionVersion
            };

            if (instance.DefinitionId != timer.DefinitionId ||
                instance.DefinitionVersion != timer.DefinitionVersion ||
                declaration is null)
            {
                CompleteTimer(timer, "Stale", "definition-binding-changed", now, processingEvidence);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new TimerProcessingResult(timer, null, AlreadyProcessed: false);
            }

            var supersedingTimer = declaration.InvalidatedByDueTimers.Count == 0
                ? null
                : await db.Timers.AsNoTracking()
                    .Where(candidate =>
                        candidate.InstanceId == instanceId &&
                        candidate.Status == "Pending" &&
                        candidate.DueAt <= now &&
                        declaration.InvalidatedByDueTimers.Contains(candidate.TimerDeclarationId))
                    .OrderBy(candidate => candidate.DueAt).ThenBy(candidate => candidate.Id)
                    .FirstOrDefaultAsync(cancellationToken);
            if (supersedingTimer is not null)
            {
                CompleteTimer(timer, "Stale",
                    $"superseded-by-due-timer:{supersedingTimer.TimerDeclarationId}",
                    now, processingEvidence);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new TimerProcessingResult(timer, null, AlreadyProcessed: false);
            }

            if (instance.CurrentState != declaration.WhileState)
            {
                CompleteTimer(timer, "Stale", "originating-state-exited", now, processingEvidence);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new TimerProcessingResult(timer, null, AlreadyProcessed: false);
            }

            var submission = await EvaluateEventWithinTransactionAsync(
                instance, declaration.Event, now, cancellationToken,
                attemptFacts: null, decisionEvidence: null, attemptFactProvenance: null);
            timer.AttemptId = submission.Attempt.Id;
            timer.ResultTransitionId = submission.Transition?.Id;
            timer.ProcessedAt = now;
            timer.ResultReason = submission.Attempt.ReasonCode;
            timer.ProcessingEvidenceJson = JsonSerializer.Serialize(new
            {
                processingEvidence,
                outcome = submission.Attempt.Status,
                reason = submission.Attempt.ReasonCode,
                attemptId = submission.Attempt.Id,
                transitionHistoryId = submission.Transition?.Id,
                resultingRevision = submission.Attempt.RevisionAfter
            }, StoredJsonOptions);
            if (submission.Attempt.Status == EvaluationStatus.Accepted.ToString())
            {
                timer.Status = "Fired";
                if (declaration.EffectIntent is not null)
                {
                    if (resolveEffectBinding is null)
                    {
                        throw new InvalidOperationException(
                            $"Timer '{timerId}' requires a configured harness binding for capability '{declaration.EffectIntent.Capability}'.");
                    }
                    var operationId = "timer-" + timer.Id;
                    var binding = resolveEffectBinding(instanceId, operationId, declaration.EffectIntent.Capability);
                    timer.ActionOperationId = CreateTimerEffectIntent(
                        instance, definition, submission.Attempt.RevisionAfter, declaration, operationId, binding, now);
                }
            }
            else
            {
                timer.Status = submission.Attempt.Status == EvaluationStatus.Rejected.ToString()
                    ? "Refused"
                    : "Unresolved";
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TimerProcessingResult(timer,
                submission with { Instance = await ReadInstanceAsync(instanceId, cancellationToken) },
                AlreadyProcessed: false);
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            lock (InstanceLocksSync)
            {
                gate.Users--;
                if (gate.Users == 0)
                {
                    InstanceLocks.Remove(instanceId);
                }
            }
        }
    }

    public async Task<bool> IsInputProtectedInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
        var definition = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion, cancellationToken)
            ?? throw new InvalidOperationException($"The registered definition for instance '{instanceId}' is missing.");
        var parsed = MachineDefinitionLoader.Load(definition.DefinitionJson);
        return parsed.InputPolicy is not null || parsed.Timers.Count != 0;
    }

    public async Task<EffectIntentResult> CreateEffectIntentAsync(
        string instanceId,
        string operationId,
        string capability,
        string providerIdentity,
        string observationType,
        string requestJson,
        string requestDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        EffectHarnessBinding? harnessBinding = null)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(capability) ||
            string.IsNullOrWhiteSpace(providerIdentity) || string.IsNullOrWhiteSpace(observationType))
        {
            throw new ArgumentException("Operation, capability, provider and observation identities are required.");
        }

        InstanceGate gate;
        lock (InstanceLocksSync)
        {
            if (!InstanceLocks.TryGetValue(instanceId, out gate!))
            {
                gate = new InstanceGate();
                InstanceLocks.Add(instanceId, gate);
            }
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            var transaction = await BeginImmediateTransactionAsync(cancellationToken);
            var existingEffect = await db.EffectOperations.AsNoTracking().SingleOrDefaultAsync(
                record => record.InstanceId == instanceId && record.OperationId == operationId, cancellationToken);
            if (existingEffect is not null)
            {
                if (existingEffect.Capability != capability ||
                    !string.Equals(existingEffect.RequestDigest, requestDigest, StringComparison.Ordinal))
                {
                    throw new DefinitionConflictException(
                        $"Operation '{operationId}' was already requested with different effect data.");
                }
                return new EffectIntentResult(existingEffect, AlreadyRequested: true);
            }

            var instance = await db.Instances.SingleOrDefaultAsync(record => record.Id == instanceId, cancellationToken)
                ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
            var definitionRecord = await db.Definitions.AsNoTracking().SingleAsync(
                record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion,
                cancellationToken);
            var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
            if (definition.States[instance.CurrentState].IsTerminal)
            {
                throw new ArgumentException("An external effect cannot be requested for a terminal instance.");
            }
            if (definition.InputPolicy is null ||
                !definition.InputPolicy.Observations.TryGetValue(observationType, out var mapping))
            {
                throw new ArgumentException(
                    $"Observation '{observationType}' is not declared by definition '{instance.DefinitionId}@{instance.DefinitionVersion}'.");
            }
            if (await db.ExpectedOperations.AsNoTracking().AnyAsync(
                    record => record.InstanceId == instanceId && record.OperationId == operationId, cancellationToken))
            {
                throw new DefinitionConflictException(
                    $"An expected operation with ID '{operationId}' already exists for instance '{instanceId}'.");
            }

            var requiredEvidenceJson = JsonSerializer.Serialize(
                mapping.RequiredEvidence.Order(StringComparer.Ordinal).ToArray(), StoredJsonOptions);
            var expected = new ExpectedOperationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instanceId,
                OperationId = operationId,
                ObservationType = observationType,
                SourceIdentity = providerIdentity,
                ExpectedAt = now,
                ExpectedRevision = instance.Revision,
                RequiredEvidenceJson = requiredEvidenceJson,
                Status = "Expected"
            };
            var effect = new EffectOperationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instanceId,
                OperationId = operationId,
                DefinitionId = instance.DefinitionId,
                DefinitionVersion = instance.DefinitionVersion,
                ExecutionRevision = instance.Revision,
                Capability = capability,
                ProviderIdentity = providerIdentity,
                ObservationType = observationType,
                RequestJson = requestJson,
                RequestDigest = requestDigest,
                Status = "Pending",
                HarnessProviderId = harnessBinding?.ProviderId,
                CredentialBindingId = harnessBinding?.CredentialBindingId,
                ApiDescriptionVersionId = harnessBinding?.ApiDescriptionVersionId,
                HarnessOperationKey = harnessBinding?.OperationKey,
                HarnessIdempotencyKey = harnessBinding?.IdempotencyKey,
                OperationReplaySafety = harnessBinding?.OperationReplaySafety,
                RequestedAt = now,
                UpdatedAt = now
            };
            db.ExpectedOperations.Add(expected);
            db.EffectOperations.Add(effect);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EffectIntentResult(effect, AlreadyRequested: false);
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            lock (InstanceLocksSync)
            {
                gate.Users--;
                if (gate.Users == 0)
                {
                    InstanceLocks.Remove(instanceId);
                }
            }
        }
    }

    public async Task<IReadOnlyList<EffectOperationRecord>> ListEffectOperationsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.EffectOperations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.RequestedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<EffectOperationRecord> GetEffectOperationAsync(
        string instanceId,
        string operationId,
        CancellationToken cancellationToken) =>
        await db.EffectOperations.AsNoTracking().SingleOrDefaultAsync(
            record => record.InstanceId == instanceId && record.OperationId == operationId, cancellationToken)
        ?? throw new ResourceNotFoundException(
            $"Effect operation '{operationId}' does not exist for instance '{instanceId}'.");

    public async Task<IReadOnlyList<EffectAttemptRecord>> ListEffectAttemptsAsync(
        string effectOperationId,
        CancellationToken cancellationToken) =>
        await db.EffectAttempts.AsNoTracking()
            .Where(record => record.EffectOperationId == effectOperationId)
            .OrderBy(record => record.AttemptNumber)
            .ToListAsync(cancellationToken);

    public async Task<ExpectedOperationRecord> RegisterExpectedOperationAsync(
        string instanceId,
        string operationId,
        string observationType,
        string sourceIdentity,
        long? expectedRevision,
        IReadOnlyList<string>? additionalRequiredEvidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(sourceIdentity))
        {
            throw new ArgumentException("Operation ID and source identity are required.");
        }
        if (additionalRequiredEvidence?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new ArgumentException("Required evidence names must not be empty.");
        }

        InstanceGate gate;
        lock (InstanceLocksSync)
        {
            if (!InstanceLocks.TryGetValue(instanceId, out gate!))
            {
                gate = new InstanceGate();
                InstanceLocks.Add(instanceId, gate);
            }
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            await using var transaction = await BeginImmediateTransactionAsync(cancellationToken);
            var instance = await db.Instances.SingleOrDefaultAsync(record => record.Id == instanceId, cancellationToken)
                ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
            if (expectedRevision.HasValue && expectedRevision.Value != instance.Revision)
            {
                throw new ArgumentException("Expected operation revision must match the current instance revision.");
            }
            var definitionRecord = await db.Definitions.AsNoTracking().SingleAsync(
                record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion, cancellationToken);
            var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
            if (definition.States[instance.CurrentState].IsTerminal)
            {
                throw new ArgumentException("Expected operations cannot be registered for a terminal instance.");
            }
            if (definition.InputPolicy is null ||
                !definition.InputPolicy.Observations.TryGetValue(observationType, out var mapping))
            {
                throw new ArgumentException($"Observation type '{observationType}' is not declared for this instance.");
            }
            var requiredEvidence = mapping.RequiredEvidence
                .Concat(additionalRequiredEvidence ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var operation = new ExpectedOperationRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instanceId,
                OperationId = operationId,
                ObservationType = observationType,
                SourceIdentity = sourceIdentity,
                ExpectedAt = now,
                ExpectedRevision = instance.Revision,
                RequiredEvidenceJson = JsonSerializer.Serialize(requiredEvidence, StoredJsonOptions),
                Status = "Expected"
            };
            db.ExpectedOperations.Add(operation);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return operation;
        }
        catch (DbUpdateException)
        {
            if (db.ChangeTracker.Entries<ExpectedOperationRecord>().Any(entry => entry.State == EntityState.Added))
            {
                foreach (var entry in db.ChangeTracker.Entries<ExpectedOperationRecord>()
                    .Where(entry => entry.State == EntityState.Added))
                {
                    entry.State = EntityState.Detached;
                }
                if (await db.ExpectedOperations.AsNoTracking().AnyAsync(
                    record => record.InstanceId == instanceId && record.OperationId == operationId, cancellationToken))
                {
                    throw new DefinitionConflictException($"Operation '{operationId}' is already expected for instance '{instanceId}'.");
                }
            }
            throw;
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            lock (InstanceLocksSync)
            {
                gate.Users--;
                if (gate.Users == 0)
                {
                    InstanceLocks.Remove(instanceId);
                }
            }
        }
    }

    public async Task<IReadOnlyList<ExpectedOperationRecord>> ListExpectedOperationsAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.ExpectedOperations.AsNoTracking()
            .Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.OperationId)
            .ToListAsync(cancellationToken);
    }

    public async Task<InboundProcessingResult> SubmitInputAsync(
        string instanceId,
        string inputKind,
        string messageId,
        string? deliveryId,
        string? operationId,
        string? observationId,
        string inputType,
        long expectedRevision,
        string? correlationId,
        string? claimedSource,
        DateTimeOffset? occurredAt,
        IReadOnlyDictionary<string, JsonElement>? payload,
        IReadOnlyDictionary<string, FactValue>? facts,
        IReadOnlyDictionary<string, JsonElement>? evidence,
        ExternalDecisionEvidence? decisionEvidence,
        IReadOnlyDictionary<string, string>? factsProvenance,
        bool trustedSource,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? stagedResultId = null)
    {
        if (inputKind is not ("Command" or "Observation"))
        {
            throw new ArgumentException($"Unsupported input kind '{inputKind}'.");
        }
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 128 ||
            deliveryId?.Length > 128 || operationId?.Length > 128 || observationId?.Length > 128 ||
            string.IsNullOrWhiteSpace(inputType) || expectedRevision < 0)
        {
            throw new ArgumentException("Message ID, input type, and a non-negative expected revision are required.");
        }
        if (inputKind == "Observation" && string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Observations require an operation correlation ID.");
        }

        InstanceGate gate;
        lock (InstanceLocksSync)
        {
            if (!InstanceLocks.TryGetValue(instanceId, out gate!))
            {
                gate = new InstanceGate();
                InstanceLocks.Add(instanceId, gate);
            }
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            await using var transaction = await BeginImmediateTransactionAsync(cancellationToken);
            var instance = await db.Instances.SingleOrDefaultAsync(record => record.Id == instanceId, cancellationToken)
                ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
            var definitionRecord = await db.Definitions.AsNoTracking().SingleAsync(
                record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion, cancellationToken);
            var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
            var payloadJson = SerializeJsonObject(payload);
            var evidenceJson = SerializeJsonObject(evidence);
            var factsWire = (facts ?? new Dictionary<string, FactValue>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => FactWire.From(pair.Value), StringComparer.Ordinal);
            var inputEnvelopeJson = CanonicalizeJson(JsonSerializer.Serialize(new
            {
                inputKind,
                messageId,
                operationId,
                observationId,
                inputType,
                correlationId,
                claimedSource,
                expectedRevision,
                payload = ReadJsonElement(payloadJson),
                facts = factsWire,
                evidence = ReadJsonElement(evidenceJson),
                decisionEvidence,
                factsProvenance = factsProvenance?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                occurredAt
            }, StoredJsonOptions));
            var digest = ComputeDigest(inputEnvelopeJson);

            var priorMessage = await db.InboundMessages.AsNoTracking()
                .Where(record => record.InstanceId == instanceId && record.InputKind == inputKind && record.MessageId == messageId)
                .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id)
                .FirstOrDefaultAsync(cancellationToken);
            var message = new InboundMessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instanceId,
                DeliveryId = deliveryId,
                MessageId = messageId,
                OperationId = operationId,
                ObservationId = observationId,
                InputKind = inputKind,
                InputType = inputType,
                Authority = inputKind == "Command"
                    ? "declared-command-endpoint"
                    : trustedSource ? "trusted-ingestion-key" : "unverified-observation-endpoint",
                CorrelationId = correlationId,
                ClaimedSource = claimedSource,
                ExpectedRevision = expectedRevision,
                DefinitionId = instance.DefinitionId,
                DefinitionVersion = instance.DefinitionVersion,
                RevisionAtReceipt = instance.Revision,
                InputEnvelopeJson = inputEnvelopeJson,
                PayloadJson = payloadJson,
                EvidenceJson = evidenceJson,
                PayloadDigest = digest,
                Status = "Received",
                ReasonCode = "durable-intake",
                Explanation = "The immutable input envelope is durable and awaits eligibility evaluation.",
                ReceivedAt = now,
                OccurredAt = occurredAt
            };

            var intakeCommitted = false;
            if (priorMessage is not null &&
                priorMessage.PayloadDigest == digest &&
                priorMessage.Status == "Received")
            {
                message = await db.InboundMessages.SingleAsync(record => record.Id == priorMessage.Id, cancellationToken);
                trustedSource = message.Authority == "trusted-ingestion-key";
            }
            else if (priorMessage is not null)
            {
                message.Status = priorMessage.PayloadDigest == digest ? "Duplicate" : "IdentityConflict";
                message.ReasonCode = priorMessage.PayloadDigest == digest ? "duplicate-message" : "message-identity-conflict";
                message.Explanation = priorMessage.PayloadDigest == digest
                    ? "This message identity and payload were already received; no second evaluation was performed."
                    : "This message identity was already used with a different payload.";
                message.AttemptId = priorMessage.AttemptId;
                db.InboundMessages.Add(message);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                intakeCommitted = true;
                await transaction.DisposeAsync();
                return new InboundProcessingResult(message, null);
            }
            else
            {
                inputFaultPlan.ThrowIfArmed(instanceId, "BeforeIntakeCommit");
                db.InboundMessages.Add(message);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                intakeCommitted = true;
                await transaction.DisposeAsync();
            }
            if (!intakeCommitted)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
            inputFaultPlan.ThrowIfArmed(instanceId, "AfterIntakeCommitBeforeEvaluation");
            await inputFaultPlan.WaitIfPausedAsync(
                instanceId, "AfterIntakeCommitBeforeEvaluation", cancellationToken);
            await using var evaluationTransaction = await BeginImmediateTransactionAsync(cancellationToken);
            await db.Entry(message).ReloadAsync(cancellationToken);
            if (message.Status != "Received")
            {
                await evaluationTransaction.CommitAsync(cancellationToken);
                return new InboundProcessingResult(message, null);
            }
            await db.Entry(instance).ReloadAsync(cancellationToken);
            definitionRecord = await db.Definitions.AsNoTracking().SingleAsync(
                record => record.Id == message.DefinitionId && record.Version == message.DefinitionVersion, cancellationToken);
            definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
            message.Status = "Evaluating";
            message.ReasonCode = "input-not-processed";
            message.Explanation = "The durable input is undergoing authority and transition eligibility checks.";

            if (definition.InputPolicy is null)
            {
                message.ReasonCode = "input-policy-not-declared";
                message.Explanation = "This definition does not declare command or observation input mappings.";
            }
            else if (inputKind == "Command" && expectedRevision != instance.Revision)
            {
                message.Status = "Stale";
                message.ReasonCode = "stale-revision";
                message.Explanation = $"Input targets revision {expectedRevision}, but the current instance revision is {instance.Revision}.";
            }
            else if (inputKind == "Command" &&
                !definition.InputPolicy.Commands.TryGetValue(inputType, out _))
            {
                message.Status = "Unexpected";
                message.ReasonCode = "unexpected-command";
                message.Explanation = $"Command '{inputType}' is not declared by this machine definition.";
            }
            else if (inputKind == "Observation" &&
                !definition.InputPolicy.Observations.TryGetValue(inputType, out _))
            {
                message.Status = "Unexpected";
                message.ReasonCode = "unexpected-observation";
                message.Explanation = $"Observation '{inputType}' is not declared by this machine definition.";
            }
            else if (inputKind == "Observation" && !trustedSource)
            {
                message.Status = "Untrusted";
                message.ReasonCode = "untrusted-ingestion-boundary";
                message.Explanation = "The observation was received without the configured trusted ingestion credential.";
            }
            else
            {
                ExpectedOperationRecord? operation = null;
                ObservationInputMapping? observationMapping = null;
                if (inputKind == "Observation")
                {
                    observationMapping = definition.InputPolicy.Observations[inputType];
                    operation = await db.ExpectedOperations.SingleOrDefaultAsync(
                        record => record.InstanceId == instanceId && record.OperationId == correlationId, cancellationToken);
                    if (operation is null)
                    {
                        message.Status = "Unexpected";
                        message.ReasonCode = "unknown-operation";
                        message.Explanation = $"Operation '{correlationId}' is not expected by this instance.";
                    }
                    else if (operation.ObservationType != inputType)
                    {
                        message.Status = "Unexpected";
                        message.ReasonCode = "unexpected-observation-type";
                        message.Explanation = $"Operation '{correlationId}' expects '{operation.ObservationType}', not '{inputType}'.";
                    }
                    else if (!string.Equals(operation.SourceIdentity, claimedSource, StringComparison.Ordinal))
                    {
                        message.Status = "Untrusted";
                        message.ReasonCode = "source-identity-mismatch";
                        message.Explanation = "The claimed source does not match the source bound to the expected operation.";
                    }
                    else if (operation.Status == "Resolved")
                    {
                        var resultDigest = ComputeObservationResultDigest(inputType, correlationId!, payloadJson,
                            evidenceJson, factsWire, decisionEvidence, factsProvenance, occurredAt);
                        message.Status = operation.ResultDigest == resultDigest ? "Duplicate" : "Conflicting";
                        message.ReasonCode = operation.ResultDigest == resultDigest ? "duplicate-operation-result" : "conflicting-operation-result";
                        message.Explanation = operation.ResultDigest == resultDigest
                            ? "This external operation result was already accepted under another delivery identity."
                            : "A different result was already accepted for this external operation.";
                    }
                    else if (definition.States[instance.CurrentState].IsTerminal)
                    {
                        message.Status = "Stale";
                        message.ReasonCode = "terminal-instance";
                        message.Explanation = "The instance is terminal; a late observation cannot reactivate it.";
                    }
                    else if (operation.ExpectedRevision != instance.Revision || operation.ExpectedRevision != expectedRevision)
                    {
                        message.Status = "Stale";
                        message.ReasonCode = expectedRevision != instance.Revision
                            ? "stale-revision"
                            : "stale-operation-revision";
                        message.Explanation = $"Operation was expected at revision {operation.ExpectedRevision}; current/input revisions are {instance.Revision}/{expectedRevision}.";
                    }
                    else
                    {
                        var requiredEvidence = JsonSerializer.Deserialize<string[]>(operation.RequiredEvidenceJson, StoredJsonOptions) ?? [];
                        var missing = requiredEvidence.Where(name =>
                            evidence is null || !evidence.TryGetValue(name, out var value) ||
                            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined).ToArray();
                        if (missing.Length != 0)
                        {
                            message.Status = "Malformed";
                            message.ReasonCode = "missing-required-evidence";
                            message.Explanation = $"Required evidence is missing or null: {string.Join(", ", missing)}.";
                        }
                    }
                }

                if (message.ReasonCode == "input-not-processed")
                {
                    var (eventName, concernId) = inputKind == "Command"
                        ? (definition.InputPolicy!.Commands[inputType].Event,
                            definition.InputPolicy.Commands[inputType].Concern)
                        : (definition.InputPolicy!.Observations[inputType].Event,
                            definition.InputPolicy.Observations[inputType].Concern);
                    SubmissionResult? submission = null;
                    try
                    {
                        _ = TransitionEvaluationContext.Create(
                            DeserializeFacts(instance.FactsJson), facts, decisionEvidence, factsProvenance);
                    }
                    catch (ArgumentException exception)
                    {
                        message.Status = "Malformed";
                        message.ReasonCode = "invalid-evaluation-input";
                        message.Explanation = exception.Message;
                    }
                    if (message.ReasonCode == "input-not-processed")
                    {
                        inputFaultPlan.ThrowIfArmed(instanceId, "DuringGuardEvaluation");
                        submission = await EvaluateEventWithinTransactionAsync(
                            instance, eventName, now, cancellationToken, facts, decisionEvidence, factsProvenance,
                            concernId);
                    }
                    if (submission is not null)
                    {
                        message.AttemptId = submission.Attempt.Id;
                        if (submission.Attempt.Status == EvaluationStatus.Accepted.ToString())
                        {
                            inputFaultPlan.ThrowIfArmed(instanceId, "DuringProjectionUpdate");
                            message.Status = "Accepted";
                            message.ReasonCode = "input-accepted";
                            message.Explanation = "Input authority checks passed and the deterministic evaluator accepted the mapped event.";
                            message.AcceptedEvent = eventName;
                            if (operation is not null)
                            {
                                operation.Status = "Resolved";
                                operation.ResultDigest = ComputeObservationResultDigest(inputType, correlationId!, payloadJson,
                                    evidenceJson, factsWire, decisionEvidence, factsProvenance, occurredAt);
                                operation.ResolvedAt = now;
                            }
                        }
                        else
                        {
                            message.Status = submission.Attempt.Status;
                            message.ReasonCode = submission.Attempt.ReasonCode;
                            message.Explanation = $"Input checks passed, but the deterministic evaluator refused the mapped event: {submission.Attempt.Explanation}";
                        }
                    }
                    if (stagedResultId is not null)
                    {
                        await FinalizeStagedObservationWithinTransactionAsync(
                            stagedResultId, message, submission, now, cancellationToken);
                        inputFaultPlan.ThrowIfArmed(instanceId, "DuringStagedObservationFinalization");
                    }
                    inputFaultPlan.ThrowIfArmed(instanceId, "BeforeFinalPersistence");
                    await inputFaultPlan.WaitIfPausedAsync(
                        instanceId, "BeforeFinalPersistence", cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    await evaluationTransaction.CommitAsync(cancellationToken);
                    if (stagedResultId is not null)
                    {
                        inputFaultPlan.ThrowIfArmed(instanceId, "AfterStagedObservationCommit");
                        await inputFaultPlan.WaitIfPausedAsync(
                            instanceId, "AfterStagedObservationCommit", cancellationToken);
                    }
                    var currentSubmission = submission is null
                        ? null
                        : submission with { Instance = await ReadInstanceAsync(instanceId, cancellationToken) };
                    return new InboundProcessingResult(message, currentSubmission);
                }
            }

            if (stagedResultId is not null)
            {
                await FinalizeStagedObservationWithinTransactionAsync(
                    stagedResultId, message, submission: null, now, cancellationToken);
                inputFaultPlan.ThrowIfArmed(instanceId, "DuringStagedObservationFinalization");
            }
            inputFaultPlan.ThrowIfArmed(instanceId, "BeforeFinalPersistence");
            await inputFaultPlan.WaitIfPausedAsync(
                instanceId, "BeforeFinalPersistence", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await evaluationTransaction.CommitAsync(cancellationToken);
            if (stagedResultId is not null)
            {
                inputFaultPlan.ThrowIfArmed(instanceId, "AfterStagedObservationCommit");
                await inputFaultPlan.WaitIfPausedAsync(
                    instanceId, "AfterStagedObservationCommit", cancellationToken);
            }
            return new InboundProcessingResult(message, null);
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            lock (InstanceLocksSync)
            {
                gate.Users--;
                if (gate.Users == 0)
                {
                    InstanceLocks.Remove(instanceId);
                }
            }
        }
    }

    private async Task FinalizeStagedObservationWithinTransactionAsync(
        string stagedResultId,
        InboundMessageRecord message,
        SubmissionResult? submission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var staged = await db.StagedObservations.SingleAsync(
            record => record.Id == stagedResultId && record.InstanceId == message.InstanceId,
            cancellationToken);
        if (staged.Status != "Received" ||
            staged.SourceAuthority != "verified-harness-adapter" ||
            ComputeStagedObservationDigest(staged) != staged.PayloadDigest)
        {
            throw new DefinitionConflictException(
                $"Staged result '{stagedResultId}' is no longer eligible for finalization.");
        }

        staged.Status = message.Status == "Accepted" ? "Accepted" : "Refused";
        staged.ReceiptId = message.Id;
        staged.AttemptId = submission?.Attempt.Id ?? message.AttemptId;
        staged.Outcome = message.Status;
        staged.Explanation = message.Explanation;
        staged.ResolvedAt = now;

        var effect = await db.EffectOperations.SingleAsync(
            record => record.InstanceId == staged.InstanceId &&
                record.OperationId == staged.WorkflowOperationId,
            cancellationToken);
        var effectAttempt = await db.EffectAttempts
            .Where(record => record.EffectOperationId == effect.Id &&
                record.HarnessExecutionId == staged.HarnessExecutionId)
            .OrderByDescending(record => record.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        effect.Status = message.Status == "Accepted"
            ? "ObservationAccepted"
            : "ObservationRefused";
        effect.UpdatedAt = now;
        if (effectAttempt is not null)
        {
            effectAttempt.ObservationStatus = message.Status;
            effectAttempt.ObservationReceiptId = message.Id;
            effectAttempt.WorkflowTransitionId = submission?.Attempt.TransitionId;
            effectAttempt.WorkflowStateBefore = submission?.Attempt.PreviousState;
            effectAttempt.WorkflowStateAfter = submission?.Attempt.TargetState;
            effectAttempt.WorkflowRevisionAfter = submission?.Attempt.RevisionAfter;
        }
    }

    public async Task<IReadOnlyList<InboundMessageRecord>> ListInboundMessagesAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.InboundMessages.AsNoTracking().Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id).ToListAsync(cancellationToken);
    }

    public async Task<InboundProcessingResult> ResumeInboundMessageAsync(
        string instanceId,
        string receiptId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var message = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == receiptId && record.InstanceId == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Inbound message '{receiptId}' does not exist for instance '{instanceId}'.");
        if (message.Status != "Received")
        {
            throw new DefinitionConflictException(
                $"Inbound message '{receiptId}' is not unresolved durable intake (status '{message.Status}').");
        }
        if (message.Authority is not ("declared-command-endpoint" or "trusted-ingestion-key"))
        {
            throw new DefinitionConflictException(
                "The original input did not cross an acceptable application-controlled authority boundary.");
        }

        var envelope = JsonSerializer.Deserialize<StoredInboundEnvelope>(message.InputEnvelopeJson, StoredJsonOptions)
            ?? throw new InvalidDataException($"Inbound message '{receiptId}' has no readable immutable request envelope.");
        if (ComputeDigest(message.InputEnvelopeJson) != message.PayloadDigest)
        {
            throw new InvalidDataException($"Inbound message '{receiptId}' failed its durable envelope digest check.");
        }

        var facts = envelope.Facts.ToDictionary(pair => pair.Key, pair => pair.Value.ToValue(), StringComparer.Ordinal);
        return await SubmitInputAsync(
            instanceId,
            message.InputKind,
            message.MessageId,
            message.DeliveryId,
            message.OperationId,
            message.ObservationId,
            message.InputType,
            envelope.ExpectedRevision,
            envelope.CorrelationId,
            envelope.ClaimedSource,
            envelope.OccurredAt,
            envelope.Payload,
            facts,
            envelope.Evidence,
            envelope.DecisionEvidence,
            envelope.FactsProvenance,
            message.Authority == "trusted-ingestion-key",
            now,
            cancellationToken);
    }

    public async Task<InboundMessageRecord> RecordMalformedInputAsync(
        string instanceId,
        string inputKind,
        string? messageId,
        string? deliveryId,
        string? operationId,
        string? observationId,
        string? inputType,
        long? expectedRevision,
        string? correlationId,
        string? claimedSource,
        DateTimeOffset? occurredAt,
        string requestEnvelopeJson,
        bool trustedSource,
        string explanation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
        var envelope = CanonicalizeJson(requestEnvelopeJson);
        var message = new InboundMessageRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceId = instanceId,
            DeliveryId = deliveryId,
            MessageId = string.IsNullOrWhiteSpace(messageId) ? $"invalid-{Guid.NewGuid():N}" : messageId,
            OperationId = operationId,
            ObservationId = observationId,
            InputKind = inputKind,
            InputType = string.IsNullOrWhiteSpace(inputType) ? "Unspecified" : inputType,
            Authority = inputKind == "Command"
                ? "declared-command-endpoint"
                : trustedSource ? "trusted-ingestion-key" : "unverified-observation-endpoint",
            CorrelationId = correlationId,
            ClaimedSource = claimedSource,
            ExpectedRevision = expectedRevision,
            DefinitionId = instance.DefinitionId,
            DefinitionVersion = instance.DefinitionVersion,
            RevisionAtReceipt = instance.Revision,
            InputEnvelopeJson = envelope,
            PayloadJson = envelope,
            EvidenceJson = "{}",
            PayloadDigest = ComputeDigest(envelope),
            Status = "Malformed",
            ReasonCode = "malformed-input",
            Explanation = explanation,
            ReceivedAt = now,
            OccurredAt = occurredAt
        };
        db.InboundMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task<InboundMessageRecord> GetInboundMessageAsync(
        string instanceId,
        string receiptId,
        CancellationToken cancellationToken) =>
        await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == receiptId && record.InstanceId == instanceId, cancellationToken)
        ?? throw new ResourceNotFoundException($"Inbound message '{receiptId}' does not exist for instance '{instanceId}'.");

    public async Task<InboundMessageRecord> RecordLegacyEventRejectionAsync(
        string instanceId,
        string eventName,
        string requestJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instanceId, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
        var message = new InboundMessageRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceId = instanceId,
            MessageId = Guid.NewGuid().ToString("N"),
            InputKind = "LegacyEvent",
            InputType = eventName,
            Authority = "restricted-legacy-event-endpoint",
            DefinitionId = instance.DefinitionId,
            DefinitionVersion = instance.DefinitionVersion,
            RevisionAtReceipt = instance.Revision,
            InputEnvelopeJson = CanonicalizeJson(requestJson),
            PayloadJson = CanonicalizeJson(requestJson),
            EvidenceJson = "{}",
            PayloadDigest = ComputeDigest(requestJson),
            Status = "Untrusted",
            ReasonCode = "legacy-event-endpoint-restricted",
            Explanation = "Direct event submission is disabled for definitions with an input policy.",
            ReceivedAt = now
        };
        db.InboundMessages.Add(message);
        await db.SaveChangesAsync(cancellationToken);
        return message;
    }

    public async Task<SubmissionResult> SubmitEventAsync(
        string instanceId,
        string eventName,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, FactValue>? attemptFacts = null,
        ExternalDecisionEvidence? decisionEvidence = null,
        IReadOnlyDictionary<string, string>? attemptFactProvenance = null)
    {
        InstanceGate gate;
        lock (InstanceLocksSync)
        {
            if (!InstanceLocks.TryGetValue(instanceId, out gate!))
            {
                gate = new InstanceGate();
                InstanceLocks.Add(instanceId, gate);
            }
            gate.Users++;
        }
        var entered = false;
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            entered = true;
            await using var transaction = await BeginImmediateTransactionAsync(cancellationToken);
            var instance = await db.Instances.SingleOrDefaultAsync(record => record.Id == instanceId, cancellationToken)
                ?? throw new ResourceNotFoundException($"Instance '{instanceId}' does not exist.");
            var result = await EvaluateEventWithinTransactionAsync(instance, eventName, now, cancellationToken,
                attemptFacts, decisionEvidence, attemptFactProvenance);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result with { Instance = await ReadInstanceAsync(instanceId, cancellationToken) };
        }
        finally
        {
            if (entered)
            {
                gate.Semaphore.Release();
            }
            lock (InstanceLocksSync)
            {
                gate.Users--;
                if (gate.Users == 0)
                {
                    InstanceLocks.Remove(instanceId);
                }
            }
        }
    }

    private async Task<SubmissionResult> EvaluateEventWithinTransactionAsync(
        InstanceRecord instance,
        string eventName,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, FactValue>? attemptFacts,
        ExternalDecisionEvidence? decisionEvidence,
        IReadOnlyDictionary<string, string>? attemptFactProvenance,
        string? concernId = null)
    {
        var definitionRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion, cancellationToken)
            ?? throw new InvalidOperationException($"The registered definition for instance '{instance.Id}' is missing.");
        var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
        var facts = DeserializeFacts(instance.FactsJson);
        var concernRows = await LoadConcernStateVectorAsync(instance, definition, cancellationToken);
        var concernStates = concernRows.ToDictionary(row => row.ConcernId, row => row.CurrentState, StringComparer.Ordinal);
        var context = TransitionEvaluationContext.Create(
            facts, attemptFacts, decisionEvidence, attemptFactProvenance, concernStates, instance.CurrentState);
        ConcernStateRecord? concernRow = null;
        TransitionEvaluation evaluation;
        TimerRecord? blockingTimer = null;
        if (concernId is not null)
        {
            if (!definition.Concerns.TryGetValue(concernId, out var concern))
            {
                throw new InvalidOperationException($"Input targets undeclared concern '{concernId}'.");
            }
            concernRow = concernRows.Single(row => row.ConcernId == concernId);
            evaluation = definition.States[instance.CurrentState].IsTerminal
                ? new TransitionEvaluation(EvaluationStatus.Rejected, concernRow.CurrentState, eventName,
                    null, null, [], "terminal-parent-state",
                    $"Terminal parent state '{instance.CurrentState}' cannot accept concern activity.")
                : TransitionEvaluator.EvaluateConcern(concern, concernRow.CurrentState, eventName, context);
        }
        else
        {
            blockingTimer = await FindBlockingTimerAsync(instance.Id, definition, eventName, now, cancellationToken);
            evaluation = blockingTimer is null
                ? TransitionEvaluator.Evaluate(definition, instance.CurrentState, eventName, context)
                : new TransitionEvaluation(
                    EvaluationStatus.Rejected,
                    instance.CurrentState,
                    eventName,
                    null,
                    null,
                    [],
                    "deadline-expired",
                    $"Event '{eventName}' was received at or after timer '{blockingTimer.TimerDeclarationId}' became due at {blockingTimer.DueAt:O}.");
        }
        var cancellationTransitions = new List<(ConcernStateRecord Record, TransitionEvaluation Evaluation)>();
        if (concernId is null && evaluation.Status == EvaluationStatus.Accepted &&
            definition.CancellationEvent == eventName)
        {
            foreach (var row in concernRows.Where(row => !definition.Concerns[row.ConcernId].States[row.CurrentState].IsTerminal))
            {
                var concernEvaluation = TransitionEvaluator.EvaluateConcern(
                    definition.Concerns[row.ConcernId], row.CurrentState, eventName, context);
                if (concernEvaluation.Status != EvaluationStatus.Accepted)
                {
                    evaluation = new TransitionEvaluation(
                        EvaluationStatus.Rejected,
                        instance.CurrentState,
                        eventName,
                        null,
                        null,
                        concernEvaluation.GuardFindings,
                        "cancellation-concern-refused",
                        $"Cancellation was refused because concern '{row.ConcernId}' could not terminate: {concernEvaluation.Explanation}");
                    cancellationTransitions.Clear();
                    break;
                }
                cancellationTransitions.Add((row, concernEvaluation));
            }
        }

        var attemptId = Guid.NewGuid().ToString("N");
        var revisionBefore = instance.Revision;
        var revisionAfter = evaluation.Status == EvaluationStatus.Accepted && concernId is null
            ? checked(revisionBefore + 1)
            : revisionBefore;
        var previousState = concernRow?.CurrentState ?? instance.CurrentState;
        var resultState = evaluation.Status == EvaluationStatus.Accepted
            ? evaluation.TargetState!
            : previousState;
        var attempt = CreateAttempt(
            attemptId,
            instance.Id,
            eventName,
            now,
            evaluation,
            context,
            instance.DefinitionId,
            instance.DefinitionVersion,
            revisionBefore,
            revisionAfter,
            resultState,
            evaluation.Status == EvaluationStatus.Accepted ? "Committed" : "NoChange");
        if (concernId is not null)
        {
            attempt.Dimension = "Concern";
            attempt.ConcernId = concernId;
        }

        if (evaluation.Status == EvaluationStatus.Accepted && concernId is null)
        {
            var updated = await db.Instances
                .Where(record => record.Id == instance.Id && record.Revision == revisionBefore)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(record => record.CurrentState, evaluation.TargetState!)
                    .SetProperty(record => record.Revision, revisionAfter)
                    .SetProperty(record => record.UpdatedAt, now), cancellationToken);
            if (updated != 1)
            {
                attempt.Status = "Conflict";
                attempt.TargetState = null;
                attempt.RevisionAfter = revisionBefore;
                attempt.ReasonCode = "concurrency-conflict";
                attempt.Explanation = "The instance changed while this event was being evaluated.";
                SetEvidence(attempt, CreateEvidence(
                    instance.DefinitionId, instance.DefinitionVersion, instance.Id, revisionBefore,
                    eventName, now, context, evaluation, instance.CurrentState, "ConcurrencyConflict",
                    "concurrency-conflict"));
                db.InputAttempts.Add(attempt);
                await db.SaveChangesAsync(cancellationToken);
                return new SubmissionResult(attempt, await ReadInstanceAsync(instance.Id, cancellationToken), null, true);
            }
        }
        else if (evaluation.Status == EvaluationStatus.Accepted && concernId is not null)
        {
            var concernRevisionBefore = concernRow!.Revision;
            var updated = await db.ConcernStates
                .Where(row => row.InstanceId == instance.Id && row.ConcernId == concernId &&
                              row.Revision == concernRevisionBefore && row.CurrentState == concernRow.CurrentState)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.CurrentState, evaluation.TargetState!)
                    .SetProperty(row => row.Revision, checked(concernRevisionBefore + 1))
                    .SetProperty(row => row.UpdatedAt, now), cancellationToken);
            if (updated != 1)
            {
                attempt.Status = "Conflict";
                attempt.TargetState = null;
                attempt.ReasonCode = "concern-concurrency-conflict";
                attempt.Explanation = $"Concern '{concernId}' changed while its event was being evaluated.";
                SetEvidence(attempt, CreateEvidence(
                    instance.DefinitionId, instance.DefinitionVersion, instance.Id, revisionBefore,
                    eventName, now, context, evaluation, previousState, "ConcurrencyConflict",
                    "concern-concurrency-conflict"));
                db.InputAttempts.Add(attempt);
                await db.SaveChangesAsync(cancellationToken);
                return new SubmissionResult(attempt, await ReadInstanceAsync(instance.Id, cancellationToken), null, true);
            }
        }
        foreach (var (record, cancellationEvaluation) in cancellationTransitions)
        {
            var concernRevisionBefore = record.Revision;
            var updated = await db.ConcernStates
                .Where(row => row.InstanceId == instance.Id && row.ConcernId == record.ConcernId &&
                              row.Revision == concernRevisionBefore && row.CurrentState == record.CurrentState)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.CurrentState, cancellationEvaluation.TargetState!)
                    .SetProperty(row => row.Revision, checked(concernRevisionBefore + 1))
                    .SetProperty(row => row.UpdatedAt, now), cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException(
                    $"Concern '{record.ConcernId}' changed inside the SQLite immediate transaction.");
            }
        }

        db.InputAttempts.Add(attempt);
        TransitionHistoryRecord? history = null;
        var concernHistories = new List<ConcernTransitionHistoryRecord>();
        if (evaluation.Status == EvaluationStatus.Accepted && concernId is not null)
        {
            concernHistories.Add(new ConcernTransitionHistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instance.Id,
                ConcernId = concernId,
                AttemptId = attemptId,
                DefinitionId = instance.DefinitionId,
                DefinitionVersion = instance.DefinitionVersion,
                TransitionId = evaluation.TransitionId!,
                PreviousState = evaluation.PreviousState,
                NewState = evaluation.TargetState!,
                Event = eventName,
                RevisionBefore = revisionBefore,
                RevisionAfter = revisionAfter,
                ConcernRevisionBefore = concernRow!.Revision,
                ConcernRevisionAfter = checked(concernRow.Revision + 1),
                OccurredAt = now
            });
        }
        foreach (var (record, cancellationEvaluation) in cancellationTransitions)
        {
            concernHistories.Add(new ConcernTransitionHistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instance.Id,
                ConcernId = record.ConcernId,
                AttemptId = attemptId,
                DefinitionId = instance.DefinitionId,
                DefinitionVersion = instance.DefinitionVersion,
                TransitionId = cancellationEvaluation.TransitionId!,
                PreviousState = cancellationEvaluation.PreviousState,
                NewState = cancellationEvaluation.TargetState!,
                Event = eventName,
                RevisionBefore = revisionBefore,
                RevisionAfter = revisionAfter,
                ConcernRevisionBefore = record.Revision,
                ConcernRevisionAfter = checked(record.Revision + 1),
                OccurredAt = now
            });
        }
        if (evaluation.Status == EvaluationStatus.Accepted && concernId is null)
        {
            history = new TransitionHistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instance.Id,
                AttemptId = attemptId,
                TransitionId = evaluation.TransitionId!,
                DefinitionId = instance.DefinitionId,
                DefinitionVersion = instance.DefinitionVersion,
                PreviousState = evaluation.PreviousState,
                NewState = evaluation.TargetState!,
                Event = eventName,
                RevisionBefore = revisionBefore,
                RevisionAfter = revisionAfter,
                OccurredAt = now
            };
            db.TransitionHistory.Add(history);
            await ApplyTimerTransitionChangesAsync(
                instance, definition, history, revisionAfter, now, cancellationToken);
        }
        db.ConcernTransitionHistory.AddRange(concernHistories);
        return new SubmissionResult(attempt, await ReadInstanceAsync(instance.Id, cancellationToken), history, false,
            concernHistories);
    }

    private async Task<IReadOnlyList<ConcernStateRecord>> LoadConcernStateVectorAsync(
        InstanceRecord instance,
        MachineDefinition definition,
        CancellationToken cancellationToken)
    {
        var rows = await db.ConcernStates.AsNoTracking()
            .Where(row => row.InstanceId == instance.Id)
            .OrderBy(row => row.ConcernId)
            .ToListAsync(cancellationToken);
        if (rows.Count != definition.Concerns.Count ||
            rows.Any(row => !definition.Concerns.ContainsKey(row.ConcernId) ||
                            row.DefinitionId != instance.DefinitionId ||
                            row.DefinitionVersion != instance.DefinitionVersion))
        {
            throw new InvalidDataException(
                $"Concern state vector for instance '{instance.Id}' does not match its immutable definition.");
        }
        foreach (var row in rows)
        {
            if (!definition.Concerns[row.ConcernId].States.ContainsKey(row.CurrentState))
            {
                throw new InvalidDataException(
                    $"Concern '{row.ConcernId}' has undeclared projected state '{row.CurrentState}'.");
            }
        }
        return rows;
    }

    private async Task<TimerRecord?> FindBlockingTimerAsync(
        string instanceId,
        MachineDefinition definition,
        string eventName,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var declarations = definition.Timers
            .Where(timer => timer.BlockedEvents.Contains(eventName, StringComparer.Ordinal))
            .ToDictionary(timer => timer.Id, StringComparer.Ordinal);
        if (declarations.Count == 0)
        {
            return null;
        }
        var pending = await db.Timers.Where(timer =>
                timer.InstanceId == instanceId &&
                (timer.Status == "Pending" || timer.Status == "Refused" || timer.Status == "Unresolved") &&
                timer.DueAt <= receivedAt)
            .OrderBy(timer => timer.DueAt).ThenBy(timer => timer.Id)
            .ToListAsync(cancellationToken);
        return pending.FirstOrDefault(timer => declarations.ContainsKey(timer.TimerDeclarationId));
    }

    private async Task ApplyTimerTransitionChangesAsync(
        InstanceRecord instance,
        MachineDefinition definition,
        TransitionHistoryRecord history,
        long resultingRevision,
        DateTimeOffset transitionTime,
        CancellationToken cancellationToken)
    {
        var declarations = definition.Timers.ToDictionary(timer => timer.Id, StringComparer.Ordinal);
        var pending = await db.Timers.Where(timer =>
                timer.InstanceId == instance.Id &&
                timer.Status == "Pending")
            .ToListAsync(cancellationToken);
        foreach (var timer in pending)
        {
            if (!declarations.TryGetValue(timer.TimerDeclarationId, out var declaration))
            {
                timer.Status = "Stale";
                timer.ResultReason = "timer-declaration-not-in-bound-definition";
                timer.ProcessedAt = transitionTime;
                continue;
            }
            if (declaration.CancelOnTransitions.Contains(history.TransitionId, StringComparer.Ordinal) ||
                declaration.WhileState != history.NewState)
            {
                timer.Status = "Cancelled";
                timer.ResultReason = declaration.CancelOnTransitions.Contains(history.TransitionId, StringComparer.Ordinal)
                    ? $"cancelled-by-transition:{history.TransitionId}"
                    : $"state-exited:{history.PreviousState}->{history.NewState}";
                timer.ProcessedAt = transitionTime;
                timer.ResultTransitionId = history.Id;
                timer.ProcessingEvidenceJson = JsonSerializer.Serialize(new
                {
                    cancelledAt = transitionTime,
                    transitionHistoryId = history.Id,
                    transitionId = history.TransitionId,
                    previousState = history.PreviousState,
                    newState = history.NewState
                }, StoredJsonOptions);
            }
        }

        foreach (var declaration in definition.Timers.Where(timer => timer.OnTransition == history.TransitionId))
        {
            var dueAt = transitionTime.Add(declaration.Duration);
            var timerId = CreateStableTimerId(instance.Id, declaration.Id, history.Id);
            if (await db.Timers.AnyAsync(timer => timer.Id == timerId, cancellationToken))
            {
                continue;
            }
            var scheduleEvidence = JsonSerializer.Serialize(new
            {
                timerDeclarationId = declaration.Id,
                duration = declaration.Duration.ToString("c"),
                anchor = "accepted-transition-time",
                triggerTransitionId = history.TransitionId,
                triggerTransitionHistoryId = history.Id,
                triggerEvent = history.Event,
                triggerRevision = resultingRevision,
                triggeredAt = history.OccurredAt,
                calculatedAt = transitionTime,
                dueAt
            }, StoredJsonOptions);
            db.Timers.Add(new TimerRecord
            {
                Id = timerId,
                InstanceId = instance.Id,
                DefinitionId = instance.DefinitionId,
                DefinitionVersion = instance.DefinitionVersion,
                TimerDeclarationId = declaration.Id,
                OriginTransitionId = history.TransitionId,
                OriginTransitionHistoryId = history.Id,
                OriginEvent = history.Event,
                OriginRevision = resultingRevision,
                TriggeredAt = history.OccurredAt,
                CalculatedAt = transitionTime,
                DueAt = dueAt,
                Status = "Pending",
                EvidenceJson = scheduleEvidence,
                ProcessingEvidenceJson = "{}"
            });
        }
    }

    private string? CreateTimerEffectIntent(
        InstanceRecord instance,
        MachineDefinition definition,
        long executionRevision,
        TimerDeclaration declaration,
        string operationId,
        EffectHarnessBinding binding,
        DateTimeOffset now)
    {
        var intent = declaration.EffectIntent
            ?? throw new InvalidOperationException("The timer declaration has no effect intent.");
        if (definition.InputPolicy is null ||
            !definition.InputPolicy.Observations.TryGetValue(intent.ObservationType, out var observation))
        {
            throw new InvalidOperationException(
                $"Timer effect observation '{intent.ObservationType}' is not declared by the bound definition.");
        }

        using var requestDocument = JsonDocument.Parse(intent.Request.GetRawText());
        var requestValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in requestDocument.RootElement.EnumerateObject())
        {
            requestValues.Add(property.Name, property.Value.Clone());
        }
        requestValues["operationId"] = JsonSerializer.SerializeToElement(operationId, StoredJsonOptions);
        requestValues["capability"] = JsonSerializer.SerializeToElement(intent.Capability, StoredJsonOptions);
        var requestJson = JsonSerializer.Serialize(requestValues, StoredJsonOptions);
        var requestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));
        var providerIdentity = binding.ProviderIdentity
            ?? throw new InvalidOperationException("The timer action's harness provider identity was not configured.");
        var idempotencyKey = "dsm-" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{instance.Id}\0{operationId}"))).ToLowerInvariant();

        if (db.EffectOperations.Local.Any(operation =>
                operation.InstanceId == instance.Id && operation.OperationId == operationId) ||
            db.ExpectedOperations.Local.Any(operation =>
                operation.InstanceId == instance.Id && operation.OperationId == operationId))
        {
            throw new DefinitionConflictException(
                $"Timer action operation '{operationId}' is already present for instance '{instance.Id}'.");
        }
        var expected = new ExpectedOperationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceId = instance.Id,
            OperationId = operationId,
            ObservationType = intent.ObservationType,
            SourceIdentity = providerIdentity,
            ExpectedAt = now,
            ExpectedRevision = executionRevision,
            RequiredEvidenceJson = JsonSerializer.Serialize(
                observation.RequiredEvidence.Order(StringComparer.Ordinal).ToArray(), StoredJsonOptions),
            Status = "Expected"
        };
        var effect = new EffectOperationRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceId = instance.Id,
            OperationId = operationId,
            DefinitionId = instance.DefinitionId,
            DefinitionVersion = instance.DefinitionVersion,
            ExecutionRevision = executionRevision,
            Capability = intent.Capability,
            ProviderIdentity = providerIdentity,
            ObservationType = intent.ObservationType,
            RequestJson = requestJson,
            RequestDigest = requestDigest,
            Status = "Pending",
            HarnessProviderId = binding.ProviderId,
            CredentialBindingId = binding.CredentialBindingId,
            ApiDescriptionVersionId = binding.ApiDescriptionVersionId,
            HarnessOperationKey = binding.OperationKey,
            HarnessIdempotencyKey = idempotencyKey,
            RequestedAt = now,
            UpdatedAt = now
        };
        db.ExpectedOperations.Add(expected);
        db.EffectOperations.Add(effect);
        return operationId;
    }

    private static void CompleteTimer(
        TimerRecord timer,
        string status,
        string reason,
        DateTimeOffset processedAt,
        object evidence)
    {
        timer.Status = status;
        timer.ResultReason = reason;
        timer.ProcessedAt = processedAt;
        timer.ProcessingEvidenceJson = JsonSerializer.Serialize(evidence, StoredJsonOptions);
    }

    private static string CreateStableTimerId(string instanceId, string declarationId, string historyId)
    {
        var identity = Encoding.UTF8.GetBytes($"{instanceId}\0{declarationId}\0{historyId}");
        return Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<InputAttemptRecord>> ListAttemptsAsync(string instanceId, CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.InputAttempts.AsNoTracking().Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id).ToListAsync(cancellationToken);
    }

    public async Task<InputAttemptRecord> GetAttemptAsync(string instanceId, string attemptId, CancellationToken cancellationToken) =>
        await db.InputAttempts.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == attemptId && record.InstanceId == instanceId, cancellationToken)
        ?? throw new ResourceNotFoundException($"Attempt '{attemptId}' does not exist for instance '{instanceId}'.");

    public async Task<IReadOnlyList<TransitionHistoryRecord>> ListHistoryAsync(string instanceId, CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.TransitionHistory.AsNoTracking().Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.RevisionAfter).ThenBy(record => record.Id).ToListAsync(cancellationToken);
    }

    public async Task<TransitionHistoryRecord> GetHistoryEntryAsync(string instanceId, string historyId, CancellationToken cancellationToken) =>
        await db.TransitionHistory.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == historyId && record.InstanceId == instanceId, cancellationToken)
        ?? throw new ResourceNotFoundException($"Transition history entry '{historyId}' does not exist for instance '{instanceId}'.");

    private async Task<bool> DefinitionExistsAsync(string id, string version, CancellationToken cancellationToken) =>
        await db.Definitions.AsNoTracking().AnyAsync(record => record.Id == id && record.Version == version, cancellationToken);

    private async Task EnsureInstanceExistsAsync(string id, CancellationToken cancellationToken)
    {
        if (!await db.Instances.AsNoTracking().AnyAsync(record => record.Id == id, cancellationToken))
        {
            throw new ResourceNotFoundException($"Instance '{id}' does not exist.");
        }
    }

    private async Task<MachineInstanceDetails> ReadInstanceAsync(string id, CancellationToken cancellationToken)
    {
        var record = await db.Instances.AsNoTracking().SingleAsync(instance => instance.Id == id, cancellationToken);
        return ToDetails(record);
    }

    private static InputAttemptRecord CreateAttempt(
        string id,
        string instanceId,
        string eventName,
        DateTimeOffset now,
        TransitionEvaluation evaluation,
        TransitionEvaluationContext context,
        string definitionId,
        string definitionVersion,
        long revisionBefore,
        long revisionAfter,
        string resultingState,
        string commitStatus)
    {
        var attempt = new InputAttemptRecord
        {
            Id = id,
            InstanceId = instanceId,
            Event = eventName,
            ReceivedAt = now,
            Status = evaluation.Status.ToString(),
            TransitionId = evaluation.TransitionId,
            PreviousState = evaluation.PreviousState,
            TargetState = evaluation.TargetState,
            RevisionBefore = revisionBefore,
            RevisionAfter = revisionAfter,
            ReasonCode = evaluation.ReasonCode,
            Explanation = evaluation.Explanation,
            GuardFindingsJson = JsonSerializer.Serialize(evaluation.GuardFindings, StoredJsonOptions),
            EvaluationEvidenceJson = "{}",
            EvaluationEvidenceDigest = string.Empty
        };
        SetEvidence(attempt, CreateEvidence(
            definitionId, definitionVersion, instanceId, revisionBefore, eventName, now, context, evaluation,
            resultingState, commitStatus, evaluation.ReasonCode));
        return attempt;
    }

    private static TransitionEvaluationEvidence CreateEvidence(
        string definitionId,
        string definitionVersion,
        string instanceId,
        long revision,
        string eventName,
        DateTimeOffset now,
        TransitionEvaluationContext context,
        TransitionEvaluation evaluation,
        string resultingState,
        string commitStatus,
        string reasonCode) =>
        new(
            definitionId,
            definitionVersion,
            instanceId,
            revision,
            eventName,
            now,
            context.InstanceFacts,
            context.AttemptFacts,
            context.AttemptFactProvenance,
            context.ResolvedFacts,
            context.UnresolvedFacts,
            context.DecisionEvidence,
            evaluation.GuardFindings,
            commitStatus,
            evaluation.Status,
            evaluation.TransitionId,
            reasonCode,
            evaluation.PreviousState,
            resultingState,
            context.ConcernStates,
            context.ParentState);

    private static void SetEvidence(InputAttemptRecord attempt, TransitionEvaluationEvidence evidence)
    {
        attempt.EvaluationEvidenceJson = JsonSerializer.Serialize(evidence, StoredJsonOptions);
        attempt.EvaluationEvidenceDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(attempt.EvaluationEvidenceJson)));
    }

    private static DefinitionSummary ToSummary(DefinitionRecord record) =>
        new(record.Id, record.Version, record.LanguageVersion, record.ContentDigest, record.RegisteredAt);

    private static MachineInstanceDetails ToDetails(InstanceRecord record) =>
        new(record.Id, record.DefinitionId, record.DefinitionVersion, record.CurrentState, record.Revision,
            DeserializeFacts(record.FactsJson), record.CreatedAt, record.UpdatedAt);

    private async Task<StoreTransaction> BeginImmediateTransactionAsync(CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        SqliteTransaction? sqliteTransaction = null;
        try
        {
            sqliteTransaction = connection.BeginTransaction(deferred: false);
            var efTransaction = await db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken)
                ?? throw new InvalidOperationException("EF Core did not enlist in the SQLite transaction.");
            return new StoreTransaction(db, sqliteTransaction, efTransaction);
        }
        catch
        {
            if (sqliteTransaction is not null)
            {
                await sqliteTransaction.DisposeAsync();
            }
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    private async Task<StoreTransaction> BeginReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        SqliteTransaction? sqliteTransaction = null;
        try
        {
            sqliteTransaction = connection.BeginTransaction(deferred: true);
            var efTransaction = await db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken)
                ?? throw new InvalidOperationException("EF Core did not enlist in the SQLite read transaction.");
            return new StoreTransaction(db, sqliteTransaction, efTransaction);
        }
        catch
        {
            if (sqliteTransaction is not null)
            {
                await sqliteTransaction.DisposeAsync();
            }
            await db.Database.CloseConnectionAsync();
            throw;
        }
    }

    private static string SerializeFacts(IReadOnlyDictionary<string, FactValue> facts) =>
        JsonSerializer.Serialize(facts.ToDictionary(pair => pair.Key, pair => FactWire.From(pair.Value), StringComparer.Ordinal), StoredJsonOptions);

    private static string SerializeJsonObject(IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null || values.Count == 0)
        {
            return "{}";
        }
        var ordered = values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return CanonicalizeJson(JsonSerializer.Serialize(ordered, StoredJsonOptions));
    }

    private static string ComputeDigest(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalizeJson(json))));

    private static string ComputeStagedObservationDigest(StagedObservationRecord staged) =>
        ComputeDigest(JsonSerializer.Serialize(new
        {
            staged.InstanceId,
            staged.ExpectedOperationId,
            staged.WorkflowOperationId,
            staged.HarnessExecutionId,
            staged.ProviderIdentity,
            staged.Capability,
            staged.DefinitionId,
            staged.DefinitionVersion,
            staged.ExecutionRevision,
            staged.SourceAuthority,
            staged.ResultClassification,
            staged.ObservationType,
            staged.ProviderEffectId,
            occurredAt = staged.OccurredAt.ToUniversalTime(),
            staged.PayloadJson,
            staged.EvidenceJson,
            staged.EvidenceReferencesJson
        }, StoredJsonOptions));

    private static string CreateStagedObservationId(
        string instanceId,
        string workflowOperationId,
        string harnessExecutionId)
    {
        var identity = Encoding.UTF8.GetBytes($"{instanceId}\0{workflowOperationId}\0{harnessExecutionId}");
        return $"staged-{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}";
    }

    private static string CanonicalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ComputeObservationResultDigest(
        string observationType,
        string operationId,
        string payloadJson,
        string evidenceJson,
        IReadOnlyDictionary<string, FactWire> facts,
        ExternalDecisionEvidence? decisionEvidence,
        IReadOnlyDictionary<string, string>? factsProvenance,
        DateTimeOffset? occurredAt) =>
        ComputeDigest(JsonSerializer.Serialize(new
        {
            observationType,
            operationId,
            payload = ReadJsonElement(payloadJson),
            evidence = ReadJsonElement(evidenceJson),
            facts,
            decisionEvidence,
            factsProvenance = factsProvenance?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            occurredAt
        }, StoredJsonOptions));

    private static JsonElement ReadJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, FactValue> DeserializeFacts(string json)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, FactWire>>(json, StoredJsonOptions) ?? [];
        return values.ToDictionary(pair => pair.Key, pair => pair.Value.ToValue(), StringComparer.Ordinal);
    }

    private sealed record FactWire(FactKind Kind, string? StringValue, decimal NumberValue, bool BooleanValue)
    {
        public static FactWire From(FactValue value) => new(value.Kind, value.StringValue, value.NumberValue, value.BooleanValue);
        public FactValue ToValue() => Kind switch
        {
            FactKind.String => FactValue.From(StringValue ?? string.Empty),
            FactKind.Number => FactValue.From(NumberValue),
            FactKind.Boolean => FactValue.From(BooleanValue),
            FactKind.Null => FactValue.Null,
            _ => throw new InvalidOperationException($"Unsupported persisted fact kind '{Kind}'.")
        };
    }

    private sealed record StoredInboundEnvelope(
        string InputKind,
        string MessageId,
        string? OperationId,
        string? ObservationId,
        string InputType,
        string? CorrelationId,
        string? ClaimedSource,
        long ExpectedRevision,
        Dictionary<string, JsonElement> Payload,
        Dictionary<string, FactWire> Facts,
        Dictionary<string, JsonElement> Evidence,
        ExternalDecisionEvidence? DecisionEvidence,
        Dictionary<string, string>? FactsProvenance,
        DateTimeOffset? OccurredAt);

    internal sealed class InstanceGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed record ReplaySnapshot(
        InstanceRecord Instance,
        IReadOnlyList<DefinitionRecord> Definitions,
        IReadOnlyList<MigrationRecord> Migrations,
        IReadOnlyList<TransitionHistoryRecord> History,
        IReadOnlyList<InputAttemptRecord> Attempts,
        IReadOnlyList<InboundMessageRecord> Receipts,
        IReadOnlyList<TimerRecord> Timers);

    private sealed class StoreTransaction(
        MachineDbContext db,
        SqliteTransaction sqliteTransaction,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction efTransaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken cancellationToken) =>
            efTransaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await efTransaction.DisposeAsync();
            await sqliteTransaction.DisposeAsync();
            await db.Database.CloseConnectionAsync();
        }
    }
}

public sealed class InstanceLockRegistry
{
    internal object Sync { get; } = new();
    internal Dictionary<string, MachineStore.InstanceGate> Locks { get; } = new(StringComparer.Ordinal);
}

public sealed record DefinitionSummary(string Id, string Version, string LanguageVersion, string ContentDigest, DateTimeOffset RegisteredAt);
public sealed record DefinitionRegistration(DefinitionSummary Definition, bool AlreadyRegistered);
public sealed record StateDetails(string Id, bool Terminal);
public sealed record DefinitionDetails(DefinitionSummary Definition, string InitialState, IReadOnlyList<StateDetails> States,
    IReadOnlyList<string> Events, int TransitionCount, string DefinitionJson);
public sealed record MachineInstanceDetails(string Id, string DefinitionId, string DefinitionVersion, string CurrentState,
    long Revision, IReadOnlyDictionary<string, FactValue> Facts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SubmissionResult(InputAttemptRecord Attempt, MachineInstanceDetails Instance,
    TransitionHistoryRecord? Transition, bool ConcurrencyConflict,
    IReadOnlyList<ConcernTransitionHistoryRecord>? ConcernTransitions = null);
public sealed record ConcernStateDetails(
    string ConcernId,
    string CurrentState,
    long Revision,
    DateTimeOffset UpdatedAt,
    string? LastTransitionId,
    string? LastAttemptId);
public sealed record ConcernIntegrityFinding(string Code, string ConcernId, string Explanation);
public sealed record ConcernInspection(
    string InstanceId,
    string DefinitionId,
    string DefinitionVersion,
    string ParentState,
    long ParentRevision,
    IReadOnlyList<ConcernStateDetails> Concerns,
    IReadOnlyList<ConcernIntegrityFinding> IntegrityFindings);
public sealed record InboundProcessingResult(InboundMessageRecord Message, SubmissionResult? Submission);
public sealed record EffectIntentResult(EffectOperationRecord Operation, bool AlreadyRequested);
public sealed record TimerProcessingResult(TimerRecord Timer, SubmissionResult? Submission, bool AlreadyProcessed);

public sealed record EffectHarnessBinding(
    string ProviderId,
    string ApiDescriptionVersionId,
    string OperationKey,
    string IdempotencyKey,
    int? OperationReplaySafety,
    string? CredentialBindingId = null,
    string? ProviderIdentity = null);

public sealed class DefinitionConflictException(string message) : Exception(message);
public sealed class ResourceNotFoundException(string message) : Exception(message);
