using System.Collections.ObjectModel;
using System.Text.Json;

namespace DurableStateMachine.Core;

public enum DecisionOutcomeState
{
    Present,
    ExplicitNull,
    Missing,
    Unknown,
    Unavailable
}

public enum FactKnowledgeState
{
    Missing,
    Unknown,
    Unavailable
}

public sealed record ExternalDecisionEvidence(
    string DecisionSystemIdentity,
    string? ExternalEvaluationId,
    DecisionOutcomeState OutcomeState,
    FactValue? Outcome,
    DateTimeOffset? EvaluatedAt,
    string? BusinessEffectiveAt,
    DateTimeOffset? KnowledgeCutoff,
    string? InputFactSetDigest,
    IReadOnlyList<string> PolicyVersionIdentities,
    string EvidenceDigest,
    string OriginalResponseJson,
    string Provenance,
    IReadOnlyList<string> MissingFacts,
    IReadOnlyList<string> UnknownFacts,
    IReadOnlyList<string> UnavailableFacts);

public sealed class TransitionEvaluationContext
{
    private TransitionEvaluationContext(
        IReadOnlyDictionary<string, FactValue> instanceFacts,
        IReadOnlyDictionary<string, FactValue> attemptFacts,
        IReadOnlyDictionary<string, string> attemptFactProvenance,
        IReadOnlyDictionary<string, FactValue> resolvedFacts,
        IReadOnlyDictionary<string, FactKnowledgeState> unresolvedFacts,
        ExternalDecisionEvidence? decisionEvidence)
    {
        InstanceFacts = instanceFacts;
        AttemptFacts = attemptFacts;
        AttemptFactProvenance = attemptFactProvenance;
        ResolvedFacts = resolvedFacts;
        UnresolvedFacts = unresolvedFacts;
        DecisionEvidence = decisionEvidence;
    }

    public IReadOnlyDictionary<string, FactValue> InstanceFacts { get; }
    public IReadOnlyDictionary<string, FactValue> AttemptFacts { get; }
    public IReadOnlyDictionary<string, string> AttemptFactProvenance { get; }
    public IReadOnlyDictionary<string, FactValue> ResolvedFacts { get; }
    public IReadOnlyDictionary<string, FactKnowledgeState> UnresolvedFacts { get; }
    public ExternalDecisionEvidence? DecisionEvidence { get; }

    public static TransitionEvaluationContext Create(
        IReadOnlyDictionary<string, FactValue> instanceFacts,
        IReadOnlyDictionary<string, FactValue>? attemptFacts = null,
        ExternalDecisionEvidence? decisionEvidence = null,
        IReadOnlyDictionary<string, string>? attemptFactProvenance = null)
    {
        ArgumentNullException.ThrowIfNull(instanceFacts);
        var durable = CopyFacts(instanceFacts, nameof(instanceFacts));
        var perAttempt = CopyFacts(attemptFacts ?? new Dictionary<string, FactValue>(), nameof(attemptFacts));
        var provenance = CopyProvenance(attemptFactProvenance ?? new Dictionary<string, string>(), perAttempt);

        foreach (var name in perAttempt.Keys)
        {
            if (durable.ContainsKey(name))
            {
                throw new ArgumentException($"Per-attempt fact '{name}' conflicts with an immutable instance fact.", nameof(attemptFacts));
            }
        }

        var evidenceSnapshot = CopyDecisionEvidence(decisionEvidence);
        var unresolved = BuildUnresolvedFactMap(evidenceSnapshot);
        foreach (var name in unresolved.Keys)
        {
            if (durable.ContainsKey(name) || perAttempt.ContainsKey(name))
            {
                throw new ArgumentException($"Fact '{name}' cannot be both bound and marked unresolved.", nameof(decisionEvidence));
            }
        }

        var resolved = new SortedDictionary<string, FactValue>(StringComparer.Ordinal);
        foreach (var pair in durable)
        {
            resolved.Add(pair.Key, pair.Value);
        }
        foreach (var pair in perAttempt)
        {
            resolved.Add(pair.Key, pair.Value);
        }

        return new TransitionEvaluationContext(
            durable,
            perAttempt,
            provenance,
            new ReadOnlyDictionary<string, FactValue>(resolved),
            new ReadOnlyDictionary<string, FactKnowledgeState>(unresolved),
            evidenceSnapshot);
    }

    public static TransitionEvaluationContext FromResolvedFacts(IReadOnlyDictionary<string, FactValue> facts) =>
        Create(new Dictionary<string, FactValue>(), facts);

