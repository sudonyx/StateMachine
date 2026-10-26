using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaValidatedAssessment(
    string Outcome,
    string? EvaluationId,
    string SelectedPolicyVersionsJson,
    string OriginalResponseJson,
    string? OriginalEvidenceJson,
    DateTimeOffset? EvaluatedAt,
    string? FactDigest,
    IReadOnlyList<string> PolicyVersionIdentities,
    IReadOnlyList<string> MissingFacts,
    IReadOnlyList<string> UnknownFacts,
    IReadOnlyList<string> UnavailableFacts,
    string BusinessEffectiveAt,
    DateTimeOffset KnownAt);

public sealed class AsteriaDecisionApiClient(HttpClient httpClient)
{
    public const string DecisionSourceIdentity = "Asteria Decision API";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static (string Json, string Fingerprint) CreateCanonicalRequest(AsteriaReleaseSnapshot release)
    {
        var material = release.Material;
        var facts = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["authenticationChange"] = new { state = "Present", value = material.AuthenticationChange },
            ["changeSize"] = new { state = "Present", value = material.ChangeSize },
            ["customerDataImpact"] = new { state = "Present", value = material.CustomerDataImpact },
            ["environment"] = new { state = "Present", value = material.Environment },
            ["plannedDeploymentAt"] = new
            {
                state = "Present",
                value = material.PlannedDeploymentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            },
            ["product"] = new { state = "Present", value = material.Product },
            ["releaseCategory"] = new { state = "Present", value = material.ReleaseCategory },
            ["releaseSummary"] = new { state = "Present", value = material.ReleaseSummary },
            ["targetVersion"] = new { state = "Present", value = material.TargetVersion }
        };
        var request = new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            businessEffectiveAt = release.Work.BusinessEffectiveAt,
            knownAt = release.Work.KnownAt,
            facts
        };
        var json = JsonSerializer.Serialize(request, JsonOptions);
        return (json, ComputeCanonicalFingerprint(json));
    }

    public async Task<string> AssessAsync(
        string idempotencyKey,
        string exactRequestJson,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "asteria/releases/assessments");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new StringContent(exactRequestJson, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Asteria Decision API returned HTTP {(int)response.StatusCode}: {body}",
                inner: null,
                response.StatusCode);
        }

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidDataException(
                $"Asteria assessment endpoint returned unexpected success status {(int)response.StatusCode}.");
        }

        return body;
    }

    public static AsteriaValidatedAssessment ValidateResponse(
        string originalResponseJson,
        AsteriaReleaseSnapshot release)
    {
        using var document = JsonDocument.Parse(originalResponseJson);
        var root = document.RootElement;
        RequireObject(root, "assessment response");

        var outcome = GetString(root, "outcome");
        if (outcome is not ("Prohibited" or "Unresolved" or "RequirementsDetermined" or "AdditionalReviewRequired"))
        {
            throw new InvalidDataException($"Decision API returned unsupported Asteria outcome '{outcome}'.");
        }
        var risk = GetString(root, "riskClassification");
        if (risk is not ("HighRisk" or "NotHighRisk" or "Unresolved"))
        {
            throw new InvalidDataException($"Decision API returned unsupported risk classification '{risk}'.");
        }
        foreach (var name in new[] { "functionalVerification", "securityVerification", "releaseReviewApproval", "emergencyApproval" })
        {
            var obligation = GetRequired(root, name);
            RequireObject(obligation, name);
            if (GetString(obligation, "status") is not ("Required" or "NotRequired" or "Unresolved"))
            {
                throw new InvalidDataException($"Decision API returned an invalid {name} status.");
            }
            _ = GetStringArray(obligation, "clauseReferences");
        }

        if (GetRequired(root, "deploymentAuthorised").ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException("Asteria assessment must never authorize deployment.");
        }
        _ = GetStringArray(root, "unresolvedFindings");

        var businessEffectiveAt = GetString(root, "businessEffectiveAt");
        if (!DateOnly.TryParseExact(businessEffectiveAt, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) ||
            !string.Equals(businessEffectiveAt, release.Work.BusinessEffectiveAt, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Decision API business-effective context does not match the durable request.");
        }
        var knownAt = GetDateTimeOffset(root, "knownAt");
        if (knownAt != release.Work.KnownAt)
        {
            throw new InvalidDataException("Decision API known-at context does not match the durable request.");
        }

        var binding = GetRequired(root, "binding");
        RequireObject(binding, "response binding");
        if (!string.Equals(GetString(binding, "releaseId"), release.ReleaseId, StringComparison.Ordinal) ||
            GetRequired(binding, "assessmentGeneration").GetInt32() != release.AssessmentGeneration ||
            !string.Equals(GetString(binding, "materialDigest"), release.MaterialDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Decision API response binding does not match the authoritative release registry.");
        }

        var evaluationIdElement = GetRequired(root, "evaluationId");
        var selected = GetRequired(root, "selectedPolicySourceVersions");
        if (selected.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Decision API selected policy versions must be an array.");
        }
        var selectedVersions = selected.EnumerateArray().Select(item => item.Clone()).ToArray();
        var selectedVersionsJson = JsonSerializer.Serialize(selectedVersions, JsonOptions);
        var evidenceElement = GetRequired(root, "evidence");
        if (evidenceElement.ValueKind == JsonValueKind.Null)
        {
            var noPolicyFactDigestElement = GetRequired(root, "factDigest");
            var noPolicyFactDigest = noPolicyFactDigestElement.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => noPolicyFactDigestElement.GetString(),
                _ => throw new InvalidDataException("No-policy fact digest must be a nullable string.")
            };
            if (evaluationIdElement.ValueKind != JsonValueKind.Null ||
                selectedVersions.Length != 0 ||
                outcome != "Unresolved" ||
                risk != "Unresolved" ||
                GetRequired(root, "functionalVerification").GetProperty("status").GetString() != "Unresolved" ||
                GetRequired(root, "securityVerification").GetProperty("status").GetString() != "Unresolved" ||
                GetRequired(root, "releaseReviewApproval").GetProperty("status").GetString() != "Unresolved" ||
                GetRequired(root, "emergencyApproval").GetProperty("status").GetString() != "Unresolved" ||
                GetStringArray(root, "unresolvedFindings").Count == 0)
            {
                throw new InvalidDataException("A no-applicable-policy response violated the documented unresolved contract.");
            }
            if (noPolicyFactDigest is not null)
            {
                ValidateDigest(noPolicyFactDigest, "fact-set digest");
            }

            return new AsteriaValidatedAssessment(
                outcome,
                null,
                selectedVersionsJson,
                originalResponseJson,
                null,
                null,
                noPolicyFactDigest,
                [],
                [],
                [],
                [],
                businessEffectiveAt,
                knownAt);
        }

        RequireObject(evidenceElement, "decision evidence");
        if (!Guid.TryParse(evaluationIdElement.GetString(), out var evaluationGuid) || evaluationGuid == Guid.Empty ||
            !string.Equals(GetString(evidenceElement, "evaluationId"), evaluationGuid.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Persisted Decision API response and evidence must have the same evaluation ID.");
        }
        var evaluatedAt = GetDateTimeOffset(evidenceElement, "evaluatedAt");
        if (GetRequired(root, "factDigest").ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("A persisted decision must include its fact-set digest.");
        }
        var factDigest = GetString(root, "factDigest");
        var factSet = GetRequired(evidenceElement, "factSet");
        RequireObject(factSet, "evidence fact set");
        if (!string.Equals(GetString(factSet, "digestAlgorithm"), "SHA-256", StringComparison.Ordinal) ||
            !string.Equals(GetString(factSet, "digest"), factDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Decision API response and evidence fact digests do not match.");
        }
        ValidateDigest(factDigest, "fact-set digest");
        if (GetRequired(evidenceElement, "businessEffectiveAt").GetString() != businessEffectiveAt ||
            GetDateTimeOffset(evidenceElement, "knownAt") != knownAt)
        {
            throw new InvalidDataException("Persisted evidence temporal context differs from the assessment response.");
        }

        var expectedFacts = ExpectedFacts(release);
        var snapshotEntries = GetRequired(factSet, "entries");
        if (snapshotEntries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Decision API fact snapshot entries must be an array.");
        }
        var entries = snapshotEntries.EnumerateArray().ToArray();
        if (entries.Length != expectedFacts.Count)
        {
            throw new InvalidDataException("Decision API fact snapshot does not contain exactly the declared Asteria facts.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = GetString(entry, "name");
            if (!seen.Add(name) || !expectedFacts.TryGetValue(name, out var expected))
            {
                throw new InvalidDataException($"Decision API returned an undeclared or repeated Asteria fact '{name}'.");
            }
            if (GetString(entry, "state") != "Present")
            {
                throw new InvalidDataException($"Decision API fact snapshot does not preserve submitted fact '{name}' as present.");
            }

            var value = GetRequired(entry, "value");
            RequireObject(value, $"fact value '{name}'");
            var type = GetString(entry, "type");
            if (expected is string expectedString)
            {
                if (type != "String" || GetRequired(value, "stringValue").GetString() != expectedString)
                {
                    throw new InvalidDataException($"Decision API fact snapshot value for '{name}' differs from the authoritative request.");
                }
            }
            else if (expected is bool expectedBoolean)
            {
                if (type != "Boolean" || GetRequired(value, "booleanValue").GetBoolean() != expectedBoolean)
                {
                    throw new InvalidDataException($"Decision API fact snapshot value for '{name}' differs from the authoritative request.");
                }
            }
            else if (expected is int expectedInteger)
            {
                if (type != "Integer" || GetRequired(value, "integerValue").GetInt32() != expectedInteger)
                {
                    throw new InvalidDataException($"Decision API fact snapshot value for '{name}' differs from the authoritative request.");
                }
            }
            else
            {
                throw new InvalidOperationException("Unsupported locally bound Asteria fact type.");
            }
        }

        var missingFacts = entries
            .Where(entry => GetString(entry, "state") == "Missing")
            .Select(entry => GetString(entry, "name"))
            .Order(StringComparer.Ordinal).ToArray();
        var unknownFacts = GetStringArray(evidenceElement, "unknownFacts").Order(StringComparer.Ordinal).ToArray();
        var unavailableFacts = GetStringArray(evidenceElement, "unavailableFacts").Order(StringComparer.Ordinal).ToArray();
        var policyVersionIdentities = ValidatePolicyVersions(
            selectedVersions, evidenceElement, knownAt, businessEffectiveAt);

        return new AsteriaValidatedAssessment(
            outcome,
            evaluationGuid.ToString(),
            selectedVersionsJson,
            originalResponseJson,
            evidenceElement.GetRawText(),
            evaluatedAt,
            factDigest,
            policyVersionIdentities,
            missingFacts,
            unknownFacts,
            unavailableFacts,
            businessEffectiveAt,
            knownAt);
    }

    private static Dictionary<string, object> ExpectedFacts(AsteriaReleaseSnapshot release)
    {
        var material = release.Material;
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["product"] = material.Product,
            ["targetVersion"] = material.TargetVersion,
            ["environment"] = material.Environment,
            ["releaseCategory"] = material.ReleaseCategory,
            ["changeSize"] = material.ChangeSize,
            ["authenticationChange"] = material.AuthenticationChange,
            ["customerDataImpact"] = material.CustomerDataImpact,
            ["releaseSummary"] = material.ReleaseSummary,
            ["plannedDeploymentAt"] = material.PlannedDeploymentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static IReadOnlyList<string> ValidatePolicyVersions(
        IReadOnlyList<JsonElement> selectedVersions,
        JsonElement evidence,
        DateTimeOffset knownAt,
        string businessEffectiveAt)
    {
        var sourceEvidence = GetRequired(evidence, "ruleSources");
        if (sourceEvidence.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Decision API policy source evidence must be an array.");
        }

        var identities = new List<string>(selectedVersions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selected in selectedVersions)
        {
            RequireObject(selected, "selected policy version");
            var sourceId = GetString(selected, "sourceId");
            var versionId = GetString(selected, "versionId");
            var versionNumber = GetRequired(selected, "versionNumber").GetInt32();
            var recordedAt = GetDateTimeOffset(selected, "recordedAt");
            var effectiveFrom = DateOnly.ParseExact(GetString(selected, "effectiveFrom"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var effectiveToElement = GetRequired(selected, "effectiveTo");
            var sourceDigest = GetString(selected, "sourceDigest");
            var ruleDigest = GetString(selected, "ruleDigest");
            if (sourceId != "AST-POL-001" ||
                !Guid.TryParse(versionId, out _) ||
                versionNumber < 1 ||
                recordedAt > knownAt ||
                effectiveFrom > DateOnly.ParseExact(businessEffectiveAt, "yyyy-MM-dd", CultureInfo.InvariantCulture) ||
                (effectiveToElement.ValueKind != JsonValueKind.Null &&
                 DateOnly.ParseExact(effectiveToElement.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture) <
                 DateOnly.ParseExact(businessEffectiveAt, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
            {
                throw new InvalidDataException("Selected policy version is inconsistent with AST-POL-001 temporal context.");
            }
            ValidateDigest(sourceDigest, "policy source digest");
            ValidateDigest(ruleDigest, "policy rule digest");
            if (!seen.Add(versionId))
            {
                throw new InvalidDataException("Decision API selected a policy version more than once.");
            }

            var matchingSource = sourceEvidence.EnumerateArray().SingleOrDefault(source =>
                source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("policySourceVersionId", out var sourceVersion) &&
                sourceVersion.ValueKind == JsonValueKind.String &&
                string.Equals(sourceVersion.GetString(), versionId, StringComparison.OrdinalIgnoreCase));
            if (matchingSource.ValueKind != JsonValueKind.Object ||
                GetString(matchingSource, "sourceId") != sourceId ||
                GetRequired(matchingSource, "versionNumber").GetInt32() != versionNumber ||
                GetDateTimeOffset(matchingSource, "recordedAt") != recordedAt ||
                GetString(matchingSource, "effectiveFrom") != effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
                !NullableDateMatches(matchingSource, "effectiveTo", effectiveToElement) ||
                GetString(matchingSource, "policySourceDigest") != sourceDigest ||
                GetString(matchingSource, "executableRuleDigest") != ruleDigest)
            {
                throw new InvalidDataException("Selected policy metadata does not match original Decision API evidence.");
            }

            identities.Add(string.Join('/', sourceId, versionId, versionNumber.ToString(CultureInfo.InvariantCulture), sourceDigest, ruleDigest));
        }
        return identities.AsReadOnly();
    }

    private static bool NullableDateMatches(JsonElement source, string name, JsonElement selected)
    {
        var evidenceDate = GetRequired(source, name);
        return evidenceDate.ValueKind == selected.ValueKind &&
               (evidenceDate.ValueKind == JsonValueKind.Null ||
                string.Equals(evidenceDate.GetString(), selected.GetString(), StringComparison.Ordinal));
    }

    private static void ValidateDigest(string value, string name)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Decision API {name} is not a 64-character hexadecimal digest.");
        }
    }

    private static string ComputeCanonicalFingerprint(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(document.RootElement, writer);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
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

    private static JsonElement GetRequired(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var property)
            ? property
            : throw new InvalidDataException($"Decision API response omitted required field '{name}'.");

    private static string GetString(JsonElement parent, string name)
    {
        var value = GetRequired(parent, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Decision API response field '{name}' must be a string.");
    }

    private static DateTimeOffset GetDateTimeOffset(JsonElement parent, string name)
    {
        var value = GetRequired(parent, name);
        if (value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
        {
            throw new InvalidDataException($"Decision API response field '{name}' must be an RFC 3339 timestamp.");
        }
        return result.ToUniversalTime();
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement parent, string name)
    {
        var value = GetRequired(parent, name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Decision API response field '{name}' must be an array.");
        }
        return value.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new InvalidDataException($"Decision API response field '{name}' contains a non-string.")).ToArray();
    }

    private static void RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Decision API {description} must be a JSON object.");
        }
    }
}
