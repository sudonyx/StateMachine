using System.Collections.ObjectModel;
using System.Collections.Frozen;
using System.Text.Json;
using System.Xml;

namespace DurableStateMachine.Core;

public sealed record ValidationIssue(string Code, string Path, string Message);

public sealed class MachineDefinitionException(IReadOnlyList<ValidationIssue> issues)
    : Exception(string.Join(Environment.NewLine, issues.Select(issue => $"{issue.Path}: {issue.Message}")))
{
    public IReadOnlyList<ValidationIssue> Issues { get; } = issues;
}

public sealed record MachineState(string Id, bool IsTerminal);
public sealed record MachineTransition(
    string Id,
    string Source,
    string Event,
    string Target,
    string? Description,
    GuardExpression? Guard);
public sealed record MachineConcern(
    string Id,
    string InitialState,
    IReadOnlyDictionary<string, MachineState> States,
    IReadOnlySet<string> Events,
    IReadOnlyList<MachineTransition> Transitions);
public sealed record CommandInputMapping(string Name, string Event, string? Concern = null);
public sealed record ObservationInputMapping(
    string Name,
    string Event,
    IReadOnlyList<string> RequiredEvidence,
    string? Concern = null);
public sealed record MachineInputPolicy(
    IReadOnlyDictionary<string, CommandInputMapping> Commands,
    IReadOnlyDictionary<string, ObservationInputMapping> Observations);
public sealed record TimerEffectIntent(string Capability, string ObservationType, JsonElement Request);
public sealed record TimerDeclaration(
    string Id,
    string OnTransition,
    TimeSpan Duration,
    string Event,
    string WhileState,
    IReadOnlyList<string> CancelOnTransitions,
    IReadOnlyList<string> BlockedEvents,
    IReadOnlyList<string> InvalidatedByDueTimers,
    TimerEffectIntent? EffectIntent);

public sealed class MachineDefinition
{
    internal MachineDefinition(
        string id,
        string version,
        string languageVersion,
        string initialState,
        string? cancellationEvent,
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        MachineInputPolicy? inputPolicy,
        IReadOnlyList<TimerDeclaration> timers)
    {
        Id = id;
        Version = version;
        LanguageVersion = languageVersion;
        InitialState = initialState;
        CancellationEvent = cancellationEvent;
        States = states;
        Events = events;
        Transitions = transitions;
        Concerns = concerns;
        InputPolicy = inputPolicy;
        Timers = timers;
    }

    public string Id { get; }
    public string Version { get; }
    public string LanguageVersion { get; }
    public string InitialState { get; }
    public string? CancellationEvent { get; }
    public IReadOnlyDictionary<string, MachineState> States { get; }
    public IReadOnlySet<string> Events { get; }
    public IReadOnlyList<MachineTransition> Transitions { get; }
    public IReadOnlyDictionary<string, MachineConcern> Concerns { get; }
    public MachineInputPolicy? InputPolicy { get; }
    public IReadOnlyList<TimerDeclaration> Timers { get; }
}

public enum FactKind
{
    String,
    Number,
    Boolean,
    Null
}

public readonly record struct FactValue(FactKind Kind, string? StringValue, decimal NumberValue, bool BooleanValue)
{
    public static FactValue From(string value) => new(FactKind.String, value, default, default);
    public static FactValue From(decimal value) => new(FactKind.Number, null, value, default);
    public static FactValue From(bool value) => new(FactKind.Boolean, null, default, value);
    public static FactValue Null => new(FactKind.Null, null, default, default);

    internal static FactValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => From(element.GetString()!),
        JsonValueKind.Number when element.TryGetDecimal(out var number) => From(number),
        JsonValueKind.True => From(true),
        JsonValueKind.False => From(false),
        JsonValueKind.Null => Null,
        _ => throw new FormatException("Facts and guard values must be scalar strings, numbers, booleans or null.")
    };
}

public abstract record GuardExpression
{
    internal sealed record Comparison(string Operator, string Fact, FactValue Value) : GuardExpression;
    internal sealed record Presence(string Operator, string Fact) : GuardExpression;
    internal sealed record Logical(string Operator, IReadOnlyList<GuardExpression> Arguments) : GuardExpression;
    internal sealed record Negation(GuardExpression Argument) : GuardExpression;
    internal sealed record ConcernState(string Concern, string State) : GuardExpression;
    internal sealed record ParentState(string State) : GuardExpression;
}

public static class MachineDefinitionLoader
{
    private static readonly string[] RootProperties =
        ["languageVersion", "id", "version", "initialState", "states", "events", "transitions", "inputPolicy", "timers", "concerns", "cancellationEvent"];

