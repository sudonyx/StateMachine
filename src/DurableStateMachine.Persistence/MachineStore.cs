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
            LanguageVersion = "1.0",
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
            var definitionRecord = await db.Definitions.AsNoTracking().SingleOrDefaultAsync(
                record => record.Id == instance.DefinitionId && record.Version == instance.DefinitionVersion, cancellationToken)
                ?? throw new InvalidOperationException($"The registered definition for instance '{instanceId}' is missing.");
            var definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
            var facts = DeserializeFacts(instance.FactsJson);
            var context = TransitionEvaluationContext.Create(facts, attemptFacts, decisionEvidence, attemptFactProvenance);
            var evaluation = TransitionEvaluator.Evaluate(definition, instance.CurrentState, eventName, context);
            var attemptId = Guid.NewGuid().ToString("N");
            var revisionBefore = instance.Revision;
            var revisionAfter = evaluation.Status == EvaluationStatus.Accepted ? checked(revisionBefore + 1) : revisionBefore;
            var attempt = CreateAttempt(
                attemptId,
                instanceId,
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
                    .Where(record => record.Id == instanceId && record.Revision == revisionBefore)
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
                        instance.DefinitionId, instance.DefinitionVersion, instanceId, revisionBefore,
                        eventName, now, context, evaluation, instance.CurrentState, "ConcurrencyConflict",
                        "concurrency-conflict"));
                    db.InputAttempts.Add(attempt);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new SubmissionResult(attempt, await ReadInstanceAsync(instanceId, cancellationToken), null, true);
                }
            }

            db.InputAttempts.Add(attempt);
            TransitionHistoryRecord? history = null;
            if (evaluation.Status == EvaluationStatus.Accepted)
            {
                history = new TransitionHistoryRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    InstanceId = instanceId,
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
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SubmissionResult(attempt, await ReadInstanceAsync(instanceId, cancellationToken), history, false);
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

public sealed class DefinitionConflictException(string message) : Exception(message);
public sealed class ResourceNotFoundException(string message) : Exception(message);
