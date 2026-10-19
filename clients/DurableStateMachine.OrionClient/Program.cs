using System.Net.Http.Json;
using System.Text.Json;

var apiUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000";
var trustedKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
var definitionPath = args.ElementAtOrDefault(0) ?? Path.Combine("definitions", "orion-v4.json");
if (string.IsNullOrWhiteSpace(trustedKey))
{
    throw new InvalidOperationException(
        "Set DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY for the controlled Orion effect demonstration.");
}

var instanceId = $"orion-demo-{Guid.NewGuid():N}";
var lateInstanceId = $"orion-late-demo-{Guid.NewGuid():N}";
using var client = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", trustedKey);
using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(definitionPath));
var definitionVersion = definition.RootElement.GetProperty("version").GetString()
    ?? throw new InvalidOperationException("The Orion definition has no version.");
await EnsureSuccess(await client.PostAsJsonAsync("api/definitions", definition.RootElement));

await CreateInstance(instanceId);
await SubmitCommand(instanceId, "SubmitApplication");

var revision = await CurrentRevision(instanceId);
client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
await SubmitObservation(
    instanceId,
    $"forged-{Guid.NewGuid():N}",
    "not-expected",
    revision,
    "synthetic-offer-provider",
    new { documentId = "forged-document", offerDigest = "sha256:forged" });
client.DefaultRequestHeaders.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", trustedKey);

await StartEffect(instanceId, "offer-document", $"offer-{Guid.NewGuid():N}", new
{
    customerReference = "synthetic-customer-1",
    offerReference = "synthetic-offer-1"
});
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/timers"));
await SubmitCommand(instanceId, "AcceptOffer", new
{
    offerValidAtEvaluation = true,
    offerEvaluationTime = DateTimeOffset.UtcNow.ToString("O")
});
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/timers"));
await StartEffect(instanceId, "equipment-reservation", $"reservation-{Guid.NewGuid():N}", new
{
    assetId = "fictional-asset-1"
});
await StartEffect(instanceId, "customer-notification", $"notification-{Guid.NewGuid():N}", new
{
    customerReference = "synthetic-customer-1",
    messageTemplate = "Synthetic offer status update"
});

await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/effects"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/history"));

await CreateInstance(lateInstanceId);
await SubmitCommand(lateInstanceId, "SubmitApplication");
var lateOperationId = $"late-offer-{Guid.NewGuid():N}";
using (var armed = await client.PostAsync(
           $"api/test-controls/effects/{Uri.EscapeDataString(lateOperationId)}/BeforeObservationAcceptance",
           content: null))
{
    await EnsureSuccess(armed);
}
using (var pendingResult = await PostEffect(lateInstanceId, "offer-document", lateOperationId, new
       {
           customerReference = "synthetic-customer-1",
           offerReference = "synthetic-late-offer"
       }))
{
    if (pendingResult.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
    {
        await EnsureSuccess(pendingResult);
        throw new InvalidOperationException("The controlled late-result interruption was not reached.");
    }
    Console.WriteLine("Harness completed the provider effect; Gate 5 observation acceptance is still pending.");
}
await SubmitCommand(lateInstanceId, "CancelApplication");
await PrintResponse(await client.PostAsync(
    $"api/instances/{Uri.EscapeDataString(lateInstanceId)}/effects/{Uri.EscapeDataString(lateOperationId)}/resume",
    content: null));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(lateInstanceId)}"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(lateInstanceId)}/inbound-messages"));

async Task CreateInstance(string id)
{
    await PrintResponse(await client.PostAsJsonAsync("api/instances", new
    {
        id,
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
}

async Task SubmitCommand(string id, string commandType, object? facts = null)
{
    var currentRevision = await CurrentRevision(id);
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
        $"api/instances/{Uri.EscapeDataString(id)}/commands", request));
}

async Task StartEffect(string id, string capability, string operationId, object request)
{
    Console.WriteLine($"Starting synthetic {capability} operation {operationId}");
    await PrintResponse(await PostEffect(id, capability, operationId, request));
}

async Task SubmitObservation(
    string id,
    string messageId,
    string operationId,
    long expectedRevision,
    string source,
    object evidence)
{
    await PrintResponse(await client.PostAsJsonAsync(
        $"api/instances/{Uri.EscapeDataString(id)}/observations",
        new
        {
            messageId,
            type = "OfferDocumentGenerated",
            correlationId = operationId,
            expectedRevision,
            source,
            evidence
        }));
}

async Task<HttpResponseMessage> PostEffect(
    string id,
    string capability,
    string operationId,
    object request)
{
    var body = JsonSerializer.SerializeToElement(request);
    var values = body.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    values["operationId"] = JsonSerializer.SerializeToElement(operationId);
    values["capability"] = JsonSerializer.SerializeToElement(capability);
    return await client.PostAsJsonAsync(
        $"api/instances/{Uri.EscapeDataString(id)}/effects", values);
}

async Task<long> CurrentRevision(string id)
{
    var instance = await client.GetFromJsonAsync<JsonElement>($"api/instances/{Uri.EscapeDataString(id)}");
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
