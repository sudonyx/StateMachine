using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Api;

public sealed class HarnessConfigurationException(string message) : Exception(message);

public sealed class HarnessHttpException(HttpStatusCode statusCode, string responseBody)
    : Exception($"The Generic Harness returned HTTP {(int)statusCode} ({statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}

public sealed record HarnessCapabilityBinding(
    string Capability,
    string ProviderIdentity,
    string ProviderId,
    string ApiDescriptionVersionId,
    string OperationKey,
    string? CredentialBindingId);

public sealed class HarnessCapabilityBindings(IConfiguration configuration)
{
    public HarnessCapabilityBinding Get(string capability)
    {
        var prefix = $"Harness:Capabilities:{capability}:";
        var providerIdentity = configuration[$"{prefix}ProviderIdentity"];
        var providerId = configuration[$"{prefix}ProviderId"];
        var versionId = configuration[$"{prefix}ApiDescriptionVersionId"];
        var operationKey = configuration[$"{prefix}OperationKey"];
        var credentialBindingId = configuration[$"{prefix}CredentialBindingId"];

        if (string.IsNullOrWhiteSpace(providerIdentity) ||
            !Guid.TryParse(providerId, out var parsedProviderId) ||
            !Guid.TryParse(versionId, out var parsedVersionId) ||
            string.IsNullOrWhiteSpace(operationKey) ||
            (credentialBindingId is not null && !Guid.TryParse(credentialBindingId, out _)))
        {
            throw new HarnessConfigurationException(
                $"Harness binding for capability '{capability}' is incomplete or invalid. Configure ProviderIdentity, ProviderId, ApiDescriptionVersionId, OperationKey, and an optional CredentialBindingId.");
        }

        return new HarnessCapabilityBinding(
            capability,
            providerIdentity,
            parsedProviderId.ToString(),
            parsedVersionId.ToString(),
            operationKey,
            credentialBindingId);
    }
}

public sealed record HarnessExecutionRequest(
    string ProviderId,
    string ApiDescriptionVersionId,
    string OperationKey,
    string? CredentialBindingId,
    string IdempotencyKey,
    int MaxAttempts,
    string[] RetryableFailureCodes,
    IReadOnlyDictionary<string, object?>? PathParameters,
    IReadOnlyDictionary<string, object?>? QueryParameters,
    IReadOnlyDictionary<string, object?>? HeaderParameters,
    JsonElement? Body);

public sealed record HarnessHttpResult(HttpStatusCode StatusCode, JsonElement? Body);

public sealed class HarnessExecutionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;

    public HarnessExecutionClient(HttpClient client, IConfiguration configuration)
    {
        var configuredUrl = configuration["Harness:BaseUrl"] ?? "http://localhost:5247";
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new HarnessConfigurationException(
                "Harness:BaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        client.BaseAddress = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        _client = client;
    }

    public async Task<JsonElement> RegisterApiDescriptionAsync(
        string providerId,
        JsonElement openApiDescription,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync(
            $"providers/{Uri.EscapeDataString(providerId)}/api-descriptions",
            openApiDescription,
            JsonOptions,
            cancellationToken);
        return await ReadSuccessJsonAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<JsonElement>> GetCallableOperationsAsync(
        string apiDescriptionVersionId,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(
            $"api-descriptions/{Uri.EscapeDataString(apiDescriptionVersionId)}/callable-operations",
            cancellationToken);
        var json = await ReadSuccessJsonAsync(response, cancellationToken);
        if (json.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The harness callable-operations response was not a JSON array.");
        }
        return json.EnumerateArray().Select(operation => operation.Clone()).ToArray();
    }

    public async Task<HarnessHttpResult> RequestExecutionAsync(
        HarnessExecutionRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsJsonAsync("executions", request, JsonOptions, cancellationToken);
        var body = await ReadOptionalJsonAsync(response, cancellationToken);
        return new HarnessHttpResult(response.StatusCode, body);
    }

    public Task<JsonElement> GetExecutionAsync(string executionId, CancellationToken cancellationToken) =>
        GetJsonAsync($"executions/{Uri.EscapeDataString(executionId)}", cancellationToken);

    public Task<JsonElement> GetInspectionAsync(string executionId, CancellationToken cancellationToken) =>
        GetJsonAsync($"executions/{Uri.EscapeDataString(executionId)}/inspection", cancellationToken);

    public async Task<IReadOnlyList<JsonElement>> GetAttemptsAsync(
        string executionId,
        CancellationToken cancellationToken) =>
        ReadArray(await GetJsonAsync(
            $"executions/{Uri.EscapeDataString(executionId)}/attempts", cancellationToken),
            "execution-attempts");

    public async Task<IReadOnlyList<JsonElement>> GetEvidenceAsync(
        string executionId,
        CancellationToken cancellationToken) =>
        ReadArray(await GetJsonAsync(
            $"executions/{Uri.EscapeDataString(executionId)}/evidence", cancellationToken),
            "execution-evidence");

    public Task<JsonElement> EvaluateReplaySafetyAsync(
        string executionId,
        CancellationToken cancellationToken) =>
        PostJsonAsync($"executions/{Uri.EscapeDataString(executionId)}/replay", cancellationToken);

    private async Task<JsonElement> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(relativeUri, cancellationToken);
        return await ReadSuccessJsonAsync(response, cancellationToken);
    }

    private async Task<JsonElement> PostJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(relativeUri, content: null, cancellationToken);
        return await ReadSuccessJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonElement> ReadSuccessJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HarnessHttpException(response.StatusCode, body);
        }
        return ParseJson(body, response.StatusCode);
    }

    private static async Task<JsonElement?> ReadOptionalJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            return null;
        }
    }

    private static JsonElement ParseJson(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The harness returned invalid JSON with HTTP {(int)statusCode}.", exception);
        }
    }

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"The harness {description} response was not a JSON array.");
        }
        return value.EnumerateArray().Select(item => item.Clone()).ToArray();
    }
}

