using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaWorkflowInstance(
    string Id,
    string DefinitionId,
    string DefinitionVersion,
    string CurrentState,
    long Revision,
    JsonElement Facts);

public sealed record AsteriaExpectedOperation(
    string OperationId,
    string InstanceId,
    string ObservationType,
    string SourceIdentity,
    long ExpectedRevision,
    IReadOnlyList<string> RequiredEvidence,
    string Status,
    string? ResultDigest);

public sealed record AsteriaConcernState(string ConcernId, string CurrentState, long Revision);

public sealed record AsteriaAcceptedObservation(
    string ReceiptId,
    string AttemptId,
    string HistoryId,
    string AcceptedEvent,
    string ResultingState);

public sealed class AsteriaStateMachineClient
{
    public const string DecisionSourceIdentity = AsteriaDecisionApiClient.DecisionSourceIdentity;
    public const string AssessmentObservation = "AsteriaAssessmentObserved";
    public const string AssessmentStartObservation = "AsteriaAssessmentStarted";
    public const string AssessmentStartSourceIdentity = "AsteriaAssessmentCoordinator";
    public const string AmendmentSourceIdentity = "AsteriaAmendmentCoordinator";
    public const string SupersessionObservation = "SupersedeAssessment";

    private static readonly string[] RequiredEvidence =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "parentRevision",
        "expectedOperationId",
        "decisionRequestFingerprint",
        "selectedPolicySourceVersions",
        "originalResponse",
        "policySelectionContext"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public AsteriaStateMachineClient(HttpClient httpClient, string trustedIngestionKey)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedIngestionKey);
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-State-Machine-Trusted-Ingestion-Key", trustedIngestionKey);
    }

    public async Task EnsureDefinitionAsync(string definitionJson, CancellationToken cancellationToken = default)
    {
        using var definition = JsonDocument.Parse(definitionJson);
        if (definition.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Asteria machine definition must be a JSON object.");
        }
        var definitionId = definition.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidDataException("Asteria machine definition has no ID.");
        var version = definition.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidDataException("Asteria machine definition has no version.");

        using var response = await _httpClient.PostAsync(
            "api/definitions",
            new StringContent(definitionJson, Encoding.UTF8, "application/json"),
            cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using var registration = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var actualId = registration.RootElement.GetProperty("definition").GetProperty("id").GetString();
            var actualVersion = registration.RootElement.GetProperty("definition").GetProperty("version").GetString();
            if (actualId != definitionId || actualVersion != version)
            {
                throw new InvalidDataException("State Machine registered a different definition identity.");
            }
            return;
        }

        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            throw await CreateHttpExceptionAsync("registering Asteria definition", response, cancellationToken);
        }

        using var existing = await GetJsonAsync(
            $"api/definitions/{Uri.EscapeDataString(definitionId)}/{Uri.EscapeDataString(version)}",
            cancellationToken);
        var expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson)));
        if (existing.RootElement.GetProperty("contentDigest").GetString() != expectedDigest)
        {
            throw new InvalidOperationException(
                $"Immutable Asteria definition '{definitionId}@{version}' is already registered with different content.");
        }
    }

    public async Task<AsteriaWorkflowInstance> EnsureInstanceAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken);
        if (instance is null)
        {
            var creationFacts = CreateInstanceFacts(release);
            var request = new
            {
                id = release.WorkflowInstanceId,
                definitionId = release.DefinitionId,
                definitionVersion = release.DefinitionVersion,
                facts = creationFacts
            };
            using var response = await _httpClient.PostAsJsonAsync("api/instances", request, JsonOptions, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken);
            }
            else if (response.IsSuccessStatusCode)
            {
                instance = await ReadInstanceAsync(response, cancellationToken);
            }
            else
            {
                throw await CreateHttpExceptionAsync("creating Asteria workflow instance", response, cancellationToken);
            }
        }

        if (instance is null)
        {
            throw new InvalidOperationException("The stable Asteria workflow instance could not be inspected after creation.");
        }
        ValidateInstance(release, instance);
        return instance;
    }

    public async Task<AsteriaWorkflowInstance?> GetInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        EnsureSuccess(response, "reading Asteria workflow instance");
        return await ReadInstanceAsync(response, cancellationToken);
    }

    public async Task<AsteriaWorkflowInstance> InspectGenerationAsync(
        AsteriaReleaseSnapshot generation,
        CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(generation.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Asteria State Machine instance '{generation.WorkflowInstanceId}' does not exist.");
        ValidateInstance(generation, instance);
        return instance;
    }

    public async Task EnsureSupersededAsync(
        AsteriaReleaseSnapshot oldGeneration,
        AsteriaReleaseSnapshot replacementGeneration,
        AsteriaAmendmentSnapshot amendment,
        IReadOnlyList<AsteriaSupersessionAttempt> attempts,
        CancellationToken cancellationToken = default)
    {
        if (oldGeneration.DefinitionVersion is not ("3.0" or "4.0" or "5.0") ||
            replacementGeneration.DefinitionVersion != oldGeneration.DefinitionVersion ||
            oldGeneration.AssessmentGeneration != amendment.ExpectedBaseGeneration ||
            replacementGeneration.AssessmentGeneration != amendment.NewGeneration ||
            amendment.OldWorkflowInstanceId != oldGeneration.WorkflowInstanceId ||
            amendment.NewWorkflowInstanceId != replacementGeneration.WorkflowInstanceId)
        {
            throw new InvalidDataException("Asteria amendment generation identities do not match the durable reservation.");
        }

        var instance = await InspectGenerationAsync(oldGeneration, cancellationToken);
        string[] requiredEvidence =
        [
            "amendmentId",
            "requestFingerprint",
            "expectedBaseGeneration",
            "replacementGeneration",
            "replacementMaterialDigest"
        ];

        if (instance.CurrentState == "Superseded")
        {
            await VerifySupersessionAcrossAttemptsAsync(
                oldGeneration.WorkflowInstanceId, oldGeneration.DefinitionVersion, amendment, attempts, cancellationToken);
            return;
        }

        if (instance.CurrentState is not ("Draft" or "UnderAssessment" or "RequirementsIdentified" or
            "ReviewObligationsIdentified" or "AssessmentUnresolved" or "ReadyForDeployment" or
            "DeploymentInProgress" or "DeploymentFailed" or "DeploymentUnknown" or
            "PostDeploymentVerification" or "HealthFailed" or "HealthUnresolved"))
        {
            throw new InvalidOperationException(
                $"Asteria generation in state '{instance.CurrentState}' cannot be superseded.");
        }

        var supersessionAttempt = attempts.SingleOrDefault(item => item.ExpectedRevision == instance.Revision)
            ?? throw new InvalidOperationException(
                $"No durable supersession attempt exists for observed revision {instance.Revision}.");
        if (supersessionAttempt.ReleaseId != oldGeneration.ReleaseId ||
            supersessionAttempt.AmendmentId != amendment.AmendmentId)
        {
            throw new InvalidDataException("Asteria supersession attempt identity differs from its amendment.");
        }
        var operationId = supersessionAttempt.OperationId;
        var messageId = supersessionAttempt.MessageId;

        var expectedOperations = await ListExpectedOperationsAsync(oldGeneration.WorkflowInstanceId, cancellationToken);
        var operation = expectedOperations.SingleOrDefault(item => item.OperationId == operationId);
        if (operation is null)
        {
            var request = new
            {
                operationId,
                observationType = SupersessionObservation,
                sourceIdentity = AmendmentSourceIdentity,
                expectedRevision = instance.Revision,
                requiredEvidence
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/expected-operations",
                request, JsonOptions, cancellationToken);
            EnsureSuccess(response, "registering Asteria supersession operation");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            operation = ReadExpectedOperation(result.RootElement);
        }
        ValidateSupersessionOperation(
            operation, oldGeneration.WorkflowInstanceId, operationId, instance.Revision, requiredEvidence);

        var receipt = await FindInboundReceiptAsync(
            oldGeneration.WorkflowInstanceId, messageId, cancellationToken);
        if (receipt is null)
        {
            var evidence = new
            {
                amendmentId = amendment.AmendmentId,
                requestFingerprint = amendment.RequestFingerprint,
                expectedBaseGeneration = amendment.ExpectedBaseGeneration,
                replacementGeneration = amendment.NewGeneration,
                replacementMaterialDigest = replacementGeneration.MaterialDigest
            };
            var observation = new
            {
                messageId,
                deliveryId = messageId,
                operationId,
                observationId = StableMessageId(messageId, "supersession-result"),
                type = SupersessionObservation,
                expectedRevision = instance.Revision,
                correlationId = operationId,
                source = AmendmentSourceIdentity,
                payload = evidence,
                facts = new
                {
                    amendmentId = amendment.AmendmentId,
                    expectedBaseGeneration = amendment.ExpectedBaseGeneration,
                    replacementGeneration = amendment.NewGeneration
                },
                evidence
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/observations",
                observation, JsonOptions, cancellationToken);
            EnsureSuccess(response, "submitting Asteria supersession observation");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            EnsureAcceptedReceipt(result.RootElement);
        }
        else
        {
            EnsureAcceptedReceipt(receipt.Value);
        }

        var superseded = await InspectGenerationAsync(oldGeneration, cancellationToken);
        if (superseded.CurrentState != "Superseded")
        {
            throw new InvalidDataException(
                $"Accepted Asteria supersession did not reach terminal state 'Superseded'; found '{superseded.CurrentState}'.");
        }
        await VerifySupersessionAcrossAttemptsAsync(
            oldGeneration.WorkflowInstanceId, oldGeneration.DefinitionVersion, amendment, attempts, cancellationToken);
    }

    public async Task<AsteriaWorkflowInstance> EnsureSubmittedAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken = default)
    {
        if (release.DefinitionVersion is "3.0" or "4.0" or "5.0")
        {
            return await EnsureInputPolicySubmittedAsync(release, cancellationToken);
        }

        var messageId = StableMessageId(release.Work.WorkId, "submit");
        var current = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance disappeared before submission.");

        if (current.CurrentState == "Draft")
        {
            var receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken);
            if (receipt is null)
            {
                var request = new
                {
                    messageId,
                    type = "SubmitRelease",
                    expectedRevision = current.Revision
                };
                using var response = await _httpClient.PostAsJsonAsync(
                    $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/commands",
                    request, JsonOptions, cancellationToken);
                EnsureSuccess(response, "submitting Asteria release");
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                EnsureAcceptedReceipt(result.RootElement);
            }
            else
            {
                EnsureAcceptedReceipt(receipt.Value);
            }
            current = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
                ?? throw new InvalidOperationException("Asteria workflow instance disappeared after submission.");
        }
        else
        {
            var receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken);
            if (receipt is null)
            {
                throw new InvalidOperationException(
                    "Asteria workflow is no longer in Draft but has no durable receipt for its stable submission identity.");
            }
            EnsureAcceptedReceipt(receipt.Value);
        }

        if (current.CurrentState != "UnderAssessment")
        {
            throw new InvalidOperationException(
                $"Expected submitted Asteria workflow state UnderAssessment, found '{current.CurrentState}'.");
        }
        return current;
    }

    private async Task<AsteriaWorkflowInstance> EnsureInputPolicySubmittedAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken)
    {
        var current = await InspectGenerationAsync(release, cancellationToken);
        var operationId = StableMessageId(release.Work.WorkId, "assessment-start-operation");
        var messageId = StableMessageId(release.Work.WorkId, "submit");
        string[] requiredEvidence = ["releaseId", "assessmentGeneration", "materialDigest"];
        var operations = await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == operationId);
        if (operation is null)
        {
            var request = new
            {
                operationId,
                observationType = AssessmentStartObservation,
                sourceIdentity = AssessmentStartSourceIdentity,
                expectedRevision = current.Revision,
                requiredEvidence
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/expected-operations",
                request, JsonOptions, cancellationToken);
            EnsureSuccess(response, "registering Asteria assessment-start operation");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            operation = ReadExpectedOperation(result.RootElement);
        }
        ValidateAssessmentStartOperation(
            operation, release, operation.ExpectedRevision, operationId, requiredEvidence);
        if (current.CurrentState == "Draft" && operation.ExpectedRevision != current.Revision)
        {
            throw new InvalidOperationException("Asteria assessment-start operation was registered against a stale revision.");
        }

        var receipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken);
        if (current.CurrentState == "Draft" && receipt is null)
        {
            var evidence = new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest
            };
            var observation = new
            {
                messageId,
                deliveryId = messageId,
                operationId,
                observationId = StableMessageId(release.Work.WorkId, "assessment-start-result"),
                type = AssessmentStartObservation,
                expectedRevision = current.Revision,
                correlationId = operationId,
                source = AssessmentStartSourceIdentity,
                payload = evidence,
                facts = new { },
                evidence
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
                observation, JsonOptions, cancellationToken);
            EnsureSuccess(response, "submitting Asteria assessment start");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            EnsureAcceptedReceipt(result.RootElement);
        }
        else if (receipt is null)
        {
            throw new InvalidOperationException(
                "Asteria workflow left Draft without a receipt for its stable trusted assessment-start identity.");
        }
        else
        {
            EnsureAcceptedReceipt(receipt.Value);
        }

        current = await InspectGenerationAsync(release, cancellationToken);
        if (current.CurrentState != "UnderAssessment")
        {
            throw new InvalidOperationException(
                $"Expected Asteria assessment state UnderAssessment, found '{current.CurrentState}'.");
        }
        var resolved = (await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken))
            .Single(item => item.OperationId == operationId);
        if (resolved.Status != "Resolved" || string.IsNullOrWhiteSpace(resolved.ResultDigest))
        {
            throw new InvalidDataException("Asteria assessment-start observation did not resolve its expected operation.");
        }
        return current;
    }

    public async Task<AsteriaExpectedOperation> EnsureExpectedOperationAsync(
        AsteriaReleaseSnapshot release,
        long parentRevision,
        CancellationToken cancellationToken = default)
    {
        var operations = await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken);
        var existing = operations.SingleOrDefault(item => item.OperationId == release.Work.ExpectedOperationId);
        if (existing is null)
        {
            var request = new
            {
                operationId = release.Work.ExpectedOperationId,
                observationType = AssessmentObservation,
                sourceIdentity = DecisionSourceIdentity,
                expectedRevision = parentRevision,
                requiredEvidence = RequiredEvidence
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/expected-operations",
                request, JsonOptions, cancellationToken);
            EnsureSuccess(response, "registering Asteria expected operation");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            existing = ReadExpectedOperation(result.RootElement);
        }

        ValidateExpectedOperation(existing, release, parentRevision);
        return existing;
    }

    public async Task<IReadOnlyList<AsteriaExpectedOperation>> ListExpectedOperationsAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/expected-operations", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("State Machine expected-operation listing must be an array.");
        }
        return document.RootElement.EnumerateArray().Select(ReadExpectedOperation).ToArray();
    }

    public async Task<IReadOnlyList<JsonElement>> ListTransitionHistoryAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        await ReadJsonArrayAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/history",
            "State Machine transition history",
            cancellationToken);

    public async Task<IReadOnlyList<JsonElement>> ListConcernHistoryAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        await ReadJsonArrayAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/concerns/history",
            "State Machine concern history",
            cancellationToken);

    public async Task<JsonElement> GetHistoricalReplayAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using var replay = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/replay", cancellationToken);
        return replay.RootElement.Clone();
    }

    public async Task<IReadOnlyList<JsonElement>> ListInboundMessagesAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        await ReadJsonArrayAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages",
            "State Machine inbound-message receipts",
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, AsteriaConcernState>> GetConcernStatesAsync(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/concerns", cancellationToken);
        var parentRevision = GetRequired(document.RootElement, "parentRevision").GetInt64();
        var concerns = GetRequired(document.RootElement, "concerns");
        if (concerns.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("State Machine concern inspection must contain an array.");
        }

        var results = new Dictionary<string, AsteriaConcernState>(StringComparer.Ordinal);
        foreach (var concern in concerns.EnumerateArray())
        {
            var id = GetString(concern, "concernId");
            var snapshot = new AsteriaConcernState(
                id,
                GetString(concern, "currentState"),
                GetRequired(concern, "revision").GetInt64());
            if (!results.TryAdd(id, snapshot))
            {
                throw new InvalidDataException($"State Machine returned duplicate concern '{id}'.");
            }
        }
        if (results.Count == 0 || parentRevision < 0)
        {
            throw new InvalidDataException("State Machine concern inspection is incomplete.");
        }
        return results;
    }

    public async Task RequestConcernWorkAsync(
        AsteriaReleaseSnapshot release,
        string concernId,
        string commandType,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(concernId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandType);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        var concerns = await GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue(concernId, out var concern))
        {
            throw new InvalidDataException($"Asteria workflow has no concern '{concernId}'.");
        }
        if (concern.CurrentState == "Requested")
        {
            return;
        }
        if (concern.CurrentState != "Required")
        {
            throw new InvalidOperationException(
                $"Asteria concern '{concernId}' cannot be requested from '{concern.CurrentState}'.");
        }
        var command = new
        {
            messageId,
            type = commandType,
            expectedRevision = instance.Revision
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/commands",
            command,
            JsonOptions,
            cancellationToken);
        EnsureSuccess(response, $"requesting Asteria concern '{concernId}'");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(result.RootElement);
        var after = await GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!after.TryGetValue(concernId, out var requested) || requested.CurrentState != "Requested")
        {
            throw new InvalidDataException($"Asteria concern '{concernId}' did not enter Requested state.");
        }
    }

    public async Task<AsteriaWorkflowInstance> CancelBeforeDispatchAsync(
        AsteriaReleaseSnapshot release,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        ValidateInstance(release, instance);
        var receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken);
        if (instance.CurrentState != "Cancelled")
        {
            if (receipt is not null)
            {
                throw new InvalidDataException("Asteria cancellation receipt exists without a terminal Cancelled state.");
            }
            var command = new
            {
                messageId,
                type = "CancelRelease",
                expectedRevision = instance.Revision
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/commands",
                command,
                JsonOptions,
                cancellationToken);
            EnsureSuccess(response, "cancelling Asteria release before external dispatch");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            EnsureAcceptedReceipt(result.RootElement);
            receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken)
                ?? throw new InvalidDataException("Accepted pre-dispatch cancellation has no durable receipt.");
        }
        if (receipt is null)
        {
            throw new InvalidDataException("Asteria release is Cancelled without the stable pre-dispatch cancellation receipt.");
        }
        EnsureAcceptedReceipt(receipt.Value);
        var cancelled = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance disappeared after cancellation.");
        if (cancelled.CurrentState != "Cancelled")
        {
            throw new InvalidDataException(
                $"Asteria cancellation receipt did not establish Cancelled; found '{cancelled.CurrentState}'.");
        }
        return cancelled;
    }

    public async Task<AsteriaWorkflowInstance> RequestCancellationIntentAsync(
        AsteriaReleaseSnapshot release,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        ValidateInstance(release, instance);
        var concerns = await GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue("CancellationIntent", out var cancellation))
        {
            throw new InvalidDataException("Asteria workflow has no cancellation-intent concern.");
        }
        var receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken);
        if (cancellation.CurrentState == "NotRequested")
        {
            if (receipt is not null)
            {
                throw new InvalidDataException("Asteria cancellation-intent receipt exists while the concern is NotRequested.");
            }
            var command = new
            {
                messageId,
                type = "RequestCancellation",
                expectedRevision = instance.Revision
            };
            using var response = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/commands",
                command,
                JsonOptions,
                cancellationToken);
            EnsureSuccess(response, "recording post-dispatch Asteria cancellation intent");
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            EnsureAcceptedReceipt(result.RootElement);
            receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken)
                ?? throw new InvalidDataException("Accepted Asteria cancellation intent has no durable receipt.");
        }
        var afterConcerns = await GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!afterConcerns.TryGetValue("CancellationIntent", out var afterCancellation) ||
            afterCancellation.CurrentState != "Requested" ||
            receipt is null)
        {
            throw new InvalidDataException(
                $"Asteria cancellation intent is '{afterCancellation?.CurrentState ?? "missing"}' without its stable accepted receipt.");
        }
        EnsureAcceptedReceipt(receipt.Value);
        return await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance disappeared after cancellation intent.");
    }

    public async Task<AsteriaAcceptedObservation> SubmitConcernObservationAsync(
        AsteriaReleaseSnapshot release,
        string concernId,
        string operationId,
        string messageId,
        string observationId,
        string observationType,
        string sourceIdentity,
        IReadOnlyList<string> requiredEvidence,
        IReadOnlyDictionary<string, object?> evidence,
        string resultFactName,
        string result,
        string expectedConcernState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(concernId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultFactName);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConcernState);
        if (requiredEvidence.Count == 0 ||
            requiredEvidence.Distinct(StringComparer.Ordinal).Count() != requiredEvidence.Count ||
            requiredEvidence.Any(name => !evidence.ContainsKey(name)))
        {
            throw new InvalidDataException("Concern observation evidence does not satisfy its exact required-evidence set.");
        }

        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        var concerns = await GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue(concernId, out var concern))
        {
            throw new InvalidDataException($"Asteria workflow has no concern '{concernId}'.");
        }
        if (concern.CurrentState == expectedConcernState)
        {
            var alreadyAccepted = await FindInboundReceiptAsync(
                release.WorkflowInstanceId, messageId, cancellationToken)
                ?? throw new InvalidDataException(
                    $"Asteria concern '{concernId}' is in '{expectedConcernState}' without its stable accepted receipt.");
            return await ReadAcceptedConcernObservationAsync(
                release.WorkflowInstanceId,
                alreadyAccepted,
                operationId,
                observationType,
                concernId,
                expectedConcernState,
                cancellationToken);
        }

        var operation = await EnsureConcernExpectedOperationAsync(
            release.WorkflowInstanceId,
            operationId,
            observationType,
            sourceIdentity,
            instance.Revision,
            requiredEvidence,
            cancellationToken);
        var receipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken);
        if (operation.Status == "Resolved")
        {
            if (receipt is null)
            {
                throw new InvalidDataException(
                    $"Resolved Asteria concern operation '{operationId}' has no stable receipt.");
            }
            return await ReadAcceptedConcernObservationAsync(
                release.WorkflowInstanceId,
                receipt.Value,
                operationId,
                observationType,
                concernId,
                expectedConcernState,
                cancellationToken);
        }
        if (receipt is not null)
        {
            throw new InvalidDataException(
                $"Unresolved Asteria concern operation '{operationId}' already has a durable receipt.");
        }
        var expectedPriorConcernState = observationType switch
        {
            "AsteriaDeploymentResult" => "Running",
            "AsteriaHealthVerificationResult" => "Requested",
            _ => "Requested"
        };
        if (concern.CurrentState != expectedPriorConcernState ||
            operation.ExpectedRevision != instance.Revision)
        {
            throw new InvalidOperationException(
                $"Asteria concern '{concernId}' is not requested or its parent-bound operation is stale.");
        }

        var observation = new
        {
            messageId,
            deliveryId = messageId,
            operationId,
            observationId,
            type = observationType,
            expectedRevision = operation.ExpectedRevision,
            correlationId = operationId,
            source = sourceIdentity,
            payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["activityId"] = evidence.GetValueOrDefault("activityId"),
                ["expectedOperationId"] = operationId,
                ["result"] = result
            },
            facts = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [resultFactName] = result
            },
            evidence
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            observation,
            JsonOptions,
            cancellationToken);
        EnsureSuccess(response, $"submitting Asteria concern result '{concernId}'");
        using var resultDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(resultDocument.RootElement);

        var acceptedReceipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken)
            ?? throw new InvalidDataException("Accepted Asteria concern observation has no durable inbound receipt.");
        return await ReadAcceptedConcernObservationAsync(
            release.WorkflowInstanceId,
            acceptedReceipt,
            operationId,
            observationType,
            concernId,
            expectedConcernState,
            cancellationToken);
    }

    public async Task<AsteriaExpectedOperation> EnsureExpectedConcernOperationAsync(
        AsteriaReleaseSnapshot release,
        string operationId,
        string observationType,
        string sourceIdentity,
        IReadOnlyList<string> requiredEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        return await EnsureConcernExpectedOperationAsync(
            release.WorkflowInstanceId,
            operationId,
            observationType,
            sourceIdentity,
            instance.Revision,
            requiredEvidence,
            cancellationToken);
    }

    public async Task<AsteriaExpectedOperation> EnsureExpectedParentOperationAsync(
        AsteriaReleaseSnapshot release,
        string operationId,
        string observationType,
        string sourceIdentity,
        long expectedRevision,
        IReadOnlyList<string> requiredEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        return await EnsureConcernExpectedOperationAsync(
            release.WorkflowInstanceId,
            operationId,
            observationType,
            sourceIdentity,
            expectedRevision,
            requiredEvidence,
            cancellationToken);
    }

    public async Task<AsteriaAcceptedObservation> SubmitReadinessObservationAsync(
        AsteriaReleaseSnapshot release,
        string operationId,
        long expectedRevision,
        string messageId,
        string observationId,
        IReadOnlyDictionary<string, object?> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(evidence);
        string[] requiredEvidence =
        [
            "releaseId",
            "assessmentGeneration",
            "materialDigest",
            "assessmentEvidenceDigest",
            "allObligationsEvidenceDigest",
            "readinessWorkId"
        ];
        if (requiredEvidence.Any(name => !evidence.ContainsKey(name)) ||
            requiredEvidence.Any(name => evidence[name] is null))
        {
            throw new InvalidDataException("Asteria readiness evidence is incomplete.");
        }

        const string observationType = "AsteriaReadinessConfirmed";
        const string sourceIdentity = "AsteriaReadinessCoordinator";
        const string expectedEvent = "ReadinessConfirmed";
        const string expectedState = "ReadyForDeployment";
        var operation = await EnsureExpectedParentOperationAsync(
            release,
            operationId,
            observationType,
            sourceIdentity,
            expectedRevision,
            requiredEvidence,
            cancellationToken);
        var receipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken);
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        if (operation.Status == "Resolved")
        {
            if (receipt is null)
            {
                throw new InvalidDataException("Resolved Asteria readiness operation has no stable accepted receipt.");
            }
            return await ReadAcceptedParentObservationAsync(
                release.WorkflowInstanceId,
                receipt.Value,
                operation,
                expectedEvent,
                expectedState,
                cancellationToken);
        }
        if (receipt is not null)
        {
            throw new InvalidDataException("Asteria readiness receipt exists while its expected operation is unresolved.");
        }
        if (instance.Revision != expectedRevision ||
            instance.CurrentState is not ("RequirementsIdentified" or "ReviewObligationsIdentified"))
        {
            throw new InvalidOperationException("Asteria readiness operation is stale or the workflow is not assessment-ready.");
        }

        var observation = new
        {
            messageId,
            deliveryId = messageId,
            operationId,
            observationId,
            type = observationType,
            expectedRevision,
            correlationId = operationId,
            source = sourceIdentity,
            payload = new { },
            facts = new { },
            evidence
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            observation,
            JsonOptions,
            cancellationToken);
        EnsureSuccess(response, "submitting Asteria readiness observation");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(result.RootElement);
        var acceptedReceipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken)
            ?? throw new InvalidDataException("Accepted Asteria readiness observation has no durable receipt.");
        var resolvedOperation = (await ListExpectedOperationsAsync(
            release.WorkflowInstanceId, cancellationToken))
            .Single(item => item.OperationId == operationId);
        if (resolvedOperation.Status != "Resolved" || string.IsNullOrWhiteSpace(resolvedOperation.ResultDigest))
        {
            throw new InvalidDataException("Accepted Asteria readiness observation did not resolve its expected operation.");
        }
        return await ReadAcceptedParentObservationAsync(
            release.WorkflowInstanceId,
            acceptedReceipt,
            resolvedOperation,
            expectedEvent,
            expectedState,
            cancellationToken);
    }

    public async Task<AsteriaAcceptedObservation> SubmitLifecycleStartConcernObservationAsync(
        AsteriaReleaseSnapshot release,
        string concernId,
        string operationId,
        string messageId,
        string observationId,
        string observationType,
        string sourceIdentity,
        long expectedRevision,
        IReadOnlyList<string> requiredEvidence,
        IReadOnlyDictionary<string, object?> evidence,
        IReadOnlyDictionary<string, object?> payload,
        string expectedParentStateBefore,
        string expectedParentStateAfter,
        string expectedConcernState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(payload);
        if (requiredEvidence.Count == 0 ||
            requiredEvidence.Distinct(StringComparer.Ordinal).Count() != requiredEvidence.Count ||
            requiredEvidence.Any(name => !evidence.ContainsKey(name)))
        {
            throw new InvalidDataException("Asteria lifecycle-start evidence does not satisfy its exact required-evidence set.");
        }

        var operation = await EnsureExpectedParentOperationAsync(
            release,
            operationId,
            observationType,
            sourceIdentity,
            expectedRevision,
            requiredEvidence,
            cancellationToken);
        var receipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken);
        if (operation.Status == "Resolved")
        {
            if (receipt is null)
            {
                throw new InvalidDataException("Resolved Asteria lifecycle-start operation has no stable receipt.");
            }
            var accepted = await ReadAcceptedConcernObservationAsync(
                release.WorkflowInstanceId,
                receipt.Value,
                operationId,
                observationType,
                concernId,
                expectedConcernState,
                cancellationToken);
            var recoveredInstance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
                ?? throw new InvalidOperationException("Asteria workflow instance is missing.");
            if (recoveredInstance.CurrentState != expectedParentStateAfter)
            {
                throw new InvalidDataException("Accepted Asteria lifecycle-start receipt has an unexpected parent state.");
            }
            return accepted;
        }
        if (receipt is not null)
        {
            throw new InvalidDataException("Asteria lifecycle-start receipt exists for an unresolved expected operation.");
        }
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance is missing.");
        if (operation.ExpectedRevision != expectedRevision ||
            instance.Revision != expectedRevision ||
            instance.CurrentState != expectedParentStateBefore)
        {
            throw new InvalidOperationException("Asteria lifecycle-start operation is stale against its parent state.");
        }

        var observation = new
        {
            messageId,
            deliveryId = messageId,
            operationId,
            observationId,
            type = observationType,
            expectedRevision,
            correlationId = operationId,
            source = sourceIdentity,
            payload,
            facts = new { },
            evidence
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            observation,
            JsonOptions,
            cancellationToken);
        EnsureSuccess(response, $"submitting Asteria lifecycle-start observation '{observationType}'");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(result.RootElement);
        var acceptedReceipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken)
            ?? throw new InvalidDataException("Accepted Asteria lifecycle-start observation has no durable receipt.");
        var acceptedObservation = await ReadAcceptedConcernObservationAsync(
            release.WorkflowInstanceId,
            acceptedReceipt,
            operationId,
            observationType,
            concernId,
            expectedConcernState,
            cancellationToken);
        var finalInstance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance disappeared after lifecycle start.");
        if (finalInstance.CurrentState != expectedParentStateAfter)
        {
            throw new InvalidDataException(
                $"Accepted Asteria lifecycle-start observation established '{finalInstance.CurrentState}', not '{expectedParentStateAfter}'.");
        }
        return acceptedObservation;
    }

    public async Task<AsteriaAcceptedObservation> SubmitParentLifecycleObservationAsync(
        AsteriaReleaseSnapshot release,
        string operationId,
        string messageId,
        string observationId,
        string observationType,
        string sourceIdentity,
        long expectedRevision,
        IReadOnlyList<string> requiredEvidence,
        IReadOnlyDictionary<string, object?> evidence,
        IReadOnlyDictionary<string, object?> payload,
        IReadOnlyDictionary<string, object?> facts,
        string expectedEvent,
        string expectedParentStateBefore,
        string expectedParentStateAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(facts);
        var operation = await EnsureExpectedParentOperationAsync(
            release,
            operationId,
            observationType,
            sourceIdentity,
            expectedRevision,
            requiredEvidence,
            cancellationToken);
        var receipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken);
        if (operation.Status == "Resolved")
        {
            if (receipt is null)
            {
                throw new InvalidDataException("Resolved Asteria parent lifecycle operation has no stable receipt.");
            }
            return await ReadAcceptedParentObservationAsync(
                release.WorkflowInstanceId,
                receipt.Value,
                operation,
                expectedEvent,
                expectedParentStateAfter,
                cancellationToken);
        }
        if (receipt is not null)
        {
            throw new InvalidDataException("Asteria parent lifecycle receipt exists for an unresolved expected operation.");
        }
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance is missing.");
        if (instance.Revision != expectedRevision ||
            instance.CurrentState != expectedParentStateBefore ||
            operation.ExpectedRevision != expectedRevision)
        {
            throw new InvalidOperationException("Asteria parent lifecycle operation is stale against its expected state.");
        }
        var observation = new
        {
            messageId,
            deliveryId = messageId,
            operationId,
            observationId,
            type = observationType,
            expectedRevision,
            correlationId = operationId,
            source = sourceIdentity,
            payload,
            facts,
            evidence
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            observation,
            JsonOptions,
            cancellationToken);
        EnsureSuccess(response, $"submitting Asteria parent lifecycle observation '{observationType}'");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(result.RootElement);
        var acceptedReceipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken)
            ?? throw new InvalidDataException("Accepted Asteria parent lifecycle observation has no durable receipt.");
        var operationAfter = (await ListExpectedOperationsAsync(
            release.WorkflowInstanceId, cancellationToken))
            .Single(item => item.OperationId == operationId);
        return await ReadAcceptedParentObservationAsync(
            release.WorkflowInstanceId,
            acceptedReceipt,
            operationAfter,
            expectedEvent,
            expectedParentStateAfter,
            cancellationToken);
    }

    public async Task<bool> TryAcceptObservationAsync(
        AsteriaReleaseSnapshot release,
        AsteriaValidatedAssessment assessment,
        CancellationToken cancellationToken = default)
    {
        var parentRevision = release.Work.ParentRevision
            ?? throw new InvalidOperationException("Expected operation cannot be observed before its parent revision is stored.");
        var operation = await EnsureExpectedOperationAsync(release, parentRevision, cancellationToken);
        var messageId = StableMessageId(release.Work.WorkId, "assessment-observation");
        var instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");

        var receipt = await FindInboundReceiptAsync(release.WorkflowInstanceId, messageId, cancellationToken);
        if (operation.Status == "Resolved")
        {
            if (receipt is null)
            {
                throw new InvalidDataException(
                    "Expected operation is resolved but its stable assessment-observation receipt is missing.");
            }
            EnsureAcceptedReceipt(receipt.Value);
            EnsureOutcomeState(instance.CurrentState, assessment.Outcome);
            return true;
        }

        if (receipt is not null)
        {
            EnsureAcceptedReceipt(receipt.Value);
            var refreshed = await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken);
            operation = refreshed.Single(item => item.OperationId == release.Work.ExpectedOperationId);
            if (operation.Status != "Resolved")
            {
                throw new InvalidDataException("Accepted Asteria observation did not resolve its expected operation.");
            }
            instance = await GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
                ?? throw new InvalidOperationException("Asteria workflow instance disappeared after observation.");
            EnsureOutcomeState(instance.CurrentState, assessment.Outcome);
            return true;
        }

        var decisionEvidence = CreateDecisionEvidence(assessment);
        var observation = new
        {
            messageId,
            deliveryId = messageId,
            operationId = release.Work.ExpectedOperationId,
            observationId = StableMessageId(release.Work.WorkId, "assessment-result"),
            type = AssessmentObservation,
            expectedRevision = parentRevision,
            correlationId = release.Work.ExpectedOperationId,
            source = DecisionSourceIdentity,
            payload = new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest,
                expectedOperationId = release.Work.ExpectedOperationId,
                outcome = assessment.Outcome,
                evaluationId = assessment.EvaluationId
            },
            facts = new { assessmentOutcome = assessment.Outcome },
            evidence = CreateObservationEvidence(release, assessment, parentRevision),
            decisionEvidence
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            observation, JsonOptions, cancellationToken);
        EnsureSuccess(response, "submitting Asteria assessment observation");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(result.RootElement);
        var finalInstance = ReadInstanceFromResponse(result.RootElement.GetProperty("instance"));
        EnsureOutcomeState(finalInstance.CurrentState, assessment.Outcome);

        var resolvedOperation = (await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken))
            .Single(item => item.OperationId == release.Work.ExpectedOperationId);
        if (resolvedOperation.Status != "Resolved" || string.IsNullOrWhiteSpace(resolvedOperation.ResultDigest))
        {
            throw new InvalidDataException("Accepted Asteria assessment observation did not resolve with a result digest.");
        }
        return true;
    }

    public async Task ApplyAssessmentApplicabilitiesAsync(
        AsteriaReleaseSnapshot release,
        AsteriaValidatedAssessment assessment,
        Func<CancellationToken, Task> assertCurrentGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(assertCurrentGeneration);
        if (release.DefinitionVersion is not ("4.0" or "5.0"))
        {
            throw new InvalidOperationException("Explicit obligation applicability observations require Asteria definition V4.");
        }

        var obligations = AsteriaDecisionApiClient.ReadObligationApplicabilities(assessment);
        var assessmentDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(assessment.OriginalResponseJson)));
        var items = new[]
        {
            ("FunctionalVerification", obligations.FunctionalVerification),
            ("SecurityVerification", obligations.SecurityVerification),
            ("ReleaseReviewApproval", obligations.ReleaseReviewApproval),
            ("EmergencyApproval", obligations.EmergencyApproval)
        };

        foreach (var (concern, applicability) in items)
        {
            await assertCurrentGeneration(cancellationToken);
            await ApplyOneApplicabilityAsync(
                release,
                concern,
                applicability,
                assessment,
                assessmentDigest,
                cancellationToken);
        }
    }

    private async Task ApplyOneApplicabilityAsync(
        AsteriaReleaseSnapshot release,
        string concern,
        string applicability,
        AsteriaValidatedAssessment assessment,
        string assessmentDigest,
        CancellationToken cancellationToken)
    {
        var mapping = $"Asteria{concern}Applicability";
        var action = $"applicability-{concern}";
        var operationId = StableMessageId(release.Work.WorkId, action + "-operation");
        var messageId = StableMessageId(release.Work.WorkId, action + "-message");
        var observationId = StableMessageId(release.Work.WorkId, action + "-observation");
        string[] requiredEvidence =
        [
            "releaseId",
            "assessmentGeneration",
            "materialDigest",
            "assessmentEvidenceDigest",
            "applicability"
        ];

        var instance = await InspectGenerationAsync(release, cancellationToken);
        var operations = await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == operationId);
        if (operation is null)
        {
            if (IsTerminalParentState(instance.CurrentState))
            {
                throw new InvalidOperationException(
                    $"Cannot apply {concern} applicability after Asteria workflow reached terminal state '{instance.CurrentState}'.");
            }

            var request = new
            {
                operationId,
                observationType = mapping,
                sourceIdentity = AssessmentStartSourceIdentity,
                expectedRevision = instance.Revision,
                requiredEvidence
            };
            using var registration = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/expected-operations",
                request,
                JsonOptions,
                cancellationToken);
            EnsureSuccess(registration, $"registering {concern} applicability operation");
            using var registered = JsonDocument.Parse(
                await registration.Content.ReadAsStringAsync(cancellationToken));
            operation = ReadExpectedOperation(registered.RootElement);
        }

        if (operation.InstanceId != release.WorkflowInstanceId ||
            operation.OperationId != operationId ||
            operation.ObservationType != mapping ||
            operation.SourceIdentity != AssessmentStartSourceIdentity ||
            !operation.RequiredEvidence.Order(StringComparer.Ordinal)
                .SequenceEqual(requiredEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Persisted {concern} applicability operation has a conflicting binding.");
        }

        var receipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken);
        if (operation.Status == "Resolved")
        {
            if (receipt is null)
            {
                throw new InvalidDataException(
                    $"Resolved {concern} applicability operation has no stable receipt.");
            }
            EnsureConcernReceipt(receipt.Value, operationId, "AsteriaObligationApplicabilityObserved");
            await EnsureConcernStateAsync(release, concern, StateForApplicability(applicability), cancellationToken);
            return;
        }
        if (receipt is not null)
        {
            throw new InvalidDataException(
                $"Unresolved {concern} applicability operation already has a durable receipt and requires reconciliation.");
        }
        if (IsTerminalParentState(instance.CurrentState) || operation.ExpectedRevision != instance.Revision)
        {
            throw new InvalidOperationException(
                $"{concern} applicability operation is stale against the current Asteria parent revision.");
        }

        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["assessmentEvidenceDigest"] = assessmentDigest,
            ["applicability"] = applicability
        };
        if (assessment.EvaluationId is not null)
        {
            evidence["evaluationId"] = assessment.EvaluationId;
        }

        var observation = new
        {
            messageId,
            deliveryId = messageId,
            operationId,
            observationId,
            type = mapping,
            expectedRevision = operation.ExpectedRevision,
            correlationId = operationId,
            source = AssessmentStartSourceIdentity,
            payload = new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest,
                concern,
                applicability
            },
            facts = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["applicability"] = applicability
            },
            evidence
        };
        using var response = await _httpClient.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            observation,
            JsonOptions,
            cancellationToken);
        EnsureSuccess(response, $"submitting {concern} applicability");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        EnsureAcceptedReceipt(result.RootElement);

        var acceptedReceipt = await FindInboundReceiptAsync(
            release.WorkflowInstanceId, messageId, cancellationToken)
            ?? throw new InvalidDataException($"Accepted {concern} applicability has no durable receipt.");
        EnsureConcernReceipt(acceptedReceipt, operationId, "AsteriaObligationApplicabilityObserved");
        var resolved = (await ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken))
            .Single(item => item.OperationId == operationId);
        if (resolved.Status != "Resolved" || string.IsNullOrWhiteSpace(resolved.ResultDigest))
        {
            throw new InvalidDataException($"Accepted {concern} applicability did not resolve its expected operation.");
        }
        await EnsureConcernStateAsync(release, concern, StateForApplicability(applicability), cancellationToken);
    }

    private async Task EnsureConcernStateAsync(
        AsteriaReleaseSnapshot release,
        string concern,
        string expectedState,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/concerns",
            cancellationToken);
        var concerns = GetRequired(document.RootElement, "concerns");
        if (concerns.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("State Machine concern inspection must contain an array.");
        }
        var match = concerns.EnumerateArray()
            .SingleOrDefault(item => GetString(item, "concernId") == concern);
        if (match.ValueKind != JsonValueKind.Object ||
            GetString(match, "currentState") != expectedState)
        {
            throw new InvalidDataException(
                $"State Machine concern '{concern}' did not reach expected applicability state '{expectedState}'.");
        }
    }

    private async Task<AsteriaExpectedOperation> EnsureConcernExpectedOperationAsync(
        string instanceId,
        string operationId,
        string observationType,
        string sourceIdentity,
        long expectedRevision,
        IReadOnlyList<string> requiredEvidence,
        CancellationToken cancellationToken)
    {
        var operations = await ListExpectedOperationsAsync(instanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == operationId);
        if (operation is null)
        {
            var request = new
            {
                operationId,
                observationType,
                sourceIdentity,
                expectedRevision,
                requiredEvidence
            };
            using var registration = await _httpClient.PostAsJsonAsync(
                $"api/instances/{Uri.EscapeDataString(instanceId)}/expected-operations",
                request,
                JsonOptions,
                cancellationToken);
            EnsureSuccess(registration, "registering Asteria concern expected operation");
            using var registered = JsonDocument.Parse(
                await registration.Content.ReadAsStringAsync(cancellationToken));
            operation = ReadExpectedOperation(registered.RootElement);
        }

        if (operation.InstanceId != instanceId ||
            operation.OperationId != operationId ||
            operation.ObservationType != observationType ||
            operation.SourceIdentity != sourceIdentity ||
            operation.ExpectedRevision != expectedRevision ||
            !operation.RequiredEvidence.Order(StringComparer.Ordinal)
                .SequenceEqual(requiredEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Persisted Asteria concern operation '{operationId}' has a conflicting binding.");
        }
        return operation;
    }

    private async Task<AsteriaAcceptedObservation> ReadAcceptedConcernObservationAsync(
        string instanceId,
        JsonElement receipt,
        string operationId,
        string observationType,
        string concernId,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var expectedEvent = EventForObservationType(observationType);
        var attemptId = GetString(receipt, "attemptId");
        if (GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "operationId") != operationId ||
            GetString(receipt, "acceptedEvent") != expectedEvent)
        {
            throw new InvalidDataException("Asteria concern receipt does not match its stable observation operation.");
        }

        var operation = (await ListExpectedOperationsAsync(instanceId, cancellationToken))
            .SingleOrDefault(item => item.OperationId == operationId)
            ?? throw new InvalidDataException("Accepted Asteria concern observation has no expected operation.");
        if (operation.Status != "Resolved" || string.IsNullOrWhiteSpace(operation.ResultDigest))
        {
            throw new InvalidDataException("Accepted Asteria concern observation did not resolve its expected operation.");
        }

        using var document = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/concerns", cancellationToken);
        var entries = GetRequired(document.RootElement, "concerns");
        var states = entries.EnumerateArray()
            .Where(item => GetString(item, "concernId") == concernId)
            .ToArray();
        if (states.Length != 1 || GetString(states[0], "currentState") != expectedState)
        {
            throw new InvalidDataException(
                $"Accepted Asteria concern result did not establish '{concernId}' state '{expectedState}'.");
        }

        using var historyDocument = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/concerns/history", cancellationToken);
        if (historyDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Asteria concern history must be an array.");
        }
        var historyEntries = historyDocument.RootElement.EnumerateArray()
            .Where(item =>
                GetString(item, "concernId") == concernId &&
                GetString(item, "attemptId") == attemptId &&
                GetString(item, "event") == expectedEvent &&
                GetString(item, "newState") == expectedState)
            .ToArray();
        if (historyEntries.Length != 1)
        {
            throw new InvalidDataException(
                "Accepted Asteria concern receipt does not map to exactly one matching concern-history entry.");
        }

        return new AsteriaAcceptedObservation(
            GetString(receipt, "id"),
            attemptId,
            GetString(historyEntries[0], "id"),
            expectedEvent,
            expectedState);
    }

    private async Task<AsteriaAcceptedObservation> ReadAcceptedParentObservationAsync(
        string instanceId,
        JsonElement receipt,
        AsteriaExpectedOperation operation,
        string expectedEvent,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var attemptId = GetString(receipt, "attemptId");
        if (GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "operationId") != operation.OperationId ||
            GetString(receipt, "acceptedEvent") != expectedEvent ||
            operation.Status != "Resolved" ||
            string.IsNullOrWhiteSpace(operation.ResultDigest))
        {
            throw new InvalidDataException("Asteria parent observation receipt does not match its resolved operation.");
        }
        var instance = await GetInstanceAsync(instanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance does not exist.");
        if (instance.CurrentState != expectedState)
        {
            throw new InvalidDataException($"Accepted Asteria parent observation did not establish '{expectedState}'.");
        }
        var history = await ListTransitionHistoryAsync(instanceId, cancellationToken);
        var matches = history.Where(item =>
            GetString(item, "attemptId") == attemptId &&
            GetString(item, "event") == expectedEvent &&
            GetString(item, "newState") == expectedState).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("Accepted Asteria parent observation does not map to exactly one transition-history entry.");
        }
        return new AsteriaAcceptedObservation(
            GetString(receipt, "id"),
            attemptId,
            GetString(matches[0], "id"),
            expectedEvent,
            expectedState);
    }

    private static string EventForObservationType(string observationType) =>
        observationType switch
        {
            "AsteriaFunctionalVerificationResult" or "AsteriaSecurityVerificationResult" =>
                "AsteriaVerificationResultObserved",
            "AsteriaReleaseReviewApprovalResult" or "AsteriaEmergencyApprovalResult" =>
                "AsteriaApprovalResultObserved",
            "AsteriaDeploymentResult" => "AsteriaDeploymentResultObserved",
            "AsteriaHealthVerificationResult" => "AsteriaHealthVerificationResultObserved",
            "AsteriaDeploymentStarted" => "AsteriaDeploymentStarted",
            "AsteriaHealthVerificationStarted" => "AsteriaHealthVerificationStarted",
            _ => throw new InvalidDataException(
                $"Asteria observation mapping '{observationType}' has no concern-result event.")
        };

    private static void EnsureConcernReceipt(JsonElement receipt, string operationId, string expectedEvent)
    {
        if (GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "operationId") != operationId ||
            GetString(receipt, "acceptedEvent") != expectedEvent)
        {
            throw new InvalidDataException("State Machine applicability receipt does not match the stable concern operation.");
        }
    }

    private static string StateForApplicability(string applicability) =>
        applicability switch
        {
            "Required" => "Required",
            "NotRequired" => "NotRequired",
            "Unresolved" => "ApplicabilityUnresolved",
            _ => throw new InvalidDataException($"Unsupported Asteria obligation applicability '{applicability}'.")
        };

    private static bool IsTerminalParentState(string state) =>
        state is "Prohibited" or "Cancelled" or "Superseded";

    private async Task<JsonElement?> FindInboundReceiptAsync(
        string instanceId,
        string messageId,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("State Machine inbound-message listing must be an array.");
        }
        var matches = document.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("messageId", out var value) &&
                           value.ValueKind == JsonValueKind.String &&
                           value.GetString() == messageId)
            .Select(item => item.Clone())
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException("State Machine contains multiple receipts for one stable Asteria message identity.");
        }
        return matches.Length == 0 ? null : matches[0];
    }

    private async Task VerifySupersessionEvidenceAsync(
        string instanceId,
        string definitionVersion,
        string operationId,
        string messageId,
        IReadOnlyList<string> requiredEvidence,
        CancellationToken cancellationToken)
    {
        var receipt = await FindInboundReceiptAsync(instanceId, messageId, cancellationToken)
            ?? throw new InvalidDataException("Superseded Asteria instance has no durable supersession receipt.");
        if (GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "acceptedEvent") != SupersessionObservation ||
            GetString(receipt, "operationId") != operationId)
        {
            throw new InvalidDataException("Asteria supersession receipt does not match the reserved stable input identity.");
        }

        var operations = await ListExpectedOperationsAsync(instanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == operationId)
            ?? throw new InvalidDataException("Asteria supersession expected operation is missing.");
        if (operation.Status != "Resolved" ||
            operation.SourceIdentity != AmendmentSourceIdentity ||
            !operation.RequiredEvidence.Order(StringComparer.Ordinal)
                .SequenceEqual(requiredEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("Asteria supersession expected operation was not resolved as reserved.");
        }

        var attemptId = GetString(receipt, "attemptId");
        using var attempt = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/attempts/{Uri.EscapeDataString(attemptId)}",
            cancellationToken);
        if (GetString(attempt.RootElement, "status") != "Accepted" ||
            GetString(attempt.RootElement, "event") != SupersessionObservation)
        {
            throw new InvalidDataException("Asteria supersession receipt does not link to an accepted evaluator attempt.");
        }
        var transitionId = GetString(attempt.RootElement, "transitionId");

        using var history = await GetJsonAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/history", cancellationToken);
        if (history.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("State Machine transition history must be an array.");
        }
        var transitions = history.RootElement.EnumerateArray()
            .Where(item => item.TryGetProperty("attemptId", out var value) &&
                           value.ValueKind == JsonValueKind.String &&
                           value.GetString() == attemptId)
            .ToArray();
        if (transitions.Length != 1)
        {
            throw new InvalidDataException("Asteria supersession attempt must have exactly one accepted transition-history entry.");
        }
        var transition = transitions[0];
        if (GetString(transition, "event") != SupersessionObservation ||
            GetString(transition, "transitionId") != transitionId ||
            GetString(transition, "newState") != "Superseded" ||
        GetString(transition, "definitionVersion") != definitionVersion)
        {
        throw new InvalidDataException("Accepted State Machine history does not prove the bound-definition supersession transition.");
        }
    }

    private async Task VerifySupersessionAcrossAttemptsAsync(
        string instanceId,
        string definitionVersion,
        AsteriaAmendmentSnapshot amendment,
        IReadOnlyList<AsteriaSupersessionAttempt> attempts,
        CancellationToken cancellationToken)
    {
        foreach (var attempt in attempts.OrderByDescending(item => item.ExpectedRevision))
        {
            if (attempt.ReleaseId != amendment.ReleaseId || attempt.AmendmentId != amendment.AmendmentId)
            {
                throw new InvalidDataException("Asteria supersession attempt belongs to a different amendment.");
            }
            var receipt = await FindInboundReceiptAsync(instanceId, attempt.MessageId, cancellationToken);
            if (receipt is null)
            {
                continue;
            }
            var status = GetString(receipt.Value, "status");
            if (status is not ("Accepted" or "Duplicate"))
            {
                continue;
            }
            await VerifySupersessionEvidenceAsync(
                instanceId,
                definitionVersion,
                attempt.OperationId,
                attempt.MessageId,
                [
                    "amendmentId",
                    "requestFingerprint",
                    "expectedBaseGeneration",
                    "replacementGeneration",
                    "replacementMaterialDigest"
                ],
                cancellationToken);
            return;
        }

        throw new InvalidDataException(
            "The terminal Asteria generation has no accepted supersession receipt for its durable amendment attempts.");
    }

    private static void ValidateSupersessionOperation(
        AsteriaExpectedOperation operation,
        string instanceId,
        string operationId,
        long expectedRevision,
        IReadOnlyList<string> requiredEvidence)
    {
        if (operation.InstanceId != instanceId ||
            operation.OperationId != operationId ||
            operation.ObservationType != SupersessionObservation ||
            operation.SourceIdentity != AmendmentSourceIdentity ||
            operation.ExpectedRevision != expectedRevision ||
            !operation.RequiredEvidence.Order(StringComparer.Ordinal)
                .SequenceEqual(requiredEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            operation.Status is not ("Expected" or "Resolved"))
        {
            throw new InvalidDataException("State Machine supersession operation conflicts with the durable amendment.");
        }
    }

    private static void ValidateAssessmentStartOperation(
        AsteriaExpectedOperation operation,
        AsteriaReleaseSnapshot release,
        long expectedRevision,
        string operationId,
        IReadOnlyList<string> requiredEvidence)
    {
        if (operation.InstanceId != release.WorkflowInstanceId ||
            operation.OperationId != operationId ||
            operation.ObservationType != AssessmentStartObservation ||
            operation.SourceIdentity != AssessmentStartSourceIdentity ||
            operation.ExpectedRevision != expectedRevision ||
            !operation.RequiredEvidence.Order(StringComparer.Ordinal)
                .SequenceEqual(requiredEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            operation.Status is not ("Expected" or "Resolved"))
        {
            throw new InvalidDataException("Asteria assessment-start operation conflicts with the generation work.");
        }
    }

    private static IReadOnlyDictionary<string, object?> CreateInstanceFacts(AsteriaReleaseSnapshot release)
    {
        var material = release.Material;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
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
            ["releaseOwner"] = material.ReleaseOwner,
            ["plannedDeploymentAt"] = material.PlannedDeploymentAt.ToUniversalTime()
        };
    }

    private static void ValidateInstance(AsteriaReleaseSnapshot release, AsteriaWorkflowInstance instance)
    {
        if (instance.Id != release.WorkflowInstanceId ||
            instance.DefinitionId != release.DefinitionId ||
            instance.DefinitionVersion != release.DefinitionVersion)
        {
            throw new InvalidDataException("State Machine instance identity differs from the authoritative release association.");
        }
        if (instance.CurrentState is not ("Draft" or "UnderAssessment" or "RequirementsIdentified" or
            "ReviewObligationsIdentified" or "AssessmentUnresolved" or "Prohibited" or "Cancelled" or "Superseded"))
        {
            throw new InvalidDataException($"Asteria initial-assessment instance has unsupported state '{instance.CurrentState}'.");
        }
        var expectedFacts = CreateInstanceFacts(release);
        if (instance.Facts.ValueKind != JsonValueKind.Object ||
            instance.Facts.EnumerateObject().Count() != expectedFacts.Count)
        {
            throw new InvalidDataException("State Machine instance facts do not match the canonical release material shape.");
        }
        foreach (var (name, expected) in expectedFacts)
        {
            if (!instance.Facts.TryGetProperty(name, out var actual) || !FactEquals(actual, expected))
            {
                throw new InvalidDataException($"State Machine instance fact '{name}' differs from the release registry.");
            }
        }
    }

    private static bool FactEquals(JsonElement actual, object? expected) => expected switch
    {
        string value => actual.ValueKind == JsonValueKind.String && actual.GetString() == value,
        bool value => actual.ValueKind is JsonValueKind.True or JsonValueKind.False && actual.GetBoolean() == value,
        int value => actual.ValueKind == JsonValueKind.Number && actual.TryGetInt32(out var actualValue) && actualValue == value,
        DateTimeOffset value => actual.ValueKind == JsonValueKind.String &&
                                DateTimeOffset.TryParse(actual.GetString(), out var actualValue) &&
                                actualValue == value,
        _ => false
    };

    private static void ValidateExpectedOperation(
        AsteriaExpectedOperation operation,
        AsteriaReleaseSnapshot release,
        long expectedRevision)
    {
        if (operation.InstanceId != release.WorkflowInstanceId ||
            operation.OperationId != release.Work.ExpectedOperationId ||
            operation.ObservationType != AssessmentObservation ||
            operation.SourceIdentity != DecisionSourceIdentity ||
            operation.ExpectedRevision != expectedRevision ||
            !operation.RequiredEvidence.Order(StringComparer.Ordinal)
                .SequenceEqual(RequiredEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            operation.Status is not ("Expected" or "Resolved"))
        {
            throw new InvalidDataException("State Machine expected operation conflicts with the durable Asteria assessment work.");
        }
    }

    private static void EnsureOutcomeState(string state, string outcome)
    {
        var expected = outcome switch
        {
            "Prohibited" => "Prohibited",
            "Unresolved" => "AssessmentUnresolved",
            "RequirementsDetermined" => "RequirementsIdentified",
            "AdditionalReviewRequired" => "ReviewObligationsIdentified",
            _ => throw new InvalidDataException($"Unsupported Asteria policy outcome '{outcome}'.")
        };
        if (!string.Equals(state, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"State Machine accepted assessment outcome '{outcome}' but reached unexpected state '{state}'.");
        }
    }

    private static object CreateObservationEvidence(
        AsteriaReleaseSnapshot release,
        AsteriaValidatedAssessment assessment,
        long parentRevision)
    {
        using var responseDocument = JsonDocument.Parse(assessment.OriginalResponseJson);
        using var selectedVersionsDocument = JsonDocument.Parse(assessment.SelectedPolicyVersionsJson);
        using var contextDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            businessEffectiveAt = assessment.BusinessEffectiveAt,
            knownAt = assessment.KnownAt,
            selectedPolicySourceVersions = selectedVersionsDocument.RootElement
        }, JsonOptions));

        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["parentRevision"] = parentRevision,
            ["expectedOperationId"] = release.Work.ExpectedOperationId,
            ["decisionRequestFingerprint"] = release.Work.DecisionRequestFingerprint,
            ["selectedPolicySourceVersions"] = selectedVersionsDocument.RootElement.Clone(),
            ["originalResponse"] = responseDocument.RootElement.Clone(),
            ["policySelectionContext"] = contextDocument.RootElement.Clone()
        };
        return evidence;
    }

    private static object CreateDecisionEvidence(AsteriaValidatedAssessment assessment) => new
    {
        decisionSystemIdentity = AsteriaDecisionApiClient.DecisionSourceIdentity,
        externalEvaluationId = assessment.EvaluationId,
        outcomeState = "Present",
        outcome = assessment.Outcome,
        evaluatedAt = assessment.EvaluatedAt,
        businessEffectiveAt = assessment.BusinessEffectiveAt,
        knowledgeCutoff = assessment.KnownAt,
        inputFactSetDigest = assessment.FactDigest,
        policyVersionIdentities = assessment.PolicyVersionIdentities,
        provenance = "Asteria coordinator validated the original Decision API response against its durable canonical release, request, and State Machine expected-operation record. The Decision API does not independently verify workflow identity, operation identity, or parent revision.",
        originalResponseJson = assessment.OriginalResponseJson,
        missingFacts = assessment.MissingFacts,
        unknownFacts = assessment.UnknownFacts,
        unavailableFacts = assessment.UnavailableFacts
    };

    private static AsteriaExpectedOperation ReadExpectedOperation(JsonElement value) =>
        new(
            GetString(value, "operationId"),
            GetString(value, "instanceId"),
            GetString(value, "observationType"),
            GetString(value, "sourceIdentity"),
            GetRequired(value, "expectedRevision").GetInt64(),
            GetRequired(value, "requiredEvidence").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            GetString(value, "status"),
            GetNullableString(value, "resultDigest"));

    private static AsteriaWorkflowInstance ReadInstanceFromResponse(JsonElement value) =>
        new(
            GetString(value, "id"),
            GetString(value, "definitionId"),
            GetString(value, "definitionVersion"),
            GetString(value, "currentState"),
            GetRequired(value, "revision").GetInt64(),
            GetRequired(value, "facts").Clone());

    private static async Task<AsteriaWorkflowInstance> ReadInstanceAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ReadInstanceFromResponse(document.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        EnsureSuccess(response, "reading State Machine resource");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private async Task<IReadOnlyList<JsonElement>> ReadJsonArrayAsync(
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(path, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{description} must be an array.");
        }
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static void EnsureAcceptedReceipt(JsonElement response)
    {
        var receipt = response.TryGetProperty("message", out var message) ? message : response;
        var status = GetString(receipt, "status");
        if (status is not ("Accepted" or "Duplicate"))
        {
            var reason = GetString(receipt, "reasonCode");
            var explanation = GetString(receipt, "explanation");
            throw new InvalidOperationException($"State Machine refused Asteria input ({reason}): {explanation}");
        }
    }

    private static async Task<HttpRequestException> CreateHttpExceptionAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        new($"Failed while {operation} (HTTP {(int)response.StatusCode}): " +
            await response.Content.ReadAsStringAsync(cancellationToken),
            inner: null,
            response.StatusCode);

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Failed while {operation} (HTTP {(int)response.StatusCode}).",
                inner: null,
                response.StatusCode);
        }
    }

    private static string StableMessageId(string workId, string action) =>
        "ast-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{workId}\0{action}")));

    private static string GetString(JsonElement parent, string name)
    {
        var value = GetRequired(parent, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"State Machine response field '{name}' must be a string.");
    }

    private static string? GetNullableString(JsonElement parent, string name)
    {
        var value = GetRequired(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new InvalidDataException($"State Machine response field '{name}' must be a nullable string.")
        };
    }

    private static JsonElement GetRequired(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"State Machine response omitted '{name}'.");
}
