using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Core;
using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Persistence;

public sealed class MachineStore(MachineDbContext db)
{
    private static readonly object InstanceLocksSync = new();
    private static readonly Dictionary<string, InstanceGate> InstanceLocks = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web);

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
        db.Instances.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.Entry(record).State = EntityState.Detached;
            if (await db.Instances.AsNoTracking().AnyAsync(instance => instance.Id == id, cancellationToken))
            {
                throw new DefinitionConflictException($"Instance '{id}' already exists.");
            }
            throw;
        }
        return ToDetails(record);
    }

    public async Task<MachineInstanceDetails> GetInstanceAsync(string id, CancellationToken cancellationToken)
    {
        var record = await db.Instances.AsNoTracking().SingleOrDefaultAsync(instance => instance.Id == id, cancellationToken)
            ?? throw new ResourceNotFoundException($"Instance '{id}' does not exist.");
        return ToDetails(record);
    }

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
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        if (inputKind is not ("Command" or "Observation"))
        {
            throw new ArgumentException($"Unsupported input kind '{inputKind}'.");
        }
        if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 128 ||
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
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
                .OrderBy(record => record.ReceivedAt)
                .FirstOrDefaultAsync(cancellationToken);
            var message = new InboundMessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                InstanceId = instanceId,
                MessageId = messageId,
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
                Status = "Rejected",
                ReasonCode = "input-not-processed",
                Explanation = "The inbound message has not passed authority and relevance checks.",
                ReceivedAt = now,
                OccurredAt = occurredAt
            };

            if (priorMessage is not null)
            {
                message.Status = priorMessage.PayloadDigest == digest ? "Duplicate" : "IdentityConflict";
                message.ReasonCode = priorMessage.PayloadDigest == digest ? "duplicate-message" : "message-identity-conflict";
                message.Explanation = priorMessage.PayloadDigest == digest
                    ? "This message identity and payload were already received; no second evaluation was performed."
                    : "This message identity was already used with a different payload.";
                message.AttemptId = priorMessage.AttemptId;
            }
            else if (definition.InputPolicy is null)
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
                message.ReasonCode = "unexpected-command";
                message.Explanation = $"Command '{inputType}' is not declared by this machine definition.";
            }
            else if (inputKind == "Observation" &&
                !definition.InputPolicy.Observations.TryGetValue(inputType, out _))
            {
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
                    var eventName = inputKind == "Command"
                        ? definition.InputPolicy.Commands[inputType].Event
                        : observationMapping!.Event;
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
                        submission = await EvaluateEventWithinTransactionAsync(
                            instance, eventName, now, cancellationToken, facts, decisionEvidence, factsProvenance);
                    }
                    if (submission is not null)
                    {
                        message.AttemptId = submission.Attempt.Id;
                        if (submission.Attempt.Status == EvaluationStatus.Accepted.ToString())
                        {
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
                    db.InboundMessages.Add(message);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    var currentSubmission = submission is null
                        ? null
                        : submission with { Instance = await ReadInstanceAsync(instanceId, cancellationToken) };
                    return new InboundProcessingResult(message, currentSubmission);
                }
            }

            db.InboundMessages.Add(message);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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

    public async Task<IReadOnlyList<InboundMessageRecord>> ListInboundMessagesAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        await EnsureInstanceExistsAsync(instanceId, cancellationToken);
        return await db.InboundMessages.AsNoTracking().Where(record => record.InstanceId == instanceId)
            .OrderBy(record => record.ReceivedAt).ThenBy(record => record.Id).ToListAsync(cancellationToken);
    }

    public async Task<InboundMessageRecord> RecordMalformedInputAsync(
        string instanceId,
        string inputKind,
        string? messageId,
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
            MessageId = string.IsNullOrWhiteSpace(messageId) ? $"invalid-{Guid.NewGuid():N}" : messageId,
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
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
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
        IReadOnlyDictionary<string, string>? attemptFactProvenance)
    {
        var definitionRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
            record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion, cancellationToken)
            ?? throw new InvalidOperationException($"The registered definition for instance '{instance.Id}' is missing.");
        var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
        var facts = DeserializeFacts(instance.FactsJson);
        var context = TransitionEvaluationContext.Create(facts, attemptFacts, decisionEvidence, attemptFactProvenance);
        var blockingTimer = await FindBlockingTimerAsync(instance.Id, definition, eventName, now, cancellationToken);
        var evaluation = blockingTimer is null
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
        var attemptId = Guid.NewGuid().ToString("N");
        var revisionBefore = instance.Revision;
        var revisionAfter = evaluation.Status == EvaluationStatus.Accepted ? checked(revisionBefore + 1) : revisionBefore;
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
            evaluation.Status == EvaluationStatus.Accepted ? evaluation.TargetState! : instance.CurrentState,
            evaluation.Status == EvaluationStatus.Accepted ? "Committed" : "NoChange");

        if (evaluation.Status == EvaluationStatus.Accepted)
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

        db.InputAttempts.Add(attempt);
        TransitionHistoryRecord? history = null;
        if (evaluation.Status == EvaluationStatus.Accepted)
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
        return new SubmissionResult(attempt, await ReadInstanceAsync(instance.Id, cancellationToken), history, false);
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
            resultingState);

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

    private sealed class InstanceGate
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }
}

public sealed record DefinitionSummary(string Id, string Version, string LanguageVersion, string ContentDigest, DateTimeOffset RegisteredAt);
public sealed record DefinitionRegistration(DefinitionSummary Definition, bool AlreadyRegistered);
public sealed record StateDetails(string Id, bool Terminal);
public sealed record DefinitionDetails(DefinitionSummary Definition, string InitialState, IReadOnlyList<StateDetails> States,
    IReadOnlyList<string> Events, int TransitionCount, string DefinitionJson);
public sealed record MachineInstanceDetails(string Id, string DefinitionId, string DefinitionVersion, string CurrentState,
    long Revision, IReadOnlyDictionary<string, FactValue> Facts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SubmissionResult(InputAttemptRecord Attempt, MachineInstanceDetails Instance,
    TransitionHistoryRecord? Transition, bool ConcurrencyConflict);
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
