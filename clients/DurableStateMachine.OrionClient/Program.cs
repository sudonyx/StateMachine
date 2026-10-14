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
        eligible = true
    }
});
await PrintResponse(created);

await Submit("Submit");
await Submit("Approve");
await Submit("Cancel");
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/history"));
await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/attempts"));

async Task Submit(string eventName)
{
    Console.WriteLine($"Submitting {eventName}");
    await PrintResponse(await client.PostAsJsonAsync($"api/instances/{Uri.EscapeDataString(instanceId)}/events",
        new { @event = eventName }));
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
