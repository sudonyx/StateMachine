using System.Text.Json;
using DurableStateMachine.AsteriaClient;

var argsView = args;
if (argsView.Length == 0 ||
    argsView[0] is not ("assess" or "prepare" or "resume" or "amend" or "resume-amendment"))
{
    Console.Error.WriteLine(
        "Usage: DurableStateMachine.AsteriaClient assess <release-id> <material.json> | " +
        "prepare <release-id> <material.json> | resume <release-id> | " +
        "amend|resume-amendment <release-id> <amendment-id> <expected-base-generation> <material.json>");
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
