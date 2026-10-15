using System.Text.Json;
using System.Security.Cryptography;
using DurableStateMachine.Core;
using DurableStateMachine.Persistence;

namespace DurableStateMachine.Api;

public sealed record CreateInstanceRequest(
    string Id,
    string DefinitionId,
    string DefinitionVersion,
    Dictionary<string, JsonElement>? Facts);

public sealed record SubmitEventRequest(
    string Event,
    Dictionary<string, JsonElement>? Facts = null,
    DecisionEvidenceRequest? DecisionEvidence = null,
    Dictionary<string, string>? FactsProvenance = null);

public sealed record DecisionEvidenceRequest(
    string DecisionSystemIdentity,
    string? ExternalEvaluationId,
    string OutcomeState,
    JsonElement? Outcome,
    DateTimeOffset? EvaluatedAt,
    string? BusinessEffectiveAt,
    DateTimeOffset? KnowledgeCutoff,
    string? InputFactSetDigest,
    IReadOnlyList<string>? PolicyVersionIdentities,
    string Provenance,
    string OriginalResponseJson,
    IReadOnlyList<string>? MissingFacts,
    IReadOnlyList<string>? UnknownFacts,
    IReadOnlyList<string>? UnavailableFacts);
public sealed record InstanceResponse(string Id, string DefinitionId, string DefinitionVersion, string CurrentState,
    long Revision, IReadOnlyDictionary<string, object?> Facts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DefinitionResponse(string Id, string Version, string LanguageVersion, string ContentDigest,
    DateTimeOffset RegisteredAt, string InitialState, IReadOnlyList<StateDetails> States, IReadOnlyList<string> Events,
    int TransitionCount, JsonElement Definition);
public sealed record AttemptResponse(string Id, string InstanceId, string Event, DateTimeOffset ReceivedAt, string Status,
    string? TransitionId, string PreviousState, string? TargetState, long RevisionBefore, long RevisionAfter,
    string ReasonCode, string Explanation, IReadOnlyList<GuardFinding> GuardFindings,
    TransitionEvaluationEvidence? EvaluationEvidence, string EvaluationEvidenceDigest);
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
        var evidence = attempt.EvaluationEvidenceJson == "{}"
            ? null
            : JsonSerializer.Deserialize<TransitionEvaluationEvidence>(attempt.EvaluationEvidenceJson, JsonDefaults.Options);
        return new AttemptResponse(attempt.Id, attempt.InstanceId, attempt.Event, attempt.ReceivedAt, attempt.Status,
            attempt.TransitionId, attempt.PreviousState, attempt.TargetState, attempt.RevisionBefore,
            attempt.RevisionAfter, attempt.ReasonCode, attempt.Explanation, findings, evidence,
            attempt.EvaluationEvidenceDigest);
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

    public static ExternalDecisionEvidence ToCoreEvidence(this DecisionEvidenceRequest request)
    {
        if (!Enum.TryParse<DecisionOutcomeState>(request.OutcomeState, ignoreCase: false, out var outcomeState) ||
            !Enum.IsDefined(outcomeState))
        {
            throw new ArgumentException($"Unsupported decision outcome state '{request.OutcomeState}'.");
        }

        FactValue? outcome = null;
        if (request.Outcome is { } outcomeElement)
        {
            outcome = outcomeElement.ValueKind switch
            {
                JsonValueKind.String => FactValue.From(outcomeElement.GetString()!),
                JsonValueKind.Number when outcomeElement.TryGetDecimal(out var number) => FactValue.From(number),
                JsonValueKind.True => FactValue.From(true),
                JsonValueKind.False => FactValue.From(false),
                JsonValueKind.Null => FactValue.Null,
                _ => throw new ArgumentException("Decision outcomes must be scalar strings, numbers, booleans or null.")
            };
        }
        else if (outcomeState == DecisionOutcomeState.ExplicitNull)
        {
            outcome = FactValue.Null;
        }

        using var responseDocument = JsonDocument.Parse(request.OriginalResponseJson);
        if (responseDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The original decision response must be a JSON object.");
        }

        return new ExternalDecisionEvidence(
            request.DecisionSystemIdentity,
            request.ExternalEvaluationId,
            outcomeState,
            outcome,
            request.EvaluatedAt,
            request.BusinessEffectiveAt,
            request.KnowledgeCutoff,
            request.InputFactSetDigest,
            request.PolicyVersionIdentities ?? [],
            ComputeCanonicalJsonDigest(responseDocument.RootElement),
            request.OriginalResponseJson,
            request.Provenance,
            request.MissingFacts ?? [],
            request.UnknownFacts ?? [],
            request.UnavailableFacts ?? []);
    }

    private static string ComputeCanonicalJsonDigest(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(value, writer);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
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
