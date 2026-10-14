using System.Text.Json;
using DurableStateMachine.Core;
using DurableStateMachine.Persistence;

namespace DurableStateMachine.Api;

public sealed record CreateInstanceRequest(
    string Id,
    string DefinitionId,
    string DefinitionVersion,
    Dictionary<string, JsonElement>? Facts);

public sealed record SubmitEventRequest(string Event);
public sealed record InstanceResponse(string Id, string DefinitionId, string DefinitionVersion, string CurrentState,
    long Revision, IReadOnlyDictionary<string, object?> Facts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DefinitionResponse(string Id, string Version, string LanguageVersion, string ContentDigest,
    DateTimeOffset RegisteredAt, string InitialState, IReadOnlyList<StateDetails> States, IReadOnlyList<string> Events,
    int TransitionCount, JsonElement Definition);
public sealed record AttemptResponse(string Id, string InstanceId, string Event, DateTimeOffset ReceivedAt, string Status,
    string? TransitionId, string PreviousState, string? TargetState, long RevisionBefore, long RevisionAfter,
    string ReasonCode, string Explanation, IReadOnlyList<GuardFinding> GuardFindings);
public sealed record TransitionResponse(string Id, string InstanceId, string AttemptId, string TransitionId,
    string DefinitionId, string DefinitionVersion, string PreviousState, string NewState, string Event,
    long RevisionBefore, long RevisionAfter, DateTimeOffset OccurredAt);
public sealed record EventSubmissionResponse(AttemptResponse Attempt, InstanceResponse Instance, TransitionResponse? Transition);
public sealed record ApiError(string Code, string Message, IReadOnlyList<ValidationIssue>? Issues = null);

public static class ApiContractMapping
{
    public static InstanceResponse ToResponse(this MachineInstanceDetails instance) =>
        new(instance.Id, instance.DefinitionId, instance.DefinitionVersion, instance.CurrentState, instance.Revision,
            instance.Facts.ToDictionary(pair => pair.Key, pair => ToObject(pair.Value), StringComparer.Ordinal),
            instance.CreatedAt, instance.UpdatedAt);

    public static DefinitionResponse ToResponse(this DefinitionDetails definition) =>
        new(definition.Definition.Id, definition.Definition.Version, definition.Definition.LanguageVersion,
            definition.Definition.ContentDigest, definition.Definition.RegisteredAt, definition.InitialState,
            definition.States, definition.Events, definition.TransitionCount,
            JsonDocument.Parse(definition.DefinitionJson).RootElement.Clone());

    public static AttemptResponse ToResponse(this InputAttemptRecord attempt)
    {
        var findings = JsonSerializer.Deserialize<GuardFinding[]>(attempt.GuardFindingsJson, JsonDefaults.Options) ?? [];
        return new AttemptResponse(attempt.Id, attempt.InstanceId, attempt.Event, attempt.ReceivedAt, attempt.Status,
            attempt.TransitionId, attempt.PreviousState, attempt.TargetState, attempt.RevisionBefore,
            attempt.RevisionAfter, attempt.ReasonCode, attempt.Explanation, findings);
    }

    public static TransitionResponse ToResponse(this TransitionHistoryRecord history) =>
        new(history.Id, history.InstanceId, history.AttemptId, history.TransitionId, history.DefinitionId,
            history.DefinitionVersion, history.PreviousState, history.NewState, history.Event,
            history.RevisionBefore, history.RevisionAfter, history.OccurredAt);

    private static object? ToObject(FactValue value) => value.Kind switch
    {
        FactKind.String => value.StringValue,
        FactKind.Number => value.NumberValue,
        FactKind.Boolean => value.BooleanValue,
        FactKind.Null => null,
        _ => throw new InvalidOperationException($"Unsupported fact kind '{value.Kind}'.")
    };
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }
}
