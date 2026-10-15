using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DurableStateMachine.MeridianClient;

public sealed class MeridianDecisionAdapter(HttpClient client)
{
    public async Task<LayeredDecisionResult> EvaluateAsync(
        Uri baseUri,
        string businessEffectiveAt,
        DateTimeOffset knownAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var request = new
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
            knownAt = knownAt.ToUniversalTime().ToString("O")
        };

        using var response = await client.PostAsJsonAsync(
            new Uri(baseUri, "supplier-qualifications/layered"),
            request,
            cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Meridian layered evaluation failed with HTTP {(int)response.StatusCode}: {responseJson}",
                null,
                response.StatusCode);
        }

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

        return new LayeredDecisionResult(
            evaluationId,
            evidence.GetProperty("evaluatedAt").GetString()!,
            factSetDigest,
            selectedVersions,
            outcome,
            outcomeState,
            responseJson,
            RequiredStringOrNull(evidence, "businessEffectiveAt"),
            RequiredStringOrNull(evidence, "knownAt"),
            missingFacts,
            unknownFacts.Concat(factSnapshotUnknowns).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            unavailableFacts.Concat(factSnapshotUnavailable).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    private static JsonElement RequiredProperty(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
            ? property
            : throw new InvalidDataException($"Meridian response is missing required property '{propertyName}'.");

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

        return entries.EnumerateArray()
            .Where(entry => entry.TryGetProperty("state", out var factState) &&
                factState.ValueKind == JsonValueKind.String &&
                string.Equals(factState.GetString(), state, StringComparison.Ordinal))
            .Select(entry => RequiredString(entry, "name"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
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
