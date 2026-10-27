using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaHealthVerificationResult(
    AsteriaActivitySnapshot Activity,
    string DeploymentId,
    string Outcome);

public sealed class AsteriaHealthVerificationCoordinator(
    AsteriaReleaseRegistry releaseRegistry,
    AsteriaActivityLedger activityLedger,
    AsteriaStateMachineClient stateMachine,
    HttpClient harnessClient,
    string definitionJson,
    AsteriaHarnessOperationBinding binding,
    TimeProvider? timeProvider = null)
{
    private const string SourceIdentity = "AsteriaHealthVerificationCoordinator";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] StartEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "deploymentId",
        "healthWorkId",
        "expectedOperationId"
    ];
    private static readonly string[] ResultEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "deploymentId",
        "healthWorkId",
        "expectedOperationId",
        "result",
        "evidenceDigest"
    ];
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly string _definitionDigest = AsteriaActivityLedger.Fingerprint(definitionJson);

    public async Task<AsteriaHealthVerificationResult> ExecuteAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var release = await releaseRegistry.GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
        EnsureCurrentAcceptedAssessment(release);
        if (release.DefinitionVersion != "5.0")
        {
            throw new InvalidOperationException("Post-deployment health verification requires Asteria definition V5.");
        }

        var deploymentWorkId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0Deployment", "work");
        var deployment = await activityLedger.GetAsync(deploymentWorkId, cancellationToken)
            ?? throw new InvalidOperationException("Asteria health verification requires a persisted deployment activity.");
        if (deployment.Status != "Accepted" || deployment.ValidatedOutcome != "Succeeded")
        {
            throw new InvalidOperationException("Asteria health verification requires an accepted successful deployment.");
        }
        var deploymentId = await ReadAcceptedDeploymentIdAsync(deployment, cancellationToken);

        var workId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0HealthVerification", "work");
        var activity = await activityLedger.GetAsync(workId, cancellationToken);
        var instance = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance is missing.");
        var concerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue("HealthVerification", out var healthConcern) ||
            !concerns.TryGetValue("CancellationIntent", out var cancellation))
        {
            throw new InvalidDataException("Asteria V5 workflow is missing health or cancellation concerns.");
        }
        if (activity is null)
        {
            if (instance.CurrentState != "PostDeploymentVerification" ||
                cancellation.CurrentState != "NotRequested" ||
                healthConcern.CurrentState != "NotRequested")
            {
                throw new InvalidOperationException(
                    "Asteria health verification requires a current, uncancelled confirmed deployment.");
            }
            activity = await ReserveAsync(
                release, deploymentId, instance.Revision, healthConcern.Revision, cancellationToken);
        }
        else
        {
            ValidateActivityBinding(release, activity, deploymentId);
            if (activity.Status is "Refused" or "Conflicting")
            {
                throw new InvalidOperationException(
                    $"Asteria health activity '{workId}' is '{activity.Status}' and cannot be changed.");
            }
            if (instance.CurrentState is not (
                    "PostDeploymentVerification" or "Completed" or "HealthFailed" or "HealthUnresolved"))
            {
                throw new InvalidOperationException(
                    $"Asteria workflow state '{instance.CurrentState}' cannot resume health verification.");
            }
        }

        if (activity.Status == "Accepted")
        {
            return new AsteriaHealthVerificationResult(activity, deploymentId, activity.ValidatedOutcome!);
        }
        if (activity.Status == "OutcomeUnknown")
        {
            throw new InvalidOperationException(
                $"Asteria health activity '{workId}' has an unknown external result and requires reconciliation.");
        }

        if (activity.Status == "Reserved")
        {
            var operation = await stateMachine.EnsureExpectedConcernOperationAsync(
                release,
                activity.ExpectedOperationId,
                "AsteriaHealthVerificationStarted",
                SourceIdentity,
                StartEvidenceFields,
                cancellationToken);
            if (operation.ExpectedRevision != activity.ParentRevision)
            {
                throw new InvalidDataException("Asteria health activity parent revision differs from its expected-operation binding.");
            }
            activity = await activityLedger.MarkExpectedOperationRegisteredAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }
        if (activity.Status == "OperationRegistered")
        {
            var startEvidence = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["deploymentId"] = deploymentId,
                ["healthWorkId"] = activity.WorkId,
                ["expectedOperationId"] = activity.ExpectedOperationId
            };
            _ = await stateMachine.SubmitLifecycleStartConcernObservationAsync(
                release,
                "HealthVerification",
                activity.ExpectedOperationId,
                StableId(activity.WorkId, "start-message"),
                StableId(activity.WorkId, "start-observation"),
                "AsteriaHealthVerificationStarted",
                SourceIdentity,
                activity.ParentRevision,
                StartEvidenceFields,
                startEvidence,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["releaseId"] = release.ReleaseId,
                    ["assessmentGeneration"] = release.AssessmentGeneration,
                    ["materialDigest"] = release.MaterialDigest,
                    ["deploymentId"] = deploymentId,
                    ["healthWorkId"] = activity.WorkId
                },
                "PostDeploymentVerification",
                "PostDeploymentVerification",
                "Requested",
                cancellationToken);
            activity = await activityLedger.MarkDispatchPendingAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }
        if (activity.Status == "DispatchPending")
        {
            await EnsureStillCurrentGenerationAsync(release, cancellationToken);
            activity = await DispatchAsync(release, activity, deploymentId, cancellationToken);
        }
        else if (activity.Status == "Dispatched")
        {
            activity = await MarkUnknownAsync(activity.WorkId, cancellationToken);
            throw new InvalidOperationException(
                $"Asteria health activity '{activity.WorkId}' has an ambiguous Harness result; it will not be resubmitted.");
        }
        else if (activity.Status == "ResponsePersisted")
        {
            activity = await ValidateArchivedResponseAsync(release, activity, deploymentId, cancellationToken);
        }
        else if (activity.Status == "ObservationSubmitted")
        {
            return await SubmitResultAsync(release, activity, deploymentId, cancellationToken);
        }

        if (activity.Status != "ResponsePersisted" ||
            activity.ValidatedOutcome is null ||
            activity.ValidatedEvidenceDigest is null)
        {
            throw new InvalidDataException(
                $"Asteria health activity '{activity.WorkId}' has unsupported status '{activity.Status}'.");
        }
        return await SubmitResultAsync(release, activity, deploymentId, cancellationToken);
    }

    private async Task<AsteriaActivitySnapshot> ReserveAsync(
        AsteriaReleaseSnapshot release,
        string deploymentId,
        long parentRevision,
        long concernRevision,
        CancellationToken cancellationToken)
    {
        var workId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0HealthVerification", "work");
        var idempotencyKey = StableId(workId, "health-idempotency");
        var requestJson = JsonSerializer.Serialize(new
        {
            deploymentId,
            targetVersion = release.Material.TargetVersion,
            environment = release.Material.Environment,
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            workflowInstanceId = release.WorkflowInstanceId,
            healthWorkId = workId
        }, JsonOptions);
        return await activityLedger.ReserveAsync(
            new AsteriaActivityReservation(
                workId,
                StableId(workId, "activity"),
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                release.WorkflowInstanceId,
                release.DefinitionId,
                release.DefinitionVersion,
                _definitionDigest,
                "HealthVerification",
                "HealthVerification",
                StableId(workId, "attempt"),
                StableId(workId, "start-operation"),
                parentRevision,
                concernRevision,
                StableId(workId, "submission"),
                idempotencyKey,
                requestJson,
                binding.ProviderIdentity,
                binding.ApiDescriptionVersionId,
                binding.OperationKey,
                null,
                null,
                null),
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task<AsteriaActivitySnapshot> DispatchAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        using var requestBody = JsonDocument.Parse(activity.OriginalRequestJson);
        using var request = JsonContent.Create(new
        {
            providerId = binding.ProviderId,
            apiDescriptionVersionId = binding.ApiDescriptionVersionId,
            operationKey = binding.OperationKey,
            idempotencyKey = activity.IdempotencyKey,
            maxAttempts = 1,
            queryParameters = new
            {
                deploymentId,
                targetVersion = String(requestBody.RootElement, "targetVersion"),
                environment = String(requestBody.RootElement, "environment")
            }
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
                "Generic Harness health execution acknowledgement was lost; no retry will be issued.",
                exception);
        }
        catch (OperationCanceledException)
        {
            _ = await MarkUnknownAsync(activity.WorkId, CancellationToken.None);
            throw;
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.Created)
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new HttpRequestException(
                    $"Generic Harness health execution did not return 201 Created (HTTP {(int)response.StatusCode}).",
                    inner: null,
                    response.StatusCode);
            }
            using var harnessResponse = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = harnessResponse.RootElement;
            var executionId = String(GetObject(root, "execution"), "id");
            if (!Guid.TryParse(executionId, out _))
            {
                _ = await MarkUnknownAsync(activity.WorkId, cancellationToken);
                throw new InvalidDataException("Generic Harness returned an invalid health execution identifier.");
            }
            activity = await activityLedger.RecordHarnessExecutionAsync(
                activity.WorkId,
                executionId,
                binding.ProviderIdentity,
                binding.ApiDescriptionVersionId,
                binding.OperationKey,
                _timeProvider.GetUtcNow(),
                cancellationToken);
            var providerResponse = GetObject(root, "providerResponse");
            var statusCode = Integer(providerResponse, "statusCode");
            var body = String(providerResponse, "body");
            var archive = JsonSerializer.Serialize(new
            {
                statusCode,
                contentType = NullableString(providerResponse, "contentType"),
                body,
                truncated = Boolean(providerResponse, "truncated")
            }, JsonOptions);
            activity = await activityLedger.RecordOriginalResponseAsync(
                activity.WorkId, archive, _timeProvider.GetUtcNow(), cancellationToken);
            if (statusCode is < 200 or >= 300 || Boolean(providerResponse, "truncated"))
            {
                return await MarkUnknownAsync(activity.WorkId, cancellationToken);
            }

            return await ValidateArchivedResponseAsync(release, activity, deploymentId, cancellationToken);
        }
    }

    private async Task<AsteriaActivitySnapshot> ValidateArchivedResponseAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        using var archive = JsonDocument.Parse(activity.OriginalResponseJson
            ?? throw new InvalidDataException("Asteria health result has no archived Harness response."));
        var root = archive.RootElement;
        if (Integer(root, "statusCode") is < 200 or >= 300 ||
            Boolean(root, "truncated"))
        {
            return await MarkUnknownAsync(activity.WorkId, cancellationToken);
        }

        using var result = JsonDocument.Parse(String(root, "body"));
        var value = result.RootElement;
        var outcome = String(value, "result");
        var checkedAt = String(value, "checkedAt");
        if (String(value, "deploymentId") != deploymentId ||
            String(value, "observedVersion") != release.Material.TargetVersion ||
            String(value, "environment") != release.Material.Environment ||
            outcome is not ("Passed" or "Failed" or "Indeterminate") ||
            !DateTimeOffset.TryParse(checkedAt, out _) ||
            string.IsNullOrWhiteSpace(String(value, "evidenceId")))
        {
            _ = await activityLedger.MarkRefusedAsync(
                activity.WorkId,
                "Health provider response does not match the confirmed deployment, target version, environment, or result contract.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new InvalidDataException("Asteria health provider response is not valid evidence for this deployment.");
        }
        return await activityLedger.RecordValidatedOutcomeAsync(
            activity.WorkId,
            outcome,
            AsteriaActivityLedger.Fingerprint(String(root, "body")),
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task<AsteriaHealthVerificationResult> SubmitResultAsync(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        string deploymentId,
        CancellationToken cancellationToken)
    {
        var outcome = activity.ValidatedOutcome!;
        var parentOperationId = StableId(activity.WorkId, "result-parent-operation");
        var concernOperationId = StableId(activity.WorkId, "result-concern-operation");
        var parent = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance is missing.");
        var concerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
        if (!concerns.TryGetValue("CancellationIntent", out var cancellation))
        {
            throw new InvalidDataException("Asteria workflow has no cancellation-intent concern.");
        }
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["deploymentId"] = deploymentId,
            ["healthWorkId"] = activity.WorkId,
            ["expectedOperationId"] = parentOperationId,
            ["result"] = outcome,
            ["evidenceDigest"] = activity.ValidatedEvidenceDigest
        };
        var expectedParent = outcome switch
        {
            "Passed" => "Completed",
            "Failed" => "HealthFailed",
            _ => "HealthUnresolved"
        };

        var concernEvidence = new Dictionary<string, object?>(evidence, StringComparer.Ordinal)
        {
            ["expectedOperationId"] = concernOperationId
        };
        var accepted = await stateMachine.SubmitConcernObservationAsync(
            release,
            "HealthVerification",
            concernOperationId,
            StableId(activity.WorkId, "result-concern-message"),
            StableId(activity.WorkId, "result-concern-observation"),
            "AsteriaHealthVerificationResult",
            SourceIdentity,
            ResultEvidenceFields,
            concernEvidence,
            "healthResult",
            outcome,
            outcome,
            cancellationToken);

        if (outcome != "Passed" || cancellation.CurrentState == "NotRequested")
        {
            var existingParentOperation = (await stateMachine.ListExpectedOperationsAsync(
                    release.WorkflowInstanceId, cancellationToken))
                .SingleOrDefault(item => item.OperationId == parentOperationId);
            _ = await stateMachine.SubmitParentLifecycleObservationAsync(
                release,
                parentOperationId,
                StableId(activity.WorkId, "result-parent-message"),
                StableId(activity.WorkId, "result-parent-observation"),
                "AsteriaHealthResultConfirmed",
                SourceIdentity,
                existingParentOperation?.ExpectedRevision ?? parent.Revision,
                ResultEvidenceFields,
                evidence,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["releaseId"] = release.ReleaseId,
                    ["assessmentGeneration"] = release.AssessmentGeneration,
                    ["materialDigest"] = release.MaterialDigest,
                    ["deploymentId"] = deploymentId,
                    ["healthWorkId"] = activity.WorkId
                },
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["healthResult"] = outcome
                },
                "AsteriaHealthVerificationResultObserved",
                "PostDeploymentVerification",
                expectedParent,
                cancellationToken);
        }

        var current = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance disappeared after health verification.");
        if (outcome != "Passed" || cancellation.CurrentState == "NotRequested")
        {
            if (current.CurrentState != expectedParent)
            {
                throw new InvalidDataException(
                    $"Accepted Asteria health result established '{current.CurrentState}', not '{expectedParent}'.");
            }
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
        return new AsteriaHealthVerificationResult(activity, deploymentId, outcome);
    }

    private async Task<string> ReadAcceptedDeploymentIdAsync(
        AsteriaActivitySnapshot deployment,
        CancellationToken cancellationToken)
    {
        foreach (var observation in await activityLedger.ListProviderObservationsAsync(
                     deployment.WorkId, cancellationToken))
        {
            if (observation.ObservationKind != "DeploymentStatusLookup")
            {
                continue;
            }
            using var lookup = JsonDocument.Parse(observation.ResponseJson);
            var body = String(lookup.RootElement, "body");
            if (AsteriaActivityLedger.Fingerprint(body) == deployment.ValidatedEvidenceDigest)
            {
                using var response = JsonDocument.Parse(body);
                return String(response.RootElement, "deploymentId");
            }
        }
        throw new InvalidDataException("Accepted deployment activity has no archived, validated provider status response.");
    }

    private void ValidateActivityBinding(
        AsteriaReleaseSnapshot release,
        AsteriaActivitySnapshot activity,
        string deploymentId)
    {
        using var request = JsonDocument.Parse(activity.OriginalRequestJson);
        if (activity.ReleaseId != release.ReleaseId ||
            activity.Generation != release.AssessmentGeneration ||
            activity.MaterialDigest != release.MaterialDigest ||
            activity.WorkflowInstanceId != release.WorkflowInstanceId ||
            activity.DefinitionId != release.DefinitionId ||
            activity.DefinitionVersion != release.DefinitionVersion ||
            activity.DefinitionDigest != _definitionDigest ||
            activity.ConcernId != "HealthVerification" ||
            activity.ProviderIdentity != binding.ProviderIdentity ||
            activity.ApiDescriptionVersionId != binding.ApiDescriptionVersionId ||
            activity.OperationKey != binding.OperationKey ||
            String(request.RootElement, "deploymentId") != deploymentId)
        {
            throw new InvalidDataException("Persisted Asteria health activity has a conflicting deployment, generation, or provider binding.");
        }
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
            throw new InvalidOperationException("Asteria generation changed before health verification.");
        }
    }

    private async Task<AsteriaActivitySnapshot> MarkUnknownAsync(
        string workId,
        CancellationToken cancellationToken)
    {
        var current = await activityLedger.GetAsync(workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        return current.Status == "OutcomeUnknown"
            ? current
            : await activityLedger.MarkOutcomeUnknownAsync(
                workId,
                "Health execution may have completed but no authoritative, fully bound result was established.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
    }

    private static void EnsureCurrentAcceptedAssessment(AsteriaReleaseSnapshot release)
    {
        if (release.RegistryState != "Operational" ||
            release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.OriginalResponseJson is null ||
            release.Work.Generation != release.AssessmentGeneration ||
            release.Work.MaterialDigest != release.MaterialDigest ||
            release.Work.WorkflowInstanceId != release.WorkflowInstanceId)
        {
            throw new InvalidOperationException("Asteria health verification requires the current accepted assessment.");
        }
        _ = AsteriaDecisionApiClient.ValidateResponse(release.Work.OriginalResponseJson, release);
    }

    private static string StableId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static JsonElement GetObject(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Object
            ? item
            : throw new InvalidDataException($"Harness response is missing object '{property}'.");

    private static string String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!
            : throw new InvalidDataException($"Health response is missing string '{property}'.");

    private static string? NullableString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()
            : null;

    private static int Integer(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.TryGetInt32(out var result)
            ? result
            : throw new InvalidDataException($"Harness response is missing integer '{property}'.");

    private static bool Boolean(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) &&
        item.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? item.GetBoolean()
            : throw new InvalidDataException($"Harness response is missing boolean '{property}'.");
}
