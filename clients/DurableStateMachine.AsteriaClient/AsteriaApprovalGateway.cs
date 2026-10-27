using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaApprovalCredential(string PrincipalId, string Role, string BearerToken);

public sealed record AsteriaAuthenticatedPrincipal(
    string PrincipalId,
    string Role,
    string AuthorityRegistryVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AsteriaApprovalSubmission(
    string ReleaseId,
    int AssessmentGeneration,
    string MaterialDigest,
    string ConcernId,
    string SubmissionId,
    string IdempotencyKey,
    string Decision,
    string EvidenceReference);

public sealed record AsteriaApprovalResponse(
    AsteriaApprovalDecision Decision,
    AsteriaActivitySnapshot Activity);

public sealed class AsteriaApprovalCredentialRegistry
{
    private readonly string _registryVersion;
    private readonly IReadOnlyList<(string PrincipalId, string Role, byte[] TokenDigest)> _credentials;

    public AsteriaApprovalCredentialRegistry(
        string registryVersion,
        IReadOnlyList<AsteriaApprovalCredential> credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryVersion);
        ArgumentNullException.ThrowIfNull(credentials);
        var supportedRoles = new HashSet<string>(
            ["ReleaseReviewer", "EmergencyReleaseAuthoriser"],
            StringComparer.Ordinal);
        if (credentials.Count < 2 ||
            credentials.Any(item =>
                string.IsNullOrWhiteSpace(item.PrincipalId) ||
                !supportedRoles.Contains(item.Role) ||
                string.IsNullOrWhiteSpace(item.BearerToken) ||
                item.BearerToken.Length < 32) ||
            credentials.Select(item => item.PrincipalId).Distinct(StringComparer.Ordinal).Count() != credentials.Count ||
            credentials.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() != 2 ||
            credentials.Select(item => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.BearerToken))))
                .Distinct(StringComparer.Ordinal).Count() != credentials.Count)
        {
            throw new ArgumentException(
                "Approval registry requires two distinct principals, one for each supported role, with unique out-of-band credentials.",
                nameof(credentials));
        }
        _registryVersion = registryVersion;
        _credentials = credentials
            .Select(item => (
                item.PrincipalId,
                item.Role,
                SHA256.HashData(Encoding.UTF8.GetBytes(item.BearerToken))))
            .ToArray();
    }

    public bool TryAuthenticate(string bearerToken, out AsteriaAuthenticatedPrincipal? principal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearerToken);
        var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));
        AsteriaAuthenticatedPrincipal? match = null;
        foreach (var credential in _credentials)
        {
            if (CryptographicOperations.FixedTimeEquals(suppliedDigest, credential.TokenDigest))
            {
                match = new AsteriaAuthenticatedPrincipal(
                    credential.PrincipalId, credential.Role, _registryVersion);
            }
        }
        principal = match;
        return match is not null;
    }

    public static AsteriaApprovalCredentialRegistry FromJson(
        string registryVersion,
        string credentialsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsJson);
        var credentials = JsonSerializer.Deserialize<AsteriaApprovalCredential[]>(
            credentialsJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Asteria approval credential configuration must be a JSON array.");
        return new AsteriaApprovalCredentialRegistry(registryVersion, credentials);
    }
}

