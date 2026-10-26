using System.Text.Json;
using DurableStateMachine.AsteriaClient;

var argsView = args;
if (argsView.Length == 0 || argsView[0] is not ("assess" or "prepare" or "resume"))
{
    Console.Error.WriteLine(
        "Usage: DurableStateMachine.AsteriaClient assess <release-id> <material.json> | " +
        "prepare <release-id> <material.json> | resume <release-id>");
    return 2;
}

var releaseId = argsView.Length > 1
    ? argsView[1]
    : throw new ArgumentException("A release ID is required.");
var materialPath = argsView[0] is "assess" or "prepare"
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
var definitionPath = Path.Combine(AppContext.BaseDirectory, "definitions", "asteria-release-governance-v2.json");
var definitionJson = await File.ReadAllTextAsync(definitionPath);

using var stateMachineHttp = new HttpClient { BaseAddress = EnsureBaseAddress(stateMachineUrl) };
using var decisionHttp = new HttpClient { BaseAddress = EnsureBaseAddress(decisionApiUrl) };
await using var registry = new AsteriaReleaseRegistry(registryPath);
await registry.InitializeAsync();

var coordinator = new AsteriaAssessmentCoordinator(
    registry,
    new AsteriaStateMachineClient(stateMachineHttp, trustedKey),
    new AsteriaDecisionApiClient(decisionHttp),
    definitionJson);

AsteriaReleaseSnapshot result;
if (argsView[0] is "assess" or "prepare")
{
    var materialJson = await File.ReadAllTextAsync(materialPath!);
    var material = JsonSerializer.Deserialize<AsteriaReleaseMaterial>(
        materialJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidDataException("The release material file must contain an Asteria material object.");
    result = argsView[0] == "prepare"
        ? await coordinator.PrepareAsync(releaseId, material)
        : await coordinator.CreateAndAssessAsync(releaseId, material);
}
else
{
    result = await coordinator.ResumeAsync(releaseId);
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    result.ReleaseId,
    result.WorkflowInstanceId,
    result.DefinitionId,
    result.DefinitionVersion,
    result.AssessmentGeneration,
    result.MaterialDigest,
    registryStatus = result.Status,
    assessmentWorkId = result.Work.WorkId,
    result.Work.ParentRevision,
    result.Work.ExpectedOperationId,
    result.Work.DecisionIdempotencyKey,
    result.Work.DecisionRequestFingerprint,
    result.Work.EvaluationId,
    result.Work.SelectedPolicyVersionsJson,
    workStatus = result.Work.Status
}, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
return 0;

static Uri EnsureBaseAddress(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
        address.Scheme is not ("http" or "https"))
    {
        throw new ArgumentException("Service URLs must be absolute HTTP or HTTPS URLs.");
    }

    return new Uri(address.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
}
