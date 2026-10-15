using System.Collections.ObjectModel;
using System.Collections.Frozen;
using System.Text.Json;

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
public sealed record CommandInputMapping(string Name, string Event);
public sealed record ObservationInputMapping(string Name, string Event, IReadOnlyList<string> RequiredEvidence);
public sealed record MachineInputPolicy(
    IReadOnlyDictionary<string, CommandInputMapping> Commands,
    IReadOnlyDictionary<string, ObservationInputMapping> Observations);

public sealed class MachineDefinition
{
    internal MachineDefinition(
        string id,
        string version,
        string languageVersion,
        string initialState,
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        MachineInputPolicy? inputPolicy)
    {
        Id = id;
        Version = version;
        LanguageVersion = languageVersion;
        InitialState = initialState;
        States = states;
        Events = events;
        Transitions = transitions;
        InputPolicy = inputPolicy;
    }

    public string Id { get; }
    public string Version { get; }
    public string LanguageVersion { get; }
    public string InitialState { get; }
    public IReadOnlyDictionary<string, MachineState> States { get; }
    public IReadOnlySet<string> Events { get; }
    public IReadOnlyList<MachineTransition> Transitions { get; }
    public MachineInputPolicy? InputPolicy { get; }
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
}

public static class MachineDefinitionLoader
{
    private static readonly string[] RootProperties =
        ["languageVersion", "id", "version", "initialState", "states", "events", "transitions", "inputPolicy"];

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
            else if (languageVersion is not null && languageVersion is not ("1.0" or "1.1"))
            {
                issues.Add(new("unsupported-language-version", "$.languageVersion", $"Language version '{languageVersion}' is not supported; expected '1.0' or '1.1'."));
            }
            if (languageVersion == "1.0" && root.TryGetProperty("inputPolicy", out _))
            {
                issues.Add(new("input-policy-version", "$.inputPolicy", "Input policies require language version '1.1'."));
            }

            var id = ReadRequiredString(root, "id", "$.id", issues);
            var version = ReadRequiredString(root, "version", "$.version", issues);
            var initialState = ReadRequiredString(root, "initialState", "$.initialState", issues);

            var states = ReadStates(root, issues);
            var events = ReadEvents(root, issues);
            var transitions = ReadTransitions(root, issues);
            var inputPolicy = ReadInputPolicy(root, languageVersion, events, issues);

            if (states.Values.All(state => !state.IsTerminal))
            {
                issues.Add(new("missing-terminal-state", "$.states", "At least one state must be terminal."));
            }

            if (initialState is not null && !states.ContainsKey(initialState))
            {
                issues.Add(new("unknown-initial-state", "$.initialState", $"Initial state '{initialState}' is not declared."));
            }

            ValidateTransitions(states, events, transitions, issues);
            if (issues.Count != 0)
            {
                throw new MachineDefinitionException(issues);
            }

