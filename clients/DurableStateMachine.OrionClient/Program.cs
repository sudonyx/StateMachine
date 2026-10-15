using System.Net.Http.Json;
using System.Text.Json;

var apiUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000";
var trustedKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
var definitionPath = args.ElementAtOrDefault(0) ?? Path.Combine("definitions", "orion-v1.json");
if (string.IsNullOrWhiteSpace(trustedKey))
{
    throw new InvalidOperationException(
        "Set DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY for the controlled Orion observation demonstration.");
}

var instanceId = $"orion-demo-{Guid.NewGuid():N}";
using var client = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(definitionPath));
var definitionVersion = definition.RootElement.GetProperty("version").GetString()
    ?? throw new InvalidOperationException("The Orion definition has no version.");
await EnsureSuccess(await client.PostAsJsonAsync("api/definitions", definition.RootElement));

await PrintResponse(await client.PostAsJsonAsync("api/instances", new
{
    id = instanceId,
    definitionId = "orion-equipment-lease",
    definitionVersion,
    facts = new
    {
        documentsComplete = true,
        riskScore = 75,
        equipmentValue = 50000,
        checksComplete = true,
        eligibilityDecision = "Eligible",
        cancellationPermitted = true
    }
}));

await SubmitCommand("SubmitApplication");

var operationId = $"offer-generation-{Guid.NewGuid():N}";
var revision = await CurrentRevision();
client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", trustedKey);
await PrintResponse(await client.PostAsJsonAsync(
    $"api/instances/{Uri.EscapeDataString(instanceId)}/expected-operations", new
    {
        operationId,
        observationType = "OfferDocumentGenerated",
        sourceIdentity = "synthetic-offer-provider",
        expectedRevision = revision
    }));

client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
await SubmitObservation(
    $"forged-{Guid.NewGuid():N}",
    "not-expected",
    revision,
    "synthetic-offer-provider",
    new { documentId = "forged-document", offerDigest = "forged-digest" });
client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", trustedKey);

await SubmitObservation(
    $"offer-result-{Guid.NewGuid():N}",
    operationId,
    revision,
    "synthetic-offer-provider",
    new { documentId = "synthetic-offer-1", offerDigest = "sha256:synthetic-offer" });

var lateOperationId = $"late-offer-generation-{Guid.NewGuid():N}";
revision = await CurrentRevision();
await PrintResponse(await client.PostAsJsonAsync(
    $"api/instances/{Uri.EscapeDataString(instanceId)}/expected-operations", new
    {
        operationId = lateOperationId,
        observationType = "OfferDocumentGenerated",
        sourceIdentity = "synthetic-offer-provider",
        expectedRevision = revision
    }));

await SubmitCommand("CancelApplication");
await SubmitObservation(
    $"late-result-{Guid.NewGuid():N}",
    lateOperationId,
    revision,
    "synthetic-offer-provider",
    new { documentId = "late-document", offerDigest = "sha256:late-offer" });
await SubmitCommand("AcceptOffer", new
{
    offerValidAtEvaluation = true,
    offerEvaluationTime = DateTimeOffset.UtcNow.ToString("O")
});

await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/history"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/attempts"));

async Task SubmitCommand(string commandType, object? facts = null)
{
    var currentRevision = await CurrentRevision();
    Console.WriteLine($"Submitting command {commandType} at revision {currentRevision}");
    var request = new Dictionary<string, object?>
    {
        ["messageId"] = $"command-{Guid.NewGuid():N}",
        ["type"] = commandType,
        ["expectedRevision"] = currentRevision,
        ["source"] = "synthetic-applicant"
    };
    if (facts is not null)
    {
        request["facts"] = facts;
    }
    if (commandType == "AcceptOffer")
    {
        request["factsProvenance"] = new Dictionary<string, string>
        {
            ["offerValidAtEvaluation"] = "Illustrative command input; not independently verified by this client.",
            ["offerEvaluationTime"] = "Clock value explicitly supplied by the client."
        };
    }
    await PrintResponse(await client.PostAsJsonAsync(
        $"api/instances/{Uri.EscapeDataString(instanceId)}/commands", request));
}

async Task SubmitObservation(
    string messageId,
    string correlationId,
    long expectedRevision,
    string source,
    object evidence)
{
    Console.WriteLine($"Submitting observation {correlationId} at revision {expectedRevision}");
    await PrintResponse(await client.PostAsJsonAsync(
        $"api/instances/{Uri.EscapeDataString(instanceId)}/observations", new
        {
            messageId,
            type = "OfferDocumentGenerated",
            expectedRevision,
            correlationId,
            source,
            occurredAt = DateTimeOffset.UtcNow,
            evidence
        }));
}

async Task<long> CurrentRevision()
{
    var instance = await client.GetFromJsonAsync<JsonElement>($"api/instances/{Uri.EscapeDataString(instanceId)}");
    return instance.GetProperty("revision").GetInt64();
}

static async Task PrintResponse(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    if (!string.IsNullOrWhiteSpace(body))
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            Console.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (JsonException)
        {
            Console.WriteLine(body);
        }
    }
    response.EnsureSuccessStatusCode();
}

static async Task EnsureSuccess(HttpResponseMessage response)
{
    if (response.IsSuccessStatusCode)
    {
        return;
    }
    var body = await response.Content.ReadAsStringAsync();
    throw new HttpRequestException($"Definition registration failed with {(int)response.StatusCode}: {body}");
}
