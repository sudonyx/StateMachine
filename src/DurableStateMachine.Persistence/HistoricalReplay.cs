using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Core;

namespace DurableStateMachine.Persistence;

public static class HistoricalReplay
{
    private static readonly JsonSerializerOptions StoredJsonOptions = new(JsonSerializerDefaults.Web);

    public static HistoricalReplayReport Reconstruct(
        InstanceRecord instance,
        DefinitionRecord? definitionRecord,
        IReadOnlyList<TransitionHistoryRecord> history,
        IReadOnlyList<InputAttemptRecord> attempts,
        IReadOnlyList<InboundMessageRecord> receipts,
        IReadOnlyList<TimerRecord> timers,
        bool verifyDecisions)
    {
        var mode = verifyDecisions ? "DecisionVerification" : "StateReconstruction";
        if (definitionRecord is null)
        {
            return Unavailable(instance, mode, "UnsupportedHistoricalFormat",
                "bound-definition-missing", "The exact immutable definition version bound to the instance is unavailable.");
        }

        MachineDefinition definition;
        try
        {
            definition = MachineDefinitionLoader.Load(definitionRecord.DefinitionJson);
        }
        catch (MachineDefinitionException exception)
        {
            return Unavailable(instance, mode, "UnsupportedHistoricalFormat",
                "bound-definition-invalid", exception.Message);
        }

        var findings = new List<ReplayFinding>();
        var decisionFindings = new List<DecisionVerificationFinding>();
        if (definition.Id != instance.DefinitionId || definition.Version != instance.DefinitionVersion ||
            definitionRecord.Id != instance.DefinitionId || definitionRecord.Version != instance.DefinitionVersion)
        {
            findings.Add(new("definition-binding-mismatch",
                "The stored definition identity does not match the instance's immutable definition binding.", []));
        }
        var definitionDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(definitionRecord.DefinitionJson)));
        if (definitionRecord.ContentDigest != definitionDigest ||
            definitionRecord.LanguageVersion != definition.LanguageVersion)
        {
            findings.Add(new("definition-evidence-mismatch",
                "The stored definition digest or language-version metadata does not agree with the immutable definition JSON.", []));
        }

        var attemptsById = attempts.ToDictionary(attempt => attempt.Id, StringComparer.Ordinal);
        var historiesByAttempt = history.GroupBy(entry => entry.AttemptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var receiptsByAttempt = receipts.Where(receipt => receipt.AttemptId is not null)
            .GroupBy(receipt => receipt.AttemptId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var timersByAttempt = timers.Where(timer => timer.AttemptId is not null)
            .GroupBy(timer => timer.AttemptId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var timersByResultTransition = timers.Where(timer => timer.ResultTransitionId is not null)
            .GroupBy(timer => timer.ResultTransitionId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var evidenceReferences = attempts.Select(attempt => $"attempt:{attempt.Id}")
            .Concat(receipts.Select(receipt => $"receipt:{receipt.Id}"))
            .Concat(timers.Select(timer => $"timer:{timer.Id}"))
            .ToList();
        var state = definition.InitialState;
        var expectedRevision = 0L;
        var seenRevisions = new HashSet<long>();

        foreach (var entry in history.OrderBy(item => item.RevisionAfter).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            evidenceReferences.Add($"history:{entry.Id}");
            if (receiptsByAttempt.TryGetValue(entry.AttemptId, out var linkedReceipts))
            {
                evidenceReferences.AddRange(linkedReceipts.Select(receipt => $"receipt:{receipt.Id}"));
            }
            if (timersByAttempt.TryGetValue(entry.AttemptId, out var linkedTimers))
            {
                evidenceReferences.AddRange(linkedTimers.Select(timer => $"timer:{timer.Id}"));
            }
            if (timersByResultTransition.TryGetValue(entry.Id, out var resultTimers))
            {
                evidenceReferences.AddRange(resultTimers.Select(timer => $"timer:{timer.Id}"));
            }
            if (!seenRevisions.Add(entry.RevisionAfter))
            {
                findings.Add(new("duplicate-accepted-revision",
                    $"More than one accepted history row claims revision {entry.RevisionAfter}.", [entry.Id]));
            }
            if (entry.RevisionBefore != expectedRevision || entry.RevisionAfter != expectedRevision + 1)
            {
                findings.Add(new("revision-gap-or-invalid-sequence",
                    $"Expected transition {expectedRevision}->{expectedRevision + 1}, found {entry.RevisionBefore}->{entry.RevisionAfter}.",
                    [entry.Id]));
            }
            if (entry.PreviousState != state)
            {
                findings.Add(new("state-discontinuity",
                    $"Expected previous state '{state}', found '{entry.PreviousState}'.", [entry.Id]));
            }
            if (!definition.States.ContainsKey(entry.PreviousState) || !definition.States.ContainsKey(entry.NewState))
            {
                findings.Add(new("state-not-in-bound-definition",
                    "The accepted transition contains a state not declared by the bound definition.", [entry.Id]));
            }
            if (definition.States.TryGetValue(state, out var priorState) && priorState.IsTerminal)
            {
                findings.Add(new("transition-after-terminal-state",
                    $"History contains a transition after terminal state '{state}'.", [entry.Id]));
            }
            if (entry.InstanceId != instance.Id ||
                entry.DefinitionId != instance.DefinitionId ||
                entry.DefinitionVersion != instance.DefinitionVersion)
            {
                findings.Add(new("transition-definition-binding-mismatch",
                    "The history row belongs to a different instance or definition version.", [entry.Id]));
            }
            var declaredTransition = definition.Transitions.SingleOrDefault(item => item.Id == entry.TransitionId);
            if (declaredTransition is null ||
                declaredTransition.Source != entry.PreviousState ||
                declaredTransition.Event != entry.Event ||
                declaredTransition.Target != entry.NewState)
            {
                findings.Add(new("transition-identity-mismatch",
                    "The recorded transition identity, event, source or target does not match the bound definition.",
                    [entry.Id]));
            }
            if (!attemptsById.TryGetValue(entry.AttemptId, out var attempt))
            {
                findings.Add(new("accepted-attempt-missing",
                    "The accepted transition has no corresponding durable input attempt.", [entry.Id, entry.AttemptId]));
            }
            else
            {
                if (attempt.Status != EvaluationStatus.Accepted.ToString() ||
                    attempt.Event != entry.Event ||
                    attempt.TransitionId != entry.TransitionId ||
                    attempt.PreviousState != entry.PreviousState ||
                    attempt.TargetState != entry.NewState ||
                    attempt.RevisionBefore != entry.RevisionBefore ||
                    attempt.RevisionAfter != entry.RevisionAfter)
                {
                    findings.Add(new("accepted-attempt-link-mismatch",
                        "The transition does not agree with its linked attempt record.",
                        [entry.Id, attempt.Id]));
                }

                if (verifyDecisions)
                {
                    decisionFindings.Add(VerifyDecision(attempt, entry, definition));
                }
            }

            expectedRevision++;
            state = entry.NewState;
        }

        foreach (var attempt in attempts.Where(item => item.Status == EvaluationStatus.Accepted.ToString()))
        {
            if (!historiesByAttempt.TryGetValue(attempt.Id, out var linked) || linked.Length == 0)
            {
                findings.Add(new("accepted-history-missing",
                    "An accepted attempt has no corresponding transition-history row.", [attempt.Id]));
            }
            else if (linked.Length > 1)
            {
                findings.Add(new("attempt-linked-to-multiple-transitions",
                    "An accepted attempt is linked to more than one transition-history row.",
                    [attempt.Id, .. linked.Select(item => item.Id)]));
            }
        }

        foreach (var receipt in receipts)
        {
            if (receipt.AttemptId is not null && !attemptsById.ContainsKey(receipt.AttemptId))
            {
                findings.Add(new("receipt-attempt-missing",
                    "An inbound receipt references an attempt that is not present in the inspected snapshot.",
                    [receipt.Id, receipt.AttemptId]));
            }
            var historyById = history.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            foreach (var timer in timers)
            {
                var timerDeclaration = definition.Timers.SingleOrDefault(item => item.Id == timer.TimerDeclarationId);
                if (timerDeclaration is null)
                {
                    findings.Add(new("timer-declaration-missing",
                        "A durable timer does not match a timer declaration in the bound definition.", [$"timer:{timer.Id}"]));
                }
                if (!historyById.TryGetValue(timer.OriginTransitionHistoryId, out var originHistory))
                {
                    findings.Add(new("timer-origin-transition-missing",
                        "A durable timer references an origin transition that is not present in accepted history.",
                        [$"timer:{timer.Id}", timer.OriginTransitionHistoryId]));
                }
                else if (originHistory.TransitionId != timer.OriginTransitionId ||
                         originHistory.Event != timer.OriginEvent ||
                         originHistory.RevisionAfter != timer.OriginRevision)
                {
                    findings.Add(new("timer-origin-transition-mismatch",
                        "A durable timer's origin identity, event or revision disagrees with accepted history.",
                        [$"timer:{timer.Id}", originHistory.Id]));
                }
                if (timer.DefinitionId != instance.DefinitionId || timer.DefinitionVersion != instance.DefinitionVersion)
                {
                    findings.Add(new("timer-definition-binding-mismatch",
                        "A durable timer does not match the instance's immutable definition binding.", [$"timer:{timer.Id}"]));
                }
                if (timer.AttemptId is not null && !attemptsById.ContainsKey(timer.AttemptId))
                {
                    findings.Add(new("timer-attempt-missing",
                        "A processed timer references an attempt that is not present in the inspected snapshot.",
                        [$"timer:{timer.Id}", timer.AttemptId]));
                }
                else if (timer.AttemptId is not null && timerDeclaration is not null &&
                         attemptsById[timer.AttemptId].Event != timerDeclaration.Event)
                {
                    findings.Add(new("timer-event-mismatch",
                        "A timer's processing attempt does not use its bound timer event.", [$"timer:{timer.Id}", timer.AttemptId]));
                }
                if (timer.ResultTransitionId is not null &&
                    (!historyById.TryGetValue(timer.ResultTransitionId, out var timerHistory) ||
                     timer.AttemptId != timerHistory.AttemptId))
                {
                    findings.Add(new("timer-result-transition-mismatch",
                        "The timer result does not link to a matching accepted transition and attempt.",
                        [$"timer:{timer.Id}", timer.ResultTransitionId]));
                }
                if (timer.Status == "Fired" && timer.ResultTransitionId is null)
                {
                    findings.Add(new("fired-timer-transition-missing",
                        "A timer is marked fired without a linked accepted transition.", [$"timer:{timer.Id}"]));
                }
            }
        }

        if (!definition.States.ContainsKey(instance.CurrentState))
        {
            findings.Add(new("projection-state-not-in-bound-definition",
                $"The current-state projection '{instance.CurrentState}' is not declared by the bound definition.", []));
        }
        if (instance.Revision != expectedRevision)
        {
            findings.Add(new("projection-revision-mismatch",
                $"The projection records revision {instance.Revision}, while the accepted history reconstructs revision {expectedRevision}.",
                []));
        }
        if (instance.CurrentState != state)
        {
            findings.Add(new("projection-state-mismatch",
                $"The projection records state '{instance.CurrentState}', while the accepted history reconstructs '{state}'.",
                []));
        }

        var stateOutcome = findings.Count == 0 ? "Consistent" : "Inconsistent";
        var decisionOutcome = !verifyDecisions
            ? "NotRequested"
            : history.Count == 0
                ? "NoAcceptedTransitions"
            : decisionFindings.Any(item => item.Outcome == "Inconsistent")
                ? "Inconsistent"
                : decisionFindings.Any(item => item.Outcome == "InsufficientEvidence")
                    ? "InsufficientEvidence"
                    : "Verified";
        var overallOutcome = stateOutcome == "Inconsistent"
            ? "Inconsistent"
            : decisionOutcome is "Inconsistent" or "InsufficientEvidence"
                ? decisionOutcome
                : "Consistent";

        return new HistoricalReplayReport(
            instance.Id,
            instance.DefinitionId,
            instance.DefinitionVersion,
            definitionRecord.ContentDigest,
            mode,
            overallOutcome,
            stateOutcome,
            instance.CreatedAt,
            definition.InitialState,
            "Derived from the immutable bound definition; no separate creation-history row exists.",
            state,
            instance.CurrentState,
            expectedRevision,
            instance.Revision,
            history.Count,
            attempts.Count,
            receipts.Count,
            timers.Count,
            instance.Revision,
            findings,
            decisionOutcome,
            decisionFindings,
            evidenceReferences.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static DecisionVerificationFinding VerifyDecision(
        InputAttemptRecord attempt,
        TransitionHistoryRecord history,
        MachineDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(attempt.EvaluationEvidenceDigest) ||
            attempt.EvaluationEvidenceJson is "" or "{}")
        {
            return new(attempt.Id, "InsufficientEvidence",
                "This historical attempt predates complete Gate 4 evaluation evidence.", history.Id);
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(attempt.EvaluationEvidenceJson)));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(digest),
                Encoding.ASCII.GetBytes(attempt.EvaluationEvidenceDigest)))
        {
            return new(attempt.Id, "Inconsistent", "The stored evaluation-evidence digest does not match its content.", history.Id);
        }

        TransitionEvaluationEvidence? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<TransitionEvaluationEvidence>(
                attempt.EvaluationEvidenceJson, StoredJsonOptions);
        }
        catch (JsonException exception)
        {
            return new(attempt.Id, "InsufficientEvidence",
                $"The stored evaluation evidence cannot be read: {exception.Message}", history.Id);
        }
        if (evidence is null)
        {
            return new(attempt.Id, "InsufficientEvidence", "The stored evaluation evidence is empty.", history.Id);
        }
        if (evidence.InstanceFacts is null || evidence.AttemptFacts is null ||
            evidence.AttemptFactProvenance is null || evidence.GuardFindings is null)
        {
            return new(attempt.Id, "InsufficientEvidence",
                "The stored evaluation evidence is missing one or more required bound-input collections.", history.Id);
        }
        if (evidence.DefinitionId != history.DefinitionId ||
            evidence.DefinitionVersion != history.DefinitionVersion ||
            evidence.InstanceId != history.InstanceId ||
            evidence.InstanceRevision != history.RevisionBefore ||
            evidence.Event != history.Event ||
            evidence.PreviousState != history.PreviousState ||
            evidence.ResultingState != history.NewState ||
            evidence.SelectedTransitionId != history.TransitionId ||
            evidence.Status != EvaluationStatus.Accepted ||
            evidence.CommitStatus != "Committed")
        {
            return new(attempt.Id, "Inconsistent",
                "The stored evaluation evidence does not describe the accepted transition.", history.Id);
        }

        try
        {
            var context = TransitionEvaluationContext.Create(
                evidence.InstanceFacts,
                evidence.AttemptFacts,
                evidence.DecisionEvidence,
                evidence.AttemptFactProvenance);
            var reproduced = TransitionEvaluator.Evaluate(definition, history.PreviousState, history.Event, context);
            var storedFindings = JsonSerializer.Deserialize<GuardFinding[]>(attempt.GuardFindingsJson, StoredJsonOptions) ?? [];
            if (reproduced.Status != EvaluationStatus.Accepted ||
                reproduced.TransitionId != history.TransitionId ||
                reproduced.TargetState != history.NewState ||
                reproduced.ReasonCode != evidence.ReasonCode ||
                !reproduced.GuardFindings.SequenceEqual(evidence.GuardFindings) ||
                !reproduced.GuardFindings.SequenceEqual(storedFindings))
            {
                return new(attempt.Id, "Inconsistent",
                    "Pure re-evaluation did not reproduce the stored accepted decision and guard findings.", history.Id);
            }
        }
        catch (ArgumentException exception)
        {
            return new(attempt.Id, "InsufficientEvidence",
                $"The bound historical facts cannot be evaluated: {exception.Message}", history.Id);
        }
        catch (JsonException exception)
        {
            return new(attempt.Id, "InsufficientEvidence",
                $"The stored guard findings cannot be read: {exception.Message}", history.Id);
        }

        return new(attempt.Id, "Verified", "Pure re-evaluation reproduced the stored transition using its bound evidence.", history.Id);
    }

    private static HistoricalReplayReport Unavailable(
        InstanceRecord instance,
        string mode,
        string outcome,
        string code,
        string message) =>
        new(instance.Id, instance.DefinitionId, instance.DefinitionVersion, null, mode, outcome, outcome,
            instance.CreatedAt, null, "Bound definition unavailable.", null, instance.CurrentState,
            null, instance.Revision, 0, 0, 0, 0, instance.Revision,
            [new ReplayFinding(code, message, [])],
            outcome == "UnsupportedHistoricalFormat" ? "NotAvailable" : "NotRequested",
            [],
            []);
}

