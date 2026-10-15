using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DurableStateMachine.MeridianClient;

public sealed class MeridianDecisionAdapter(HttpClient client)
{
    public async Task<MeridianEvaluationCallResult> EvaluateAsync(
        Uri baseUri,
        MeridianAssessmentRequest assessment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(assessment);
        assessment.Validate();

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, "supplier-qualifications/layered"));
        request.Headers.Add("Idempotency-Key", assessment.IdempotencyKey);
        request.Content = new StringContent(
            assessment.RequestJson,
            System.Text.Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new MeridianEvaluationUncertainException(
                assessment.IdempotencyKey,
                "The request was canceled, timed out or its response was lost. The service may have committed the assessment; retry only with the same saved key and request.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MeridianEvaluationUncertainException(
                assessment.IdempotencyKey,
                "The HTTP transport failed before the outcome could be established. The service may have committed the assessment; retry only with the same saved key and request.",
                exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                if (response.Headers.RetryAfter is { } retryAfter)
                {
                    var delay = retryAfter.Delta ??
                        (retryAfter.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : null);
                    return MeridianEvaluationCallResult.InProgress(delay);
                }

                var conflictBody = await ReadResponseBodyAsync(response, assessment.IdempotencyKey, cancellationToken);
                throw new MeridianIdempotencyConflictException(
                    assessment.IdempotencyKey,
                    conflictBody);
            }

            var responseJson = await ReadResponseBodyAsync(response, assessment.IdempotencyKey, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                throw new MeridianEvaluationUncertainException(
                    assessment.IdempotencyKey,
                    $"Meridian returned HTTP {(int)response.StatusCode}; the assessment outcome may be uncertain. Retry only with the same saved key and request.",
                    new HttpRequestException(responseJson, null, response.StatusCode));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MeridianApiException(response.StatusCode, responseJson);
            }

            try
            {
                return MeridianEvaluationCallResult.Completed(ParseDecision(responseJson, assessment));
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                throw new MeridianEvaluationUncertainException(
                    assessment.IdempotencyKey,
                    $"Meridian returned an unusable success response. The assessment may be committed; retry only with the same saved key and request. {exception.Message}",
                    exception);
            }
        }
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpResponseMessage response,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new MeridianEvaluationUncertainException(
                idempotencyKey,
                "The response body was not received completely. The service may have committed the assessment; retry only with the same saved key and request.",
                exception);
        }
        catch (IOException exception)
        {
            throw new MeridianEvaluationUncertainException(
                idempotencyKey,
                "The response body could not be received completely. The service may have committed the assessment; retry only with the same saved key and request.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new MeridianEvaluationUncertainException(
                idempotencyKey,
                "The response body could not be received completely. The service may have committed the assessment; retry only with the same saved key and request.",
                exception);
        }
    }

    private static LayeredDecisionResult ParseDecision(
        string responseJson,
        MeridianAssessmentRequest assessment)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var evidence = RequiredObject(root, "evidence");
            var evaluationId = RequiredString(evidence, "evaluationId");
            if (!Guid.TryParse(evaluationId, out _))
            {
                throw new InvalidDataException("Meridian returned an evaluationId that is not a UUID.");
            }

            var finalDecision = RequiredProperty(evidence, "finalDecision");
            JsonNode? outcome;
            string outcomeState;
            if (finalDecision.ValueKind == JsonValueKind.Null)
            {
                outcome = null;
                outcomeState = "ExplicitNull";
            }
            else if (finalDecision.ValueKind == JsonValueKind.String)
            {
                var value = finalDecision.GetString();
                if (value is not ("Proceed" or "Review" or "Blocked"))
                {
                    throw new InvalidDataException($"Meridian returned an unsupported finalDecision '{value}'.");
                }
                outcome = JsonValue.Create(value);
                outcomeState = "Present";
            }
            else
            {
                throw new InvalidDataException("Meridian finalDecision must be a string or null.");
            }

            var selectedVersions = RequiredStringArray(root, "selectedPolicySourceVersionIds");
            var factSet = RequiredObject(evidence, "factSet");
            var factSetDigest = RequiredString(factSet, "digest");
            var unknownFacts = RequiredStringArray(evidence, "unknownFacts");
            var unavailableFacts = RequiredStringArray(evidence, "unavailableFacts");
            var missingFacts = GetFactNamesWithState(factSet, "Missing");
            var factSnapshotUnknowns = GetFactNamesWithState(factSet, "Unknown");
            var factSnapshotUnavailable = GetFactNamesWithState(factSet, "Unavailable");
            var evaluatedAt = RequiredString(evidence, "evaluatedAt");
            var businessEffectiveAt = RequiredStringOrNull(evidence, "businessEffectiveAt");
            var knownAt = RequiredStringOrNull(evidence, "knownAt");
            var expectedRequest = assessment.ReadTemporalContext();
            if (!string.Equals(businessEffectiveAt, expectedRequest.BusinessEffectiveAt, StringComparison.Ordinal) ||
                !string.Equals(knownAt, expectedRequest.KnownAt, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Meridian response temporal context does not match the keyed request.");
            }

            return new LayeredDecisionResult(
                evaluationId,
                evaluatedAt,
                factSetDigest,
                selectedVersions,
                outcome,
                outcomeState,
                responseJson,
                businessEffectiveAt,
                knownAt,
                missingFacts,
                unknownFacts.Concat(factSnapshotUnknowns).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                unavailableFacts.Concat(factSnapshotUnavailable).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Meridian returned malformed JSON for idempotency key '{assessment.IdempotencyKey}'. The evaluation may be committed; retry only with the same saved key and request.",
                exception);
        }
    }

    private static JsonElement RequiredProperty(JsonElement value, string propertyName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Meridian response value containing '{propertyName}' must be an object.");
        }
        return value.TryGetProperty(propertyName, out var property)
            ? property
            : throw new InvalidDataException($"Meridian response is missing required property '{propertyName}'.");
    }

    private static JsonElement RequiredObject(JsonElement value, string propertyName)
    {
        var property = RequiredProperty(value, propertyName);
        return property.ValueKind == JsonValueKind.Object
            ? property
            : throw new InvalidDataException($"Meridian property '{propertyName}' must be an object.");
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        var property = RequiredProperty(value, propertyName);
        return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()!
            : throw new InvalidDataException($"Meridian property '{propertyName}' must be a non-empty string.");
    }

    private static string? RequiredStringOrNull(JsonElement value, string propertyName)
    {
        var property = RequiredProperty(value, propertyName);
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null => null,
            _ => throw new InvalidDataException($"Meridian property '{propertyName}' must be a string or null.")
        };
    }

    private static string[] RequiredStringArray(JsonElement value, string propertyName)
    {
        var property = RequiredProperty(value, propertyName);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Meridian property '{propertyName}' must be an array.");
        }
        return property.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException($"Meridian property '{propertyName}' must contain only strings."))
            .ToArray();
    }

    private static string[] GetFactNamesWithState(JsonElement factSet, string state)
    {
        if (!factSet.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Meridian factSet.entries must be an array.");
        }
        if (entries.EnumerateArray().Any(entry => entry.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException("Meridian factSet.entries must contain only objects.");
        }

        return entries.EnumerateArray()
            .Where(entry => entry.TryGetProperty("state", out var factState) &&
                factState.ValueKind == JsonValueKind.String &&
                string.Equals(factState.GetString(), state, StringComparison.Ordinal))
            .Select(entry => RequiredString(entry, "name"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed record MeridianAssessmentRequest
{
    public required string OperationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string RequestJson { get; init; }

    public static MeridianAssessmentRequest CreateSynthetic(
        string operationId,
        string idempotencyKey,
        string businessEffectiveAt,
        string knownAt)
    {
        var payload = new
        {
            supplierId = "SUP-0001",
            supplierCountry = "Exampleland",
            countryRiskClassification = "Standard",
            supplierCategory = "Software",
            annualContractValue = 25000,
            currency = "USD",
            handlesPersonalData = false,
            handlesPaymentCardData = false,
            insuranceStatus = "Current",
            completeYearsTrading = 5,
            sanctionsScreeningStatus = "Clear",
            usesSubcontractors = false,
            businessUnit = "Finance",
            requestedPaymentTermsDays = 30,
            customerId = "CUSTOMER-001",
            contractId = "CONTRACT-001",
            qualificationDate = businessEffectiveAt,
            businessEffectiveAt,
            knownAt
        };

        return Create(operationId, idempotencyKey, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web));
    }

    public static MeridianAssessmentRequest Create(
        string operationId,
        string idempotencyKey,
        string requestJson)
    {
        var request = new MeridianAssessmentRequest
        {
            OperationId = operationId,
            IdempotencyKey = idempotencyKey,
            RequestJson = requestJson
        };
        request.Validate();
        return request;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OperationId))
        {
            throw new ArgumentException("Assessment operation ID must not be empty.", nameof(OperationId));
        }
        ValidateIdempotencyKey(IdempotencyKey);
        _ = ReadTemporalContext();
    }

    internal (string BusinessEffectiveAt, string KnownAt) ReadTemporalContext()
    {
        using var document = JsonDocument.Parse(RequestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The keyed assessment request must be a JSON object.", nameof(RequestJson));
        }
        if (!document.RootElement.TryGetProperty("businessEffectiveAt", out var effectiveAt) ||
            effectiveAt.ValueKind != JsonValueKind.String ||
            !DateOnly.TryParseExact(effectiveAt.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            throw new ArgumentException("A keyed assessment request must contain an explicit YYYY-MM-DD businessEffectiveAt.", nameof(RequestJson));
        }
        if (!document.RootElement.TryGetProperty("knownAt", out var knownAt) ||
            knownAt.ValueKind != JsonValueKind.String ||
            !HasExplicitOffset(knownAt.GetString()))
        {
            throw new ArgumentException("A keyed assessment request must contain an explicit ISO-8601 knownAt timestamp with an offset.", nameof(RequestJson));
        }

        return (effectiveAt.GetString()!, knownAt.GetString()!);
    }

    public static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128 ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '~' or '-')))
        {
            throw new ArgumentException(
                "Idempotency-Key must contain 1-128 ASCII characters from A-Z, a-z, 0-9, '.', '_', '~' or '-'.",
                nameof(key));
        }
    }

    private static bool HasExplicitOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return false;
        }
        return value.EndsWith('Z') ||
            (value.Length >= 6 && (value[^6] == '+' || value[^6] == '-') &&
                value[^3] == ':' && char.IsAsciiDigit(value[^5]) && char.IsAsciiDigit(value[^4]) &&
                char.IsAsciiDigit(value[^2]) && char.IsAsciiDigit(value[^1]));
    }
}

