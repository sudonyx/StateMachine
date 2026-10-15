using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DurableStateMachine.MeridianClient;

var options = ParseOptions(args);
var decisionApi = EnsureTrailingSlash(new Uri(options.GetValueOrDefault("decision-url") ??
    Environment.GetEnvironmentVariable("MERIDIAN_DECISION_API_URL") ?? "http://localhost:5267/"));
var workflowApi = EnsureTrailingSlash(new Uri(options.GetValueOrDefault("workflow-url") ??
    Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000/"));
var definitionPath = FindFile(options.GetValueOrDefault("definition") ??
    Path.Combine("definitions", "meridian-supplier-qualification-v1.json"));
var assessmentFile = options.GetValueOrDefault("assessment-file") ?? "meridian-assessment.json";
var envelope = await LoadOrCreateAssessmentAsync(assessmentFile, options);
var assessment = envelope.ToRequest();

Console.WriteLine($"Meridian API: {decisionApi}");
Console.WriteLine($"Workflow API: {workflowApi}");
Console.WriteLine($"Assessment operation: {assessment.OperationId}");
Console.WriteLine($"Idempotency key: {assessment.IdempotencyKey}");
Console.WriteLine($"Saved immutable request: {Path.GetFullPath(assessmentFile)}");

using var decisionClient = new HttpClient();
using var workflowClient = new HttpClient { BaseAddress = workflowApi };
var adapter = new MeridianDecisionAdapter(decisionClient);

var initialCall = await adapter.EvaluateAsync(decisionApi, assessment);
if (initialCall.IsInProgress)
{
    ReportInProgress(assessment.IdempotencyKey, initialCall.RetryAfter);
    Environment.ExitCode = 2;
    return;
}
var decision = initialCall.Decision!;
Console.WriteLine($"Meridian evaluation: {decision.EvaluationId}; outcome state: {decision.OutcomeState}");

var repeatedCall = await adapter.EvaluateAsync(decisionApi, assessment);
if (repeatedCall.IsInProgress)
{
    ReportInProgress(assessment.IdempotencyKey, repeatedCall.RetryAfter);
    Environment.ExitCode = 2;
    return;
}
var repeatedDecision = repeatedCall.Decision!;
if (!string.Equals(decision.EvaluationId, repeatedDecision.EvaluationId, StringComparison.Ordinal) ||
    !JsonNode.DeepEquals(JsonNode.Parse(decision.OriginalResponseJson), JsonNode.Parse(repeatedDecision.OriginalResponseJson)))
{
    throw new InvalidDataException("An identical keyed retry did not return the original evaluation ID and complete response.");
}
Console.WriteLine($"Explicit same-key repeat returned the original evaluation {repeatedDecision.EvaluationId} and complete response.");

if (options.ContainsKey("verify-conflict"))
{
    var conflictingAssessment = CreateConflictingAssessment(assessment);
    try
    {
        var conflictCall = await adapter.EvaluateAsync(decisionApi, conflictingAssessment);
        if (conflictCall.IsInProgress)
        {
            ReportInProgress(assessment.IdempotencyKey, conflictCall.RetryAfter);
            Environment.ExitCode = 2;
            return;
        }
        throw new InvalidDataException("Meridian accepted a modified payload under an already completed idempotency key.");
    }
    catch (MeridianIdempotencyConflictException)
    {
        Console.WriteLine("Changed request under the same key was correctly rejected with 409 Conflict.");
    }
}

if (options.ContainsKey("decision-only"))
{
    Console.WriteLine("Decision-only mode: no state-machine event was submitted.");
    return;
}

var definitionJson = await File.ReadAllTextAsync(definitionPath);
using (var registration = await workflowClient.PostAsync("api/definitions", JsonContent(definitionJson)))
{
    await EnsureSuccessAsync(registration, "State-machine definition registration");
}

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
if (!JsonNode.DeepEquals(JsonNode.Parse(persistedResponse), JsonNode.Parse(decision.OriginalResponseJson)))
{
    throw new InvalidDataException("The persisted Meridian response differs from the complete received response.");
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
Console.WriteLine("Workflow event was submitted once; Meridian idempotency does not deduplicate state-machine events.");

static async Task<MeridianAssessmentEnvelope> LoadOrCreateAssessmentAsync(
    string path,
    IReadOnlyDictionary<string, string> options)
{
    if (File.Exists(path))
    {
        var existing = await MeridianAssessmentEnvelope.LoadAsync(path);
        ValidateSuppliedAssessmentOptions(existing.ToRequest(), options);
        Console.WriteLine("Reusing the exact saved assessment request and operation identity.");
        return existing;
    }

    var operationId = options.GetValueOrDefault("assessment-id") ?? Guid.NewGuid().ToString("N");
    var key = options.GetValueOrDefault("idempotency-key") ?? $"assessment-{operationId}";
    var businessEffectiveAt = options.GetValueOrDefault("business-effective-at") ??
        DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
    var knownAt = options.GetValueOrDefault("known-at") ?? DateTimeOffset.UtcNow.ToString("O");
    var request = MeridianAssessmentRequest.CreateSynthetic(operationId, key, businessEffectiveAt, knownAt);
    var envelope = MeridianAssessmentEnvelope.FromRequest(request);
    await envelope.SaveNewAsync(path);
    Console.WriteLine("Created and saved a new assessment operation before making the HTTP request.");
    return envelope;
}

static void ValidateSuppliedAssessmentOptions(
    MeridianAssessmentRequest request,
    IReadOnlyDictionary<string, string> options)
{
    if (options.TryGetValue("assessment-id", out var operationId) &&
        !string.Equals(operationId, request.OperationId, StringComparison.Ordinal))
    {
        throw new ArgumentException("The saved assessment has a different --assessment-id; use a new assessment file for reassessment.");
    }
    if (options.TryGetValue("idempotency-key", out var key) &&
        !string.Equals(key, request.IdempotencyKey, StringComparison.Ordinal))
    {
        throw new ArgumentException("The saved assessment has a different --idempotency-key; do not change the key for a retry.");
    }
    var temporalContext = request.ReadTemporalContext();
    if (options.TryGetValue("business-effective-at", out var effectiveAt) &&
        !string.Equals(effectiveAt, temporalContext.BusinessEffectiveAt, StringComparison.Ordinal))
    {
        throw new ArgumentException("The saved request has a different businessEffectiveAt; use a new assessment file for reassessment.");
    }
    if (options.TryGetValue("known-at", out var knownAt) &&
        !string.Equals(knownAt, temporalContext.KnownAt, StringComparison.Ordinal))
    {
        throw new ArgumentException("The saved request has a different knownAt; use a new assessment file for reassessment.");
    }
}

static MeridianAssessmentRequest CreateConflictingAssessment(MeridianAssessmentRequest assessment)
{
    var json = JsonNode.Parse(assessment.RequestJson)?.AsObject()
        ?? throw new InvalidDataException("Saved supplier request is not a JSON object.");
    json["supplierCountry"] = "DifferentExampleland";
    return MeridianAssessmentRequest.Create(assessment.OperationId, assessment.IdempotencyKey, json.ToJsonString());
}

static Dictionary<string, string> ParseOptions(string[] arguments)
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var switches = new HashSet<string>(["verify-conflict", "decision-only"], StringComparer.Ordinal);
    var accepted = new HashSet<string>(
        ["decision-url", "workflow-url", "definition", "assessment-file", "assessment-id",
         "idempotency-key", "business-effective-at", "known-at"],
        StringComparer.Ordinal);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument == "--help")
        {
            Console.WriteLine("Options: --decision-url URL --workflow-url URL --definition PATH " +
                "--assessment-file PATH [--assessment-id ID] [--idempotency-key KEY] " +
                "[--business-effective-at YYYY-MM-DD] [--known-at ISO-8601] " +
                "[--verify-conflict] [--decision-only]");
            Environment.Exit(0);
        }
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid option '{argument}'. Use --help for supported options.");
        }

        var name = argument[2..];
        if (switches.Contains(name))
        {
            if (!values.TryAdd(name, string.Empty))
            {
                throw new ArgumentException($"Option '{argument}' was supplied more than once.");
            }
            continue;
        }
        if (!accepted.Contains(name) || index + 1 >= arguments.Length ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Invalid or incomplete option '{argument}'. Use --help for supported options.");
        }
        if (!values.TryAdd(name, arguments[++index]))
        {
            throw new ArgumentException($"Option '{argument}' was supplied more than once.");
        }
    }
    return values;
}

static void ReportInProgress(string idempotencyKey, TimeSpan? retryAfter)
{
    var delay = retryAfter is null ? "not supplied" : $"{Math.Max(0, retryAfter.Value.TotalSeconds):0.###} seconds";
    Console.WriteLine($"Assessment '{idempotencyKey}' is still in progress (Retry-After: {delay}).");
    Console.WriteLine("No new key was generated and no workflow event was submitted. Retry later with the same saved assessment file.");
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