    private static IReadOnlyDictionary<string, FactValue> CopyFacts(
        IReadOnlyDictionary<string, FactValue> facts,
        string parameterName)
    {
        var copy = new SortedDictionary<string, FactValue>(StringComparer.Ordinal);
        foreach (var (name, value) in facts)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Fact names must not be empty.", parameterName);
            }
            if (!Enum.IsDefined(value.Kind))
            {
                throw new ArgumentException($"Fact '{name}' has an unsupported value kind.", parameterName);
            }
            copy.Add(name, value);
        }
        return new ReadOnlyDictionary<string, FactValue>(copy);
    }

    private static IReadOnlyDictionary<string, string> CopyProvenance(
        IReadOnlyDictionary<string, string> provenance,
        IReadOnlyDictionary<string, FactValue> attemptFacts)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, source) in provenance)
        {
            if (!attemptFacts.ContainsKey(name))
            {
                throw new ArgumentException($"Provenance was supplied for unbound per-attempt fact '{name}'.", nameof(provenance));
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException($"Provenance for fact '{name}' must not be empty.", nameof(provenance));
            }
            copy.Add(name, source);
        }
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static SortedDictionary<string, FactKnowledgeState> BuildUnresolvedFactMap(
        ExternalDecisionEvidence? evidence)
    {
        var result = new SortedDictionary<string, FactKnowledgeState>(StringComparer.Ordinal);
        if (evidence is null)
        {
            return result;
        }

        if (string.IsNullOrWhiteSpace(evidence.DecisionSystemIdentity))
        {
            throw new ArgumentException("Decision system identity must not be empty.", nameof(evidence));
        }
        if (string.IsNullOrWhiteSpace(evidence.EvidenceDigest))
        {
            throw new ArgumentException("Decision evidence digest must not be empty.", nameof(evidence));
        }
        if (string.IsNullOrWhiteSpace(evidence.Provenance))
        {
            throw new ArgumentException("Decision evidence provenance must not be empty.", nameof(evidence));
        }
        if (string.IsNullOrWhiteSpace(evidence.OriginalResponseJson))
        {
            throw new ArgumentException("The original decision response snapshot is required.", nameof(evidence));
        }
        if (!Enum.IsDefined(evidence.OutcomeState))
        {
            throw new ArgumentException($"Unsupported decision outcome state '{evidence.OutcomeState}'.", nameof(evidence));
        }
        if (evidence.Outcome is { } outcome && !Enum.IsDefined(outcome.Kind))
        {
            throw new ArgumentException("Decision outcome has an unsupported value kind.", nameof(evidence));
        }
        if (evidence.PolicyVersionIdentities.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Policy version identities must not be empty.", nameof(evidence));
        }

        switch (evidence.OutcomeState)
        {
            case DecisionOutcomeState.Present when evidence.Outcome is null || evidence.Outcome.Value.Kind == FactKind.Null:
                throw new ArgumentException("A present decision outcome must have a non-null scalar value.", nameof(evidence));
            case DecisionOutcomeState.ExplicitNull when evidence.Outcome is not { Kind: FactKind.Null }:
                throw new ArgumentException("An explicit-null decision outcome must carry a null fact value.", nameof(evidence));
            case DecisionOutcomeState.Missing or DecisionOutcomeState.Unknown or DecisionOutcomeState.Unavailable
                when evidence.Outcome is not null:
                throw new ArgumentException("A missing, unknown or unavailable outcome cannot also carry a value.", nameof(evidence));
        }

        Add(evidence.MissingFacts, FactKnowledgeState.Missing);
        Add(evidence.UnknownFacts, FactKnowledgeState.Unknown);
        Add(evidence.UnavailableFacts, FactKnowledgeState.Unavailable);
        return result;

        void Add(IEnumerable<string> names, FactKnowledgeState state)
        {
            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || !result.TryAdd(name, state))
                {
                    throw new ArgumentException($"Decision evidence contains an empty or multiply classified fact name '{name}'.", nameof(evidence));
                }
            }
        }
    }

    private static ExternalDecisionEvidence? CopyDecisionEvidence(ExternalDecisionEvidence? evidence)
    {
        if (evidence is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(evidence.PolicyVersionIdentities);
        ArgumentNullException.ThrowIfNull(evidence.MissingFacts);
        ArgumentNullException.ThrowIfNull(evidence.UnknownFacts);
        ArgumentNullException.ThrowIfNull(evidence.UnavailableFacts);
        using var response = JsonDocument.Parse(evidence.OriginalResponseJson);
        if (response.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Decision response snapshot must be a JSON object.", nameof(evidence));
        }

        return evidence with
        {
            PolicyVersionIdentities = Array.AsReadOnly(evidence.PolicyVersionIdentities.ToArray()),
            MissingFacts = Array.AsReadOnly(evidence.MissingFacts.ToArray()),
            UnknownFacts = Array.AsReadOnly(evidence.UnknownFacts.ToArray()),
            UnavailableFacts = Array.AsReadOnly(evidence.UnavailableFacts.ToArray())
        };
    }
}

public sealed record TransitionEvaluationEvidence(
    string DefinitionId,
    string DefinitionVersion,
    string InstanceId,
    long InstanceRevision,
    string Event,
    DateTimeOffset EvaluatedAt,
    IReadOnlyDictionary<string, FactValue> InstanceFacts,
    IReadOnlyDictionary<string, FactValue> AttemptFacts,
    IReadOnlyDictionary<string, string> AttemptFactProvenance,
    IReadOnlyDictionary<string, FactValue> ResolvedFacts,
    IReadOnlyDictionary<string, FactKnowledgeState> UnresolvedFacts,
    ExternalDecisionEvidence? DecisionEvidence,
    IReadOnlyList<GuardFinding> GuardFindings,
    string CommitStatus,
    EvaluationStatus Status,
    string? SelectedTransitionId,
    string ReasonCode,
    string PreviousState,
    string? ResultingState);
