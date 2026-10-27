using System.Text.Json;
using System.Text;
using DurableStateMachine.AsteriaClient;

internal static class AsteriaClientProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "approval-gateway")
        {
            await AsteriaApprovalGatewayHost.RunFromEnvironmentAsync(args.Skip(1).ToArray());
            return 0;
        }

var argsView = args;
if (argsView.Length == 0 ||
    argsView[0] is not (
        "assess" or "prepare" or "resume" or "amend" or "resume-amendment" or "export-evidence" or
        "verify" or "readiness" or "deploy" or "health" or "cancel" or "process-deadlines"))
{
    Console.Error.WriteLine(
        "Usage: DurableStateMachine.AsteriaClient assess <release-id> <material.json> | " +
        "prepare <release-id> <material.json> | resume <release-id> | " +
        "amend|resume-amendment <release-id> <amendment-id> <expected-base-generation> <material.json> | " +
        "verify <release-id> <FunctionalVerification|SecurityVerification> | " +
        "readiness|deploy|health|cancel|process-deadlines <release-id> | " +
        "export-evidence <release-id> <output.json> | approval-gateway");
    return 2;
}

var releaseId = argsView.Length > 1
    ? argsView[1]
    : throw new ArgumentException("A release ID is required.");
var amendmentCommand = argsView[0] is "amend" or "resume-amendment";
var materialPath = amendmentCommand
    ? argsView.ElementAtOrDefault(4) ?? throw new ArgumentException("An Asteria amendment material JSON path is required.")
    : argsView[0] is "assess" or "prepare"
        ? argsView.ElementAtOrDefault(2) ?? throw new ArgumentException("An Asteria release material JSON path is required.")
        : null;

var trustedKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
if (string.IsNullOrWhiteSpace(trustedKey))
{
    throw new InvalidOperationException("Set DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY for trusted Asteria observations.");
}

var stateMachineUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000/";
var decisionApiUrl = Environment.GetEnvironmentVariable("ASTERIA_DECISION_API_URL") ?? "http://localhost:5267/";
var registryPath = Environment.GetEnvironmentVariable("ASTERIA_RELEASE_REGISTRY_PATH")
    ?? Path.Combine(Environment.CurrentDirectory, "asteria-release-registry.db");

using var stateMachineHttp = new HttpClient { BaseAddress = EnsureBaseAddress(stateMachineUrl) };
using var decisionHttp = new HttpClient { BaseAddress = EnsureBaseAddress(decisionApiUrl) };
await using var registry = new AsteriaReleaseRegistry(registryPath);
await registry.InitializeAsync();
await using var activityLedger = new AsteriaActivityLedger(registryPath);
await activityLedger.InitializeAsync();
await using var deadlineLedger = new AsteriaDeadlineLedger(registryPath);
await deadlineLedger.InitializeAsync();
var approvalRegistry = new AsteriaApprovalRegistry(registryPath);
await approvalRegistry.InitializeAsync();
var stateMachine = new AsteriaStateMachineClient(stateMachineHttp, trustedKey);
var deadlineCoordinator = new AsteriaDeadlineCoordinator(
    registry, activityLedger, deadlineLedger, stateMachine);
var existingRelease = await registry.GetReleaseAsync(releaseId);
var definitionVersion = amendmentCommand
    ? existingRelease?.DefinitionVersion ?? "3.0"
    : existingRelease?.DefinitionVersion ?? "5.0";
