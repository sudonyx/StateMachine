using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DurableStateMachine.AsteriaClient;

public sealed class AsteriaReadinessCoordinator(
    AsteriaReleaseRegistry releaseRegistry,
    AsteriaActivityLedger activityLedger,
    AsteriaApprovalRegistry approvalRegistry,
    AsteriaStateMachineClient stateMachine,
    IReadOnlyDictionary<string, string> definitionJsonByVersion,
    IReadOnlyDictionary<string, AsteriaHarnessOperationBinding> verificationBindings,
    TimeProvider? timeProvider = null)
{
    private const string SourceIdentity = "AsteriaReadinessCoordinator";
    private static readonly string[] ReadinessEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "assessmentEvidenceDigest",
        "allObligationsEvidenceDigest",
        "readinessWorkId"
    ];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyDictionary<string, string> _definitionDigests =
        definitionJsonByVersion.ToDictionary(
            item => item.Key,
            item => AsteriaActivityLedger.Fingerprint(item.Value),
            StringComparer.Ordinal);

    public async Task<AsteriaActivitySnapshot> ConfirmAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var release = await releaseRegistry.GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
        EnsureCurrentAcceptedAssessment(release);
        if (release.DefinitionVersion != "5.0")
        {
            throw new InvalidOperationException("Deployment readiness coordination requires Asteria definition V5.");
        }
        if (!_definitionDigests.TryGetValue(release.DefinitionVersion, out var definitionDigest))
        {
            throw new InvalidDataException(
                $"No immutable definition digest is configured for Asteria version '{release.DefinitionVersion}'.");
        }

        var assessment = AsteriaDecisionApiClient.ValidateResponse(
            release.Work.OriginalResponseJson!, release);
        if (assessment.Outcome is "Prohibited" or "Unresolved" ||
            assessment.EvaluationId is null)
        {
            throw new InvalidOperationException(
                $"Assessment outcome '{assessment.Outcome}' cannot enter deployment readiness.");
        }
        var assessmentDigest = AsteriaActivityLedger.Fingerprint(assessment.OriginalResponseJson);
        var workId = StableId($"{release.ReleaseId}\0{release.AssessmentGeneration}\0Readiness", "work");
        var existingActivity = await activityLedger.GetAsync(workId, cancellationToken);
        var obligations = AsteriaDecisionApiClient.ReadObligationApplicabilities(assessment);
        var instance = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance is missing.");
        if (instance.DefinitionId != release.DefinitionId ||
            instance.DefinitionVersion != release.DefinitionVersion ||
            instance.CurrentState is not ("RequirementsIdentified" or "ReviewObligationsIdentified" or "ReadyForDeployment") ||
            (instance.CurrentState == "ReadyForDeployment" &&
             existingActivity?.Status is not ("Accepted" or "ObservationSubmitted")))
        {
            throw new InvalidOperationException(
                $"Asteria workflow state '{instance.CurrentState}' is not eligible for readiness.");
        }
        var concerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue("CancellationIntent", out var cancellation) ||
            (cancellation.CurrentState != "NotRequested" &&
             !(instance.CurrentState == "ReadyForDeployment" && existingActivity?.Status == "Accepted")))
        {
            throw new InvalidOperationException("Asteria readiness is blocked by cancellation intent.");
        }

        var activities = await activityLedger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration, cancellationToken);
        var obligationsEvidence = new List<object>(4);
        var required = new[]
        {
            ("FunctionalVerification", obligations.FunctionalVerification, "Passed", "AsteriaFunctionalVerificationResult"),
            ("SecurityVerification", obligations.SecurityVerification, "Passed", "AsteriaSecurityVerificationResult"),
            ("ReleaseReviewApproval", obligations.ReleaseReviewApproval, "Approved", "AsteriaReleaseReviewApprovalResult"),
            ("EmergencyApproval", obligations.EmergencyApproval, "Approved", "AsteriaEmergencyApprovalResult")
        };
        foreach (var (concernId, applicability, requiredOutcome, mapping) in required)
        {
            if (!concerns.TryGetValue(concernId, out var concern))
            {
                throw new InvalidDataException($"Asteria workflow has no concern '{concernId}'.");
            }
            await ValidateApplicabilityObservationAsync(
                release, concernId, applicability, concern.CurrentState, cancellationToken);
            var concernActivities = activities
                .Where(activity => activity.ConcernId == concernId)
                .ToArray();
            if (concernActivities.Any(activity =>
                    activity.Status is "OutcomeUnknown" or "Refused" or "Conflicting"))
            {
                throw new InvalidOperationException(
                    $"Asteria concern '{concernId}' has refused, conflicting, or unknown work.");
            }

            if (applicability == "NotRequired")
            {
                if (concernActivities.Length != 0 || concern.CurrentState != "NotRequired")
                {
                    throw new InvalidOperationException(
                        $"Asteria concern '{concernId}' is NotRequired but has inconsistent workflow or activity evidence.");
                }
                obligationsEvidence.Add(new
                {
                    concernId,
                    applicability,
                    state = concern.CurrentState,
                    applicabilityEvidenceDigest = assessmentDigest
                });
                continue;
            }
            if (applicability != "Required")
            {
                throw new InvalidOperationException(
                    $"Asteria concern '{concernId}' applicability '{applicability}' blocks readiness.");
            }

            var activity = concernActivities.SingleOrDefault(item => item.Status == "Accepted");
            if (activity is null ||
                concernActivities.Count(item => item.Status == "Accepted") != 1 ||
                activity.ValidatedOutcome != requiredOutcome ||
                activity.ValidatedEvidenceDigest is null ||
                activity.OriginalResponseJson is null ||
                activity.ResponseDigest is null ||
                activity.ObservationReceiptId is null ||
                activity.AcceptedAttemptId is null ||
                activity.AcceptedHistoryId is null ||
                activity.ReleaseId != release.ReleaseId ||
                activity.Generation != release.AssessmentGeneration ||
                activity.MaterialDigest != release.MaterialDigest ||
                activity.WorkflowInstanceId != release.WorkflowInstanceId ||
                activity.DefinitionId != release.DefinitionId ||
                activity.DefinitionVersion != release.DefinitionVersion ||
                activity.DefinitionDigest != definitionDigest ||
                activity.ActivityType != concernId ||
                activity.ResponseDigest != AsteriaActivityLedger.Fingerprint(activity.OriginalResponseJson) ||
                !IsFingerprint(activity.ValidatedEvidenceDigest))
            {
                throw new InvalidOperationException(
                    $"Required Asteria concern '{concernId}' lacks one current-generation accepted {requiredOutcome} activity.");
            }
            await ValidateAcceptedActivityAsync(activity, mapping, cancellationToken);
            if (concernId is "ReleaseReviewApproval" or "EmergencyApproval")
            {
                await ValidateApprovalDecisionAsync(activity, concernId, requiredOutcome, cancellationToken);
            }
            if (concern.CurrentState != requiredOutcome)
            {
                throw new InvalidOperationException(
                    $"Asteria concern '{concernId}' projection is '{concern.CurrentState}', not '{requiredOutcome}'.");
            }
            obligationsEvidence.Add(new
            {
                concernId,
                applicability,
                state = concern.CurrentState,
                activity.WorkId,
                activity.ActivityId,
                activity.ExpectedOperationId,
                activity.ObservationReceiptId,
                activity.AcceptedAttemptId,
                activity.AcceptedHistoryId,
                activity.ValidatedOutcome,
                activity.ValidatedEvidenceDigest,
                activity.ResponseDigest
            });
        }

        await ValidateAcceptedAssessmentAsync(release, assessment, cancellationToken);
        var obligationsEvidenceJson = JsonSerializer.Serialize(obligationsEvidence);
        var obligationsDigest = AsteriaActivityLedger.Fingerprint(obligationsEvidenceJson);
        var activityId = StableId(workId, "activity");
        var operationId = StableId(workId, "expected-operation");
        var messageId = StableId(workId, "readiness-message");
        var submissionId = StableId(workId, "submission");
        var idempotencyKey = StableId(workId, "idempotency");
        var requestJson = JsonSerializer.Serialize(new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            assessmentEvidenceDigest = assessmentDigest,
            allObligationsEvidenceDigest = obligationsDigest,
            workId,
            operationId
        });
        var parentRevision = existingActivity?.ParentRevision ?? instance.Revision;
        var reservation = new AsteriaActivityReservation(
            workId,
            activityId,
            release.ReleaseId,
            release.AssessmentGeneration,
            release.MaterialDigest,
            release.WorkflowInstanceId,
            release.DefinitionId,
            release.DefinitionVersion,
            definitionDigest,
            "Readiness",
            "Readiness",
            submissionId,
            operationId,
            parentRevision,
            0,
            submissionId,
            idempotencyKey,
            requestJson,
            null,
            null,
            null,
            null,
            null,
            null);
        var activityResult = await activityLedger.ReserveAsync(
            reservation, _timeProvider.GetUtcNow(), cancellationToken);
        if (activityResult.Status is "OutcomeUnknown" or "Refused" or "Conflicting")
        {
            throw new InvalidOperationException(
                $"Asteria readiness activity '{workId}' is '{activityResult.Status}' and requires reconciliation.");
        }
        if (activityResult.Status == "Reserved")
        {
            var operation = await stateMachine.EnsureExpectedParentOperationAsync(
                release,
                operationId,
                "AsteriaReadinessConfirmed",
                SourceIdentity,
                activityResult.ParentRevision,
                ReadinessEvidenceFields,
                cancellationToken);
            if (operation.ExpectedRevision != activityResult.ParentRevision)
            {
                throw new InvalidDataException("Asteria readiness operation has a conflicting parent revision.");
            }
            activityResult = await activityLedger.MarkExpectedOperationRegisteredAsync(
                workId, _timeProvider.GetUtcNow(), cancellationToken);
        }
        if (activityResult.Status == "OperationRegistered")
        {
            var coordinatorResponse = JsonSerializer.Serialize(new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest,
                assessmentEvidenceDigest = assessmentDigest,
                allObligationsEvidenceDigest = obligationsDigest,
                obligations = obligationsEvidence,
                workId,
                operationId,
                result = "Ready"
            });
            activityResult = await activityLedger.RecordCoordinatorResponseAsync(
                workId, coordinatorResponse, _timeProvider.GetUtcNow(), cancellationToken);
        }
        if (activityResult.Status == "ResponsePersisted")
        {
            activityResult = await activityLedger.RecordValidatedOutcomeAsync(
                workId,
                "Ready",
                AsteriaActivityLedger.Fingerprint(activityResult.OriginalResponseJson!),
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }
        if (activityResult.Status is not ("ResponsePersisted" or "ObservationSubmitted" or "Accepted"))
        {
            throw new InvalidDataException(
                $"Asteria readiness activity '{workId}' has unsupported status '{activityResult.Status}'.");
        }

        var accepted = await stateMachine.SubmitReadinessObservationAsync(
            release,
            operationId,
            activityResult.ParentRevision,
            messageId,
            StableId(workId, "readiness-observation"),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["assessmentEvidenceDigest"] = assessmentDigest,
                ["allObligationsEvidenceDigest"] = obligationsDigest,
                ["readinessWorkId"] = workId
            },
            cancellationToken);
        activityResult = await activityLedger.MarkObservationSubmittedAsync(
            workId, accepted.ReceiptId, _timeProvider.GetUtcNow(), cancellationToken);
        return await activityLedger.MarkAcceptedAsync(
            workId,
            accepted.ReceiptId,
            accepted.AttemptId,
            accepted.HistoryId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task ValidateApplicabilityObservationAsync(
        AsteriaReleaseSnapshot release,
        string concernId,
        string applicability,
        string currentState,
        CancellationToken cancellationToken)
    {
        var expectedState = applicability switch
        {
            "Required" => "Required",
            "NotRequired" => "NotRequired",
            "Unresolved" => "ApplicabilityUnresolved",
            _ => throw new InvalidDataException($"Unsupported applicability '{applicability}'.")
        };
        if (currentState is not ("Passed" or "Failed" or "Indeterminate" or "Approved" or "Rejected" or "Unresolved") &&
            currentState != expectedState)
        {
            throw new InvalidOperationException(
                $"Asteria concern '{concernId}' projection '{currentState}' disagrees with '{applicability}' applicability.");
        }
        var action = $"applicability-{concernId}";
        var operationId = StableAssessmentId(release.Work.WorkId, action + "-operation");
        var messageId = StableAssessmentId(release.Work.WorkId, action + "-message");
        var expectedMapping = $"Asteria{concernId}Applicability";
        var operations = await stateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == operationId);
        if (operation is null ||
            operation.ObservationType != expectedMapping ||
            operation.SourceIdentity != AsteriaStateMachineClient.AssessmentStartSourceIdentity ||
            operation.Status != "Resolved" ||
            string.IsNullOrWhiteSpace(operation.ResultDigest))
        {
            throw new InvalidDataException($"Asteria {concernId} applicability operation is not durably accepted.");
        }
        var receipts = await stateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId, cancellationToken);
        var receipt = receipts.SingleOrDefault(item =>
            item.TryGetProperty("messageId", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() == messageId);
        if (receipt.ValueKind != JsonValueKind.Object ||
            GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "operationId") != operationId ||
            GetString(receipt, "acceptedEvent") != "AsteriaObligationApplicabilityObserved")
        {
            throw new InvalidDataException($"Asteria {concernId} applicability has no matching accepted receipt.");
        }
        var evidence = GetObject(receipt, "evidence");
        if (GetString(evidence, "releaseId") != release.ReleaseId ||
            GetInt32(evidence, "assessmentGeneration") != release.AssessmentGeneration ||
            GetString(evidence, "materialDigest") != release.MaterialDigest ||
            GetString(evidence, "assessmentEvidenceDigest") !=
                AsteriaActivityLedger.Fingerprint(release.Work.OriginalResponseJson!) ||
            GetString(evidence, "applicability") != applicability)
        {
            throw new InvalidDataException($"Asteria {concernId} applicability receipt has a conflicting assessment binding.");
        }
        var history = await stateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId, cancellationToken);
        if (!history.Any(item =>
                GetString(item, "concernId") == concernId &&
                GetString(item, "attemptId") == GetString(receipt, "attemptId") &&
                GetString(item, "event") == "AsteriaObligationApplicabilityObserved" &&
                GetString(item, "newState") == expectedState))
        {
            throw new InvalidDataException($"Asteria {concernId} applicability receipt has no matching concern history.");
        }
    }

    private async Task ValidateAcceptedActivityAsync(
        AsteriaActivitySnapshot activity,
        string observationType,
        CancellationToken cancellationToken)
    {
        var operations = await stateMachine.ListExpectedOperationsAsync(
            activity.WorkflowInstanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == activity.ExpectedOperationId);
        var expectedEvent = observationType is "AsteriaFunctionalVerificationResult" or "AsteriaSecurityVerificationResult"
            ? "AsteriaVerificationResultObserved"
            : "AsteriaApprovalResultObserved";
        if (operation is null ||
            operation.ObservationType != observationType ||
            operation.ExpectedRevision != activity.ParentRevision ||
            operation.Status != "Resolved" ||
            string.IsNullOrWhiteSpace(operation.ResultDigest))
        {
            throw new InvalidDataException($"Accepted Asteria work '{activity.WorkId}' has no resolved expected operation.");
        }
        var receipts = await stateMachine.ListInboundMessagesAsync(activity.WorkflowInstanceId, cancellationToken);
        var receipt = receipts.SingleOrDefault(item =>
            item.TryGetProperty("id", out var receiptId) &&
            receiptId.ValueKind == JsonValueKind.String &&
            receiptId.GetString() == activity.ObservationReceiptId);
        if (receipt.ValueKind != JsonValueKind.Object ||
            GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "operationId") != activity.ExpectedOperationId ||
            GetString(receipt, "acceptedEvent") != expectedEvent ||
            GetString(receipt, "attemptId") != activity.AcceptedAttemptId)
        {
            throw new InvalidDataException($"Accepted Asteria work '{activity.WorkId}' has no matching State Machine receipt.");
        }
        var observationEvidence = GetObject(receipt, "evidence");
        if (        GetString(observationEvidence, "activityId") != activity.ActivityId ||
        GetString(observationEvidence, "expectedOperationId") != activity.ExpectedOperationId ||
        GetString(observationEvidence, "result") != activity.ValidatedOutcome ||
        GetString(observationEvidence, "evidenceDigest") != activity.ValidatedEvidenceDigest)
        {
            throw new InvalidDataException($"Accepted Asteria work '{activity.WorkId}' receipt has conflicting result evidence.");
        }
        if (activity.ConcernId is "FunctionalVerification" or "SecurityVerification")
        {
            using var archived = JsonDocument.Parse(activity.OriginalResponseJson!);
            var body = GetString(archived.RootElement, "body");
            if (GetBoolean(archived.RootElement, "truncated") ||
                AsteriaActivityLedger.Fingerprint(body) != activity.ValidatedEvidenceDigest)
            {
                throw new InvalidDataException($"Accepted Asteria verification '{activity.WorkId}' has inconsistent archived evidence.");
            }
            using var providerResult = JsonDocument.Parse(body);
            var result = providerResult.RootElement;
            if (!verificationBindings.TryGetValue(activity.ConcernId, out var binding) ||
                binding.ProviderIdentity != activity.ProviderIdentity ||
                binding.ApiDescriptionVersionId != activity.ApiDescriptionVersionId ||
                binding.OperationKey != activity.OperationKey)
            {
                throw new InvalidDataException(
                    $"Accepted Asteria verification '{activity.WorkId}' has no matching configured Harness binding.");
            }
            if (GetString(result, "releaseId") != activity.ReleaseId ||
                GetInt32(result, "assessmentGeneration") != activity.Generation ||
                GetString(result, "materialDigest") != activity.MaterialDigest ||
                GetString(result, "workflowInstanceId") != activity.WorkflowInstanceId ||
                GetString(result, "activityId") != activity.ActivityId ||
                GetString(result, "expectedOperationId") != activity.ExpectedOperationId ||
                GetString(result, "result") != activity.ValidatedOutcome ||
                string.IsNullOrWhiteSpace(GetString(result, "evidenceId")))
            {
                throw new InvalidDataException($"Accepted Asteria verification '{activity.WorkId}' has conflicting provider evidence.");
            }
        }
        var concernHistory = await stateMachine.ListConcernHistoryAsync(
            activity.WorkflowInstanceId, cancellationToken);
        if (!concernHistory.Any(item =>
                GetString(item, "id") == activity.AcceptedHistoryId &&
                GetString(item, "concernId") == activity.ConcernId &&
                GetString(item, "attemptId") == activity.AcceptedAttemptId &&
                GetString(item, "event") == expectedEvent &&
                GetString(item, "newState") == activity.ValidatedOutcome))
        {
            throw new InvalidDataException($"Accepted Asteria work '{activity.WorkId}' has no matching concern history.");
        }
    }

    private async Task ValidateApprovalDecisionAsync(
        AsteriaActivitySnapshot activity,
        string concernId,
        string expectedOutcome,
        CancellationToken cancellationToken)
    {
        var decision = await approvalRegistry.GetByWorkIdAsync(activity.WorkId, cancellationToken)
            ?? throw new InvalidDataException($"Approval activity '{activity.WorkId}' has no immutable decision.");
        if (decision.ReleaseId != activity.ReleaseId ||
            decision.Generation != activity.Generation ||
            decision.MaterialDigest != activity.MaterialDigest ||
            decision.WorkflowInstanceId != activity.WorkflowInstanceId ||
            decision.ConcernId != concernId ||
            decision.ActivityId != activity.ActivityId ||
            decision.ExpectedOperationId != activity.ExpectedOperationId ||
            decision.Decision != expectedOutcome ||
            decision.EvidenceDigest != activity.ValidatedEvidenceDigest ||
            decision.AuthorityRole != activity.AuthorityRole ||
            decision.PrincipalId != activity.AuthorityPrincipalId ||
            decision.AuthorityRegistryVersion != activity.AuthorityRegistryVersion ||
            decision.AuthorityRole != (concernId == "ReleaseReviewApproval"
                ? "ReleaseReviewer"
                : "EmergencyReleaseAuthoriser") ||
            string.IsNullOrWhiteSpace(decision.PrincipalId) ||
            string.IsNullOrWhiteSpace(decision.AuthorityRegistryVersion))
        {
            throw new InvalidDataException($"Approval activity '{activity.WorkId}' has conflicting immutable authority evidence.");
        }
    }

    private async Task ValidateAcceptedAssessmentAsync(
        AsteriaReleaseSnapshot release,
        AsteriaValidatedAssessment assessment,
        CancellationToken cancellationToken)
    {
        var operations = await stateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId, cancellationToken);
        var operation = operations.SingleOrDefault(item => item.OperationId == release.Work.ExpectedOperationId);
        var messageId = StableAssessmentId(release.Work.WorkId, "assessment-observation");
        var receipt = (await stateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId, cancellationToken))
            .SingleOrDefault(item =>
                item.TryGetProperty("messageId", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                id.GetString() == messageId);
        var expectedState = assessment.Outcome switch
        {
            "RequirementsDetermined" => "RequirementsIdentified",
            "AdditionalReviewRequired" => "ReviewObligationsIdentified",
            _ => throw new InvalidOperationException($"Assessment outcome '{assessment.Outcome}' blocks readiness.")
        };
        if (operation is null ||
            operation.ObservationType != AsteriaStateMachineClient.AssessmentObservation ||
            operation.SourceIdentity != AsteriaStateMachineClient.DecisionSourceIdentity ||
            operation.ExpectedRevision != release.Work.ParentRevision ||
            operation.Status != "Resolved" ||
            string.IsNullOrWhiteSpace(operation.ResultDigest) ||
            receipt.ValueKind != JsonValueKind.Object ||
            GetString(receipt, "status") is not ("Accepted" or "Duplicate") ||
            GetString(receipt, "operationId") != release.Work.ExpectedOperationId ||
            GetString(receipt, "acceptedEvent") != AsteriaStateMachineClient.AssessmentObservation)
        {
            throw new InvalidDataException("Asteria assessment lacks its accepted generation-bound workflow receipt.");
        }
        var evidence = GetObject(receipt, "evidence");
        if (GetString(evidence, "releaseId") != release.ReleaseId ||
            GetInt32(evidence, "assessmentGeneration") != release.AssessmentGeneration ||
            GetString(evidence, "materialDigest") != release.MaterialDigest ||
            GetInt64(evidence, "parentRevision") != release.Work.ParentRevision.Value ||
            GetString(evidence, "expectedOperationId") != release.Work.ExpectedOperationId)
        {
            throw new InvalidDataException("Asteria accepted assessment receipt has conflicting evidence binding.");
        }
        var storedResponse = JsonNode.Parse(GetObject(evidence, "originalResponse").GetRawText());
        var originalResponse = JsonNode.Parse(assessment.OriginalResponseJson);
        if (!JsonNode.DeepEquals(storedResponse, originalResponse))
        {
            throw new InvalidDataException("Asteria accepted assessment receipt does not preserve the original Decision API response.");
        }
        var history = await stateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId, cancellationToken);
        if (!history.Any(item =>
                GetString(item, "attemptId") == GetString(receipt, "attemptId") &&
                GetString(item, "event") == AsteriaStateMachineClient.AssessmentObservation &&
                GetString(item, "newState") == expectedState))
        {
            throw new InvalidDataException("Asteria assessment receipt has no matching accepted transition history.");
        }
    }

    private static string StableAssessmentId(string workId, string action) =>
        "ast-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{workId}\0{action}")));

    private static string StableId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static string GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind == JsonValueKind.String
            ? propertyValue.GetString()!
            : throw new InvalidDataException($"State Machine response field '{property}' must be a string.");

    private static JsonElement GetObject(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind == JsonValueKind.Object
            ? propertyValue
            : throw new InvalidDataException($"State Machine response field '{property}' must be an object.");

    private static int GetInt32(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Asteria provider response field '{property}' must be an integer.");

    private static long GetInt64(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.TryGetInt64(out var result)
            ? result
            : throw new InvalidDataException($"State Machine response field '{property}' must be an integer.");

    private static bool GetBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? propertyValue.GetBoolean()
            : throw new InvalidDataException($"Asteria provider response field '{property}' must be a boolean.");

    private static bool IsFingerprint(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void EnsureCurrentAcceptedAssessment(AsteriaReleaseSnapshot release)
    {
        if (release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.OriginalResponseJson is null ||
            release.Work.OriginalEvidenceJson is null ||
            release.Work.EvaluationId is null ||
            release.Work.ParentRevision is null ||
            release.RegistryState != "Operational" ||
            release.AssessmentGeneration != release.Work.Generation ||
            release.MaterialDigest != release.Work.MaterialDigest ||
            release.WorkflowInstanceId != release.Work.WorkflowInstanceId)
        {
            throw new InvalidOperationException(
                "Readiness requires a current generation's accepted, durably evidenced assessment.");
        }
    }
}
