using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Json;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaDeploymentResult(
    AsteriaActivitySnapshot Activity,
    string DeploymentId,
    string Outcome);

public sealed record AsteriaDeploymentHarnessBindings(
    AsteriaHarnessOperationBinding Request,
    AsteriaHarnessOperationBinding StatusById,
    AsteriaHarnessOperationBinding StatusByIdempotencyKey);

public sealed class AsteriaDeploymentCoordinator(
    AsteriaReleaseRegistry releaseRegistry,
    AsteriaActivityLedger activityLedger,
    AsteriaStateMachineClient stateMachine,
    HttpClient harnessClient,
    string definitionJson,
    AsteriaDeploymentHarnessBindings bindings,
    TimeProvider? timeProvider = null,
    AsteriaDeadlineCoordinator? deadlineCoordinator = null)
{
    private const string SourceIdentity = "AsteriaDeploymentCoordinator";
    private const string UnknownDetail =
        "Deployment may have been admitted, but no authoritative status was established; the mutating request will not be resent.";
    private static readonly string[] StartEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "deploymentWorkId",
        "expectedOperationId",
        "idempotencyKey"
    ];
    private static readonly string[] ResultEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "deploymentWorkId",
        "expectedOperationId",
        "deploymentId",
        "result",
        "evidenceDigest"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _definitionDigest = AsteriaActivityLedger.Fingerprint(definitionJson);

    public async Task<AsteriaDeploymentResult> ExecuteAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var release = await releaseRegistry.GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
        EnsureCurrentAcceptedAssessment(release);
        if (release.DefinitionVersion != "5.0")
        {
            throw new InvalidOperationException("Deployment execution requires Asteria definition V5.");
        }
        ValidateBinding(bindings.Request);
        ValidateBinding(bindings.StatusById);
        ValidateBinding(bindings.StatusByIdempotencyKey);
        _ = await EnsureReadinessAccepted(release, cancellationToken);

        var workId = StableId($"{release.ReleaseId}\0{release.AssessmentGeneration}\0Deployment", "work");
        var activity = await activityLedger.GetAsync(workId, cancellationToken);
        var instance = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance is missing.");
        var concerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (activity is null)
        {
            if (instance.CurrentState != "ReadyForDeployment" ||
                !concerns.TryGetValue("CancellationIntent", out var cancellation) ||
                cancellation.CurrentState != "NotRequested" ||
                !concerns.TryGetValue("Deployment", out var deploymentConcern) ||
                deploymentConcern.CurrentState != "NotRequested")
            {
                throw new InvalidOperationException("Asteria deployment requires the current, uncancelled ReadyForDeployment generation.");
            }
            activity = await ReserveAsync(release, instance.Revision, deploymentConcern.Revision, cancellationToken);
        }
        else
        {
            ValidateActivityBinding(release, activity);
            if (activity.Status is "OutcomeUnknown" or "Refused" or "Conflicting")
            {
                if (activity.Status is "Refused" or "Conflicting")
                {
                    throw new InvalidOperationException(
                        $"Asteria deployment activity '{workId}' is '{activity.Status}' and cannot be changed.");
                }
            }
            else if (instance.CurrentState is not (
                         "ReadyForDeployment" or "DeploymentInProgress" or
                         "DeploymentFailed" or "DeploymentUnknown" or "PostDeploymentVerification"))
            {
                throw new InvalidOperationException(
                    $"Asteria workflow state '{instance.CurrentState}' cannot resume deployment work.");
            }
        }
        if (activity.Status == "Accepted")
        {
            using var acceptedStatus = await GetValidatedStatusResponseAsync(activity, cancellationToken);
            ValidateStatusBinding(release, activity, acceptedStatus.RootElement);
            if (activity.ValidatedOutcome != OutcomeForStatus(
                    GetString(acceptedStatus.RootElement, "status")))
            {
                throw new InvalidDataException("Accepted Asteria deployment outcome conflicts with its archived provider status.");
            }
            return new AsteriaDeploymentResult(
                activity,
                GetString(acceptedStatus.RootElement, "deploymentId"),
                activity.ValidatedOutcome);
        }

        if (activity.Status == "Reserved")
        {
            _ = await stateMachine.EnsureExpectedParentOperationAsync(
                release,
                activity.ExpectedOperationId,
                "AsteriaDeploymentStartAuthorized",
                SourceIdentity,
                activity.ParentRevision,
                StartEvidenceFields,
                cancellationToken);
            activity = await activityLedger.MarkExpectedOperationRegisteredAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }
        if (activity.Status == "OperationRegistered")
        {
            var parentStartEvidence = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["deploymentWorkId"] = activity.WorkId,
                ["expectedOperationId"] = activity.ExpectedOperationId,
                ["idempotencyKey"] = activity.IdempotencyKey
            };
            _ = await stateMachine.SubmitParentLifecycleObservationAsync(
                release,
                activity.ExpectedOperationId,
                StableId(activity.WorkId, "deployment-start-message"),
                StableId(activity.WorkId, "deployment-start-observation"),
                "AsteriaDeploymentStartAuthorized",
                SourceIdentity,
                activity.ParentRevision,
                StartEvidenceFields,
                parentStartEvidence,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["releaseId"] = release.ReleaseId,
                    ["assessmentGeneration"] = release.AssessmentGeneration,
                    ["materialDigest"] = release.MaterialDigest,
                    ["deploymentWorkId"] = activity.WorkId,
                    ["idempotencyKey"] = activity.IdempotencyKey
                },
                new Dictionary<string, object?>(StringComparer.Ordinal),
                "AsteriaDeploymentStarted",
                "ReadyForDeployment",
                "DeploymentInProgress",
                cancellationToken);
            var parentAfterStart = await stateMachine.GetInstanceAsync(
                release.WorkflowInstanceId, cancellationToken)
                ?? throw new InvalidDataException("Asteria workflow instance disappeared after deployment start.");
            var concernOperationId = StableId(activity.WorkId, "deployment-concern-start-operation");
            var concernStartEvidence = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["deploymentWorkId"] = activity.WorkId,
                ["expectedOperationId"] = concernOperationId,
                ["idempotencyKey"] = activity.IdempotencyKey
            };
            _ = await stateMachine.SubmitLifecycleStartConcernObservationAsync(
                release,
                "Deployment",
                concernOperationId,
                StableId(activity.WorkId, "deployment-concern-start-message"),
                StableId(activity.WorkId, "deployment-concern-start-observation"),
                "AsteriaDeploymentStarted",
                SourceIdentity,
                parentAfterStart.Revision,
                StartEvidenceFields,
                concernStartEvidence,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["releaseId"] = release.ReleaseId,
                    ["assessmentGeneration"] = release.AssessmentGeneration,
                    ["materialDigest"] = release.MaterialDigest,
                    ["deploymentWorkId"] = activity.WorkId,
                    ["idempotencyKey"] = activity.IdempotencyKey
                },
                "DeploymentInProgress",
                "DeploymentInProgress",
                "Running",
                cancellationToken);
            activity = await activityLedger.MarkDispatchPendingAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }

        if (deadlineCoordinator is not null &&
            activity.Status is "DispatchPending" or "Dispatched" or "ResponsePersisted" or "ObservationSubmitted")
        {
            _ = await deadlineCoordinator.EnsureDeploymentInvestigationAsync(release, cancellationToken);
        }

        if (activity.Status == "DispatchPending")
        {
            await EnsureStillCurrentGenerationAsync(release, cancellationToken);
            var dispatchClaim = await activityLedger.MarkDispatchStartedAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
            activity = dispatchClaim.Activity;
            if (dispatchClaim.ShouldDispatch)
            {
                try
                {
                    activity = await DispatchAsync(release, activity, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    activity = await ReconcileByKeyAsync(release, activity, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _ = await MarkUnknownAsync(activity.WorkId, CancellationToken.None);
                    throw;
                }
            }
        }
        if (activity.Status is "Dispatched" or "OutcomeUnknown" or "ResponsePersisted")
        {
            if (activity.ValidatedOutcome is null)
            {
                activity = await ReconcileByKeyAsync(release, activity, cancellationToken);
            }
        }
        if (activity.Status is not ("ResponsePersisted" or "ObservationSubmitted" or "Accepted") ||
            activity.ValidatedOutcome is null ||
            activity.ValidatedEvidenceDigest is null)
        {
            throw new InvalidDataException(
                $"Asteria deployment activity '{activity.WorkId}' has unsupported status '{activity.Status}'.");
        }
        return await SubmitResultAsync(release, activity, cancellationToken);
    }

    private async Task<AsteriaActivitySnapshot> ReserveAsync(
        AsteriaReleaseSnapshot release,
        long parentRevision,
        long concernRevision,
        CancellationToken cancellationToken)
    {
        var workId = StableId($"{release.ReleaseId}\0{release.AssessmentGeneration}\0Deployment", "work");
        var activityId = StableId(workId, "activity");
        var idempotencyKey = StableId(workId, "deployment-idempotency");
        var operationId = StableId(workId, "deployment-parent-start-operation");
        var requestJson = JsonSerializer.Serialize(new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            product = release.Material.Product,
            targetVersion = release.Material.TargetVersion,
            environment = release.Material.Environment,
            idempotencyKey
        }, JsonOptions);
        return await activityLedger.ReserveAsync(
            new AsteriaActivityReservation(
                workId,
                activityId,
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                release.WorkflowInstanceId,
                release.DefinitionId,
                release.DefinitionVersion,
                _definitionDigest,
                "Deployment",
                "Deployment",
                StableId(workId, "attempt"),
                operationId,
                parentRevision,
                concernRevision,
                StableId(workId, "submission"),
                idempotencyKey,
                requestJson,
                bindings.Request.ProviderIdentity,
                bindings.Request.ApiDescriptionVersionId,
                bindings.Request.OperationKey,
                null,
                null,
                null),
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task<AsteriaActivitySnapshot> DispatchAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        CancellationToken cancellationToken)
    {
        using var requestBody = JsonDocument.Parse(activity.OriginalRequestJson);
        using var request = JsonContent.Create(new
        {
            providerId = bindings.Request.ProviderId,
            apiDescriptionVersionId = bindings.Request.ApiDescriptionVersionId,
            operationKey = bindings.Request.OperationKey,
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
            _ = await MarkUnknownAsync(activity.WorkId, CancellationToken.None);
            throw new HttpRequestException(
                "Generic Harness deployment dispatch acknowledgement was lost; no retry will be issued.",
                exception);
        }
        using (response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.Created)
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new HttpRequestException(
                    $"Generic Harness deployment execution did not return 201 Created (HTTP {(int)response.StatusCode}).",
                    inner: null,
                    response.StatusCode);
            }
            using var harnessResponse = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = harnessResponse.RootElement;
            var execution = GetObject(root, "execution");
            var executionId = GetString(execution, "id");
            if (!Guid.TryParse(executionId, out _))
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new InvalidDataException("Generic Harness returned an invalid deployment execution identifier.");
            }
            activity = await activityLedger.RecordHarnessExecutionAsync(
                activity.WorkId,
                executionId,
                bindings.Request.ProviderIdentity,
                bindings.Request.ApiDescriptionVersionId,
                bindings.Request.OperationKey,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            var providerResponse = GetObject(root, "providerResponse");
            var admissionEnvelope = JsonSerializer.Serialize(new
            {
                statusCode = GetInt32(providerResponse, "statusCode"),
                contentType = GetNullableString(providerResponse, "contentType"),
                body = GetString(providerResponse, "body"),
                truncated = GetBoolean(providerResponse, "truncated")
            }, JsonOptions);
            activity = await activityLedger.RecordOriginalResponseAsync(
                activity.WorkId, admissionEnvelope, _timeProvider.GetUtcNow(), cancellationToken);
            if (GetInt32(providerResponse, "statusCode") is < 200 or >= 300 ||
                GetBoolean(providerResponse, "truncated"))
            {
                return await MarkUnknownAsync(activity.WorkId, cancellationToken);
            }
            return activity;
        }
    }

    private async Task<AsteriaActivitySnapshot> ReconcileByKeyAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        CancellationToken cancellationToken)
    {
        var admissionDeploymentId = ReadAdmissionDeploymentId(activity.OriginalResponseJson);
        var lookupBinding = admissionDeploymentId is null
            ? bindings.StatusByIdempotencyKey
            : bindings.StatusById;
        var pathParameters = admissionDeploymentId is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["idempotencyKey"] = activity.IdempotencyKey
            }
            : new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deploymentId"] = admissionDeploymentId
            };
        using var request = JsonContent.Create(new
        {
            providerId = lookupBinding.ProviderId,
            apiDescriptionVersionId = lookupBinding.ApiDescriptionVersionId,
            operationKey = lookupBinding.OperationKey,
            maxAttempts = 1,
            pathParameters
        }, options: JsonOptions);
        HttpResponseMessage response;
        try
        {
            response = await harnessClient.PostAsync("executions", request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            _ = await MarkUnknownAsync(activity.WorkId, CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            _ = await MarkUnknownAsync(activity.WorkId, CancellationToken.None);
            throw;
        }

        using (response)
        {
            if (response.StatusCode != System.Net.HttpStatusCode.Created)
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new HttpRequestException(
                    $"Generic Harness deployment status lookup did not return 201 Created (HTTP {(int)response.StatusCode}).",
                    inner: null,
                    response.StatusCode);
            }
            using var harnessResponse = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var executionId = GetString(GetObject(harnessResponse.RootElement, "execution"), "id");
            if (!Guid.TryParse(executionId, out _))
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new InvalidDataException("Generic Harness returned an invalid deployment status execution identifier.");
            }
            var providerResponse = GetObject(harnessResponse.RootElement, "providerResponse");
            var providerStatusCode = GetInt32(providerResponse, "statusCode");
            var body = GetString(providerResponse, "body");
            var truncated = GetBoolean(providerResponse, "truncated");
            var archivedLookup = JsonSerializer.Serialize(new
            {
                lookupKind = admissionDeploymentId is null ? "ByIdempotencyKey" : "ById",
                providerId = lookupBinding.ProviderId,
                operationKey = lookupBinding.OperationKey,
                providerIdentity = lookupBinding.ProviderIdentity,
                apiDescriptionVersionId = lookupBinding.ApiDescriptionVersionId,
                executionId,
                statusCode = providerStatusCode,
                body,
                truncated
            }, JsonOptions);
            var lookupDigest = AsteriaActivityLedger.Fingerprint(archivedLookup);
            await activityLedger.RecordProviderObservationAsync(
                activity.WorkId,
                "deployment-status-" + lookupDigest[7..],
                "DeploymentStatusLookup",
                archivedLookup,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            if (providerStatusCode != (int)System.Net.HttpStatusCode.OK || truncated)
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new InvalidOperationException(
                    $"Deployment provider status lookup did not establish an authoritative result (HTTP {providerStatusCode}).");
            }

            JsonDocument statusDocument;
            try
            {
                statusDocument = JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                activity = await ResolveProviderObservationAsync(activity, archivedLookup, cancellationToken);
                _ = await activityLedger.MarkRefusedAsync(
                    activity.WorkId,
                    $"Deployment provider status response is malformed: {exception.Message}",
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
                throw new InvalidDataException("Deployment provider returned malformed status JSON.", exception);
            }
            using (statusDocument)
            {
                var status = statusDocument.RootElement;
                try
                {
                    ValidateStatusBinding(release, activity, status);
                }
                catch (InvalidDataException exception)
                {
                    activity = await ResolveProviderObservationAsync(activity, archivedLookup, cancellationToken);
                    _ = await activityLedger.MarkRefusedAsync(
                        activity.WorkId,
                        exception.Message,
                        _timeProvider.GetUtcNow(),
                        cancellationToken);
                    throw;
                }
                activity = await ResolveProviderObservationAsync(activity, archivedLookup, cancellationToken);
                var providerState = GetString(status, "status");
                var outcome = OutcomeForStatus(providerState);
                var evidenceDigest = AsteriaActivityLedger.Fingerprint(body);
                return await activityLedger.RecordValidatedOutcomeAsync(
                    activity.WorkId,
                    outcome,
                    evidenceDigest,
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
            }
        }
    }

    private async Task<AsteriaActivitySnapshot> ResolveProviderObservationAsync(
        AsteriaActivitySnapshot activity,
        string archivedLookup,
        CancellationToken cancellationToken)
    {
        if (activity.Status is not ("ResponsePersisted" or "ObservationSubmitted" or "Accepted"))
        {
            activity = await activityLedger.ResolveOutcomeUnknownAsync(
                activity.WorkId,
                archivedLookup,
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }
        return activity;
    }

    private async Task<AsteriaDeploymentResult> SubmitResultAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        CancellationToken cancellationToken)
    {
        using var statusResponse = await GetValidatedStatusResponseAsync(activity, cancellationToken);
        var status = statusResponse.RootElement;
        ValidateStatusBinding(release, activity, status);
        var deploymentId = GetString(status, "deploymentId");
        var outcome = activity.ValidatedOutcome!;
        var parentOperationId = StableId(activity.WorkId, "deployment-result-parent-operation");
        var concernOperationId = StableId(activity.WorkId, "deployment-result-concern-operation");
        var parentEvidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["deploymentWorkId"] = activity.WorkId,
            ["expectedOperationId"] = parentOperationId,
            ["deploymentId"] = deploymentId,
            ["result"] = outcome,
            ["evidenceDigest"] = activity.ValidatedEvidenceDigest
        };
        var parentInstance = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance is missing.");
        var existingParentOperation = (await stateMachine.ListExpectedOperationsAsync(
                release.WorkflowInstanceId, cancellationToken))
            .SingleOrDefault(item => item.OperationId == parentOperationId);
        var resultParentRevision = existingParentOperation?.ExpectedRevision ?? parentInstance.Revision;
        var expectedParent = outcome switch
        {
            "Succeeded" => "PostDeploymentVerification",
            "Failed" => "DeploymentFailed",
            _ => "DeploymentUnknown"
        };
        _ = await stateMachine.SubmitParentLifecycleObservationAsync(
            release,
            parentOperationId,
            StableId(activity.WorkId, "deployment-result-parent-message"),
            StableId(activity.WorkId, "deployment-result-parent-observation"),
            "AsteriaDeploymentResultConfirmed",
            SourceIdentity,
            resultParentRevision,
            ResultEvidenceFields,
            parentEvidence,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["deploymentWorkId"] = activity.WorkId,
                ["deploymentId"] = deploymentId
            },
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["deploymentResult"] = outcome
            },
            "AsteriaDeploymentResultObserved",
            "DeploymentInProgress",
            expectedParent,
            cancellationToken);

        var concernEvidence = new Dictionary<string, object?>(parentEvidence, StringComparer.Ordinal)
        {
            ["expectedOperationId"] = concernOperationId
        };
        var accepted = await stateMachine.SubmitConcernObservationAsync(
            release,
            "Deployment",
            concernOperationId,
            StableId(activity.WorkId, "deployment-result-concern-message"),
            StableId(activity.WorkId, "deployment-result-concern-observation"),
            "AsteriaDeploymentResult",
            SourceIdentity,
            ResultEvidenceFields,
            concernEvidence,
            "deploymentResult",
            outcome,
            outcome,
            cancellationToken);
        var instance = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria workflow instance disappeared after deployment result.");
        if (instance.CurrentState != expectedParent)
        {
            throw new InvalidDataException(
                $"Accepted Asteria deployment result established '{instance.CurrentState}', not '{expectedParent}'.");
        }
        activity = await activityLedger.MarkObservationSubmittedAsync(
            activity.WorkId, accepted.ReceiptId, _timeProvider.GetUtcNow(), cancellationToken);
        activity = await activityLedger.MarkAcceptedAsync(
            activity.WorkId,
            accepted.ReceiptId,
            accepted.AttemptId,
            accepted.HistoryId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        return new AsteriaDeploymentResult(activity, deploymentId, outcome);
    }

    private async Task<JsonDocument> GetValidatedStatusResponseAsync(
        AsteriaActivitySnapshot activity,
        CancellationToken cancellationToken)
    {
        var observations = await activityLedger.ListProviderObservationsAsync(
            activity.WorkId, cancellationToken);
        AsteriaActivityProviderObservation? observation = null;
        foreach (var candidate in observations.Where(item => item.ObservationKind == "DeploymentStatusLookup"))
        {
            using var candidateArchive = JsonDocument.Parse(candidate.ResponseJson);
            var archiveRoot = candidateArchive.RootElement;
            var lookupKind = GetString(archiveRoot, "lookupKind");
            var expectedBinding = lookupKind switch
            {
                "ById" => bindings.StatusById,
                "ByIdempotencyKey" => bindings.StatusByIdempotencyKey,
                _ => throw new InvalidDataException("Archived deployment status lookup has an unsupported operation kind.")
            };
            if (GetString(archiveRoot, "providerId") != expectedBinding.ProviderId ||
                GetString(archiveRoot, "providerIdentity") != expectedBinding.ProviderIdentity ||
                GetString(archiveRoot, "apiDescriptionVersionId") != expectedBinding.ApiDescriptionVersionId ||
                GetString(archiveRoot, "operationKey") != expectedBinding.OperationKey)
            {
                throw new InvalidDataException("Archived deployment status lookup has a conflicting Harness binding.");
            }
            var candidateBody = GetString(archiveRoot, "body");
            if (AsteriaActivityLedger.Fingerprint(candidateBody) == activity.ValidatedEvidenceDigest)
            {
                observation = candidate;
                break;
            }
        }
        if (observation is null)
        {
            throw new InvalidDataException("Accepted deployment activity has no persisted status lookup response.");
        }
        using var archive = JsonDocument.Parse(observation.ResponseJson);
        return JsonDocument.Parse(GetString(archive.RootElement, "body"));
    }

    private async Task<AsteriaActivitySnapshot> MarkUnknownAsync(
        string workId,
        CancellationToken cancellationToken)
    {
        var current = await activityLedger.GetAsync(workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == "OutcomeUnknown")
        {
            return current;
        }
        return await activityLedger.MarkOutcomeUnknownAsync(
            workId, UnknownDetail, _timeProvider.GetUtcNow(), cancellationToken);
    }

    private async Task EnsureStillCurrentGenerationAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken)
    {
        var current = await releaseRegistry.GetReleaseAsync(release.ReleaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{release.ReleaseId}' disappeared.");
        EnsureCurrentAcceptedAssessment(current);
        if (current.AssessmentGeneration != release.AssessmentGeneration ||
            current.MaterialDigest != release.MaterialDigest ||
            current.WorkflowInstanceId != release.WorkflowInstanceId)
        {
            throw new InvalidOperationException("Asteria deployment generation changed before external dispatch.");
        }
    }

    private async Task<AsteriaActivitySnapshot> EnsureReadinessAccepted(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken)
    {
        var readinessWorkId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0Readiness", "work");
        var readiness = await activityLedger.GetAsync(readinessWorkId, cancellationToken);
        if (readiness is null ||
            readiness.Status != "Accepted" ||
            readiness.ValidatedOutcome != "Ready" ||
            readiness.ReleaseId != release.ReleaseId ||
            readiness.Generation != release.AssessmentGeneration ||
            readiness.MaterialDigest != release.MaterialDigest ||
            readiness.WorkflowInstanceId != release.WorkflowInstanceId ||
            readiness.ObservationReceiptId is null ||
            readiness.AcceptedHistoryId is null)
        {
            throw new InvalidOperationException("Asteria deployment requires accepted current-generation readiness evidence.");
        }
        return readiness;
    }

    private void ValidateActivityBinding(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity)
    {
        if (activity.ReleaseId != release.ReleaseId ||
            activity.Generation != release.AssessmentGeneration ||
            activity.MaterialDigest != release.MaterialDigest ||
            activity.WorkflowInstanceId != release.WorkflowInstanceId ||
            activity.DefinitionId != release.DefinitionId ||
            activity.DefinitionVersion != release.DefinitionVersion ||
            activity.DefinitionDigest != _definitionDigest ||
            activity.ConcernId != "Deployment" ||
            activity.ProviderIdentity != bindings.Request.ProviderIdentity ||
            activity.ApiDescriptionVersionId != bindings.Request.ApiDescriptionVersionId ||
            activity.OperationKey != bindings.Request.OperationKey)
        {
            throw new InvalidDataException("Persisted Asteria deployment activity has a conflicting generation or provider binding.");
        }
    }

    private static void ValidateBinding(AsteriaHarnessOperationBinding value)
    {
        if (string.IsNullOrWhiteSpace(value.ProviderIdentity) ||
            !Guid.TryParse(value.ProviderId, out _) ||
            !Guid.TryParse(value.ApiDescriptionVersionId, out _) ||
            string.IsNullOrWhiteSpace(value.OperationKey))
        {
            throw new InvalidDataException("Asteria deployment Generic Harness binding is incomplete or invalid.");
        }
    }

    private void ValidateStatusBinding(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        JsonElement status)
    {
        if (GetString(status, "deploymentId").Length == 0 ||
            GetString(status, "idempotencyKey") != activity.IdempotencyKey ||
            GetString(status, "releaseId") != release.ReleaseId ||
            GetInt32(status, "assessmentGeneration") != release.AssessmentGeneration ||
            GetString(status, "materialDigest") != release.MaterialDigest ||
            GetString(status, "product") != release.Material.Product ||
            GetString(status, "targetVersion") != release.Material.TargetVersion ||
            GetString(status, "environment") != release.Material.Environment ||
            GetString(status, "status") is not ("Pending" or "Running" or "Succeeded" or "Failed"))
        {
            throw new InvalidDataException("Deployment provider status does not match the exact persisted Asteria request.");
        }
    }

    private static string OutcomeForStatus(string status) =>
        status switch
        {
            "Succeeded" => "Succeeded",
            "Failed" => "Failed",
            "Pending" or "Running" => "Indeterminate",
            _ => throw new InvalidDataException($"Unsupported deployment provider status '{status}'.")
        };

    private string? ReadAdmissionDeploymentId(string? originalResponseJson)
    {
        if (originalResponseJson is null)
        {
            return null;
        }
        try
        {
            using var envelope = JsonDocument.Parse(originalResponseJson);
            var body = GetString(envelope.RootElement, "body");
            using var response = JsonDocument.Parse(body);
            return response.RootElement.TryGetProperty("deploymentId", out var deploymentId) &&
                   deploymentId.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(deploymentId.GetString())
                ? deploymentId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void EnsureCurrentAcceptedAssessment(AsteriaReleaseSnapshot release)
    {
        if (release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.OriginalResponseJson is null ||
            release.Work.Generation != release.AssessmentGeneration ||
            release.Work.MaterialDigest != release.MaterialDigest ||
            release.Work.WorkflowInstanceId != release.WorkflowInstanceId ||
            release.RegistryState != "Operational")
        {
            throw new InvalidOperationException("Deployment requires the current accepted Asteria assessment generation.");
        }
    }

    private static string StableId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static JsonElement GetObject(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind == JsonValueKind.Object
            ? propertyValue
            : throw new InvalidDataException($"Provider response field '{property}' must be an object.");

    private static string GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind == JsonValueKind.String
            ? propertyValue.GetString()!
            : throw new InvalidDataException($"Provider response field '{property}' must be a string.");

    private static int GetInt32(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Provider response field '{property}' must be an integer.");

    private static bool GetBoolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var propertyValue) &&
        propertyValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? propertyValue.GetBoolean()
            : throw new InvalidDataException($"Provider response field '{property}' must be a boolean.");

    private static string? GetNullableString(JsonElement value, string property) =>
        !value.TryGetProperty(property, out var propertyValue) ||
        propertyValue.ValueKind == JsonValueKind.Null
            ? null
            : propertyValue.ValueKind == JsonValueKind.String
                ? propertyValue.GetString()
                : throw new InvalidDataException($"Provider response field '{property}' must be a string or null.");
}