var definitionFile = definitionVersion switch
{
    "1.0" => "asteria-release-governance-v1.json",
    "2.0" => "asteria-release-governance-v2.json",
    "3.0" => "asteria-release-governance-v3.json",
    "4.0" => "asteria-release-governance-v4.json",
    "5.0" => "asteria-release-governance-v5.json",
    _ => throw new InvalidDataException($"Unsupported persisted Asteria definition version '{definitionVersion}'.")
};
var definitionPath = Path.Combine(AppContext.BaseDirectory, "definitions", definitionFile);
var definitionJson = await File.ReadAllTextAsync(definitionPath);
var definitionJsonByVersion = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["4.0"] = await File.ReadAllTextAsync(Path.Combine(
        AppContext.BaseDirectory, "definitions", "asteria-release-governance-v4.json")),
    ["5.0"] = await File.ReadAllTextAsync(Path.Combine(
        AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"))
};

var coordinator = new AsteriaAssessmentCoordinator(
    registry,
    stateMachine,
    new AsteriaDecisionApiClient(decisionHttp),
    definitionJson,
    deadlineCoordinator: deadlineCoordinator);

if (argsView[0] == "verify")
{
    if (argsView.Length != 3)
    {
        throw new ArgumentException("verify requires a release ID and concern.");
    }
    var concernId = argsView[2];
    using var harnessHttp = new HttpClient
    {
        BaseAddress = EnsureBaseAddress(RequiredEnvironmentVariable("GENERIC_HARNESS_API_URL"))
    };
    var bindings = new Dictionary<string, AsteriaHarnessOperationBinding>(StringComparer.Ordinal)
    {
        [concernId] = ReadHarnessBinding($"ASTERIA_{ToEnvironmentKey(concernId)}")
    };
    var result = await new AsteriaVerificationCoordinator(
        registry, activityLedger, stateMachine, harnessHttp, definitionJson, bindings)
        .ExecuteAsync(releaseId, concernId);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}

if (argsView[0] == "readiness")
{
    using var harnessHttp = new HttpClient
    {
        BaseAddress = EnsureBaseAddress(RequiredEnvironmentVariable("GENERIC_HARNESS_API_URL"))
    };
    var bindings = new Dictionary<string, AsteriaHarnessOperationBinding>(StringComparer.Ordinal)
    {
        ["FunctionalVerification"] = ReadHarnessBinding("ASTERIA_FUNCTIONAL_VERIFICATION"),
        ["SecurityVerification"] = ReadHarnessBinding("ASTERIA_SECURITY_VERIFICATION")
    };
    var result = await new AsteriaReadinessCoordinator(
        registry,
        activityLedger,
        approvalRegistry,
        stateMachine,
        definitionJsonByVersion,
        bindings).ConfirmAsync(releaseId);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}

if (argsView[0] == "deploy")
{
    using var harnessHttp = new HttpClient
    {
        BaseAddress = EnsureBaseAddress(RequiredEnvironmentVariable("GENERIC_HARNESS_API_URL"))
    };
    var result = await new AsteriaDeploymentCoordinator(
        registry,
        activityLedger,
        stateMachine,
        harnessHttp,
        definitionJson,
        new AsteriaDeploymentHarnessBindings(
            ReadHarnessBinding("ASTERIA_DEPLOYMENT"),
            ReadHarnessBinding("ASTERIA_DEPLOYMENT_STATUS"),
            ReadHarnessBinding("ASTERIA_DEPLOYMENT_IDEMPOTENCY_LOOKUP")),
        deadlineCoordinator: deadlineCoordinator).ExecuteAsync(releaseId);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}

if (argsView[0] == "health")
{
    using var harnessHttp = new HttpClient
    {
        BaseAddress = EnsureBaseAddress(RequiredEnvironmentVariable("GENERIC_HARNESS_API_URL"))
    };
    var result = await new AsteriaHealthVerificationCoordinator(
        registry,
        activityLedger,
        stateMachine,
        harnessHttp,
        definitionJson,
        ReadHarnessBinding("ASTERIA_HEALTH")).ExecuteAsync(releaseId);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}

if (argsView[0] == "cancel")
{
    var result = await new AsteriaCancellationCoordinator(
        registry, activityLedger, stateMachine).CancelAsync(releaseId);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
    return 0;
}

if (argsView[0] == "process-deadlines")
{
    var acceptedRelease = await coordinator.ResumeAsync(releaseId);
    _ = await deadlineCoordinator.EnsureVerificationEscalationAsync(acceptedRelease);
    var history = await stateMachine.ListTransitionHistoryAsync(acceptedRelease.WorkflowInstanceId);
    if (history.Any(item =>
            item.TryGetProperty("transitionId", out var transitionId) &&
            transitionId.GetString() == "deployment-start-from-ready"))
    {
        _ = await deadlineCoordinator.EnsureDeploymentInvestigationAsync(acceptedRelease);
    }
    var processed = await deadlineCoordinator.ProcessDueAsync(releaseId);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        processedAtUtc = DateTimeOffset.UtcNow,
        releaseId,
        scheduledDeadlines = await deadlineLedger.ListGenerationAsync(
            releaseId, acceptedRelease.AssessmentGeneration),
        processed
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return 0;
}

if (argsView[0] == "export-evidence")
{
    var outputPath = Path.GetFullPath(argsView.ElementAtOrDefault(2)
        ?? throw new ArgumentException("An Asteria evidence output path is required."));
    var protectedPaths = new List<string> { Path.GetFullPath(registryPath) };
    var stateMachineDatabasePath = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
    if (!string.IsNullOrWhiteSpace(stateMachineDatabasePath))
    {
        protectedPaths.Add(Path.GetFullPath(stateMachineDatabasePath));
    }
    if (protectedPaths.Contains(outputPath, StringComparer.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Evidence output must not overwrite a configured workflow database.");
    }

    var current = existingRelease
        ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
    var generations = await registry.ListGenerationsAsync(releaseId);
    var amendments = await registry.ListAmendmentsAsync(releaseId);
    var amendmentEvidence = new List<object>(amendments.Count);
    foreach (var amendment in amendments)
    {
        amendmentEvidence.Add(new
        {
            amendment,
            supersessionAttempts = await registry.ListSupersessionAttemptsAsync(
                releaseId, amendment.AmendmentId)
        });
    }

    var generationEvidence = new List<object>(generations.Count);
    foreach (var generation in generations)
    {
        var instance = await stateMachine.GetInstanceAsync(generation.WorkflowInstanceId);
        IReadOnlyList<JsonElement> history = [];
        IReadOnlyList<JsonElement> inboundMessages = [];
        IReadOnlyList<AsteriaExpectedOperation> operations = [];
        IReadOnlyList<JsonElement> concernHistory = [];
        JsonElement? historicalReplay = null;
        IReadOnlyDictionary<string, AsteriaConcernState> concernProjection =
            new Dictionary<string, AsteriaConcernState>(StringComparer.Ordinal);
        if (instance is not null)
        {
            history = await stateMachine.ListTransitionHistoryAsync(generation.WorkflowInstanceId);
            concernHistory = await stateMachine.ListConcernHistoryAsync(generation.WorkflowInstanceId);
            inboundMessages = await stateMachine.ListInboundMessagesAsync(generation.WorkflowInstanceId);
            operations = await stateMachine.ListExpectedOperationsAsync(generation.WorkflowInstanceId);
            concernProjection = await stateMachine.GetConcernStatesAsync(generation.WorkflowInstanceId);
            historicalReplay = await stateMachine.GetHistoricalReplayAsync(generation.WorkflowInstanceId);
        }
        var activities = await activityLedger.ListGenerationAsync(
            generation.ReleaseId, generation.AssessmentGeneration);
        var activityEvidence = new List<object>(activities.Count);
        foreach (var activity in activities)
        {
            activityEvidence.Add(new
            {
                activity,
                providerObservations = await activityLedger.ListProviderObservationsAsync(activity.WorkId),
                conflicts = await activityLedger.ListConflictsAsync(activity.WorkId),
                approvalDecision = await approvalRegistry.GetByWorkIdAsync(activity.WorkId)
            });
        }
        var deadlines = await deadlineLedger.ListGenerationAsync(
            generation.ReleaseId, generation.AssessmentGeneration);
        var approvalConflicts = await approvalRegistry.ListConflictEvidenceAsync(
            generation.ReleaseId, generation.AssessmentGeneration);

        JsonElement? originalResponse = ParseOptionalJson(generation.Work.OriginalResponseJson);
        JsonElement? originalEvidence = ParseOptionalJson(generation.Work.OriginalEvidenceJson);
        var selectedVersions = JsonDocument.Parse(generation.Work.SelectedPolicyVersionsJson);
        using (selectedVersions)
        {
            var parentTransition = generation.Work.ParentRevision is long parentRevision
                ? history.FirstOrDefault(item =>
                    item.TryGetProperty("revisionAfter", out var revision) &&
                    revision.GetInt64() == parentRevision)
                : default;
            var expectedOperation = operations.SingleOrDefault(
                item => item.OperationId == generation.Work.ExpectedOperationId);
            var obligations = originalResponse is JsonElement response
                ? ProjectObligations(response)
                : null;

            generationEvidence.Add(new
            {
                generation.ReleaseId,
                generation = generation.AssessmentGeneration,
                generation.MaterialDigest,
                generation.WorkflowInstanceId,
                generation.DefinitionId,
                generation.DefinitionVersion,
                generation.Material,
                evaluationId = generation.Work.EvaluationId,
                selectedPolicyVersions = selectedVersions.RootElement.Clone(),
                assessmentOutcome = originalResponse is JsonElement persistedResponse &&
                    persistedResponse.TryGetProperty("outcome", out var outcome)
                        ? outcome.GetString()
                        : null,
                originalDecisionResponse = originalResponse,
                originalDecisionEvidence = originalEvidence,
                independentObligations = obligations,
                activities = activityEvidence,
                approvalConflicts,
                deadlines,
                concernProjection,
                acceptedConcernHistory = concernHistory,
                historicalReplay,
                expectedOperation = expectedOperation is null
                    ? null
                    : new
                    {
                        expectedOperation.OperationId,
                        expectedOperation.ObservationType,
                        expectedOperation.SourceIdentity,
                        expectedOperation.ExpectedRevision,
                        expectedOperation.Status,
                        expectedOperation.ResultDigest
                    },
                assessmentBinding = new
                {
                    description = "Immutable assessment binding recorded when the expected operation was registered. Historical; not the current workflow state.",
                    boundParentRevision = generation.Work.ParentRevision,
                    boundParentState = parentTransition.ValueKind == JsonValueKind.Object &&
                        parentTransition.TryGetProperty("newState", out var boundState)
                            ? boundState.GetString()
                            : null
                },
                assessmentWork = generation.Work,
                currentStateMachineProjection = instance is null
                    ? null
                    : new
                    {
                        description = "Live State Machine instance read at export time; advances after assessment binding.",
                        instance.Id,
                        instance.CurrentState,
                        instance.Revision,
                        instance.Facts
                    },
                acceptedTransitionHistory = history,
                inboundReceipts = inboundMessages
            });
        }
    }

    var evidence = new
    {
        exportedAtUtc = DateTimeOffset.UtcNow,
        releaseId,
        currentGeneration = current.AssessmentGeneration,
        currentRegistryState = current.RegistryState,
        amendments = amendmentEvidence,
        generations = generationEvidence
    };
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
    Console.WriteLine($"Exported persisted Asteria evidence to {outputPath}");
    return 0;
}

if (amendmentCommand)
{
    var materialJson = await File.ReadAllTextAsync(materialPath!);
    var material = JsonSerializer.Deserialize<AsteriaReleaseMaterial>(
        materialJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("The amendment material file must contain an Asteria material object.");
    var amendmentId = argsView.ElementAtOrDefault(2)
        ?? throw new ArgumentException("A stable amendment ID is required.");
    if (!int.TryParse(argsView.ElementAtOrDefault(3), out var expectedBaseGeneration))
    {
        throw new ArgumentException("Expected base generation must be an integer.");
    }

    var amendment = await new AsteriaAmendmentCoordinator(
        registry,
        new AsteriaStateMachineClient(stateMachineHttp, trustedKey),
        definitionJson).AmendAsync(
            releaseId, amendmentId, expectedBaseGeneration, material);
    var current = await registry.GetReleaseAsync(releaseId)
        ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' disappeared.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        amendment,
        currentGeneration = current.AssessmentGeneration,
        current.WorkflowInstanceId,
        current.MaterialDigest,
        current.RegistryState,
        assessmentWorkId = current.Work.WorkId,
        workStatus = current.Work.Status
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return 0;
}

if (argsView[0] is "assess" or "prepare")
{
    var materialJson = await File.ReadAllTextAsync(materialPath!);
    var material = JsonSerializer.Deserialize<AsteriaReleaseMaterial>(
        materialJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("The release material file must contain an Asteria material object.");
    var result = argsView[0] == "prepare"
        ? await coordinator.PrepareAsync(releaseId, material)
        : await coordinator.CreateAndAssessAsync(releaseId, material);
    PrintRelease(result);
    return 0;
}

var resumed = await coordinator.ResumeAsync(releaseId);
PrintRelease(resumed);
return 0;

void PrintRelease(AsteriaReleaseSnapshot result) =>
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        result.ReleaseId,
        result.WorkflowInstanceId,
        result.DefinitionId,
        result.DefinitionVersion,
        result.AssessmentGeneration,
        result.MaterialDigest,
        registryStatus = result.Status,
        result.RegistryState,
        assessmentWorkId = result.Work.WorkId,
        result.Work.ParentRevision,
        result.Work.ExpectedOperationId,
        result.Work.DecisionIdempotencyKey,
        result.Work.DecisionRequestFingerprint,
        result.Work.EvaluationId,
        result.Work.SelectedPolicyVersionsJson,
        workStatus = result.Work.Status
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static Uri EnsureBaseAddress(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
        address.Scheme is not ("http" or "https"))
    {
        throw new ArgumentException("Service URLs must be absolute HTTP or HTTPS URLs.");
    }

    return new Uri(address.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
}

static string RequiredEnvironmentVariable(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Set {name} to the configured Asteria service value.");

static AsteriaHarnessOperationBinding ReadHarnessBinding(string prefix) =>
    new(
        RequiredEnvironmentVariable(prefix + "_PROVIDER_IDENTITY"),
        RequiredEnvironmentVariable(prefix + "_PROVIDER_ID"),
        RequiredEnvironmentVariable(prefix + "_API_DESCRIPTION_VERSION_ID"),
        RequiredEnvironmentVariable(prefix + "_OPERATION_KEY"));

static string ToEnvironmentKey(string value)
{
    var result = new StringBuilder(value.Length + 8);
    for (var index = 0; index < value.Length; index++)
    {
        var character = value[index];
        if (char.IsUpper(character) && index > 0)
        {
            result.Append('_');
        }
        result.Append(char.ToUpperInvariant(character));
    }
    return result.ToString();
}

static JsonElement? ParseOptionalJson(string? json)
{
    if (json is null)
    {
        return null;
    }
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

static object ProjectObligations(JsonElement response)
{
    static object Project(JsonElement value) => new
    {
        status = value.GetProperty("status").GetString(),
        clauseReferences = value.GetProperty("clauseReferences").EnumerateArray()
            .Select(reference => reference.GetString()).ToArray()
    };

    return new
    {
        functionalVerification = Project(response.GetProperty("functionalVerification")),
        securityVerification = Project(response.GetProperty("securityVerification")),
        releaseReviewApproval = Project(response.GetProperty("releaseReviewApproval")),
        emergencyApproval = Project(response.GetProperty("emergencyApproval"))
    };
}
    }
}