public sealed record MeridianEvaluationCallResult(
    MeridianEvaluationCallStatus Status,
    LayeredDecisionResult? Decision,
    TimeSpan? RetryAfter)
{
    public bool IsInProgress => Status == MeridianEvaluationCallStatus.InProgress;

    public static MeridianEvaluationCallResult Completed(LayeredDecisionResult decision) =>
        new(MeridianEvaluationCallStatus.Completed, decision, null);

    public static MeridianEvaluationCallResult InProgress(TimeSpan? retryAfter) =>
        new(MeridianEvaluationCallStatus.InProgress, null, retryAfter);
}

public enum MeridianEvaluationCallStatus
{
    Completed,
    InProgress
}

public sealed class MeridianIdempotencyConflictException(
    string idempotencyKey,
    string responseBody)
    : Exception($"Idempotency-Key '{idempotencyKey}' was already used with a different request. Meridian response: {responseBody}")
{
    public string IdempotencyKey { get; } = idempotencyKey;
    public string ResponseBody { get; } = responseBody;
}

public sealed class MeridianApiException(HttpStatusCode statusCode, string responseBody)
    : Exception($"Meridian returned HTTP {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}

public sealed class MeridianEvaluationUncertainException(
    string idempotencyKey,
    string message,
    Exception innerException)
    : Exception(message, innerException)
{
    public string IdempotencyKey { get; } = idempotencyKey;
}

public sealed record LayeredDecisionResult(
    string EvaluationId,
    string EvaluatedAt,
    string InputFactSetDigest,
    IReadOnlyList<string> SelectedPolicyVersionIds,
    JsonNode? Outcome,
    string OutcomeState,
    string OriginalResponseJson,
    string? BusinessEffectiveAt,
    string? KnownAt,
    IReadOnlyList<string> MissingFacts,
    IReadOnlyList<string> UnknownFacts,
    IReadOnlyList<string> UnavailableFacts)
{
    public JsonObject CreateEventSubmission(string eventName)
    {
        var decisionEvidence = new JsonObject
        {
            ["decisionSystemIdentity"] = "Meridian Decision API",
            ["externalEvaluationId"] = EvaluationId,
            ["outcomeState"] = OutcomeState,
            ["outcome"] = Outcome?.DeepClone(),
            ["evaluatedAt"] = EvaluatedAt,
            ["businessEffectiveAt"] = BusinessEffectiveAt,
            ["knowledgeCutoff"] = KnownAt,
            ["inputFactSetDigest"] = InputFactSetDigest,
            ["policyVersionIdentities"] = JsonSerializer.SerializeToNode(SelectedPolicyVersionIds),
            ["provenance"] = "Received from the configured Meridian HTTP endpoint; caller-supplied provenance is not cryptographically verified.",
            ["originalResponseJson"] = OriginalResponseJson,
            ["missingFacts"] = JsonSerializer.SerializeToNode(MissingFacts),
            ["unknownFacts"] = JsonSerializer.SerializeToNode(UnknownFacts),
            ["unavailableFacts"] = JsonSerializer.SerializeToNode(UnavailableFacts)
        };
        return new JsonObject
        {
            ["event"] = eventName,
            ["facts"] = new JsonObject { ["decisionOutcome"] = Outcome?.DeepClone() },
            ["factsProvenance"] = new JsonObject
            {
                ["decisionOutcome"] = "Outcome mapped from the received Meridian response; not independently verified."
            },
            ["decisionEvidence"] = decisionEvidence
        };
    }
}