public sealed record HistoricalReplayReport(
    string InstanceId,
    string DefinitionId,
    string DefinitionVersion,
    string? BoundDefinitionContentDigest,
    string Mode,
    string Outcome,
    string StateReconstructionOutcome,
    DateTimeOffset InstanceCreatedAt,
    string? InitialStateFromBoundDefinition,
    string InitialStateEvidence,
    string? ReconstructedFinalState,
    string RecordedCurrentState,
    long? ReconstructedRevision,
    long RecordedRevision,
    int AcceptedTransitionsExamined,
    int HistoricalAttemptsExamined,
    int InboundReceiptsExamined,
    int HistoricalTimersExamined,
    long InspectedProjectionRevision,
    IReadOnlyList<ReplayFinding> IntegrityFindings,
    string DecisionVerificationOutcome,
    IReadOnlyList<DecisionVerificationFinding> DecisionVerificationFindings,
    IReadOnlyList<string> EvidenceReferences);

public sealed record ReplayFinding(string Code, string Explanation, IReadOnlyList<string> EvidenceReferences);

public sealed record DecisionVerificationFinding(
    string AttemptId,
    string Outcome,
    string Explanation,
    string TransitionHistoryId);

public sealed record RecoveryAssessment(
    string InstanceId,
    string Classification,
    string CurrentState,
    long CurrentRevision,
    DateTimeOffset AssessedAt,
    long InspectedRevision,
    HistoricalReplayReport Replay,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<RecoveryInput> PendingLocalInputs,
    IReadOnlyList<RecoveryEffect> OutstandingEffects,
    IReadOnlyList<RecoveryTimer> Timers,
    IReadOnlyList<RecoveryExpectedOperation> ExpectedOperations);

public sealed record RecoveryInput(
    string Id,
    string Status,
    string ReasonCode,
    string? AttemptId,
    string RecoveryGuidance);

public sealed record RecoveryEffect(
    string OperationId,
    string Capability,
    string Status,
    string? HarnessExecutionId,
    string? LatestProviderOutcome,
    string? LatestObservationStatus,
    string RecoveryGuidance);

public sealed record RecoveryTimer(
    string TimerId,
    string Status,
    DateTimeOffset DueAt,
    bool Overdue,
    string? ResultReason);

public sealed record RecoveryExpectedOperation(
    string OperationId,
    string Status,
    long ExpectedRevision,
    string ObservationType);