public sealed class HarnessEffectCoordinator(
    MachineDbContext db,
    MachineStore machineStore,
    HarnessExecutionClient harness,
    HarnessCapabilityBindings bindings,
    EffectFaultPlan faults)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EffectOperationResponse> RequestAsync(
        string instanceId,
        StartEffectRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalized = EffectCoordinator.NormalizeRequest(request);
        var binding = bindings.Get(normalized.Capability);
        var idempotencyKey = CreateIdempotencyKey(instanceId, normalized.OperationId);
        var requestJson = JsonSerializer.Serialize(normalized, JsonOptions);
        var requestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));
        var observationType = GetObservationType(normalized.Capability);
        var intent = await machineStore.CreateEffectIntentAsync(
            instanceId,
            normalized.OperationId,
            normalized.Capability,
            binding.ProviderIdentity,
            observationType,
            requestJson,
            requestDigest,
            now,
            cancellationToken,
            new EffectHarnessBinding(
                binding.ProviderId,
                binding.ApiDescriptionVersionId,
                binding.OperationKey,
                idempotencyKey,
                OperationReplaySafety: null,
                binding.CredentialBindingId));

        EnsureBindingMatches(intent.Operation, binding, idempotencyKey);
        if (!intent.AlreadyRequested)
        {
            ThrowIfArmed(normalized.OperationId, "AfterLocalIntentCommitBeforeHarness");
            await ExecuteInitialAsync(intent.Operation, normalized, binding, cancellationToken);
        }
        return await GetResponseAsync(instanceId, normalized.OperationId, cancellationToken);
    }

    public async Task<EffectOperationResponse> ResumeAsync(
        string instanceId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var operation = await machineStore.GetEffectOperationAsync(instanceId, operationId, cancellationToken);
        if (operation.HarnessExecutionId is null)
        {
            var latestAttemptExists = await db.EffectAttempts.AsNoTracking()
                .AnyAsync(item => item.EffectOperationId == operation.Id, cancellationToken);
            if (!latestAttemptExists)
            {
                var binding = new HarnessCapabilityBinding(
                    operation.Capability,
                    operation.ProviderIdentity,
                    operation.HarnessProviderId
                        ?? throw new InvalidOperationException("Pending harness effect has no persisted provider ID."),
                    operation.ApiDescriptionVersionId
                        ?? throw new InvalidOperationException("Pending harness effect has no persisted description version."),
                    operation.HarnessOperationKey
                        ?? throw new InvalidOperationException("Pending harness effect has no persisted operation key."),
                    operation.CredentialBindingId);
                var pendingOperation = await db.EffectOperations.SingleAsync(
                    item => item.Id == operation.Id, cancellationToken);
                var request = JsonSerializer.Deserialize<SyntheticEffectRequest>(
                    operation.RequestJson, JsonOptions)
                    ?? throw new InvalidDataException(
                        $"Stored request for effect operation '{operationId}' is invalid.");
                await ExecuteInitialAsync(pendingOperation, request, binding, cancellationToken);
            }
            return await GetResponseAsync(instanceId, operationId, cancellationToken);
        }

        var trackedOperation = await db.EffectOperations.SingleAsync(
            item => item.Id == operation.Id, cancellationToken);
        var attempt = await db.EffectAttempts
            .Where(item => item.EffectOperationId == operation.Id)
            .OrderByDescending(item => item.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (attempt is null)
        {
            throw new InvalidOperationException(
                $"Harness execution '{operation.HarnessExecutionId}' has no corresponding durable local attempt.");
        }

        await RefreshExecutionAsync(trackedOperation, attempt, cancellationToken);
        return await GetResponseAsync(instanceId, operationId, cancellationToken);
    }

    public async Task<EffectOperationResponse> GetResponseAsync(
        string instanceId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var operation = await machineStore.GetEffectOperationAsync(instanceId, operationId, cancellationToken);
        var attempts = await machineStore.ListEffectAttemptsAsync(operation.Id, cancellationToken);
        var responses = attempts.Select(ToResponse).ToArray();
        var status = operation.Status;
        if (attempts.LastOrDefault() is
            { TransportOutcome: "AwaitingHarnessResponse" or "AwaitingResponse", ProviderOutcome: "Unknown" })
        {
            status = "OutcomeUnknown";
        }
        return new EffectOperationResponse(
            operation.InstanceId,
            operation.OperationId,
            operation.DefinitionId,
            operation.DefinitionVersion,
            operation.ExecutionRevision,
            operation.Capability,
            operation.ProviderIdentity,
            operation.ObservationType,
            operation.RequestDigest,
            status,
            operation.RequestedAt,
            operation.UpdatedAt,
            responses,
            operation.HarnessProviderId,
            operation.ApiDescriptionVersionId,
            operation.HarnessOperationKey,
            operation.HarnessExecutionId,
            operation.HarnessIdempotencyKey,
            operation.HarnessExecutionState,
            operation.OperationReplaySafety,
            operation.IdempotencyDecision,
            operation.ReplayDecision,
            operation.OutcomeConfidence);
    }

    public Task<IReadOnlyList<EffectOperationRecord>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken) =>
        machineStore.ListEffectOperationsAsync(instanceId, cancellationToken);

    public async Task<JsonElement> InspectHarnessAsync(
        string instanceId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var operation = await machineStore.GetEffectOperationAsync(instanceId, operationId, cancellationToken);
        if (operation.HarnessExecutionId is null)
        {
            return JsonSerializer.SerializeToElement(new
            {
                operation = await GetResponseAsync(instanceId, operationId, cancellationToken),
                correlationAvailable = false,
                explanation = "The invocation response did not establish a harness execution ID. The published API has no lookup-by-idempotency-key route; inspection and automatic re-invocation are unavailable."
            }, JsonOptions);
        }

        var inspection = await harness.GetInspectionAsync(operation.HarnessExecutionId, cancellationToken);
        var attempts = await harness.GetAttemptsAsync(operation.HarnessExecutionId, cancellationToken);
        var evidence = await harness.GetEvidenceAsync(operation.HarnessExecutionId, cancellationToken);
        var replay = await harness.EvaluateReplaySafetyAsync(operation.HarnessExecutionId, cancellationToken);
        return JsonSerializer.SerializeToElement(new
        {
            operation = await GetResponseAsync(instanceId, operationId, cancellationToken),
            correlationAvailable = true,
            inspection,
            attempts,
            evidence,
            replaySafetyEvaluation = replay
        }, JsonOptions);
    }

    private async Task ExecuteInitialAsync(
        EffectOperationRecord operation,
        SyntheticEffectRequest request,
        HarnessCapabilityBinding binding,
        CancellationToken cancellationToken)
    {
        var operations = await harness.GetCallableOperationsAsync(binding.ApiDescriptionVersionId, cancellationToken);
        var selected = operations.FirstOrDefault(item =>
            item.TryGetProperty("key", out var key) && key.GetString() == binding.OperationKey);
        if (selected.ValueKind == JsonValueKind.Object &&
            selected.TryGetProperty("replaySafety", out var replaySafety) &&
            replaySafety.ValueKind == JsonValueKind.Number)
        {
            operation.OperationReplaySafety = replaySafety.GetInt32();
            await db.SaveChangesAsync(cancellationToken);
        }

        var attempt = new EffectAttemptRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            EffectOperationId = operation.Id,
            AttemptNumber = await db.EffectAttempts.CountAsync(
                item => item.EffectOperationId == operation.Id, cancellationToken) + 1,
            StartedAt = DateTimeOffset.UtcNow,
            TransportOutcome = "AwaitingHarnessResponse",
            ProviderOutcome = "Unknown",
            LocalPersistenceOutcome = "Pending",
            ObservationStatus = "NotSubmitted",
            OutcomeConfidence = "RemoteOutcomeUnknown"
        };
        operation.Status = "AttemptInitiated";
        operation.UpdatedAt = attempt.StartedAt;
        db.EffectAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        var invocation = BuildRequest(operation, request, binding);
        HarnessHttpResult result;
        try
        {
            result = await harness.RequestExecutionAsync(invocation, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            attempt.TransportOutcome = "HarnessResponseMissing";
            attempt.ProviderOutcome = "Unknown";
            attempt.Error = $"The execution request may have reached the harness, but no response was received: {exception.Message}";
            attempt.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.LocalPersistenceOutcome = "Persisted";
            operation.Status = "OutcomeUnknown";
            operation.OutcomeConfidence = "RemoteOutcomeUnknown";
            operation.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        ThrowIfArmed(request.OperationId, "AfterHarnessResponseBeforeLocalPersistence");
        if (result.StatusCode != HttpStatusCode.Created)
        {
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            attempt.TransportOutcome = "HarnessResponseReceived";
            attempt.Error = $"Harness POST /executions returned HTTP {(int)result.StatusCode}; no execution ID was received.";
            if (result.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            {
                attempt.ProviderOutcome = "Rejected";
                operation.Status = "HarnessRejected";
                attempt.OutcomeConfidence = "NotApplicable";
                operation.OutcomeConfidence = "NotApplicable";
            }
            else
            {
                operation.Status = "OutcomeUnknown";
                attempt.OutcomeConfidence = "RemoteOutcomeUnknown";
                operation.OutcomeConfidence = "RemoteOutcomeUnknown";
            }
            attempt.LocalPersistenceOutcome = "Persisted";
            operation.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (result.Body is not { } responseBody ||
            !responseBody.TryGetProperty("execution", out var execution) ||
            !execution.TryGetProperty("id", out var executionIdElement) ||
            executionIdElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("The accepted harness response did not include an execution identity.");
        }

        var executionId = executionIdElement.GetString();
        if (string.IsNullOrWhiteSpace(executionId))
        {
            throw new InvalidDataException("The accepted harness response included an empty execution identity.");
        }
        operation.HarnessExecutionId = executionId;
        attempt.HarnessExecutionId = executionId;
        ApplyExecutionSummary(operation, attempt, execution);
        if (responseBody.TryGetProperty("providerResponse", out var providerResponse) &&
            providerResponse.ValueKind == JsonValueKind.Object &&
            providerResponse.TryGetProperty("body", out var responseContent))
        {
            var providerBody = responseContent.ValueKind switch
            {
                JsonValueKind.String => responseContent.GetString(),
                JsonValueKind.Object => responseContent.GetRawText(),
                _ => null
            };
            if (providerBody is not null)
            {
                attempt.HarnessResponseStatusCode = ReadOptionalInteger(providerResponse, "statusCode");
                attempt.HarnessResponseTruncated =
                    providerResponse.TryGetProperty("truncated", out var truncated) &&
                    truncated.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? truncated.GetBoolean()
                        : null;
                attempt.ProviderEvidenceJson = providerBody;
            }
        }
        attempt.TransportOutcome = "HarnessResponseReceived";
        attempt.LocalPersistenceOutcome = "Persisted";
        attempt.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await RefreshExecutionAsync(operation, attempt, cancellationToken);
    }

    private async Task RefreshExecutionAsync(
        EffectOperationRecord operation,
        EffectAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        var executionId = operation.HarnessExecutionId
            ?? throw new InvalidOperationException("Cannot inspect a harness execution without its identity.");
        var inspection = await harness.GetInspectionAsync(executionId, cancellationToken);
        var attempts = await harness.GetAttemptsAsync(executionId, cancellationToken);
        var evidence = await harness.GetEvidenceAsync(executionId, cancellationToken);
        var replay = await harness.EvaluateReplaySafetyAsync(executionId, cancellationToken);
        if (!replay.TryGetProperty("providerCalled", out var providerCalled) ||
            providerCalled.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException("Harness replay-safety evaluation must confirm that the provider was not called.");
        }

        if (!inspection.TryGetProperty("execution", out var inspectedExecution) ||
            !MatchesConfiguredOperation(inspectedExecution, operation))
        {
            operation.Status = "HarnessEvidenceMismatch";
            operation.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.ProviderOutcome = "Unknown";
            attempt.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.Error = "Retrieved harness execution identity does not match the durable provider, description version, operation, and idempotency binding.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        ApplyExecutionSummary(operation, attempt, inspectedExecution);
        if (inspection.TryGetProperty("outcomeConfidence", out var confidence) &&
            confidence.ValueKind == JsonValueKind.String)
        {
            operation.OutcomeConfidence = confidence.GetString();
            attempt.OutcomeConfidence = confidence.GetString();
        }
        if (inspection.TryGetProperty("operation", out var importedOperation) &&
            importedOperation.ValueKind == JsonValueKind.Object &&
            importedOperation.TryGetProperty("replaySafety", out var replaySafety) &&
            replaySafety.ValueKind == JsonValueKind.Number)
        {
            operation.OperationReplaySafety = replaySafety.GetInt32();
        }
        if (replay.TryGetProperty("replayDecision", out var replayDecision) &&
            replayDecision.ValueKind == JsonValueKind.Number)
        {
            operation.ReplayDecision = replayDecision.GetInt32();
            attempt.ReplayDecision = replayDecision.GetInt32();
        }
        attempt.HarnessAttemptIdsJson = SerializeIdentifiers(attempts);
        attempt.HarnessEvidenceIdsJson = SerializeIdentifiers(evidence);
        attempt.LocalPersistenceOutcome = "Persisted";
        await db.SaveChangesAsync(cancellationToken);

        if (operation.HarnessExecutionState != 2)
        {
            ApplyNonSuccessfulHarnessState(operation, attempt);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (operation.OutcomeConfidence != "ObservedAndValidated")
        {
            operation.Status = "HarnessOutcomeNotValidated";
            attempt.ProviderOutcome = "Unknown";
            attempt.Error = "The harness execution succeeded, but its inspection does not establish a validated remote outcome.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (attempt.ProviderEvidenceJson is null)
        {
            operation.Status = "HarnessSucceededResponseMissing";
            operation.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.ProviderOutcome = "Unknown";
            attempt.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.Error = "The harness confirms a successful operation, but its transient provider response body is unavailable and the body is not durably archived by the harness.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (attempt.HarnessResponseTruncated == true ||
            attempt.HarnessResponseStatusCode is not (>= 200 and < 300))
        {
            operation.Status = "HarnessResponseNotUsable";
            operation.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.ProviderOutcome = "Unknown";
            attempt.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.Error = "The harness response was truncated or did not carry a successful provider HTTP status; business response data was not accepted.";
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        ProviderEffectResult providerResult;
        if (attempt.ProviderOutcome is "Succeeded" or "Failed" or "Refused")
        {
            if (attempt.ProviderOccurredAt is null)
            {
                throw new InvalidDataException(
                    $"Persisted provider outcome for operation '{operation.OperationId}' has no occurrence time.");
            }
            using var evidenceDocument = JsonDocument.Parse(attempt.ProviderEvidenceJson);
            providerResult = new ProviderEffectResult(
                operation.ProviderIdentity,
                attempt.ProviderOutcome,
                attempt.ProviderEffectId,
                evidenceDocument.RootElement.Clone(),
                attempt.ProviderOccurredAt.Value);
        }
        else
        {
            providerResult = ParseProviderResult(attempt.ProviderEvidenceJson);
            attempt.ProviderEvidenceJson = providerResult.Evidence.GetRawText();
            attempt.ProviderEffectId = providerResult.EffectId;
            attempt.ProviderOccurredAt = providerResult.OccurredAt;
            attempt.ProviderOutcome = providerResult.Outcome;
        }
        operation.Status = providerResult.Outcome switch
        {
            "Succeeded" => "ProviderSucceeded",
            "Failed" => "ProviderFailed",
            "Refused" => "ProviderRefused",
            _ => throw new InvalidDataException($"Provider returned unsupported outcome '{providerResult.Outcome}'.")
        };
        if (attempt.ObservationStatus != "NotSubmitted")
        {
            operation.Status = attempt.ObservationStatus is "Accepted" or "Duplicate"
                ? "ObservationAccepted"
                : "ObservationRefused";
        }
        operation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        if (providerResult.Outcome == "Succeeded" && attempt.ObservationStatus == "NotSubmitted")
        {
            if (faults.Consume(operation.OperationId, "BeforeObservationAcceptance"))
            {
                throw new EffectInterruptionException(
                    $"Controlled Gate 7 interruption before observation acceptance for operation '{operation.OperationId}'.");
            }
            await AcceptObservationAsync(
                operation,
                attempt,
                providerResult.Evidence,
                providerResult.OccurredAt,
                DateTimeOffset.UtcNow,
                cancellationToken);
        }
    }

    private async Task AcceptObservationAsync(
        EffectOperationRecord operation,
        EffectAttemptRecord attempt,
        JsonElement evidence,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var evidenceValues = evidence.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        var result = await machineStore.SubmitInputAsync(
            operation.InstanceId,
            "Observation",
            $"harness-effect-result-{operation.OperationId}",
            operation.ObservationType,
            operation.ExecutionRevision,
            operation.OperationId,
            operation.ProviderIdentity,
            occurredAt,
            payload: new Dictionary<string, JsonElement>
            {
                ["effectId"] = JsonSerializer.SerializeToElement(attempt.ProviderEffectId, JsonOptions),
                ["harnessExecutionId"] = JsonSerializer.SerializeToElement(operation.HarnessExecutionId, JsonOptions)
            },
            facts: null,
            evidence: evidenceValues,
            decisionEvidence: null,
            factsProvenance: null,
            trustedSource: true,
            now: receivedAt,
            cancellationToken: cancellationToken);

        var accepted = result.Message.Status == "Accepted";
        InputAttemptRecord? acceptedAttempt = result.Submission?.Attempt;
        if (result.Message.Status == "Duplicate" && result.Message.AttemptId is not null)
        {
            acceptedAttempt = await machineStore.GetAttemptAsync(
                operation.InstanceId, result.Message.AttemptId, cancellationToken);
            accepted = acceptedAttempt.Status == "Accepted";
        }

        var persistedAttempt = await db.EffectAttempts.SingleAsync(
            item => item.Id == attempt.Id, cancellationToken);
        var persistedOperation = await db.EffectOperations.SingleAsync(
            item => item.Id == operation.Id, cancellationToken);
        persistedAttempt.ObservationStatus = result.Message.Status;
        persistedAttempt.ObservationReceiptId = result.Message.Id;
        persistedAttempt.WorkflowTransitionId = acceptedAttempt?.TransitionId;
        persistedAttempt.WorkflowStateBefore = acceptedAttempt?.PreviousState;
        persistedAttempt.WorkflowStateAfter = acceptedAttempt?.TargetState;
        persistedAttempt.WorkflowRevisionAfter = acceptedAttempt?.RevisionAfter;
        persistedOperation.Status = accepted ? "ObservationAccepted" : "ObservationRefused";
        persistedOperation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static HarnessExecutionRequest BuildRequest(
        EffectOperationRecord operation,
        SyntheticEffectRequest request,
        HarnessCapabilityBinding binding)
    {
        var body = request.Capability switch
        {
            "offer-document" => JsonSerializer.SerializeToElement(new
            {
                customerReference = request.CustomerReference,
                offerReference = request.OfferReference
            }, JsonOptions),
            "equipment-reservation" => JsonSerializer.SerializeToElement(new
            {
                operationId = operation.HarnessIdempotencyKey,
                assetId = request.AssetId
            }, JsonOptions),
            "customer-notification" => JsonSerializer.SerializeToElement(new
            {
                operationId = operation.HarnessIdempotencyKey,
                customerReference = request.CustomerReference,
                messageTemplate = request.MessageTemplate
            }, JsonOptions),
            _ => throw new ArgumentException($"Unsupported capability '{request.Capability}'.")
        };
        IReadOnlyDictionary<string, object?>? pathParameters = request.Capability == "offer-document"
            ? new Dictionary<string, object?> { ["operationId"] = operation.HarnessIdempotencyKey }
            : null;
        return new HarnessExecutionRequest(
            binding.ProviderId,
            binding.ApiDescriptionVersionId,
            binding.OperationKey,
            binding.CredentialBindingId,
            operation.HarnessIdempotencyKey
                ?? throw new InvalidOperationException("Harness idempotency key was not durably assigned."),
            MaxAttempts: 1,
            RetryableFailureCodes: [],
            pathParameters,
            QueryParameters: null,
            HeaderParameters: null,
            body);
    }

    private static bool MatchesConfiguredOperation(JsonElement execution, EffectOperationRecord operation) =>
        ReadString(execution, "id") == operation.HarnessExecutionId &&
        ReadString(execution, "providerId") == operation.HarnessProviderId &&
        ReadString(execution, "apiDescriptionVersionId") == operation.ApiDescriptionVersionId &&
        ReadString(execution, "operationKey") == operation.HarnessOperationKey &&
        ReadString(execution, "idempotencyKey") == operation.HarnessIdempotencyKey;

    private static void ApplyExecutionSummary(
        EffectOperationRecord operation,
        EffectAttemptRecord attempt,
        JsonElement execution)
    {
        var state = ReadInteger(execution, "state");
        var decision = ReadOptionalInteger(execution, "idempotencyDecision");
        operation.HarnessExecutionState = state;
        operation.IdempotencyDecision = decision;
        attempt.HarnessExecutionState = state;
        attempt.HarnessResponseStatusCode = ReadOptionalInteger(execution, "responseStatusCode");
        attempt.IdempotencyDecision = decision;
        attempt.HarnessExecutionId = ReadString(execution, "id") ?? attempt.HarnessExecutionId;
        attempt.HarnessRejectionReason = ReadOptionalInteger(execution, "rejectionReason");
        attempt.HarnessFailureCode = ReadString(execution, "failureCode");
        attempt.HarnessFailureMessage = ReadString(execution, "failureMessage");
    }

    private static void ApplyNonSuccessfulHarnessState(
        EffectOperationRecord operation,
        EffectAttemptRecord attempt)
    {
        (operation.Status, attempt.ProviderOutcome) = operation.HarnessExecutionState switch
        {
            0 => ("HarnessRequested", "Unknown"),
            1 => ("HarnessRunning", "Unknown"),
            3 => ("HarnessFailed", "HarnessFailed"),
            4 => ("OutcomeUnknown", "Unknown"),
            5 => ("HarnessRejected", "HarnessRejected"),
            _ => ("OutcomeUnknown", "Unknown")
        };
        if (operation.HarnessExecutionState is 0 or 1 or 4)
        {
            operation.OutcomeConfidence = "RemoteOutcomeUnknown";
            attempt.OutcomeConfidence = "RemoteOutcomeUnknown";
        }
        else if (operation.HarnessExecutionState == 5)
        {
            operation.OutcomeConfidence = "NotApplicable";
            attempt.OutcomeConfidence = "NotApplicable";
        }
        operation.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static ProviderEffectResult ParseProviderResult(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("outcome", out var outcomeElement) ||
            outcomeElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("evidence", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("occurredAt", out var occurredAtElement) ||
            occurredAtElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(occurredAtElement.GetString(), out var occurredAt))
        {
            throw new InvalidDataException("The validated provider response did not contain the expected outcome, evidence object, and occurrence time.");
        }
        var effectId = root.TryGetProperty("effectId", out var effectIdElement) &&
                       effectIdElement.ValueKind == JsonValueKind.String
            ? effectIdElement.GetString()
            : null;
        return new ProviderEffectResult(
            ProviderIdentity: string.Empty,
            outcomeElement.GetString()!,
            effectId,
            evidence.Clone(),
            occurredAt);
    }

    private static string GetObservationType(string capability) => capability switch
    {
        "offer-document" => "OfferDocumentGenerated",
        "equipment-reservation" => "EquipmentReserved",
        "customer-notification" => "CustomerNotified",
        _ => throw new ArgumentException($"Unsupported capability '{capability}'.")
    };

    private static string CreateIdempotencyKey(string instanceId, string operationId)
    {
        var identity = Encoding.UTF8.GetBytes($"{instanceId}\0{operationId}");
        return $"dsm-{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}";
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int ReadInteger(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : throw new InvalidDataException($"Harness response omitted integer-valued '{property}'.");

    private static int? ReadOptionalInteger(JsonElement value, string property) =>
        value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32()
            : null;

    private static string SerializeIdentifiers(IReadOnlyList<JsonElement> values) =>
        JsonSerializer.Serialize(values.Select(value => ReadString(value, "id")).Where(id => id is not null), JsonOptions);

    private static EffectAttemptResponse ToResponse(EffectAttemptRecord attempt)
    {
        JsonElement? evidence = attempt.ProviderEvidenceJson is null
            ? null
            : ParseElement(attempt.ProviderEvidenceJson);
        return new EffectAttemptResponse(
            attempt.Id,
            attempt.AttemptNumber,
            attempt.StartedAt,
            attempt.CompletedAt,
            attempt.TransportOutcome,
            attempt.ProviderOutcome,
            attempt.ProviderEffectId,
            attempt.ProviderOccurredAt,
            evidence,
            attempt.LocalPersistenceOutcome,
            attempt.ObservationStatus,
            attempt.ObservationReceiptId,
            attempt.Error,
            attempt.HarnessExecutionId,
            attempt.HarnessExecutionState,
            attempt.HarnessResponseStatusCode,
            attempt.HarnessResponseTruncated,
            attempt.HarnessRejectionReason,
            attempt.HarnessFailureCode,
            attempt.HarnessFailureMessage,
            DeserializeStringList(attempt.HarnessAttemptIdsJson),
            DeserializeStringList(attempt.HarnessEvidenceIdsJson),
            attempt.IdempotencyDecision,
            attempt.ReplayDecision,
            attempt.OutcomeConfidence,
            attempt.WorkflowTransitionId,
            attempt.WorkflowStateBefore,
            attempt.WorkflowStateAfter,
            attempt.WorkflowRevisionAfter);
    }

    private static IReadOnlyList<string>? DeserializeStringList(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<string[]>(json, JsonOptions);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void EnsureBindingMatches(
        EffectOperationRecord operation,
        HarnessCapabilityBinding binding,
        string idempotencyKey)
    {
        if (operation.HarnessProviderId != binding.ProviderId ||
            operation.ProviderIdentity != binding.ProviderIdentity ||
            operation.ApiDescriptionVersionId != binding.ApiDescriptionVersionId ||
            operation.HarnessOperationKey != binding.OperationKey ||
            operation.HarnessIdempotencyKey != idempotencyKey)
        {
            throw new DefinitionConflictException(
                $"Effect operation '{operation.OperationId}' is already bound to a different exact harness provider description or operation.");
        }
    }

    private void ThrowIfArmed(string operationId, string point)
    {
        if (faults.Consume(operationId, point))
        {
            throw new EffectInterruptionException(
                $"Controlled Gate 7 interruption at '{point}' for operation '{operationId}'.");
        }
    }
}
