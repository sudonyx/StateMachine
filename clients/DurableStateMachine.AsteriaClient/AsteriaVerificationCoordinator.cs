using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using DurableStateMachine.AsteriaClient;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaHarnessOperationBinding(
    string ProviderIdentity,
    string ProviderId,
    string ApiDescriptionVersionId,
    string OperationKey);

public sealed class AsteriaVerificationCoordinator(
    AsteriaReleaseRegistry registry,
    AsteriaActivityLedger ledger,
    AsteriaStateMachineClient stateMachine,
    HttpClient harnessClient,
    string definitionJson,
    IReadOnlyDictionary<string, AsteriaHarnessOperationBinding> bindings,
    TimeProvider? timeProvider = null)
{
    private const string SourceIdentity = "AsteriaVerificationCoordinator";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _definitionDigest = AsteriaActivityLedger.Fingerprint(definitionJson);

    public async Task<AsteriaActivitySnapshot> ExecuteAsync(
        string releaseId,
        string concernId,
        string attemptId = "initial",
        CancellationToken cancellationToken = default)
    {
        var binding = GetBinding(concernId);
        var release = await registry.GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
        EnsureCurrentAcceptedAssessment(release);

        var validation = AsteriaDecisionApiClient.ValidateResponse(
            release.Work.OriginalResponseJson!, release);
        var applicability = AsteriaDecisionApiClient.ReadObligationApplicabilities(validation);
        var requiredApplicability = concernId switch
        {
            "FunctionalVerification" => applicability.FunctionalVerification,
            "SecurityVerification" => applicability.SecurityVerification,
            _ => throw new ArgumentOutOfRangeException(nameof(concernId), concernId,
                "Only functional and security verification are executed through Generic Harness.")
        };
        if (requiredApplicability != "Required")
        {
            throw new InvalidOperationException(
                $"Asteria concern '{concernId}' is '{requiredApplicability}', not Required.");
        }

        var concerns = await stateMachine.GetConcernStatesAsync(
            release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue(concernId, out var concern))
        {
            throw new InvalidDataException($"Asteria workflow has no concern '{concernId}'.");
        }

        var identifiers = CreateIdentifiers(release, concernId, attemptId);
        var request = JsonSerializer.Serialize(new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            workflowInstanceId = release.WorkflowInstanceId,
            concernId,
            activityId = identifiers.ActivityId,
            expectedOperationId = identifiers.OperationId
        }, JsonOptions);
        var reservation = new AsteriaActivityReservation(
            identifiers.WorkId,
            identifiers.ActivityId,
            release.ReleaseId,
            release.AssessmentGeneration,
            release.MaterialDigest,
            release.WorkflowInstanceId,
            release.DefinitionId,
            release.DefinitionVersion,
            _definitionDigest,
            concernId,
            concernId,
            attemptId,
            identifiers.OperationId,
            (await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
                ?? throw new InvalidDataException("Asteria workflow instance is missing.")).Revision,
            concern.Revision,
            identifiers.SubmissionId,
            identifiers.IdempotencyKey,
            request,
            binding.ProviderIdentity,
            binding.ApiDescriptionVersionId,
            binding.OperationKey,
            null,
            null,
            null);
        var activity = await ledger.ReserveAsync(reservation, _timeProvider.GetUtcNow(), cancellationToken);
        if (activity.Status == "Accepted")
        {
            return activity;
        }
        if (activity.Status is "OutcomeUnknown" or "Refused" or "Conflicting")
        {
            throw new InvalidOperationException(
                $"Asteria verification activity '{activity.WorkId}' is '{activity.Status}' and requires reconciliation.");
        }

        if (activity.Status == "Reserved")
        {
            var expected = await stateMachine.EnsureExpectedConcernOperationAsync(
                release,
                activity.ExpectedOperationId,
                MappingForConcern(concernId),
                SourceIdentity,
                VerificationEvidenceFields,
                cancellationToken);
            if (expected.ExpectedRevision != activity.ParentRevision)
            {
                throw new InvalidDataException("Asteria activity parent revision differs from its expected-operation binding.");
            }
            activity = await ledger.MarkExpectedOperationRegisteredAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }

        if (activity.Status == "OperationRegistered")
        {
            await stateMachine.RequestConcernWorkAsync(
                release,
                concernId,
                CommandForConcern(concernId),
                StableMessageId(activity.WorkId, "request"),
                cancellationToken);
            activity = await ledger.MarkDispatchPendingAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
            return await DispatchAndProcessAsync(release, activity, binding, cancellationToken);
        }

        if (activity.Status is "DispatchPending" or "Dispatched")
        {
            _ = await ledger.MarkOutcomeUnknownAsync(
                activity.WorkId,
                "A prior dispatch may have reached Generic Harness; a fresh mutating call is prohibited.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new InvalidOperationException(
                $"Asteria verification activity '{activity.WorkId}' has an ambiguous external outcome and will not be resent.");
        }

        if (activity.Status is "ResponsePersisted" or "ObservationSubmitted")
        {
            return await ProcessArchivedResponseAsync(release, activity, cancellationToken);
        }

        throw new InvalidDataException(
            $"Asteria verification activity '{activity.WorkId}' has unsupported status '{activity.Status}'.");
    }

    private async Task<AsteriaActivitySnapshot> DispatchAndProcessAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        AsteriaHarnessOperationBinding binding,
        CancellationToken cancellationToken)
    {
        await EnsureStillCurrentGenerationAsync(release, cancellationToken);
        using var requestBody = JsonDocument.Parse(activity.OriginalRequestJson);
        using var request = JsonContent.Create(new
        {
            providerId = binding.ProviderId,
            apiDescriptionVersionId = binding.ApiDescriptionVersionId,
            operationKey = binding.OperationKey,
            idempotencyKey = activity.IdempotencyKey,
            maxAttempts = 1,
            body = requestBody.RootElement.Clone()
        }, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await harnessClient.PostAsync("executions", request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            _ = await ledger.MarkOutcomeUnknownAsync(
                activity.WorkId,
                $"Generic Harness dispatch failed after dispatch intent was committed: {exception.Message}",
                _timeProvider.GetUtcNow(),
                CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            _ = await ledger.MarkOutcomeUnknownAsync(
                activity.WorkId,
                $"Generic Harness dispatch was interrupted after dispatch intent was committed: {exception.Message}",
                _timeProvider.GetUtcNow(),
                CancellationToken.None);
            throw;
        }

        using (response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.Created)
            {
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                _ = await ledger.MarkOutcomeUnknownAsync(
                    activity.WorkId,
                    $"Generic Harness did not return its documented 201 response; HTTP {(int)response.StatusCode}.",
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None);
                throw new HttpRequestException(
                    $"Generic Harness execution response was not 201 Created: {responseText}",
                    inner: null,
                    response.StatusCode);
            }

            JsonDocument harnessDocument;
            try
            {
                harnessDocument = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                _ = await ledger.MarkOutcomeUnknownAsync(
                    activity.WorkId,
                    $"Generic Harness response could not establish an execution identity: {exception.Message}",
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None);
                throw new InvalidDataException("Generic Harness returned an invalid execution response.", exception);
            }

            using (harnessDocument)
            {
                var root = harnessDocument.RootElement;
                var execution = Required(root, "execution");
                var executionId = String(execution, "id");
                if (!Guid.TryParse(executionId, out _))
                {
                    _ = await ledger.MarkOutcomeUnknownAsync(
                        activity.WorkId,
                        "Generic Harness response did not contain a valid execution identifier.",
                        _timeProvider.GetUtcNow(),
                        CancellationToken.None);
                    throw new InvalidDataException("Generic Harness returned an invalid execution identifier.");
                }
                if (String(execution, "providerId") != binding.ProviderId ||
                    String(execution, "apiDescriptionVersionId") != binding.ApiDescriptionVersionId ||
                    String(execution, "operationKey") != binding.OperationKey)
                {
                    _ = await ledger.MarkOutcomeUnknownAsync(
                        activity.WorkId,
                        "Generic Harness execution metadata does not match the persisted provider operation binding.",
                        _timeProvider.GetUtcNow(),
                        cancellationToken);
                    throw new InvalidDataException(
                        "Generic Harness execution metadata does not match the configured provider, description, and operation.");
                }
                activity = await ledger.RecordHarnessExecutionAsync(
                    activity.WorkId,
                    executionId,
                    binding.ProviderIdentity,
                    binding.ApiDescriptionVersionId,
                    binding.OperationKey,
                    _timeProvider.GetUtcNow(),
                    cancellationToken);

                var providerResponse = Required(root, "providerResponse");
                if (providerResponse.ValueKind != JsonValueKind.Object)
                {
                    _ = await ledger.MarkOutcomeUnknownAsync(
                        activity.WorkId,
                        "Generic Harness returned no provider response body; business outcome is not established.",
                        _timeProvider.GetUtcNow(),
                        cancellationToken);
                    throw new InvalidOperationException(
                        $"Generic Harness execution '{executionId}' has no recoverable provider response body.");
                }

                var statusCode = Required(providerResponse, "statusCode").GetInt32();
                var contentType = NullableString(providerResponse, "contentType");
                var responseBody = AsteriaHarnessResponseBody.Read(providerResponse);
                var truncated = Required(providerResponse, "truncated").GetBoolean();
                var archivedResponse = JsonSerializer.Serialize(
                    new ProviderResponseEnvelope(statusCode, contentType, responseBody, truncated),
                    JsonOptions);
                activity = await ledger.RecordOriginalResponseAsync(
                    activity.WorkId, archivedResponse, _timeProvider.GetUtcNow(), cancellationToken);
                if (Required(execution, "state").GetInt32() != 2 ||
                    statusCode is < 200 or >= 300 ||
                    truncated ||
                    string.IsNullOrWhiteSpace(responseBody))
                {
                    _ = await ledger.MarkOutcomeUnknownAsync(
                        activity.WorkId,
                        "Harness execution did not provide a complete successful provider response; no business result is inferred.",
                        _timeProvider.GetUtcNow(),
                        cancellationToken);
                    throw new InvalidOperationException(
                        $"Generic Harness execution '{executionId}' did not establish a complete provider business result.");
                }
            }
        }

        return await ProcessArchivedResponseAsync(release, activity, cancellationToken);
    }

    private async Task<AsteriaActivitySnapshot> ProcessArchivedResponseAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        CancellationToken cancellationToken)
    {
        await EnsureStillCurrentGenerationAsync(release, cancellationToken);
        var envelope = JsonDocument.Parse(activity.OriginalResponseJson
            ?? throw new InvalidDataException("Asteria activity has no archived provider response."));
        string body;
        using (envelope)
        {
            body = String(envelope.RootElement, "body");
            if (Required(envelope.RootElement, "truncated").GetBoolean())
            {
                _ = await ledger.MarkOutcomeUnknownAsync(
                    activity.WorkId,
                    "Archived Generic Harness provider response was truncated.",
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
                throw new InvalidOperationException("Truncated verification response remains unresolved.");
            }
        }

        string result;
        string evidenceId;
        try
        {
            using var providerDocument = JsonDocument.Parse(body);
            var root = providerDocument.RootElement;
            ValidateProviderBinding(root, release, activity);
            result = String(root, "result");
            if (result is not ("Passed" or "Failed" or "Indeterminate"))
            {
                throw new InvalidDataException($"Unsupported verification result '{result}'.");
            }
            evidenceId = String(root, "evidenceId");
            if (string.IsNullOrWhiteSpace(evidenceId))
            {
                throw new InvalidDataException("Verification response evidenceId must not be empty.");
            }
        }
        catch (JsonException exception)
        {
            _ = await ledger.MarkRefusedAsync(
                activity.WorkId,
                $"Provider response is not valid JSON: {exception.Message}",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new InvalidDataException("Verification provider returned malformed JSON.", exception);
        }
        catch (InvalidDataException exception)
        {
            _ = await ledger.MarkRefusedAsync(
                activity.WorkId,
                exception.Message,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw;
        }

        var evidenceDigest = AsteriaActivityLedger.Fingerprint(body);
        activity = await ledger.RecordValidatedOutcomeAsync(
            activity.WorkId, result, evidenceDigest, _timeProvider.GetUtcNow(), cancellationToken);
        var binding = GetBinding(activity.ConcernId);
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["activityId"] = activity.ActivityId,
            ["expectedOperationId"] = activity.ExpectedOperationId,
            ["result"] = result,
            ["evidenceDigest"] = evidenceDigest,
            ["providerExecutionId"] = activity.HarnessExecutionId
        };
        var observation = await stateMachine.SubmitConcernObservationAsync(
            release,
            activity.ConcernId,
            activity.ExpectedOperationId,
            StableMessageId(activity.WorkId, "result-message"),
            StableMessageId(activity.WorkId, "result-observation"),
            MappingForConcern(activity.ConcernId),
            SourceIdentity,
            VerificationEvidenceFields,
            evidence,
            "verificationResult",
            result,
            result,
            cancellationToken);
        activity = await ledger.MarkObservationSubmittedAsync(
            activity.WorkId,
            observation.ReceiptId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        return await ledger.MarkAcceptedAsync(
            activity.WorkId,
            observation.ReceiptId,
            observation.AttemptId,
            observation.HistoryId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private void ValidateProviderBinding(
        JsonElement response,
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity)
    {
        var binding = GetBinding(activity.ConcernId);
        if (activity.ProviderIdentity != binding.ProviderIdentity ||
            activity.ApiDescriptionVersionId != binding.ApiDescriptionVersionId ||
            activity.OperationKey != binding.OperationKey ||
            String(response, "releaseId") != release.ReleaseId ||
            Required(response, "assessmentGeneration").GetInt32() != release.AssessmentGeneration ||
            String(response, "materialDigest") != release.MaterialDigest ||
            String(response, "workflowInstanceId") != release.WorkflowInstanceId ||
            String(response, "activityId") != activity.ActivityId ||
            String(response, "expectedOperationId") != activity.ExpectedOperationId)
        {
            throw new InvalidDataException(
                "Verification provider result does not match the persisted release, activity, and operation binding.");
        }
    }

    private void EnsureCurrentAcceptedAssessment(AsteriaReleaseSnapshot release)
    {
        if (release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.OriginalResponseJson is null ||
            release.Work.Generation != release.AssessmentGeneration ||
            release.Work.MaterialDigest != release.MaterialDigest ||
            release.Work.WorkflowInstanceId != release.WorkflowInstanceId ||
            release.DefinitionVersion is not ("4.0" or "5.0"))
        {
            throw new InvalidOperationException(
                "Verification requires the current generation's accepted, durably bound assessment.");
        }
    }

    private async Task EnsureStillCurrentGenerationAsync(
        AsteriaReleaseSnapshot expected,
        CancellationToken cancellationToken)
    {
        var current = await registry.GetReleaseAsync(expected.ReleaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{expected.ReleaseId}' does not exist.");
        if (current.AssessmentGeneration != expected.AssessmentGeneration ||
            current.MaterialDigest != expected.MaterialDigest ||
            current.WorkflowInstanceId != expected.WorkflowInstanceId ||
            current.Work.WorkId != expected.Work.WorkId)
        {
            throw new InvalidOperationException(
                "Asteria verification activity belongs to a stale assessment generation.");
        }
    }

    private AsteriaHarnessOperationBinding GetBinding(string concernId)
    {
        if (!bindings.TryGetValue(concernId, out var binding))
        {
            throw new InvalidDataException($"No Generic Harness operation binding is configured for '{concernId}'.");
        }
        if (string.IsNullOrWhiteSpace(binding.ProviderIdentity) ||
            !Guid.TryParse(binding.ProviderId, out _) ||
            !Guid.TryParse(binding.ApiDescriptionVersionId, out _) ||
            string.IsNullOrWhiteSpace(binding.OperationKey))
        {
            throw new InvalidDataException(
                $"Generic Harness binding for '{concernId}' is incomplete or has invalid provider/description IDs.");
        }
        return binding;
    }

    private static (string WorkId, string ActivityId, string OperationId, string SubmissionId, string IdempotencyKey)
        CreateIdentifiers(AsteriaReleaseSnapshot release, string concernId, string attemptId)
    {
        var prefix = $"{release.ReleaseId}\0{release.AssessmentGeneration}\0{concernId}\0{attemptId}";
        return (
            StableMessageId(prefix, "work"),
            StableMessageId(prefix, "activity"),
            StableMessageId(prefix, "operation"),
            StableMessageId(prefix, "submission"),
            StableMessageId(prefix, "harness-idempotency"));
    }

    private static string MappingForConcern(string concernId) =>
        concernId switch
        {
            "FunctionalVerification" => "AsteriaFunctionalVerificationResult",
            "SecurityVerification" => "AsteriaSecurityVerificationResult",
            _ => throw new ArgumentOutOfRangeException(nameof(concernId), concernId,
                "Only functional and security verification mappings are supported.")
        };

    private static string CommandForConcern(string concernId) =>
        concernId switch
        {
            "FunctionalVerification" => "RequestFunctionalVerification",
            "SecurityVerification" => "RequestSecurityVerification",
            _ => throw new ArgumentOutOfRangeException(nameof(concernId), concernId,
                "Only functional and security verification commands are supported.")
        };

    private static string StableMessageId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static JsonElement Required(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Provider or Harness response is missing required field '{name}'.");

    private static string String(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Provider or Harness response field '{name}' must be a string.");
    }

    private static string? NullableString(JsonElement parent, string name)
    {
        var value = Required(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException($"Provider or Harness response field '{name}' must be a nullable string.")
        };
    }

    private static readonly string[] VerificationEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "activityId",
        "expectedOperationId",
        "result",
        "evidenceDigest",
        "providerExecutionId"
    ];

    private sealed record ProviderResponseEnvelope(
        int StatusCode,
        string? ContentType,
        string Body,
        bool Truncated);
}
