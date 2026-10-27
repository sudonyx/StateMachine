using System.Text.Json;
using DurableStateMachine.AsteriaClient;

var argsView = args;
if (argsView.Length == 0 ||
    argsView[0] is not ("assess" or "prepare" or "resume" or "amend" or "resume-amendment" or "export-evidence"))
{
    Console.Error.WriteLine(
        "Usage: DurableStateMachine.AsteriaClient assess <release-id> <material.json> | " +
        "prepare <release-id> <material.json> | resume <release-id> | " +
        "amend|resume-amendment <release-id> <amendment-id> <expected-base-generation> <material.json> | " +
        "export-evidence <release-id> <output.json>");
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
var existingRelease = await registry.GetReleaseAsync(releaseId);
var definitionVersion = amendmentCommand
    ? "3.0"
    : existingRelease?.DefinitionVersion ?? "3.0";
var definitionFile = definitionVersion switch
{
    "1.0" => "asteria-release-governance-v1.json",
    "2.0" => "asteria-release-governance-v2.json",
    "3.0" => "asteria-release-governance-v3.json",
    _ => throw new InvalidDataException($"Unsupported persisted Asteria definition version '{definitionVersion}'.")
};
var definitionPath = Path.Combine(AppContext.BaseDirectory, "definitions", definitionFile);
var definitionJson = await File.ReadAllTextAsync(definitionPath);

var coordinator = new AsteriaAssessmentCoordinator(
    registry,
    new AsteriaStateMachineClient(stateMachineHttp, trustedKey),
    new AsteriaDecisionApiClient(decisionHttp),
    definitionJson);

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

    var stateMachine = new AsteriaStateMachineClient(stateMachineHttp, trustedKey);
    var generationEvidence = new List<object>(generations.Count);
    foreach (var generation in generations)
    {
        var instance = await stateMachine.GetInstanceAsync(generation.WorkflowInstanceId);
        IReadOnlyList<JsonElement> history = [];
        IReadOnlyList<JsonElement> inboundMessages = [];
        IReadOnlyList<AsteriaExpectedOperation> operations = [];
        if (instance is not null)
        {
            history = await stateMachine.ListTransitionHistoryAsync(generation.WorkflowInstanceId);
            inboundMessages = await stateMachine.ListInboundMessagesAsync(generation.WorkflowInstanceId);
            operations = await stateMachine.ListExpectedOperationsAsync(generation.WorkflowInstanceId);
        }

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
