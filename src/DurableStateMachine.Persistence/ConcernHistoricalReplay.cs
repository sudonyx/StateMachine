using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Core;

namespace DurableStateMachine.Persistence;

public static class ConcernHistoricalReplay
{
    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web);

    public static ConcernReplayReport Reconstruct(
        InstanceRecord instance,
        DefinitionRecord definitionRecord,
        IReadOnlyList<ConcernStateRecord> projections,
        IReadOnlyList<ConcernTransitionHistoryRecord> history,
        IReadOnlyList<InputAttemptRecord> attempts)
    {
        var findings = new List<ConcernReplayFinding>();
        MachineDefinition definition;
        try
        {
            definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
        }
        catch (MachineDefinitionException exception)
        {
            return Failure(instance, null, "bound-definition-invalid", exception.Message);
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definitionRecord.DefinitionJson)));
        if (definitionRecord.Id != instance.DefinitionId || definitionRecord.Version != instance.DefinitionVersion ||
            definition.Id != instance.DefinitionId || definition.Version != instance.DefinitionVersion ||
            digest != definitionRecord.ContentDigest)
        {
            findings.Add(new("definition-identity-or-digest-mismatch", null,
                "Concern replay requires the exact immutable definition bound to the instance."));
        }

        var states = definition.Concerns.ToDictionary(pair => pair.Key, pair => pair.Value.InitialState, StringComparer.Ordinal);
        var revisions = definition.Concerns.Keys.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
        var attemptsById = attempts.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var projectionById = projections.ToDictionary(item => item.ConcernId, StringComparer.Ordinal);

        foreach (var extra in projections.Where(item => !definition.Concerns.ContainsKey(item.ConcernId)))
        {
            findings.Add(new("undeclared-concern-projection", extra.ConcernId,
                "A persisted concern projection is not declared in the bound definition."));
        }

        foreach (var item in history.OrderBy(row => row.ConcernId, StringComparer.Ordinal)
                     .ThenBy(row => row.ConcernRevisionAfter).ThenBy(row => row.Id, StringComparer.Ordinal))
        {
            if (!definition.Concerns.TryGetValue(item.ConcernId, out var concern))
            {
                findings.Add(new("unknown-historical-concern", item.ConcernId,
                    "Concern history references an identity not declared in the bound definition.", item.Id));
                continue;
            }
            if (item.DefinitionId != definition.Id || item.DefinitionVersion != definition.Version)
            {
                findings.Add(new("concern-history-definition-mismatch", item.ConcernId,
                    "Concern history is not bound to the instance's immutable definition.", item.Id));
            }
            if (item.ConcernRevisionBefore != revisions[item.ConcernId] ||
                item.ConcernRevisionAfter != revisions[item.ConcernId] + 1)
            {
                findings.Add(new("concern-revision-gap", item.ConcernId,
                    $"Expected concern revision {revisions[item.ConcernId]}->{revisions[item.ConcernId] + 1}, " +
                    $"found {item.ConcernRevisionBefore}->{item.ConcernRevisionAfter}.", item.Id));
            }
            if (states[item.ConcernId] != item.PreviousState)
            {
                findings.Add(new("concern-state-discontinuity", item.ConcernId,
                    $"History starts from '{item.PreviousState}', but prior evidence reconstructs '{states[item.ConcernId]}'.",
                    item.Id));
            }

            if (!attemptsById.TryGetValue(item.AttemptId, out var attempt))
            {
                findings.Add(new("concern-attempt-missing", item.ConcernId,
                    "Concern history has no corresponding durable input attempt.", item.Id));
            }
            else
            {
                VerifyAttempt(definition, concern, item, attempt, findings);
            }

            revisions[item.ConcernId] = item.ConcernRevisionAfter;
            states[item.ConcernId] = item.NewState;
        }

        foreach (var concern in definition.Concerns.Values)
        {
            if (!projectionById.TryGetValue(concern.Id, out var projection))
            {
                findings.Add(new("concern-projection-missing", concern.Id,
                    "The current concern projection is missing."));
                continue;
            }
            if (projection.DefinitionId != instance.DefinitionId ||
                projection.DefinitionVersion != instance.DefinitionVersion)
            {
                findings.Add(new("concern-projection-definition-mismatch", concern.Id,
                    "The current concern projection belongs to a different definition."));
            }
            if (projection.CurrentState != states[concern.Id])
            {
                findings.Add(new("concern-projection-state-mismatch", concern.Id,
                    $"Projection state '{projection.CurrentState}' differs from replayed state '{states[concern.Id]}'."));
            }
            if (projection.Revision != revisions[concern.Id])
            {
                findings.Add(new("concern-projection-revision-mismatch", concern.Id,
                    $"Projection revision {projection.Revision} differs from replayed revision {revisions[concern.Id]}."));
            }
            if (!concern.States.ContainsKey(projection.CurrentState))
            {
                findings.Add(new("concern-projection-state-undeclared", concern.Id,
                    $"Projection state '{projection.CurrentState}' is not declared."));
            }
        }

        var output = definition.Concerns.Values.OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(concern => new ConcernReplayState(
                concern.Id,
                concern.InitialState,
                states[concern.Id],
                projectionById.TryGetValue(concern.Id, out var projection) ? projection.CurrentState : null,
                revisions[concern.Id],
                projectionById.TryGetValue(concern.Id, out projection) ? projection.Revision : null))
            .ToArray();
        return new ConcernReplayReport(instance.Id, definition.Id, definition.Version, digest,
            findings.Count == 0 ? "Consistent" : "Inconsistent", output, findings,
            history.Select(item => $"concern-history:{item.Id}")
                .Concat(attempts.Where(item => item.Dimension == "Concern" || history.Any(row => row.AttemptId == item.Id))
                    .Select(item => $"attempt:{item.Id}"))
                .Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void VerifyAttempt(
        MachineDefinition definition,
        MachineConcern concern,
        ConcernTransitionHistoryRecord history,
        InputAttemptRecord attempt,
        List<ConcernReplayFinding> findings)
    {
        var mappingExists = definition.InputPolicy is not null &&
            (definition.InputPolicy.Commands.Values.Any(mapping =>
                 mapping.Concern == history.ConcernId && mapping.Event == history.Event) ||
             definition.InputPolicy.Observations.Values.Any(mapping =>
                 mapping.Concern == history.ConcernId && mapping.Event == history.Event));
        var parentCancellation = attempt.Dimension == "Parent" &&
            definition.CancellationEvent == history.Event &&
            definition.Transitions.Any(transition =>
                transition.Id == attempt.TransitionId &&
                transition.Source == attempt.PreviousState &&
                transition.Event == history.Event &&
                transition.Target == attempt.TargetState &&
                definition.States.TryGetValue(transition.Target, out var target) && target.IsTerminal);
        if (attempt.Status != EvaluationStatus.Accepted.ToString() ||
            attempt.Event != history.Event ||
            (attempt.Dimension == "Concern" &&
             (attempt.ConcernId != history.ConcernId || attempt.TransitionId != history.TransitionId ||
              attempt.PreviousState != history.PreviousState || attempt.TargetState != history.NewState ||
              !mappingExists)) ||
            (attempt.Dimension == "Parent" && !parentCancellation) ||
            (attempt.Dimension != "Concern" && attempt.Dimension != "Parent") ||
            attempt.RevisionBefore != history.RevisionBefore ||
            attempt.RevisionAfter != history.RevisionAfter)
        {
            findings.Add(new("concern-attempt-link-mismatch", history.ConcernId,
                "Concern history does not agree with its accepted input attempt.", history.Id));
        }
        if (attempt.EvaluationEvidenceJson == "{}" ||
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attempt.EvaluationEvidenceJson))) !=
            attempt.EvaluationEvidenceDigest)
        {
            findings.Add(new("concern-evaluation-evidence-digest-mismatch", history.ConcernId,
                "The linked attempt's evaluation evidence is missing or its digest is invalid.", history.Id));
            return;
        }

        TransitionEvaluationEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<TransitionEvaluationEvidence>(
                attempt.EvaluationEvidenceJson, StoredJsonOptions);
        }
        catch (JsonException exception)
        {
            findings.Add(new("concern-evaluation-evidence-invalid", history.ConcernId,
                exception.Message, history.Id));
            return;
        }
        if (evidence is null || evidence.ConcernStates is null ||
            evidence.InstanceId != attempt.InstanceId ||
            evidence.DefinitionId != definition.Id || evidence.DefinitionVersion != definition.Version ||
            evidence.Event != history.Event ||
            evidence.ConcernStates.GetValueOrDefault(history.ConcernId) != history.PreviousState)
        {
            findings.Add(new("concern-evaluation-context-mismatch", history.ConcernId,
                "Stored evidence does not identify the concern's historical input state.", history.Id));
            return;
        }

        var context = TransitionEvaluationContext.Create(
            evidence.InstanceFacts,
            evidence.AttemptFacts,
            evidence.DecisionEvidence,
            evidence.AttemptFactProvenance,
            evidence.ConcernStates,
            evidence.ParentState);
        var evaluation = TransitionEvaluator.EvaluateConcern(
            concern, history.PreviousState, history.Event, context);
        if (evaluation.Status != EvaluationStatus.Accepted ||
            evaluation.TransitionId != history.TransitionId ||
            evaluation.TargetState != history.NewState)
        {
            findings.Add(new("concern-transition-verification-failed", history.ConcernId,
                $"Historical evaluation produced {evaluation.Status}/{evaluation.TransitionId}/{evaluation.TargetState}, " +
                $"not Accepted/{history.TransitionId}/{history.NewState}.", history.Id));
        }

        foreach (var (concernId, state) in evidence.ConcernStates)
        {
            if (concernId == history.ConcernId)
            {
                continue;
            }
            if (!definition.Concerns.TryGetValue(concernId, out var other) || !other.States.ContainsKey(state))
            {
                findings.Add(new("concern-evaluation-vector-invalid", history.ConcernId,
                    $"Evidence references unknown concern state '{concernId}={state}'.", history.Id));
            }
        }
    }

    private static ConcernReplayReport Failure(
        InstanceRecord instance,
        string? digest,
        string code,
        string explanation) =>
        new(instance.Id, instance.DefinitionId, instance.DefinitionVersion, digest, "Unavailable", [],
            [new ConcernReplayFinding(code, null, explanation)], []);
}

public sealed record ConcernReplayState(
    string ConcernId,
    string InitialState,
    string ReconstructedState,
    string? ProjectedState,
    long ReconstructedRevision,
    long? ProjectedRevision);

public sealed record ConcernReplayFinding(string Code, string? ConcernId, string Explanation, string? EvidenceId = null);

public sealed record ConcernReplayReport(
    string InstanceId,
    string DefinitionId,
    string DefinitionVersion,
    string? DefinitionDigest,
    string Outcome,
    IReadOnlyList<ConcernReplayState> Concerns,
    IReadOnlyList<ConcernReplayFinding> Findings,
    IReadOnlyList<string> EvidenceReferences);