            return new MachineDefinition(
                id!,
                version!,
                languageVersion!,
                initialState!,
                new ReadOnlyDictionary<string, MachineState>(states),
                events.ToFrozenSet(StringComparer.Ordinal),
                transitions.AsReadOnly(),
                inputPolicy);
        }
    }

    private static MachineInputPolicy? ReadInputPolicy(
        JsonElement root,
        string? languageVersion,
        IReadOnlySet<string> events,
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
        var commands = ReadCommandMappings(policy, events, issues);
        var observations = ReadObservationMappings(policy, events, issues);
        if (languageVersion != "1.1")
        {
            return null;
        }
        return new MachineInputPolicy(
            new ReadOnlyDictionary<string, CommandInputMapping>(commands),
            new ReadOnlyDictionary<string, ObservationInputMapping>(observations));
    }

    private static Dictionary<string, CommandInputMapping> ReadCommandMappings(
        JsonElement policy,
        IReadOnlySet<string> events,
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
            CheckProperties(value, ["name", "event"], path, issues);
            var name = ReadRequiredString(value, "name", path + ".name", issues);
            var eventName = ReadRequiredString(value, "event", path + ".event", issues);
            if (eventName is not null && !events.Contains(eventName))
            {
                issues.Add(new("undeclared-input-event", path + ".event", $"Event '{eventName}' is not declared."));
            }
            if (name is not null && eventName is not null && !commands.TryAdd(name, new CommandInputMapping(name, eventName)))
            {
                issues.Add(new("duplicate-command-mapping", path + ".name", $"Command '{name}' is declared more than once."));
            }
        }
        return commands;
    }

    private static Dictionary<string, ObservationInputMapping> ReadObservationMappings(
        JsonElement policy,
        IReadOnlySet<string> events,
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
            CheckProperties(value, ["name", "event", "requiredEvidence"], path, issues);
            var name = ReadRequiredString(value, "name", path + ".name", issues);
            var eventName = ReadRequiredString(value, "event", path + ".event", issues);
            if (eventName is not null && !events.Contains(eventName))
            {
                issues.Add(new("undeclared-input-event", path + ".event", $"Event '{eventName}' is not declared."));
            }

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
                !observations.TryAdd(name, new ObservationInputMapping(name, eventName, requiredEvidence.AsReadOnly())))
            {
                issues.Add(new("duplicate-observation-mapping", path + ".name", $"Observation '{name}' is declared more than once."));
            }
        }
        return observations;
    }

    private static Dictionary<string, MachineState> ReadStates(JsonElement root, List<ValidationIssue> issues)
    {
        var states = new Dictionary<string, MachineState>(StringComparer.Ordinal);
        if (!root.TryGetProperty("states", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("missing-states", "$.states", "States must be an array."));
            return states;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.states[{index}]";
            index++;
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-state", path, "Each state must be an object."));
                continue;
            }

            CheckProperties(value, ["id", "terminal"], path, issues);
            var id = ReadRequiredString(value, "id", $"{path}.id", issues);
            var terminal = false;
            if (value.TryGetProperty("terminal", out var terminalValue))
            {
                if (terminalValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    terminal = terminalValue.GetBoolean();
                }
                else
                {
                    issues.Add(new("invalid-terminal-flag", $"{path}.terminal", "Terminal must be a boolean."));
                }
            }

            if (id is not null && !states.TryAdd(id, new MachineState(id, terminal)))
            {
                issues.Add(new("duplicate-state", $"{path}.id", $"State '{id}' is declared more than once."));
            }
        }

        return states;
    }

    private static HashSet<string> ReadEvents(JsonElement root, List<ValidationIssue> issues)
    {
        var events = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("events", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("missing-events", "$.events", "Events must be an array of strings."));
            return events;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            {
                issues.Add(new("invalid-event", $"$.events[{index}]", "Event identifiers must be non-empty strings."));
            }
            else if (!events.Add(value.GetString()!))
            {
                issues.Add(new("duplicate-event", $"$.events[{index}]", $"Event '{value.GetString()}' is declared more than once."));
            }

            index++;
        }

        return events;
    }

    private static List<MachineTransition> ReadTransitions(JsonElement root, List<ValidationIssue> issues)
    {
        var transitions = new List<MachineTransition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("transitions", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new("missing-transitions", "$.transitions", "Transitions must be an array."));
            return transitions;
        }

        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.transitions[{index}]";
            index++;
            if (value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new("invalid-transition", path, "Each transition must be an object."));
                continue;
            }

            CheckProperties(value, ["id", "source", "event", "target", "description", "guard"], path, issues);
            var id = ReadRequiredString(value, "id", $"{path}.id", issues);
            var source = ReadRequiredString(value, "source", $"{path}.source", issues);
            var eventName = ReadRequiredString(value, "event", $"{path}.event", issues);
            var target = ReadRequiredString(value, "target", $"{path}.target", issues);
            var description = ReadString(value, "description", $"{path}.description", issues);
            GuardExpression? guard = null;
            if (value.TryGetProperty("guard", out var guardElement))
            {
                guard = ParseGuard(guardElement, $"{path}.guard", issues);
            }

            if (id is not null && !ids.Add(id))
            {
                issues.Add(new("duplicate-transition-id", $"{path}.id", $"Transition '{id}' is declared more than once."));
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

            default:
                issues.Add(new("unsupported-guard-operator", $"{path}.op", $"Guard operator '{op}' is not supported."));
                return null;
        }
    }

    private static void ValidateTransitions(
        IReadOnlyDictionary<string, MachineState> states,
        IReadOnlySet<string> events,
        IReadOnlyList<MachineTransition> transitions,
        List<ValidationIssue> issues)
    {
        foreach (var transition in transitions)
        {
            var path = $"$.transitions[{transition.Id}]";
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
                    "$.transitions",
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
