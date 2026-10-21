using System.Net.Http.Json;
using System.Text.Json;

var apiUrl = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_API_URL") ?? "http://localhost:5000";
var argsList = args.ToList();
if (argsList.Count >= 2 && argsList[0] == "--url")
{
    apiUrl = argsList[1];
    argsList.RemoveRange(0, 2);
}
if (argsList.Count == 0)
{
    PrintUsage();
    return 2;
}

using var client = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };
try
{
    var command = argsList[0];
    switch (command)
    {
        case "register" when argsList.Count == 2:
        {
            using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(argsList[1]));
            await PrintResponse(await client.PostAsJsonAsync("api/definitions", definition.RootElement));
            break;
        }
        case "definitions" when argsList.Count == 1:
            await PrintResponse(await client.GetAsync("api/definitions"));
            break;
        case "migrate-preview" when argsList.Count == 9:
        case "migrate" when argsList.Count == 9:
        {
            if (!long.TryParse(argsList[6], out var expectedRevision) || expectedRevision < 0)
            {
                throw new ArgumentException("Expected revision must be a non-negative integer.");
            }
            var request = new
            {
                migrationId = argsList[7],
                sourceDefinitionId = argsList[2],
                sourceDefinitionVersion = argsList[3],
                targetDefinitionId = argsList[4],
                targetDefinitionVersion = argsList[5],
                expectedRevision,
                authorisationReason = argsList[8]
            };
            if (command == "migrate")
            {
                var key = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
                if (!string.IsNullOrWhiteSpace(key))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "X-State-Machine-Trusted-Ingestion-Key", key);
                }
            }
            var route = $"api/instances/{Uri.EscapeDataString(argsList[1])}";
            await PrintResponse(await client.PostAsJsonAsync(
                command == "migrate" ? $"{route}/migrations" : $"{route}/migration-preview", request));
            break;
        }
        case "migrations" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync(
                $"api/instances/{Uri.EscapeDataString(argsList[1])}/migrations"));
            break;
        case "create" when argsList.Count is 4 or 5:
        {
            JsonDocument? facts = null;
            if (argsList.Count == 5)
            {
                var factsInput = argsList[4];
                var factsJson = factsInput.StartsWith('@')
                    ? await File.ReadAllTextAsync(factsInput[1..])
                    : factsInput;
                factsJson = factsJson.TrimStart('\uFEFF');
                facts = JsonDocument.Parse(factsJson);
            }
            using (facts)
            {
                var request = new
                {
                    id = argsList[1],
                    definitionId = argsList[2],
                    definitionVersion = argsList[3],
                    facts = facts?.RootElement
                };
                await PrintResponse(await client.PostAsJsonAsync("api/instances", request));
            }
            break;
        }
        case "inspect" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}"));
            break;
        case "concerns" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync(
                $"api/instances/{Uri.EscapeDataString(argsList[1])}/concerns"));
            break;
        case "concern-history" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync(
                $"api/instances/{Uri.EscapeDataString(argsList[1])}/concerns/history"));
            break;
        case "concern-replay" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync(
                $"api/instances/{Uri.EscapeDataString(argsList[1])}/concerns/replay"));
            break;
        case "send" when argsList.Count == 3:
            await PrintResponse(await client.PostAsJsonAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/events",
                new { @event = argsList[2] }));
            break;
        case "attempts" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/attempts"));
            break;
        case "history" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/history"));
            break;
        case "receipts" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/inbound-messages"));
            break;
        case "expected-operations" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync(
                $"api/instances/{Uri.EscapeDataString(argsList[1])}/expected-operations"));
            break;
        case "timers" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/timers"));
            break;
        case "effects" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/effects"));
            break;
        case "replay" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/replay"));
            break;
        case "verify-decisions" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync(
                $"api/instances/{Uri.EscapeDataString(argsList[1])}/replay/decision-verification"));
            break;
        case "recovery" when argsList.Count == 2:
            await PrintResponse(await client.GetAsync($"api/instances/{Uri.EscapeDataString(argsList[1])}/recovery"));
            break;
        default:
            PrintUsage();
            return 2;
    }
    return 0;
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"API request failed: {exception.Message}");
    return 1;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"Could not read definition file: {exception.Message}");
    return 1;
}
catch (JsonException exception)
{
    Console.Error.WriteLine($"Invalid JSON: {exception.Message}");
    return 1;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Invalid command input: {exception.Message}");
    return 2;
}

static async Task PrintResponse(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
    if (string.IsNullOrWhiteSpace(body))
    {
        return;
    }
    try
    {
        using var json = JsonDocument.Parse(body);
        Console.WriteLine(JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        var attempt = json.RootElement.ValueKind == JsonValueKind.Object &&
            json.RootElement.TryGetProperty("attempt", out var value) ? value : default;
        if (attempt.ValueKind == JsonValueKind.Object && attempt.TryGetProperty("status", out var status))
        {
            Console.WriteLine($"Workflow result: {status.GetString()} ({attempt.GetProperty("reasonCode").GetString()})");
        }
    }
    catch (JsonException)
    {
        Console.WriteLine(body);
    }
    response.EnsureSuccessStatusCode();
}

static void PrintUsage() =>
    Console.WriteLine("""
        Usage: dsm [--url http://localhost:5000] <command>
          register <definition.json>
          definitions
          migrate-preview <instance-id> <source-id> <source-version> <target-id> <target-version> <expected-revision> <migration-id> <reason>
          migrate <instance-id> <source-id> <source-version> <target-id> <target-version> <expected-revision> <migration-id> <reason>
          migrations <instance-id>
          create <instance-id> <definition-id> <version> [facts-json | @facts-file]
          inspect <instance-id>
          concerns <instance-id>
          concern-history <instance-id>
          concern-replay <instance-id>
          send <instance-id> <event>
          attempts <instance-id>
          history <instance-id>
          receipts <instance-id>
          expected-operations <instance-id>
          timers <instance-id>
          effects <instance-id>
          replay <instance-id>
          verify-decisions <instance-id>
          recovery <instance-id>
        Set DURABLE_STATE_MACHINE_API_URL to configure the API URL.
        Set DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY for development-only migration execution.
        """);
