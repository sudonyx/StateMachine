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
        if (oldGeneration.DefinitionVersion != "3.0" ||
            replacementGeneration.DefinitionVersion != "3.0" ||
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
                oldGeneration.WorkflowInstanceId, amendment, attempts, cancellationToken);
            return;
        }

        if (instance.CurrentState is not ("Draft" or "UnderAssessment" or "RequirementsIdentified" or
            "ReviewObligationsIdentified" or "AssessmentUnresolved"))
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
            oldGeneration.WorkflowInstanceId, amendment, attempts, cancellationToken);
    }

    public async Task<AsteriaWorkflowInstance> EnsureSubmittedAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken = default)
    {
        if (release.DefinitionVersion == "3.0")
        {
            return await EnsureV3SubmittedAsync(release, cancellationToken);
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

    private async Task<AsteriaWorkflowInstance> EnsureV3SubmittedAsync(
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
            throw new InvalidOperationException("V3 assessment-start operation was registered against a stale revision.");
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
                $"Expected V3 Asteria assessment state UnderAssessment, found '{current.CurrentState}'.");
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

    public async Task<IReadOnlyList<JsonElement>> ListInboundMessagesAsync(
        string instanceId,
        CancellationToken cancellationToken = default) =>
        await ReadJsonArrayAsync(
            $"api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages",
            "State Machine inbound-message receipts",
            cancellationToken);

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
            GetString(transition, "definitionVersion") != "3.0")
        {
            throw new InvalidDataException("Accepted State Machine history does not prove the V3 supersession transition.");
        }
    }

    private async Task VerifySupersessionAcrossAttemptsAsync(
        string instanceId,
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