public sealed class AsteriaApprovalGateway(
    AsteriaReleaseRegistry releaseRegistry,
    AsteriaActivityLedger activityLedger,
    AsteriaApprovalRegistry approvalRegistry,
    AsteriaApprovalCredentialRegistry credentials,
    AsteriaStateMachineClient stateMachine,
    IReadOnlyDictionary<string, string> definitionJsonByVersion,
    TimeProvider? timeProvider = null)
{
    private const string SourceIdentity = "AsteriaApprovalGateway";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyDictionary<string, string> _definitionDigests =
        definitionJsonByVersion.ToDictionary(
            item => item.Key,
            item => AsteriaActivityLedger.Fingerprint(item.Value),
            StringComparer.Ordinal);

    public void MapEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/approvals", (Func<HttpContext, Task<IResult>>)HandleSubmissionAsync)
            .WithName("SubmitAsteriaApproval")
            .WithSummary("Record a synthetic authenticated Asteria approval decision.");
    }

    public async Task<AsteriaApprovalResponse> SubmitAsync(
        AsteriaAuthenticatedPrincipal principal,
        AsteriaApprovalSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(submission);
        ValidateSubmission(submission);
        var requestFingerprint = AsteriaActivityLedger.Fingerprint(
            JsonSerializer.Serialize(new
            {
                request = submission,
                principalId = principal.PrincipalId,
                role = principal.Role,
                authorityRegistryVersion = principal.AuthorityRegistryVersion
            }, JsonOptions));

        var requiredRole = submission.ConcernId switch
        {
            "ReleaseReviewApproval" => "ReleaseReviewer",
            "EmergencyApproval" => "EmergencyReleaseAuthoriser",
            _ => null
        };
        if (requiredRole is null)
        {
            await approvalRegistry.RecordRefusalAsync(
                principal.PrincipalId,
                submission.ReleaseId,
                submission.ConcernId,
                requestFingerprint,
                "Unsupported approval concern.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new InvalidDataException($"'{submission.ConcernId}' is not an approval concern.");
        }
        if (!string.Equals(principal.Role, requiredRole, StringComparison.Ordinal))
        {
            await approvalRegistry.RecordRefusalAsync(
                principal.PrincipalId,
                submission.ReleaseId,
                submission.ConcernId,
                requestFingerprint,
                "Authenticated principal is not authorised for the requested approval concern.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new AsteriaApprovalForbiddenException(
                $"Authenticated principal is not authorised for '{submission.ConcernId}'.");
        }

        var release = await releaseRegistry.GetReleaseAsync(submission.ReleaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{submission.ReleaseId}' does not exist.");
        EnsureCurrentAcceptedAssessment(release, submission);
        if (!_definitionDigests.TryGetValue(release.DefinitionVersion, out var definitionDigest))
        {
            throw new InvalidDataException(
                $"No immutable definition digest is configured for Asteria version '{release.DefinitionVersion}'.");
        }
        var assessment = AsteriaDecisionApiClient.ValidateResponse(
            release.Work.OriginalResponseJson!, release);
        var applicability = AsteriaDecisionApiClient.ReadObligationApplicabilities(assessment);
        var policyApplicability = submission.ConcernId switch
        {
            "ReleaseReviewApproval" => applicability.ReleaseReviewApproval,
            "EmergencyApproval" => applicability.EmergencyApproval,
            _ => "Unresolved"
        };
        if (policyApplicability != "Required")
        {
            await approvalRegistry.RecordRefusalAsync(
                principal.PrincipalId,
                submission.ReleaseId,
                submission.ConcernId,
                requestFingerprint,
                $"Policy applicability is '{policyApplicability}', not Required.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new InvalidOperationException(
                $"Approval concern '{submission.ConcernId}' is '{policyApplicability}', not Required.");
        }

        var identifiers = CreateIdentifiers(release, submission);
        var originalRequest = JsonSerializer.Serialize(new
        {
            submission.ReleaseId,
            submission.AssessmentGeneration,
            submission.MaterialDigest,
            submission.ConcernId,
            submission.SubmissionId,
            submission.IdempotencyKey,
            submission.Decision,
            submission.EvidenceReference,
            authenticatedPrincipalId = principal.PrincipalId,
            authenticatedRole = principal.Role,
            authorityRegistryVersion = principal.AuthorityRegistryVersion
        }, JsonOptions);
        var existingActivity = await activityLedger.GetAsync(identifiers.WorkId, cancellationToken);
        AsteriaConcernState? concern = null;
        if (existingActivity is null)
        {
            var concerns = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId, cancellationToken);
            if (!concerns.TryGetValue(submission.ConcernId, out concern))
            {
                throw new InvalidDataException($"Asteria workflow has no concern '{submission.ConcernId}'.");
            }
        }
        var parent = await stateMachine.GetInstanceAsync(release.WorkflowInstanceId, cancellationToken)
            ?? throw new InvalidDataException("Asteria workflow instance is missing.");
        var reservation = new AsteriaActivityReservation(
            identifiers.WorkId,
            identifiers.ActivityId,
            release.ReleaseId,
            release.AssessmentGeneration,
            release.MaterialDigest,
            release.WorkflowInstanceId,
            release.DefinitionId,
            release.DefinitionVersion,
            definitionDigest,
            submission.ConcernId,
            submission.ConcernId,
            submission.SubmissionId,
            identifiers.OperationId,
            existingActivity?.ParentRevision ?? parent.Revision,
            existingActivity?.ConcernRevision ?? concern!.Revision,
            submission.SubmissionId,
            submission.IdempotencyKey,
            originalRequest,
            null,
            null,
            null,
            principal.PrincipalId,
            principal.Role,
            principal.AuthorityRegistryVersion);
        var activity = await activityLedger.ReserveAsync(
            reservation, _timeProvider.GetUtcNow(), cancellationToken);
        if (activity.Status is "OutcomeUnknown" or "Refused" or "Conflicting")
        {
            throw new InvalidOperationException(
                $"Asteria approval activity '{activity.WorkId}' is '{activity.Status}' and requires reconciliation.");
        }

        if (activity.Status == "Accepted")
        {
            var stored = await approvalRegistry.GetByWorkIdAsync(activity.WorkId, cancellationToken)
                ?? throw new InvalidDataException("Accepted Asteria approval has no immutable decision record.");
            return new AsteriaApprovalResponse(stored, activity);
        }

        if (activity.Status == "Reserved")
        {
            var operation = await stateMachine.EnsureExpectedConcernOperationAsync(
                release,
                activity.ExpectedOperationId,
                MappingForConcern(submission.ConcernId),
                SourceIdentity,
                ApprovalEvidenceFields,
                cancellationToken);
            if (operation.ExpectedRevision != activity.ParentRevision)
            {
                throw new InvalidDataException("Asteria approval parent revision differs from its expected-operation binding.");
            }
            activity = await activityLedger.MarkExpectedOperationRegisteredAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }

        if (activity.Status == "OperationRegistered")
        {
            await stateMachine.RequestConcernWorkAsync(
                release,
                submission.ConcernId,
                CommandForConcern(submission.ConcernId),
                StableId(activity.WorkId, "request"),
                cancellationToken);
            activity = await activityLedger.MarkDispatchPendingAsync(
                activity.WorkId, _timeProvider.GetUtcNow(), cancellationToken);
        }

        if (activity.Status is "DispatchPending" or "Dispatched")
        {
            var storedDecision = await approvalRegistry.GetByWorkIdAsync(activity.WorkId, cancellationToken);
            var decision = storedDecision ?? await CreateDecisionAsync(
                release,
                submission,
                principal,
                activity,
                originalRequest,
                requestFingerprint,
                cancellationToken);
            if (activity.OriginalResponseJson is null)
            {
                activity = await activityLedger.RecordOriginalResponseAsync(
                    activity.WorkId,
                    decision.DecisionJson,
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
            }
        }

        if (activity.Status is not ("ResponsePersisted" or "ObservationSubmitted"))
        {
            throw new InvalidDataException(
                $"Asteria approval activity '{activity.WorkId}' has unsupported status '{activity.Status}'.");
        }
        return await ProcessDecisionAsync(
            release, submission, principal, activity, requestFingerprint, cancellationToken);
    }

    private async Task<IResult> HandleSubmissionAsync(HttpContext context)
    {
        if (!AuthenticationHeaderValue.TryParse(
                context.Request.Headers.Authorization,
                out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter) ||
            !credentials.TryAuthenticate(authorization.Parameter, out var principal) ||
            principal is null)
        {
            return Results.Json(new { error = "Valid synthetic approval credentials are required." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        AsteriaApprovalSubmission? submission;
        try
        {
            submission = await JsonSerializer.DeserializeAsync<AsteriaApprovalSubmission>(
                context.Request.Body,
                JsonOptions,
                context.RequestAborted);
        }
        catch (JsonException exception)
        {
            return Results.Json(new { error = $"Invalid approval request: {exception.Message}" },
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (submission is null)
        {
            return Results.Json(new { error = "Approval request body is required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var response = await SubmitAsync(principal, submission, context.RequestAborted);
            return Results.Ok(response);
        }
        catch (AsteriaApprovalForbiddenException exception)
        {
            return Results.Json(new { error = exception.Message },
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.Json(new { error = exception.Message },
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidDataException exception)
        {
            return Results.Json(new { error = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new { error = exception.Message },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Results.Json(new { error = exception.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private async Task<AsteriaApprovalDecision> CreateDecisionAsync(
        AsteriaReleaseSnapshot release,
        AsteriaApprovalSubmission submission,
        AsteriaAuthenticatedPrincipal principal,
        AsteriaActivitySnapshot activity,
        string requestJson,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var decisionAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var evidenceBasisJson = JsonSerializer.Serialize(new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            workflowInstanceId = release.WorkflowInstanceId,
            concernId = submission.ConcernId,
            activityId = activity.ActivityId,
            expectedOperationId = activity.ExpectedOperationId,
            submissionId = submission.SubmissionId,
            idempotencyKey = submission.IdempotencyKey,
            principalId = principal.PrincipalId,
            authorityRole = principal.Role,
            authorityRegistryVersion = principal.AuthorityRegistryVersion,
            decision = submission.Decision,
            decisionAt,
            evidenceReference = submission.EvidenceReference
        }, JsonOptions);
        var evidenceDigest = AsteriaActivityLedger.Fingerprint(evidenceBasisJson);
        var decisionJson = JsonSerializer.Serialize(new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            workflowInstanceId = release.WorkflowInstanceId,
            concernId = submission.ConcernId,
            activityId = activity.ActivityId,
            expectedOperationId = activity.ExpectedOperationId,
            submissionId = submission.SubmissionId,
            idempotencyKey = submission.IdempotencyKey,
            principalId = principal.PrincipalId,
            authorityRole = principal.Role,
            authorityRegistryVersion = principal.AuthorityRegistryVersion,
            decision = submission.Decision,
            decisionAt,
            evidenceReference = submission.EvidenceReference,
            evidenceDigest
        }, JsonOptions);
        var proposal = new AsteriaApprovalDecision(
            StableId(activity.WorkId, "decision"),
            activity.WorkId,
            release.ReleaseId,
            release.AssessmentGeneration,
            release.MaterialDigest,
            release.WorkflowInstanceId,
            submission.ConcernId,
            activity.ActivityId,
            activity.ExpectedOperationId,
            submission.SubmissionId,
            submission.IdempotencyKey,
            principal.PrincipalId,
            principal.Role,
            principal.AuthorityRegistryVersion,
            submission.Decision,
            decisionAt,
            submission.EvidenceReference,
            evidenceDigest,
            requestFingerprint,
            decisionJson,
            decisionAt);
        return await approvalRegistry.ReserveDecisionAsync(
            proposal, requestJson, decisionAt, cancellationToken);
    }

    private async Task<AsteriaApprovalResponse> ProcessDecisionAsync(
        AsteriaReleaseSnapshot release,
        AsteriaApprovalSubmission submission,
        AsteriaAuthenticatedPrincipal principal,
        AsteriaActivitySnapshot activity,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        var decision = await approvalRegistry.GetByWorkIdAsync(activity.WorkId, cancellationToken)
            ?? throw new InvalidDataException("Asteria approval activity has no durable decision registry record.");
        if (decision.RequestFingerprint != requestFingerprint ||
            decision.ReleaseId != release.ReleaseId ||
            decision.Generation != release.AssessmentGeneration ||
            decision.MaterialDigest != release.MaterialDigest ||
            decision.WorkflowInstanceId != release.WorkflowInstanceId ||
            decision.ConcernId != submission.ConcernId ||
            decision.ActivityId != activity.ActivityId ||
            decision.ExpectedOperationId != activity.ExpectedOperationId ||
            decision.SubmissionId != submission.SubmissionId ||
            decision.IdempotencyKey != submission.IdempotencyKey ||
            decision.PrincipalId != principal.PrincipalId ||
            decision.AuthorityRole != principal.Role ||
            decision.AuthorityRegistryVersion != principal.AuthorityRegistryVersion ||
            decision.Decision != submission.Decision ||
            decision.EvidenceReference != submission.EvidenceReference)
        {
            _ = await activityLedger.MarkRefusedAsync(
                activity.WorkId,
                "Persisted approval decision does not match the authenticated submission binding.",
                _timeProvider.GetUtcNow(),
                cancellationToken);
            throw new InvalidDataException("Persisted approval decision binding does not match the current submission.");
        }

        activity = await activityLedger.RecordOriginalResponseAsync(
            activity.WorkId,
            decision.DecisionJson,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        activity = await activityLedger.RecordValidatedOutcomeAsync(
            activity.WorkId,
            decision.Decision,
            decision.EvidenceDigest,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = decision.ReleaseId,
            ["assessmentGeneration"] = decision.Generation,
            ["materialDigest"] = decision.MaterialDigest,
            ["activityId"] = decision.ActivityId,
            ["expectedOperationId"] = decision.ExpectedOperationId,
            ["result"] = decision.Decision,
            ["evidenceDigest"] = decision.EvidenceDigest,
            ["principalId"] = decision.PrincipalId,
            ["authorityRegistryVersion"] = decision.AuthorityRegistryVersion,
            ["decisionAt"] = decision.DecisionAt
        };
        var accepted = await stateMachine.SubmitConcernObservationAsync(
            release,
            decision.ConcernId,
            decision.ExpectedOperationId,
            StableId(activity.WorkId, "decision-message"),
            StableId(activity.WorkId, "decision-observation"),
            MappingForConcern(decision.ConcernId),
            SourceIdentity,
            ApprovalEvidenceFields,
            evidence,
            "approvalDecision",
            decision.Decision,
            decision.Decision,
            cancellationToken);
        activity = await activityLedger.MarkObservationSubmittedAsync(
            activity.WorkId, accepted.ReceiptId, _timeProvider.GetUtcNow(), cancellationToken);
        activity = await activityLedger.MarkAcceptedAsync(
            activity.WorkId,
            accepted.ReceiptId,
            accepted.AttemptId,
            accepted.HistoryId,
            _timeProvider.GetUtcNow(),
            cancellationToken);
        return new AsteriaApprovalResponse(decision, activity);
    }

    private void EnsureCurrentAcceptedAssessment(
        AsteriaReleaseSnapshot release,
        AsteriaApprovalSubmission submission)
    {
        if (release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.OriginalResponseJson is null ||
            release.AssessmentGeneration != submission.AssessmentGeneration ||
            release.MaterialDigest != submission.MaterialDigest ||
            release.DefinitionVersion is not ("4.0" or "5.0"))
        {
            throw new InvalidOperationException(
                "Approval must bind the current generation's accepted assessment.");
        }
    }

    private static void ValidateSubmission(AsteriaApprovalSubmission value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var (name, field) in new (string, string?)[]
        {
            (nameof(value.ReleaseId), value.ReleaseId),
            (nameof(value.MaterialDigest), value.MaterialDigest),
            (nameof(value.ConcernId), value.ConcernId),
            (nameof(value.SubmissionId), value.SubmissionId),
            (nameof(value.IdempotencyKey), value.IdempotencyKey),
            (nameof(value.Decision), value.Decision),
            (nameof(value.EvidenceReference), value.EvidenceReference)
        })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field, name);
        }
        if (value.AssessmentGeneration < 1 ||
            value.IdempotencyKey.Length > 200 ||
            value.EvidenceReference.Length > 1024 ||
            value.Decision is not ("Approved" or "Rejected" or "Unresolved"))
        {
            throw new ArgumentException("Approval request fields are invalid.", nameof(value));
        }
    }

    private static (string WorkId, string ActivityId, string OperationId) CreateIdentifiers(
        AsteriaReleaseSnapshot release,
        AsteriaApprovalSubmission submission)
    {
        var basis = $"{release.ReleaseId}\0{release.AssessmentGeneration}\0{submission.ConcernId}\0{submission.SubmissionId}";
        return (
            StableId(basis, "work"),
            StableId(basis, "activity"),
            StableId(basis, "expected-operation"));
    }

    private static string MappingForConcern(string concernId) =>
        concernId switch
        {
            "ReleaseReviewApproval" => "AsteriaReleaseReviewApprovalResult",
            "EmergencyApproval" => "AsteriaEmergencyApprovalResult",
            _ => throw new ArgumentOutOfRangeException(nameof(concernId))
        };

    private static string CommandForConcern(string concernId) =>
        concernId switch
        {
            "ReleaseReviewApproval" => "RequestReleaseReview",
            "EmergencyApproval" => "RequestEmergencyApproval",
            _ => throw new ArgumentOutOfRangeException(nameof(concernId))
        };

    private static string StableId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static readonly string[] ApprovalEvidenceFields =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "activityId",
        "expectedOperationId",
        "result",
        "evidenceDigest",
        "principalId",
        "authorityRegistryVersion",
        "decisionAt"
    ];
}

public sealed class AsteriaApprovalForbiddenException(string message) : Exception(message);

public static class AsteriaApprovalGatewayHost
{
    public static async Task RunFromEnvironmentAsync(string[] args)
    {
        var databasePath = Environment.GetEnvironmentVariable("ASTERIA_RELEASE_REGISTRY_PATH")
            ?? Path.Combine(Environment.CurrentDirectory, "asteria-release-registry.db");
        var trustedKey = RequiredEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        var authorityVersion = RequiredEnvironmentVariable("ASTERIA_APPROVAL_AUTHORITY_REGISTRY_VERSION");
        var credentialJson = RequiredEnvironmentVariable("ASTERIA_APPROVAL_PRINCIPALS_JSON");
        var stateMachineUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL")
            ?? "http://localhost:5000/";
        using var stateMachineHttp = new HttpClient
        {
            BaseAddress = new Uri(stateMachineUrl.EndsWith("/", StringComparison.Ordinal)
                ? stateMachineUrl
                : stateMachineUrl + "/", UriKind.Absolute)
        };
        await using var releaseRegistry = new AsteriaReleaseRegistry(databasePath);
        await releaseRegistry.InitializeAsync();
        await using var activityLedger = new AsteriaActivityLedger(databasePath);
        await activityLedger.InitializeAsync();
        var approvalRegistry = new AsteriaApprovalRegistry(databasePath);
        await approvalRegistry.InitializeAsync();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["4.0"] = await File.ReadAllTextAsync(Path.Combine(
                AppContext.BaseDirectory, "definitions", "asteria-release-governance-v4.json")),
            ["5.0"] = await File.ReadAllTextAsync(Path.Combine(
                AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"))
        };
        var credentialRegistry = AsteriaApprovalCredentialRegistry.FromJson(
            authorityVersion, credentialJson);
        var gateway = new AsteriaApprovalGateway(
            releaseRegistry,
            activityLedger,
            approvalRegistry,
            credentialRegistry,
            new AsteriaStateMachineClient(stateMachineHttp, trustedKey),
            definitions);
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();
        gateway.MapEndpoints(app);
        await app.RunAsync();
    }

    private static string RequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set {name} to start the synthetic Asteria approval gateway.");
}