    public static MachineDefinition Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var issues = new List<ValidationIssue>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new MachineDefinitionException([new("invalid-json", "$", exception.Message)]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new MachineDefinitionException([new("invalid-root", "$", "The definition must be a JSON object.")]);
            }

            CheckProperties(root, RootProperties, "$", issues);
            var languageVersion = ReadString(root, "languageVersion", "$.languageVersion", issues);
            if (languageVersion is null && !root.TryGetProperty("languageVersion", out _))
            {
                issues.Add(new("missing-property", "$.languageVersion", "Required property 'languageVersion' is missing."));
            }
            else if (languageVersion is not null && languageVersion is not ("1.0" or "1.1" or "1.2" or "1.3"))
            {
                issues.Add(new("unsupported-language-version", "$.languageVersion", $"Language version '{languageVersion}' is not supported; expected '1.0', '1.1', '1.2' or '1.3'."));
            }
            if (languageVersion == "1.0" && root.TryGetProperty("inputPolicy", out _))
            {
                issues.Add(new("input-policy-version", "$.inputPolicy", "Input policies require language version '1.1'."));
            }
            if (languageVersion is not ("1.2" or "1.3") && root.TryGetProperty("timers", out _))
            {
                issues.Add(new("timer-version", "$.timers", "Timer declarations require language version '1.2' or '1.3'."));
            }
            if (languageVersion is not "1.3" && root.TryGetProperty("concerns", out _))
            {
                issues.Add(new("concern-version", "$.concerns", "Concern declarations require language version '1.3'."));
            }
            if (languageVersion is not "1.3" && root.TryGetProperty("cancellationEvent", out _))
            {
                issues.Add(new("cancellation-version", "$.cancellationEvent",
                    "Cancellation declarations require language version '1.3'."));
            }

            var id = ReadRequiredString(root, "id", "$.id", issues);
            var version = ReadRequiredString(root, "version", "$.version", issues);
            var initialState = ReadRequiredString(root, "initialState", "$.initialState", issues);
            var cancellationEvent = ReadString(root, "cancellationEvent", "$.cancellationEvent", issues);

            var states = ReadStates(root, issues);
            var events = ReadEvents(root, issues);
            var transitions = ReadTransitions(root, issues);
            var concerns = ReadConcerns(root, languageVersion, issues);
            var inputPolicy = ReadInputPolicy(root, languageVersion, events, concerns, issues);
            var timers = ReadTimers(root, languageVersion, states, events, transitions, inputPolicy, issues);

            if (states.Values.All(state => !state.IsTerminal))
            {
                issues.Add(new("missing-terminal-state", "$.states", "At least one state must be terminal."));
            }

            if (initialState is not null && !states.ContainsKey(initialState))
            {
                issues.Add(new("unknown-initial-state", "$.initialState", $"Initial state '{initialState}' is not declared."));
            }

            ValidateTransitions(states, events, transitions, issues);
            ValidateConcernReferences(transitions, concerns, "$.transitions", issues);
            ValidateParentStateReferences(transitions, states, languageVersion, "$.transitions", issues);
            foreach (var concern in concerns.Values)
            {
                ValidateConcernReferences(concern.Transitions, concerns, $"$.concerns[{concern.Id}].transitions", issues);
                ValidateParentStateReferences(
                    concern.Transitions, states, languageVersion, $"$.concerns[{concern.Id}].transitions", issues);
            }
            ValidateCancellation(cancellationEvent, events, concerns, issues);
            ValidateTimers(timers, states, events, transitions, inputPolicy, issues);
            if (timers.Count > 0 && inputPolicy is null)
            {
                issues.Add(new("timer-input-policy-required", "$.inputPolicy",
                    "Definitions with timers must declare an input policy so timer events cannot be submitted by ordinary clients."));
            }
            if (issues.Count != 0)
            {
                throw new MachineDefinitionException(issues);
            }

