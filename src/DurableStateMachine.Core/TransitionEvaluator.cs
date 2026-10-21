namespace DurableStateMachine.Core;

public enum EvaluationStatus
{
    Accepted,
    Rejected,
    Ambiguous,
    Unresolved
}

public enum GuardStatus
{
    Enabled,
    Disabled,
    Unresolved
}

public sealed record GuardFinding(string TransitionId, GuardStatus Status, string Code, string Explanation);

public sealed record TransitionEvaluation(
    EvaluationStatus Status,
    string PreviousState,
    string Event,
    string? TransitionId,
    string? TargetState,
    IReadOnlyList<GuardFinding> GuardFindings,
    string ReasonCode,
    string Explanation);

public static class TransitionEvaluator
{
    public static TransitionEvaluation Evaluate(
        MachineDefinition definition,
        string currentState,
        string eventName,
        IReadOnlyDictionary<string, FactValue> facts)
        => Evaluate(definition, currentState, eventName, TransitionEvaluationContext.FromResolvedFacts(facts));

    public static TransitionEvaluation Evaluate(
        MachineDefinition definition,
        string currentState,
        string eventName,
        TransitionEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(context);

        if (!definition.States.TryGetValue(currentState, out var state))
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName, "unknown-current-state", $"State '{currentState}' is not in definition '{definition.Id}@{definition.Version}'.");
        }
        if (state.IsTerminal)
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName, "terminal-state", $"Terminal state '{currentState}' cannot accept further transitions.");
        }
        if (!definition.Events.Contains(eventName))
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName, "undeclared-event", $"Event '{eventName}' is not declared by definition '{definition.Id}@{definition.Version}'.");
        }

        return EvaluateTransitions(definition.States, definition.Events, definition.Transitions, currentState, eventName, context);
    }

    public static TransitionEvaluation EvaluateConcern(
        MachineConcern concern,
        string currentState,
        string eventName,
        TransitionEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(concern);
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(context);
        if (!concern.States.TryGetValue(currentState, out var state))
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName,
                "unknown-current-concern-state", $"State '{currentState}' is not declared by concern '{concern.Id}'.");
        }
        if (state.IsTerminal)
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName,
                "terminal-concern-state", $"Terminal state '{currentState}' of concern '{concern.Id}' cannot accept further transitions.");
        }
        if (!concern.Events.Contains(eventName))
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName,
                "undeclared-concern-event", $"Event '{eventName}' is not declared by concern '{concern.Id}'.");
        }
        return EvaluateTransitions(concern.States, concern.Events, concern.Transitions, currentState, eventName, context);
    }

    private static TransitionEvaluation EvaluateTransitions(
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        string currentState,
        string eventName,
        TransitionEvaluationContext context)
    {
        var candidates = transitions
            .Where(transition => transition.Source == currentState && transition.Event == eventName)
            .ToArray();
        if (candidates.Length == 0)
        {
            return Result(EvaluationStatus.Rejected, currentState, eventName, "no-applicable-transition", $"No transition from '{currentState}' accepts event '{eventName}'.");
        }

        var findings = new List<GuardFinding>(candidates.Length);
        var enabled = new List<MachineTransition>();
        var unresolved = false;
        foreach (var transition in candidates)
        {
            var guardResult = transition.Guard is null
                ? GuardResult.Enabled
                : EvaluateGuard(transition.Guard, context);
            findings.Add(new GuardFinding(transition.Id, guardResult.Status, guardResult.Code, guardResult.Explanation));
            if (guardResult.Status == GuardStatus.Enabled)
            {
                enabled.Add(transition);
            }
            else if (guardResult.Status == GuardStatus.Unresolved)
            {
                unresolved = true;
            }
        }

        if (enabled.Count > 1)
        {
            return new TransitionEvaluation(
                EvaluationStatus.Ambiguous, currentState, eventName, null, null, findings,
                "multiple-enabled-transitions",
                $"{enabled.Count} transitions are enabled for state '{currentState}' and event '{eventName}'.");
        }

        if (unresolved)
        {
            return new TransitionEvaluation(
                EvaluationStatus.Unresolved, currentState, eventName, null, null, findings,
                enabled.Count == 1 ? "unresolved-competing-guard" : "unresolved-guard",
                enabled.Count == 1
                    ? "A guard could not be evaluated, so the selected transition may not be unique."
                    : "At least one transition guard could not be evaluated.");
        }

        if (enabled.Count == 0)
        {
            return new TransitionEvaluation(
                EvaluationStatus.Rejected, currentState, eventName, null, null, findings,
                "guards-disabled", "All applicable transition guards evaluated to false.");
        }

        var selected = enabled[0];
        return new TransitionEvaluation(
            EvaluationStatus.Accepted, currentState, eventName, selected.Id, selected.Target, findings,
            "transition-enabled", selected.Description ?? $"Transition '{selected.Id}' is enabled.");
    }

    private static GuardResult EvaluateGuard(GuardExpression expression, TransitionEvaluationContext context)
    {
        switch (expression)
        {
            case GuardExpression.Presence presence:
                var present = context.ResolvedFacts.ContainsKey(presence.Fact);
                if (!present && context.UnresolvedFacts.TryGetValue(presence.Fact, out var presenceKnowledge) &&
                    presenceKnowledge is FactKnowledgeState.Unknown or FactKnowledgeState.Unavailable)
                {
                    var presenceCode = presenceKnowledge == FactKnowledgeState.Unknown ? "unknown-fact" : "unavailable-fact";
                    return GuardResult.UnresolvedResult(presenceCode,
                        $"Fact '{presence.Fact}' is {presenceKnowledge.ToString().ToLowerInvariant()}.");
                }
                var exists = presence.Operator == "exists";
                var matched = present == exists;
                return matched
                    ? GuardResult.Enabled
                    : new GuardResult(GuardStatus.Disabled, "presence-check-failed", $"Presence check '{presence.Operator}' for '{presence.Fact}' was false.");

            case GuardExpression.Comparison comparison:
                if (!context.ResolvedFacts.TryGetValue(comparison.Fact, out var actual))
                {
                    if (context.UnresolvedFacts.TryGetValue(comparison.Fact, out var knowledgeState))
                    {
                        var code = knowledgeState switch
                        {
                            FactKnowledgeState.Unknown => "unknown-fact",
                            FactKnowledgeState.Unavailable => "unavailable-fact",
                            _ => "missing-fact"
                        };
                        return GuardResult.UnresolvedResult(code, $"Fact '{comparison.Fact}' is {knowledgeState.ToString().ToLowerInvariant()}.");
                    }
                    return GuardResult.UnresolvedResult("missing-fact", $"Fact '{comparison.Fact}' is absent.");
                }
                if (actual.Kind == FactKind.Null)
                {
                    return GuardResult.UnresolvedResult("null-fact", $"Fact '{comparison.Fact}' is explicitly null and cannot be compared.");
                }
                if (actual.Kind != comparison.Value.Kind)
                {
                    return GuardResult.UnresolvedResult("incompatible-fact-type", $"Fact '{comparison.Fact}' has type {actual.Kind}; the comparison requires {comparison.Value.Kind}.");
                }
                return Compare(comparison, actual);

            case GuardExpression.Logical logical:
                var values = logical.Arguments.Select(argument => EvaluateGuard(argument, context)).ToArray();
                if (logical.Operator == "and")
                {
                    if (values.Any(value => value.Status == GuardStatus.Disabled))
                    {
                        var details = string.Join("; ", values
                            .Where(value => value.Status == GuardStatus.Disabled)
                            .Select(value => value.Explanation));
                        return new GuardResult(GuardStatus.Disabled, "logical-and-false",
                            $"At least one 'and' prerequisite is false: {details}");
                    }
                    if (values.Any(value => value.Status == GuardStatus.Unresolved))
                    {
                        var details = string.Join("; ", values
                            .Where(value => value.Status == GuardStatus.Unresolved)
                            .Select(value => value.Explanation));
                        return GuardResult.UnresolvedResult("logical-and-unresolved",
                            $"An 'and' prerequisite is unresolved: {details}");
                    }
                    return GuardResult.Enabled;
                }
                if (values.Any(value => value.Status == GuardStatus.Enabled))
                {
                    return GuardResult.Enabled;
                }
                if (values.Any(value => value.Status == GuardStatus.Unresolved))
                {
                    var details = string.Join("; ", values
                        .Where(value => value.Status == GuardStatus.Unresolved)
                        .Select(value => value.Explanation));
                    return GuardResult.UnresolvedResult("logical-or-unresolved",
                        $"An 'or' operand is unresolved: {details}");
                }
                var failedAlternatives = string.Join("; ", values.Select(value => value.Explanation));
                return new GuardResult(GuardStatus.Disabled, "logical-or-false",
                    $"Every 'or' alternative is false: {failedAlternatives}");

            case GuardExpression.Negation negation:
                var inner = EvaluateGuard(negation.Argument, context);
                return inner.Status switch
                {
                    GuardStatus.Enabled => new GuardResult(GuardStatus.Disabled, "logical-not-false", "The operand of 'not' is true."),
                    GuardStatus.Disabled => GuardResult.Enabled,
                    _ => GuardResult.UnresolvedResult("logical-not-unresolved", "The operand of 'not' is unresolved.")
                };

            case GuardExpression.ConcernState concernState:
                if (!context.ConcernStates.TryGetValue(concernState.Concern, out var currentConcernState))
                {
                    return GuardResult.UnresolvedResult("missing-concern-state",
                        $"Current state for concern '{concernState.Concern}' is unavailable.");
                }
                return currentConcernState == concernState.State
                    ? GuardResult.Enabled
                    : new GuardResult(GuardStatus.Disabled, "concern-state-mismatch",
                        $"Concern '{concernState.Concern}' is '{currentConcernState}', not '{concernState.State}'.");

            case GuardExpression.ParentState parentState:
                if (context.ParentState is null)
                {
                    return GuardResult.UnresolvedResult("missing-parent-state",
                        "The current parent state is unavailable.");
                }
                return context.ParentState == parentState.State
                    ? GuardResult.Enabled
                    : new GuardResult(GuardStatus.Disabled, "parent-state-mismatch",
                        $"Parent state is '{context.ParentState}', not '{parentState.State}'.");

            default:
                throw new InvalidOperationException($"Unsupported parsed guard type '{expression.GetType().Name}'.");
        }
    }

    private static GuardResult Compare(GuardExpression.Comparison comparison, FactValue actual)
    {
        var result = comparison.Operator switch
        {
            "eq" => actual == comparison.Value,
            "ne" => actual != comparison.Value,
            "gt" => actual.NumberValue > comparison.Value.NumberValue,
            "gte" => actual.NumberValue >= comparison.Value.NumberValue,
            "lt" => actual.NumberValue < comparison.Value.NumberValue,
            "lte" => actual.NumberValue <= comparison.Value.NumberValue,
            _ => throw new InvalidOperationException($"Unsupported comparison operator '{comparison.Operator}'.")
        };
        if (comparison.Operator is "eq" or "ne" && actual.Kind == FactKind.String)
        {
            var equal = string.Equals(actual.StringValue, comparison.Value.StringValue, StringComparison.Ordinal);
            result = comparison.Operator == "eq" ? equal : !equal;
        }
        return result
            ? GuardResult.Enabled
            : new GuardResult(GuardStatus.Disabled, "comparison-false", $"Comparison '{comparison.Operator}' for '{comparison.Fact}' was false.");
    }

    private static TransitionEvaluation Result(EvaluationStatus status, string state, string eventName, string code, string explanation) =>
        new(status, state, eventName, null, null, [], code, explanation);

    private readonly record struct GuardResult(GuardStatus Status, string Code, string Explanation)
    {
        public static GuardResult Enabled { get; } = new(GuardStatus.Enabled, "guard-true", "Guard evaluated to true.");
        public static GuardResult UnresolvedResult(string code, string explanation) => new(GuardStatus.Unresolved, code, explanation);
    }
}
