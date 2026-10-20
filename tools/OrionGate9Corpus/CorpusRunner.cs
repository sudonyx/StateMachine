using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrionGate9Corpus;

public static class CorpusRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args, string root)
    {
        var mode = Option(args, "--mode", "baseline");
        var parallelism = int.Parse(Option(args, "--concurrency", mode == "baseline" ? "1" : "8"));
        var seed = int.Parse(Option(args, "--seed", CorpusGenerator.DefaultSeed.ToString()));
        var runId = Option(args, "--run-id", $"gate9-{Guid.NewGuid():N}");
        var key = Option(args, "--trusted-key", Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY") ?? "");
        var corpusPath = Option(args, "--corpus", Path.Combine(root, "test-data", "orion-gate9-deliveries.ndjson"));
        var definitionPath = Option(args, "--definition", Path.Combine(root, "definitions", "orion-v4.json"));
        var reportPath = Option(args, "--report", Path.Combine(Path.GetTempPath(), $"orion-gate9-{runId}.json"));
        var hosts = Values(args, "--host-url").ToList();
        var baseUrl = Option(args, "--base-url", "");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            hosts.Insert(0, baseUrl);
        }
        if (hosts.Count == 0 || hosts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Provide --base-url or one or more --host-url values.");
        }
        if (mode is not ("baseline" or "concurrent" or "multi-host"))
        {
            throw new ArgumentException("--mode must be baseline, concurrent, or multi-host.");
        }
        if (parallelism is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(parallelism), "Concurrency must be between 1 and 64.");
        }
        if (mode == "multi-host" && hosts.Count < 2)
        {
            throw new ArgumentException("Multi-host mode requires at least two --host-url endpoints sharing one database.");
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Provide --trusted-key for the required trusted observation paths.");
        }

        var corpusBytes = File.ReadAllBytes(corpusPath);
        var records = CorpusGenerator.Deserialize(corpusBytes);
        var validation = CorpusValidator.Validate(records, corpusBytes, seed, definitionPath);
        var definitionBytes = await File.ReadAllBytesAsync(definitionPath);
        using var clients = new RunnerClients(hosts);
        var setupClient = clients.Get(0);
        var registerDefinition = await setupClient.PostAsync("/api/definitions",
            new ByteArrayContent(definitionBytes) { Headers = { ContentType = new("application/json") } });
        await EnsureSuccess(registerDefinition, "register Orion definition");

        var recordsByApplication = records
            .GroupBy(record => record.ApplicationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(record => record.DeliveredAt).ToArray(), StringComparer.Ordinal);
        foreach (var (applicationId, applicationRecords) in recordsByApplication)
        {
            var instanceId = InstanceId(runId, applicationId);
            var create = new
            {
                id = instanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "4.0",
                facts = applicationRecords[0].InstanceFacts,
            };
            await EnsureSuccess(await setupClient.PostAsJsonAsync("/api/instances", create, JsonOptions),
                $"create instance {instanceId}");
        }

        var stopwatch = Stopwatch.StartNew();
        var outcomes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var failures = new ConcurrentQueue<string>();
        var clientSequence = -1;
        var laneCount = mode == "baseline" ? 1 : parallelism;
        await Parallel.ForEachAsync(
            recordsByApplication,
            new ParallelOptions { MaxDegreeOfParallelism = laneCount },
            async (application, cancellationToken) =>
            {
                var appId = application.Key;
                var instanceId = InstanceId(runId, appId);
                var appRecords = application.Value;
                var registeredOperations = new HashSet<string>(StringComparer.Ordinal);
                var handledRaceGroups = new HashSet<string>(StringComparer.Ordinal);

                async Task EnsureOperationsAtRevision(long revision)
                {
                    var candidates = appRecords
                        .Where(record => record.InputKind == "Observation" && record.ExpectedRevision == revision)
                        .GroupBy(record => record.CorrelationId, StringComparer.Ordinal)
                        .Where(group => !string.IsNullOrWhiteSpace(group.Key));
                    foreach (var candidate in candidates)
                    {
                        var operationId = candidate.Key!;
                        if (!registeredOperations.Add(operationId))
                        {
                            continue;
                        }
                        var sample = candidate.First();
                        var registerClient = clients.Get(NextClient());
                        using var registerRequest = new HttpRequestMessage(HttpMethod.Post,
                            $"/api/instances/{Uri.EscapeDataString(instanceId)}/expected-operations")
                        {
                            Content = JsonContent.Create(new
                            {
                                operationId,
                                observationType = sample.InputType,
                                sourceIdentity = sample.Source,
                                expectedRevision = revision,
                            }, options: JsonOptions),
                        };
                        registerRequest.Headers.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", key);
                        using var register = await registerClient.SendAsync(registerRequest, cancellationToken);
                        if (register.StatusCode != HttpStatusCode.Created)
                        {
                            failures.Enqueue($"Expected-operation setup failed for {instanceId}/{operationId}: {(int)register.StatusCode} {await register.Content.ReadAsStringAsync(cancellationToken)}");
                        }
                    }
                }

                async Task<DeliveryOutcome> Send(DeliveryRecord record)
                {
                    var client = clients.Get(NextClient());
                    var route = record.InputKind == "Observation" ? "observations" : "commands";
                    var body = new Dictionary<string, object?>
                    {
                        ["messageId"] = record.MessageId,
                        ["deliveryId"] = record.DeliveryId,
                        ["operationId"] = record.OperationId,
                        ["observationId"] = record.ObservationId,
                        ["type"] = record.InputType,
                        ["expectedRevision"] = record.ExpectedRevision,
                        ["correlationId"] = record.CorrelationId,
                        ["source"] = record.Source,
                        ["occurredAt"] = record.OccurredAt,
                        ["payload"] = record.Payload,
                        ["facts"] = record.Facts,
                        ["evidence"] = record.Evidence,
                    };
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/{route}")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
                    };
                    if (record.InputKind == "Observation")
                    {
                        request.Headers.TryAddWithoutValidation("X-State-Machine-Trusted-Ingestion-Key", key);
                    }
                    try
                    {
                        using var response = await client.SendAsync(request, cancellationToken);
                        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            outcomes.AddOrUpdate("http-error", 1, (_, count) => count + 1);
                            failures.Enqueue($"{record.DeliveryId} returned {(int)response.StatusCode}: {responseBody}");
                            return new DeliveryOutcome(record, "HttpError", null);
                        }
                        using var document = JsonDocument.Parse(responseBody);
                        var rootElement = document.RootElement;
                        var status = rootElement.GetProperty("message").GetProperty("status").GetString()!;
                        outcomes.AddOrUpdate(status, 1, (_, count) => count + 1);
                        var revision = rootElement.GetProperty("instance").GetProperty("revision").GetInt64();
                        return new DeliveryOutcome(record, status, revision);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
                    {
                        outcomes.AddOrUpdate("transport-error", 1, (_, count) => count + 1);
                        failures.Enqueue($"{record.DeliveryId}: {exception.Message}");
                        return new DeliveryOutcome(record, "TransportError", null);
                    }
                }

                for (var index = 0; index < appRecords.Length; index++)
                {
                    var record = appRecords[index];
                    if (mode != "baseline" && record.RaceGroup is not null
                        && handledRaceGroups.Add(record.RaceGroup))
                    {
                        var racePair = appRecords.Where(item => item.RaceGroup == record.RaceGroup).ToArray();
                        var results = await Task.WhenAll(racePair.Select(Send));
                        var current = await clients.Get(NextClient()).GetFromJsonAsync<JsonElement>(
                            $"/api/instances/{Uri.EscapeDataString(instanceId)}", JsonOptions, cancellationToken);
                        if (current.ValueKind == JsonValueKind.Object)
                        {
                            await EnsureOperationsAtRevision(current.GetProperty("revision").GetInt64());
                        }
                        continue;
                    }
                    if (record.RaceGroup is not null && mode != "baseline")
                    {
                        var sameGroupEarlier = appRecords.Take(index).Any(item => item.RaceGroup == record.RaceGroup);
                        if (sameGroupEarlier)
                        {
                            continue;
                        }
                    }
                    if (record.RaceGroup is not null && mode == "baseline")
                    {
                        handledRaceGroups.Add(record.RaceGroup);
                    }
                    var result = await Send(record);
                    if (result.Revision.HasValue)
                    {
                        await EnsureOperationsAtRevision(result.Revision.Value);
                    }
                }

                int NextClient() => Interlocked.Increment(ref clientSequence);
            });
        stopwatch.Stop();

        var invariantFailures = new ConcurrentQueue<string>();
        await Parallel.ForEachAsync(
            recordsByApplication.Keys,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(parallelism, 16) },
            async (applicationId, cancellationToken) =>
            {
                var instanceId = InstanceId(runId, applicationId);
                try
                {
                    var client = clients.Get(NextClient());
                    var instance = await client.GetFromJsonAsync<JsonElement>(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}", JsonOptions, cancellationToken);
                    var history = await client.GetFromJsonAsync<JsonElement[]>(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/history", JsonOptions, cancellationToken) ?? [];
                    var messages = await client.GetFromJsonAsync<JsonElement[]>(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages", JsonOptions, cancellationToken) ?? [];
                    var timers = await client.GetFromJsonAsync<JsonElement[]>(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/timers", JsonOptions, cancellationToken) ?? [];
                    var effects = await client.GetFromJsonAsync<JsonElement[]>(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/effects", JsonOptions, cancellationToken) ?? [];

                    var revision = instance.GetProperty("revision").GetInt64();
                    if (revision != history.Length)
                    {
                        invariantFailures.Enqueue($"{instanceId}: revision {revision} differs from {history.Length} history entries.");
                    }
                    for (var index = 0; index < history.Length; index++)
                    {
                        var transition = history[index];
                        if (transition.GetProperty("revisionBefore").GetInt64() != index
                            || transition.GetProperty("revisionAfter").GetInt64() != index + 1)
                        {
                            invariantFailures.Enqueue($"{instanceId}: non-contiguous transition revision at history index {index}.");
                        }
                        if (index > 0 && transition.GetProperty("previousState").GetString()
                            != history[index - 1].GetProperty("newState").GetString())
                        {
                            invariantFailures.Enqueue($"{instanceId}: transition history state chain is inconsistent.");
                        }
                    }
                    var accepted = messages.Where(message => message.GetProperty("status").GetString() == "Accepted")
                        .GroupBy(message => $"{message.GetProperty("inputKind").GetString()}:{message.GetProperty("messageId").GetString()}",
                            StringComparer.Ordinal);
                    if (accepted.Any(group => group.Count() > 1))
                    {
                        invariantFailures.Enqueue($"{instanceId}: one logical input has multiple accepted receipts.");
                    }
                    var timerKeys = timers.Select(timer =>
                        $"{timer.GetProperty("timerDeclarationId").GetString()}:{timer.GetProperty("originTransitionHistoryId").GetString()}");
                    if (timerKeys.Distinct(StringComparer.Ordinal).Count() != timers.Length)
                    {
                        invariantFailures.Enqueue($"{instanceId}: duplicate logical timer schedules exist.");
                    }
                    var effectIds = effects.Select(effect => effect.GetProperty("operationId").GetString()).ToArray();
                    if (effectIds.Distinct(StringComparer.Ordinal).Count() != effectIds.Length)
                    {
                        invariantFailures.Enqueue($"{instanceId}: duplicate effect operation IDs exist.");
                    }
                    if (history.Length > 0 && history[^1].GetProperty("newState").GetString()
                        != instance.GetProperty("currentState").GetString())
                    {
                        invariantFailures.Enqueue($"{instanceId}: current state differs from the final accepted history entry.");
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
                {
                    invariantFailures.Enqueue($"{instanceId}: integrity inspection failed: {exception.Message}");
                }
            });

        var report = new
        {
            corpusSeed = seed,
            corpusIdentifier = Convert.ToHexString(SHA256.HashData(corpusBytes)),
            corpusValidation = validation,
            runId,
            instanceCount = recordsByApplication.Count,
            deliveryCount = records.Count,
            mode,
            concurrency = parallelism,
            hostCount = hosts.Count,
            environment = Environment.OSVersion.ToString(),
            acceptedTransitions = outcomes.GetValueOrDefault("Accepted"),
            outcomes = outcomes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            failedInvariants = invariantFailures.ToArray(),
            transportFailures = failures.Take(100).ToArray(),
            durationMilliseconds = stopwatch.ElapsedMilliseconds,
            reportPath,
        };
        var reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, reportJson);
        Console.WriteLine(reportJson);
        Console.WriteLine($"Structured report: {reportPath}");
        return invariantFailures.IsEmpty && failures.IsEmpty ? 0 : 2;

        int NextClient() => Interlocked.Increment(ref clientSequence);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string operation)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{operation} failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }

    private static string InstanceId(string runId, string applicationId) =>
        $"{runId}-{applicationId}";

    private static string Option(string[] args, string option, string fallback)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0)
        {
            return fallback;
        }
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option {option} requires a value.");
        }
        return args[index + 1];
    }

    private static IEnumerable<string> Values(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == option)
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException($"Option {option} requires a value.");
                }
                yield return args[++index];
            }
        }
    }

    private sealed record DeliveryOutcome(DeliveryRecord Record, string Status, long? Revision);

    private sealed class RunnerClients : IDisposable
    {
        private readonly HttpClient[] clients;
        public RunnerClients(IReadOnlyList<string> hosts) =>
            clients = hosts.Select(host => new HttpClient
            {
                BaseAddress = new Uri(host.EndsWith("/", StringComparison.Ordinal) ? host : host + "/"),
                Timeout = TimeSpan.FromMinutes(2),
            }).ToArray();

        public HttpClient Get(int index) => clients[(uint)index % (uint)clients.Length];
        public void Dispose()
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }
}
