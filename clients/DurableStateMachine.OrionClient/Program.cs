using System.Net.Http.Json;
using System.Text.Json;

var apiUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000";
var definitionPath = args.ElementAtOrDefault(0) ?? Path.Combine("definitions", "orion-v1.json");
var instanceId = $"orion-demo-{Guid.NewGuid():N}";
using var client = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };

using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(definitionPath));
await EnsureSuccess(await client.PostAsJsonAsync("api/definitions", definition.RootElement));

var created = await client.PostAsJsonAsync("api/instances", new
{
    id = instanceId,
    definitionId = "orion-equipment-lease",
    definitionVersion = "1.0",
    facts = new
    {
        documentsComplete = true,
        riskScore = 75,
        equipmentValue = 50000,
        checksComplete = true,
        eligibilityDecision = "Eligible"
    }
});
await PrintResponse(created);

await Submit("Submit");
await Submit("IssueOffer");
await Submit("AcceptOffer",
    new Dictionary<string, object?>
    {
        ["offerValidAtEvaluation"] = true,
        ["offerEvaluationTime"] = DateTimeOffset.UtcNow.ToString("O")
    },
    new Dictionary<string, string>
    {
        ["offerValidAtEvaluation"] = "Illustrative trusted evaluation input; not independently verified by this client.",
        ["offerEvaluationTime"] = "Clock value explicitly supplied by the client."
    });
await Submit("Cancel");
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/history"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/attempts"));

async Task Submit(
    string eventName,
    IReadOnlyDictionary<string, object?>? facts = null,
    IReadOnlyDictionary<string, string>? factsProvenance = null)
{
    Console.WriteLine($"Submitting {eventName}");
    var request = new Dictionary<string, object?> { ["event"] = eventName };
    if (facts is not null)
    {
        request["facts"] = facts;
    }
    if (factsProvenance is not null)
    {
        request["factsProvenance"] = factsProvenance;
    }
    await PrintResponse(await client.PostAsJsonAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/events",
        request));
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
