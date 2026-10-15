using System.Net.Http.Json;
using System.Text.Json;
using DurableStateMachine.MeridianClient;

var options = ParseOptions(args);
var decisionApi = EnsureTrailingSlash(new Uri(options.GetValueOrDefault("decision-url") ??
    Environment.GetEnvironmentVariable("MERIDIAN_DECISION_API_URL") ?? "http://localhost:5267/"));
var workflowApi = new Uri(options.GetValueOrDefault("workflow-url") ??
    Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000/");
var definitionPath = FindFile(options.GetValueOrDefault("definition") ??
    Path.Combine("definitions", "meridian-supplier-qualification-v1.json"));
var businessEffectiveAt = options.GetValueOrDefault("business-effective-at") ??
    DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
if (!DateOnly.TryParseExact(businessEffectiveAt, "yyyy-MM-dd", out _))
{
    throw new ArgumentException("--business-effective-at must use YYYY-MM-DD.");
}
var knownAt = DateTimeOffset.TryParse(options.GetValueOrDefault("known-at"), out var parsedKnownAt)
    ? parsedKnownAt
    : DateTimeOffset.UtcNow;

using var decisionClient = new HttpClient();
using var workflowClient = new HttpClient { BaseAddress = EnsureTrailingSlash(workflowApi) };
var adapter = new MeridianDecisionAdapter(decisionClient);
var definitionJson = await File.ReadAllTextAsync(definitionPath);

Console.WriteLine($"Meridian API: {decisionApi}");
Console.WriteLine($"Workflow API: {workflowApi}");
Console.WriteLine($"Business effective date: {businessEffectiveAt}; known at: {knownAt:O}");

using (var registration = await workflowClient.PostAsync("api/definitions", JsonContent(definitionJson)))
{
    await EnsureSuccessAsync(registration, "State-machine definition registration");
}

var decision = await adapter.EvaluateAsync(decisionApi, businessEffectiveAt, knownAt);
Console.WriteLine($"Meridian evaluation: {decision.EvaluationId}; outcome state: {decision.OutcomeState}");

var instanceId = $"meridian-demo-{Guid.NewGuid():N}";
using (var created = await workflowClient.PostAsJsonAsync("api/instances", new
{
    id = instanceId,
    definitionId = "meridian-supplier-qualification",
    definitionVersion = "1.0",
    facts = new { }
}))
{
    await EnsureSuccessAsync(created, "Workflow instance creation");
}

using var submitted = await workflowClient.PostAsync(
    $"api/instances/{Uri.EscapeDataString(instanceId)}/events",
    JsonContent(decision.CreateEventSubmission("DecisionAvailable").ToJsonString()));
await EnsureSuccessAsync(submitted, "Workflow event submission");
using var submittedDocument = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
var attempt = submittedDocument.RootElement.GetProperty("attempt");
Console.WriteLine($"Workflow evaluation: {attempt.GetProperty("status").GetString()} " +
    $"({attempt.GetProperty("reasonCode").GetString()})");

using var attemptsResponse = await workflowClient.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/attempts");
await EnsureSuccessAsync(attemptsResponse, "Workflow attempt inspection");
using var attemptsDocument = JsonDocument.Parse(await attemptsResponse.Content.ReadAsStringAsync());
var storedAttempt = attemptsDocument.RootElement.EnumerateArray().Single();
var storedEvidence = storedAttempt.GetProperty("evaluationEvidence");
if (storedEvidence.ValueKind != JsonValueKind.Object)
{
    throw new InvalidDataException("The persisted workflow attempt has no evaluation evidence.");
}
var storedDecisionEvidence = storedEvidence.GetProperty("decisionEvidence");
if (storedDecisionEvidence.GetProperty("externalEvaluationId").GetString() != decision.EvaluationId ||
    storedDecisionEvidence.GetProperty("inputFactSetDigest").GetString() != decision.InputFactSetDigest ||
    !storedDecisionEvidence.GetProperty("policyVersionIdentities").EnumerateArray()
        .Select(item => item.GetString()).SequenceEqual(decision.SelectedPolicyVersionIds))
{
    throw new InvalidDataException("Persisted decision evidence does not match the received Meridian response.");
}
var persistedResponse = storedDecisionEvidence.GetProperty("originalResponseJson").GetString()
    ?? throw new InvalidDataException("The original Meridian response snapshot was not persisted.");
using (var persistedDecision = JsonDocument.Parse(persistedResponse))
{
    if (persistedDecision.RootElement.GetProperty("evidence").GetProperty("evaluationId").GetString() != decision.EvaluationId)
    {
        throw new InvalidDataException("The original Meridian response snapshot has a different evaluation ID.");
    }
}
Console.WriteLine($"Persisted evidence digest: {storedAttempt.GetProperty("evaluationEvidenceDigest").GetString()}");

using var historyResponse = await workflowClient.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/history");
await EnsureSuccessAsync(historyResponse, "Workflow history inspection");
using var historyDocument = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
Console.WriteLine($"Accepted workflow transitions: {historyDocument.RootElement.GetArrayLength()}");

var replayUri = new Uri(decisionApi,
    $"historical-decisions/{Uri.EscapeDataString(decision.EvaluationId)}/replay");
using var replayResponse = await decisionClient.PostAsync(replayUri, content: null);
await EnsureSuccessAsync(replayResponse, "Explicit historical Meridian replay verification");
using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
var replay = replayDocument.RootElement;
if (replay.GetProperty("evaluationId").GetString() != decision.EvaluationId)
{
    throw new InvalidDataException("Historical replay returned a different evaluation ID.");
}
Console.WriteLine($"Historical replay matches stored outcome: {replay.GetProperty("matchesStoredOutcome").GetBoolean()}");
Console.WriteLine($"Historical replay differences: {replay.GetProperty("differences").GetArrayLength()}");
Console.WriteLine("Integration completed using live HTTP responses; no evaluation retry was performed.");

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument == "--help")
        {
            Console.WriteLine("Options: --decision-url URL --workflow-url URL --definition PATH " +
                "--business-effective-at YYYY-MM-DD --known-at ISO-8601");
            Environment.Exit(0);
        }
        if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
        {
            throw new ArgumentException($"Invalid option '{argument}'. Use --help for supported options.");
        }
        values.Add(argument[2..], arguments[++index]);
    }
    return values;
}

static string FindFile(string path)
{
    if (File.Exists(path))
    {
        return Path.GetFullPath(path);
    }

    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        var candidate = Path.Combine(directory.FullName, path);
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }
    throw new FileNotFoundException($"Could not find '{path}'. Supply --definition with an existing file.");
}

static Uri EnsureTrailingSlash(Uri value) =>
    new(value.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);

static StringContent JsonContent(string json) =>
    new(json, System.Text.Encoding.UTF8, "application/json");

static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
{
    if (!response.IsSuccessStatusCode)
    {
        var responseBody = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"{operation} failed with HTTP {(int)response.StatusCode}: {responseBody}",
            null,
            response.StatusCode);
    }
}