            return new MachineDefinition(
                id!,
                version!,
                languageVersion!,
                initialState!,
                cancellationEvent,
                new ReadOnlyDictionary<string, MachineState>(states),
                events.ToFrozenSet(StringComparer.Ordinal),
                transitions.AsReadOnly(),
                new ReadOnlyDictionary<string, MachineConcern>(concerns),
                inputPolicy,
                timers.AsReadOnly());
        }
    }

    private static void ValidateParentStateReferences(
        IReadOnlyList<MachineTransition> transitions,
        IReadOnlyDictionary<string, MachineState> states,
        string? languageVersion,
        string path,
        List<ValidationIssue> issues)
    {
        static IEnumerable<GuardExpression.ParentState> Find(GuardExpression? guard)
        {
            if (guard is GuardExpression.ParentState state)
            {
                yield return state;
            }
            else if (guard is GuardExpression.Logical logical)
            {
                foreach (var child in logical.Arguments)
                {
                    foreach (var match in Find(child))
                    {
                        yield return match;
                    }
                }
            }
            else if (guard is GuardExpression.Negation negation)
            {
                foreach (var match in Find(negation.Argument))
                {
                    yield return match;
                }
            }
        }

        foreach (var transition in transitions)
        {
            foreach (var stateReference in Find(transition.Guard))
            {
                if (languageVersion is not "1.3")
                {
                    issues.Add(new("parent-state-guard-version", $"{path}[{transition.Id}].guard",
                        "Parent-state guards require language version '1.3'."));
                }
                if (!states.ContainsKey(stateReference.State))
                {
                    issues.Add(new("unknown-guard-parent-state", $"{path}[{transition.Id}].guard",
                        $"Guard references undeclared parent state '{stateReference.State}'."));
                }
            }
        }
    }

    private static void ValidateCancellation(
        string? cancellationEvent,
        IReadOnlySet<string> events,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        List<ValidationIssue> issues)
    {
        if (cancellationEvent is null)
        {
            return;
        }
        if (!events.Contains(cancellationEvent))
        {
            issues.Add(new("undeclared-cancellation-event", "$.cancellationEvent",
                $"Cancellation event '{cancellationEvent}' is not declared by the parent machine."));
            return;
        }
        foreach (var concern in concerns.Values)
        {
            foreach (var state in concern.States.Values.Where(state => !state.IsTerminal))
            {
                var matching = concern.Transitions.Where(transition =>
                    transition.Source == state.Id && transition.Event == cancellationEvent).ToArray();
                if (matching.Length != 1 || matching[0].Guard is not null ||
                    !concern.States.TryGetValue(matching[0].Target, out var target) || !target.IsTerminal)
                {
                    issues.Add(new("invalid-concern-cancellation",
                        $"$.concerns[{concern.Id}].transitions",
                        $"Nonterminal state '{state.Id}' must have exactly one unguarded '{cancellationEvent}' transition to a terminal state."));
                }
            }
        }
    }

    private static List<TimerDeclaration> ReadTimers(
        JsonElement root,
        string? languageVersion,
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        MachineInputPolicy? inputPolicy,
        List<ValidationIssue> issues)
    {
        var timers = new List<TimerDeclaration>();
        if (!root.TryGetProperty("timers", out var values))
        {
            return timers;
        }
        if (languageVersion is not ("1.2" or "1.3"))
        {
            return timers;
        }
        if (values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("invalid-timers", "$.timers", "Timers must be an array."));
            return timers;
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.timers[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-timer", path, "Each timer must be an object."));
                continue;
            }

            CheckProperties(value, ["id", "onTransition", "duration", "event", "whileState",
                "cancelOnTransitions", "blockedEvents", "invalidatedByDueTimers", "effectIntent"], path, issues);
            var id = ReadRequiredString(value, "id", path + ".id", issues);
            var onTransition = ReadRequiredString(value, "onTransition", path + ".onTransition", issues);
            var durationText = ReadRequiredString(value, "duration", path + ".duration", issues);
            var eventName = ReadRequiredString(value, "event", path + ".event", issues);
            var whileState = ReadRequiredString(value, "whileState", path + ".whileState", issues);
            TimeSpan duration = default;
            if (durationText is not null)
            {
                try
                {
                    duration = XmlConvert.ToTimeSpan(durationText);
                    if (duration <= TimeSpan.Zero)
                    {
                        issues.Add(new("invalid-timer-duration", path + ".duration", "Timer duration must be positive."));
                    }
                }
                catch (Exception exception) when (exception is FormatException or OverflowException)
                {
                    issues.Add(new("invalid-timer-duration", path + ".duration", "Timer duration must be a positive ISO-8601 duration."));
                }
            }

            var cancelOnTransitions = ReadStringArray(value, "cancelOnTransitions", path, issues);
            var blockedEvents = ReadStringArray(value, "blockedEvents", path, issues);
            var invalidatedByDueTimers = ReadStringArray(value, "invalidatedByDueTimers", path, issues);
            TimerEffectIntent? effectIntent = null;
            if (value.TryGetProperty("effectIntent", out var effectElement))
            {
                effectIntent = ReadTimerEffectIntent(effectElement, path + ".effectIntent", inputPolicy, issues);
            }

            if (id is not null && !identifiers.Add(id))
            {
                issues.Add(new("duplicate-timer-id", path + ".id", $"Timer '{id}' is declared more than once."));
            }
            if (onTransition is not null && !transitions.Any(transition => transition.Id == onTransition))
            {
                issues.Add(new("unknown-timer-trigger", path + ".onTransition", $"Transition '{onTransition}' is not declared."));
            }
            if (eventName is not null && !events.Contains(eventName))
            {
                issues.Add(new("undeclared-timer-event", path + ".event", $"Event '{eventName}' is not declared."));
            }
            if (whileState is not null && !states.ContainsKey(whileState))
            {
                issues.Add(new("unknown-timer-state", path + ".whileState", $"State '{whileState}' is not declared."));
            }

            if (id is not null && onTransition is not null && duration > TimeSpan.Zero &&
                eventName is not null && whileState is not null)
            {
                timers.Add(new TimerDeclaration(id, onTransition, duration, eventName, whileState,
                    cancelOnTransitions, blockedEvents, invalidatedByDueTimers, effectIntent));
            }
        }
        return timers;
    }

    private static TimerEffectIntent? ReadTimerEffectIntent(
        JsonElement element,
        string path,
        MachineInputPolicy? inputPolicy,
        List<ValidationIssue> issues)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("invalid-timer-effect-intent", path, "Effect intent must be an object."));
            return null;
        }
        CheckProperties(element, ["capability", "observationType", "request"], path, issues);
        var capability = ReadRequiredString(element, "capability", path + ".capability", issues);
        var observationType = ReadRequiredString(element, "observationType", path + ".observationType", issues);
        if (!element.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("invalid-timer-effect-request", path + ".request", "Effect request must be a JSON object."));
        }
        if (observationType is not null &&
            (inputPolicy is null || !inputPolicy.Observations.ContainsKey(observationType)))
        {
            issues.Add(new("undeclared-timer-observation", path + ".observationType",
                $"Observation '{observationType}' is not declared by the input policy."));
        }
        return capability is null || observationType is null || request.ValueKind != JsonValueKind.Object
            ? null
            : new TimerEffectIntent(capability, observationType, request.Clone());
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement value,
        string property,
        string parentPath,
        List<ValidationIssue> issues)
    {
        var path = parentPath + "." + property;
        if (!value.TryGetProperty(property, out var items))
        {
            return [];
        }
        if (items.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("invalid-timer-list", path, $"'{property}' must be an array of strings."));
            return [];
        }
        var result = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                issues.Add(new("invalid-timer-list-item", path, $"'{property}' entries must be non-empty strings."));
                continue;
            }
            var text = item.GetString()!;
            if (result.Contains(text, StringComparer.Ordinal))
            {
                issues.Add(new("duplicate-timer-list-item", path, $"'{text}' is declared more than once."));
            }
            else
            {
                result.Add(text);
            }
        }
        return result.AsReadOnly();
    }

    private static void ValidateTimers(
        IReadOnlyList<TimerDeclaration> timers,
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        MachineInputPolicy? inputPolicy,
        List<ValidationIssue> issues)
    {
        foreach (var timer in timers)
        {
            var path = $"$.timers[{timer.Id}]";
            var trigger = transitions.SingleOrDefault(transition => transition.Id == timer.OnTransition);
            if (trigger is not null && trigger.Target != timer.WhileState)
            {
                issues.Add(new("timer-trigger-state-mismatch", path + ".whileState",
                    $"Trigger transition '{trigger.Id}' targets '{trigger.Target}', not '{timer.WhileState}'."));
            }
            if (states.TryGetValue(timer.WhileState, out var state) && state.IsTerminal)
            {
                issues.Add(new("timer-terminal-state", path + ".whileState",
                    "A timer cannot remain active in a terminal state."));
            }
            if (inputPolicy is not null &&
                (inputPolicy.Commands.Values.Any(mapping => mapping.Event == timer.Event) ||
                 inputPolicy.Observations.Values.Any(mapping => mapping.Event == timer.Event)))
            {
                issues.Add(new("timer-event-input-mapping", path + ".event",
                    $"Timer event '{timer.Event}' cannot be mapped to a client command or observation."));
            }
            foreach (var transition in timer.CancelOnTransitions)
            {
                if (!transitions.Any(candidate => candidate.Id == transition))
                {
                    issues.Add(new("unknown-timer-cancellation-transition", path + ".cancelOnTransitions",
                        $"Transition '{transition}' is not declared."));
                }
            }
            foreach (var eventName in timer.BlockedEvents)
            {
                if (!events.Contains(eventName))
                {
                    issues.Add(new("unknown-timer-blocked-event", path + ".blockedEvents",
                        $"Event '{eventName}' is not declared."));
                }
            }
            foreach (var invalidatingTimer in timer.InvalidatedByDueTimers)
            {
                if (!timers.Any(candidate => candidate.Id == invalidatingTimer))
                {
                    issues.Add(new("unknown-timer-invalidation", path + ".invalidatedByDueTimers",
                        $"Timer '{invalidatingTimer}' is not declared."));
                }
                if (invalidatingTimer == timer.Id)
                {
                    issues.Add(new("self-invalidating-timer", path + ".invalidatedByDueTimers",
                        "A timer cannot invalidate itself."));
                }
            }
        }
    }

    private static MachineInputPolicy? ReadInputPolicy(
        JsonElement root,
        string? languageVersion,
        IReadOnlySet<string> events,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        List<ValidationIssue> issues)
    {
        if (!root.TryGetProperty("inputPolicy", out var policy))
        {
            return null;
        }
        if (policy.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new("invalid-input-policy", "$.inputPolicy", "Input policy must be an object."));
            return null;
        }

        CheckProperties(policy, ["commands", "observations"], "$.inputPolicy", issues);
        var commands = ReadCommandMappings(policy, languageVersion, events, concerns, issues);
        var observations = ReadObservationMappings(policy, languageVersion, events, concerns, issues);
        if (languageVersion is not ("1.1" or "1.2" or "1.3"))
        {
            return null;
        }
        return new MachineInputPolicy(
            new ReadOnlyDictionary<string, CommandInputMapping>(commands),
            new ReadOnlyDictionary<string, ObservationInputMapping>(observations));
    }

    private static Dictionary<string, CommandInputMapping> ReadCommandMappings(
        JsonElement policy,
        string? languageVersion,
        IReadOnlySet<string> events,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        List<ValidationIssue> issues)
    {
        var commands = new Dictionary<string, CommandInputMapping>(StringComparer.Ordinal);
        if (!policy.TryGetProperty("commands", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("invalid-command-mappings", "$.inputPolicy.commands", "Commands must be an array."));
            return commands;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.inputPolicy.commands[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-command-mapping", path, "Each command mapping must be an object."));
                continue;
            }
            CheckProperties(value, ["name", "event", "concern"], path, issues);
            var name = ReadRequiredString(value, "name", path + ".name", issues);
            var eventName = ReadRequiredString(value, "event", path + ".event", issues);
            var concernId = ReadString(value, "concern", path + ".concern", issues);
            ValidateInputTarget(name, eventName, concernId, languageVersion, events, concerns, path, issues);
            if (name is not null && eventName is not null &&
                !commands.TryAdd(name, new CommandInputMapping(name, eventName, concernId)))
            {
                issues.Add(new("duplicate-command-mapping", path + ".name", $"Command '{name}' is declared more than once."));
            }
        }
        return commands;
    }

    private static Dictionary<string, ObservationInputMapping> ReadObservationMappings(
        JsonElement policy,
        string? languageVersion,
        IReadOnlySet<string> events,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        List<ValidationIssue> issues)
    {
        var observations = new Dictionary<string, ObservationInputMapping>(StringComparer.Ordinal);
        if (!policy.TryGetProperty("observations", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("invalid-observation-mappings", "$.inputPolicy.observations", "Observations must be an array."));
            return observations;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.inputPolicy.observations[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-observation-mapping", path, "Each observation mapping must be an object."));
                continue;
            }
            CheckProperties(value, ["name", "event", "requiredEvidence", "concern"], path, issues);
            var name = ReadRequiredString(value, "name", path + ".name", issues);
            var eventName = ReadRequiredString(value, "event", path + ".event", issues);
            var concernId = ReadString(value, "concern", path + ".concern", issues);
            ValidateInputTarget(name, eventName, concernId, languageVersion, events, concerns, path, issues);

            var requiredEvidence = new List<string>();
            if (!value.TryGetProperty("requiredEvidence", out var required) || required.ValueKind != JsonValueKind.Array)
            {
                issues.Add(new("invalid-required-evidence", path + ".requiredEvidence", "Required evidence must be an array of strings."));
            }
            else
            {
                foreach (var item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        issues.Add(new("invalid-required-evidence", path + ".requiredEvidence", "Evidence names must be non-empty strings."));
                        continue;
                    }
                    var evidenceName = item.GetString()!;
                    if (requiredEvidence.Contains(evidenceName, StringComparer.Ordinal))
                    {
                        issues.Add(new("duplicate-required-evidence", path + ".requiredEvidence", $"Evidence '{evidenceName}' is declared more than once."));
                    }
                    else
                    {
                        requiredEvidence.Add(evidenceName);
                    }
                }
            }

            if (name is not null && eventName is not null &&
                !observations.TryAdd(name, new ObservationInputMapping(name, eventName, requiredEvidence.AsReadOnly(), concernId)))
            {
                issues.Add(new("duplicate-observation-mapping", path + ".name", $"Observation '{name}' is declared more than once."));
            }
        }
        return observations;
    }

    private static void ValidateInputTarget(
        string? name,
        string? eventName,
        string? concernId,
        string? languageVersion,
        IReadOnlySet<string> events,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        string path,
        List<ValidationIssue> issues)
    {
        if (concernId is null)
        {
            if (eventName is not null && !events.Contains(eventName))
            {
                issues.Add(new("undeclared-input-event", path + ".event", $"Event '{eventName}' is not declared."));
            }
            return;
        }
        if (languageVersion != "1.3")
        {
            issues.Add(new("concern-input-version", path + ".concern", "Concern input routing requires language version '1.3'."));
        }
        if (!concerns.TryGetValue(concernId, out var concern))
        {
            issues.Add(new("unknown-input-concern", path + ".concern", $"Concern '{concernId}' is not declared."));
        }
        else if (eventName is not null && !concern.Events.Contains(eventName))
        {
            issues.Add(new("undeclared-concern-input-event", path + ".event",
                $"Event '{eventName}' is not declared by concern '{concernId}'."));
        }
    }

    private static Dictionary<string, MachineConcern> ReadConcerns(
        JsonElement root,
        string? languageVersion,
        List<ValidationIssue> issues)
    {
        var concerns = new Dictionary<string, MachineConcern>(StringComparer.Ordinal);
        if (!root.TryGetProperty("concerns", out var values))
        {
            return concerns;
        }
        if (values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("invalid-concerns", "$.concerns", "Concerns must be an array."));
            return concerns;
        }
        if (languageVersion != "1.3")
        {
            return concerns;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.concerns[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-concern", path, "Each concern must be an object."));
                continue;
            }

            CheckProperties(value, ["id", "initialState", "states", "events", "transitions"], path, issues);
            var id = ReadRequiredString(value, "id", path + ".id", issues);
            var initialState = ReadRequiredString(value, "initialState", path + ".initialState", issues);
            var states = ReadStates(value, issues, path + ".states");
            var events = ReadEvents(value, issues, path + ".events");
            var transitions = ReadTransitions(value, issues, path + ".transitions");
            if (states.Values.All(state => !state.IsTerminal))
            {
                issues.Add(new("missing-concern-terminal-state", path + ".states",
                    "Each concern must declare at least one terminal state."));
            }
            if (initialState is not null && !states.ContainsKey(initialState))
            {
                issues.Add(new("unknown-concern-initial-state", path + ".initialState",
                    $"Initial state '{initialState}' is not declared by concern '{id}'."));
            }
            ValidateTransitions(states, events, transitions, issues, path + ".transitions");

            if (id is not null)
            {
                var concern = new MachineConcern(id, initialState ?? string.Empty,
                    new ReadOnlyDictionary<string, MachineState>(states),
                    events.ToFrozenSet(StringComparer.Ordinal),
                    transitions.AsReadOnly());
                if (!concerns.TryAdd(id, concern))
                {
                    issues.Add(new("duplicate-concern-id", path + ".id", $"Concern '{id}' is declared more than once."));
                }
            }
        }
        return concerns;
    }

    private static void ValidateConcernReferences(
        IReadOnlyList<MachineTransition> transitions,
        IReadOnlyDictionary<string, MachineConcern> concerns,
        string path,
        List<ValidationIssue> issues)
    {
        static IEnumerable<GuardExpression.ConcernState> Find(GuardExpression? guard)
        {
            if (guard is GuardExpression.ConcernState state)
            {
                yield return state;
            }
            else if (guard is GuardExpression.Logical logical)
            {
                foreach (var child in logical.Arguments)
                {
                    foreach (var match in Find(child))
                    {
                        yield return match;
                    }
                }
            }
            else if (guard is GuardExpression.Negation negation)
            {
                foreach (var match in Find(negation.Argument))
                {
                    yield return match;
                }
            }
        }

        foreach (var transition in transitions)
        {
            foreach (var stateReference in Find(transition.Guard))
            {
                var guardPath = $"{path}[{transition.Id}].guard";
                if (!concerns.TryGetValue(stateReference.Concern, out var concern))
                {
                    issues.Add(new("unknown-guard-concern", guardPath,
                        $"Guard references undeclared concern '{stateReference.Concern}'."));
                }
                else if (!concern.States.ContainsKey(stateReference.State))
                {
                    issues.Add(new("unknown-guard-concern-state", guardPath,
                        $"Guard references unknown state '{stateReference.State}' of concern '{stateReference.Concern}'."));
                }
            }
        }
    }

    private static Dictionary<string, MachineState> ReadStates(
        JsonElement root,
        List<ValidationIssue> issues,
        string path = "$.states")
    {
        var states = new Dictionary<string, MachineState>(StringComparer.Ordinal);
        if (!root.TryGetProperty("states", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("missing-states", path, "States must be an array."));
            return states;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            index++;
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-state", itemPath, "Each state must be an object."));
                continue;
            }

            CheckProperties(value, ["id", "terminal"], itemPath, issues);
            var id = ReadRequiredString(value, "id", $"{itemPath}.id", issues);
            var terminal = false;
            if (value.TryGetProperty("terminal", out var terminalValue))
            {
                if (terminalValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    terminal = terminalValue.GetBoolean();
                }
                else
                {
                    issues.Add(new("invalid-terminal-flag", $"{itemPath}.terminal", "Terminal must be a boolean."));
                }
            }

            if (id is not null && !states.TryAdd(id, new MachineState(id, terminal)))
            {
                issues.Add(new("duplicate-state", $"{itemPath}.id", $"State '{id}' is declared more than once."));
            }
        }

        return states;
    }

    private static HashSet<string> ReadEvents(JsonElement root, List<ValidationIssue> issues, string path = "$.events")
    {
        var events = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("events", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("missing-events", path, "Events must be an array of strings."));
            return events;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                issues.Add(new("invalid-event", $"{path}[{index}]", "Event identifiers must be non-empty strings."));
            }
            else if (!events.Add(value.GetString()!))
            {
                issues.Add(new("duplicate-event", $"{path}[{index}]", $"Event '{value.GetString()}' is declared more than once."));
            }

            index++;
        }

        return events;
    }

    private static List<MachineTransition> ReadTransitions(
        JsonElement root,
        List<ValidationIssue> issues,
        string path = "$.transitions")
    {
        var transitions = new List<MachineTransition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("transitions", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("missing-transitions", path, "Transitions must be an array."));
            return transitions;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var itemPath = $"{path}[{index}]";
            index++;
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-transition", itemPath, "Each transition must be an object."));
                continue;
            }

            CheckProperties(value, ["id", "source", "event", "target", "description", "guard"], itemPath, issues);
            var id = ReadRequiredString(value, "id", $"{itemPath}.id", issues);
            var source = ReadRequiredString(value, "source", $"{itemPath}.source", issues);
            var eventName = ReadRequiredString(value, "event", $"{itemPath}.event", issues);
            var target = ReadRequiredString(value, "target", $"{itemPath}.target", issues);
            var description = ReadString(value, "description", $"{itemPath}.description", issues);
            GuardExpression? guard = null;
            if (value.TryGetProperty("guard", out var guardElement))
            {
                guard = ParseGuard(guardElement, $"{itemPath}.guard", issues);
            }

            if (id is not null && !ids.Add(id))
            {
                issues.Add(new("duplicate-transition-id", $"{itemPath}.id", $"Transition '{id}' is declared more than once."));
            }

            if (id is not null && source is not null && eventName is not null && target is not null)
            {
                transitions.Add(new MachineTransition(id, source, eventName, target, description, guard));
            }
        }

        return transitions;
    }

    private static GuardExpression? ParseGuard(JsonElement element, string path, List<ValidationIssue> issues)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("op", out var operatorElement) ||
            operatorElement.ValueKind != JsonValueKind.String)
        {
            issues.Add(new("invalid-guard", path, "A guard must be an object containing a string 'op'."));
            return null;
        }

        var op = operatorElement.GetString()!;
        switch (op)
        {
            case "exists":
            case "missing":
                CheckProperties(element, ["op", "fact"], path, issues);
                var presenceFact = ReadRequiredString(element, "fact", $"{path}.fact", issues);
                return presenceFact is null ? null : new GuardExpression.Presence(op, presenceFact);

            case "eq":
            case "ne":
            case "gt":
            case "gte":
            case "lt":
            case "lte":
                CheckProperties(element, ["op", "fact", "value"], path, issues);
                var fact = ReadRequiredString(element, "fact", $"{path}.fact", issues);
                if (!element.TryGetProperty("value", out var valueElement))
                {
                    issues.Add(new("missing-guard-value", $"{path}.value", "A comparison guard requires a value."));
                    return null;
                }

                try
                {
                    var value = FactValue.FromJson(valueElement);
                    if (value.Kind == FactKind.Null)
                    {
                        issues.Add(new("invalid-guard-value", $"{path}.value", "Null cannot be used as a comparison literal."));
                        return null;
                    }
                    if (op is "gt" or "gte" or "lt" or "lte" && value.Kind != FactKind.Number)
                    {
                        issues.Add(new("invalid-guard-value", $"{path}.value", $"Operator '{op}' requires a numeric value."));
                        return null;
                    }
                    return fact is null ? null : new GuardExpression.Comparison(op, fact, value);
                }
                catch (FormatException exception)
                {
                    issues.Add(new("invalid-guard-value", $"{path}.value", exception.Message));
                    return null;
                }

            case "and":
            case "or":
                CheckProperties(element, ["op", "args"], path, issues);
                if (!element.TryGetProperty("args", out var arguments) || arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() < 2)
                {
                    issues.Add(new("invalid-guard-arguments", $"{path}.args", $"Operator '{op}' requires at least two guard arguments."));
                    return null;
                }
                var parsedArguments = arguments.EnumerateArray()
                    .Select((argument, argumentIndex) => ParseGuard(argument, $"{path}.args[{argumentIndex}]", issues))
                    .Where(argument => argument is not null)
                    .Cast<GuardExpression>()
                    .ToArray();
                return parsedArguments.Length == arguments.GetArrayLength()
                    ? new GuardExpression.Logical(op, parsedArguments)
                    : null;

            case "not":
                CheckProperties(element, ["op", "arg"], path, issues);
                if (!element.TryGetProperty("arg", out var argument))
                {
                    issues.Add(new("missing-guard-argument", $"{path}.arg", "Operator 'not' requires one guard argument."));
                    return null;
                }
                var parsedArgument = ParseGuard(argument, $"{path}.arg", issues);
                return parsedArgument is null ? null : new GuardExpression.Negation(parsedArgument);

            case "concernState":
                CheckProperties(element, ["op", "concern", "state"], path, issues);
                var concern = ReadRequiredString(element, "concern", path + ".concern", issues);
                var state = ReadRequiredString(element, "state", path + ".state", issues);
                return concern is null || state is null ? null : new GuardExpression.ConcernState(concern, state);

            case "parentState":
                CheckProperties(element, ["op", "state"], path, issues);
                var parentState = ReadRequiredString(element, "state", path + ".state", issues);
                return parentState is null ? null : new GuardExpression.ParentState(parentState);

            default:
                issues.Add(new("unsupported-guard-operator", $"{path}.op", $"Guard operator '{op}' is not supported."));
                return null;
        }
    }

    private static void ValidateTransitions(
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        List<ValidationIssue> issues,
        string pathPrefix = "$.transitions")
    {
        foreach (var transition in transitions)
        {
            var path = $"{pathPrefix}[{transition.Id}]";
            if (!states.TryGetValue(transition.Source, out var source))
            {
                issues.Add(new("unknown-transition-source", path + ".source", $"Source state '{transition.Source}' is not declared."));
            }
            else if (source.IsTerminal)
            {
                issues.Add(new("terminal-state-transition", path + ".source", $"Terminal state '{transition.Source}' cannot have outgoing transitions."));
            }
            if (!states.ContainsKey(transition.Target))
            {
                issues.Add(new("unknown-transition-target", path + ".target", $"Target state '{transition.Target}' is not declared."));
            }
            if (!events.Contains(transition.Event))
            {
                issues.Add(new("undeclared-transition-event", path + ".event", $"Event '{transition.Event}' is not declared."));
            }
        }

        foreach (var group in transitions.GroupBy(transition => (transition.Source, transition.Event)))
        {
            if (group.Count(transition => transition.Guard is null) > 1)
            {
                issues.Add(new(
                    "ambiguous-unguarded-transitions",
                    pathPrefix,
                    $"More than one unguarded transition is declared for state '{group.Key.Source}' and event '{group.Key.Event}'."));
            }
        }
    }

    private static void CheckProperties(JsonElement element, IEnumerable<string> allowed, string path, List<ValidationIssue> issues)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                issues.Add(new("duplicate-property", path + "." + property.Name, $"Property '{property.Name}' is declared more than once."));
            }
            if (!allowedSet.Contains(property.Name))
            {
                issues.Add(new("unknown-property", path + "." + property.Name, $"Property '{property.Name}' is not supported."));
            }
        }
    }

    private static string? ReadRequiredString(JsonElement element, string property, string path, List<ValidationIssue> issues)
    {
        var value = ReadString(element, property, path, issues);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!element.TryGetProperty(property, out _))
            {
                issues.Add(new("missing-property", path, $"Required property '{property}' is missing."));
            }
            else if (value is not null)
            {
                issues.Add(new("empty-property", path, $"Property '{property}' must not be empty."));
            }
            return null;
        }
        return value;
    }

    private static string? ReadString(JsonElement element, string property, string path, List<ValidationIssue> issues)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            issues.Add(new("invalid-property-type", path, $"Property '{property}' must be a string."));
            return null;
        }
        return value.GetString();
    }
}
