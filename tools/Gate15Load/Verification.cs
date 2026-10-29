using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Gate15Load;

internal static class Gate15Verifier
{
    public static async Task<int> RunAsync(
        string[] args,
        string corpusDirectory,
        int seed,
        int instanceCount,
        int deliveryCount,
        int historyDays,
        string profile)
    {
        var baseUrl = Program.Option(args, "--base-url", "");
        var runId = Program.Option(args, "--run-id", "gate15-full-20261029");
        var concurrency = int.Parse(Program.Option(args, "--concurrency", "4"));
        var sampleSize = int.Parse(Program.Option(args, "--sample-size", "1000"));
        var scenario = Program.Option(args, "--scenario", "all");
        var reportPath = Program.Option(args, "--report", Path.Combine(corpusDirectory, "correctness-results.json"));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Supply an HTTP --base-url for the restarted isolated StateMachine API.");
        }
        if (concurrency is < 1 or > 16 || sampleSize < 1
            || scenario is not ("all" or "ordinary" or "adversarial" or "long-history"))
        {
            throw new ArgumentOutOfRangeException(nameof(concurrency), "Concurrency must be 1-16, sample size positive, and scenario all, ordinary, adversarial, or long-history.");
        }

        var corpusValidation = Gate15Corpus.Validate(corpusDirectory, seed, instanceCount, deliveryCount, historyDays, profile);
        var instances = Gate15Corpus.ReadNdjson<Gate15Instance>(Path.Combine(corpusDirectory, "instances.ndjson"))
            .Where(item => Program.MatchesScenario(item.ScenarioProfile, scenario)).ToArray();
        var selectedIds = instances.Select(item => item.InstanceId).ToHashSet(StringComparer.Ordinal);
        var deliveries = Gate15Corpus.ReadNdjson<Gate15Delivery>(Path.Combine(corpusDirectory, "deliveries.ndjson"))
            .Where(item => selectedIds.Contains(item.InstanceId)).ToArray();
        var expectedDuplicateReceipts = deliveries.GroupBy(
                item => (item.InstanceId, item.MessageId))
            .Sum(group => Math.Max(0, group.Count() - 1));
        var expectedConcurrentPairs = deliveries.Count(item => item.ConcurrentGroup is not null) / 2;
        var deliveriesByInstance = deliveries.GroupBy(item => item.InstanceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var failures = new ConcurrentQueue<string>();
        var currentReadTicks = new ConcurrentBag<long>();
        var replayTicks = new ConcurrentBag<long>();
        var pendingReadTicks = new ConcurrentBag<long>();
        var historiesChecked = 0;
        var receiptsChecked = 0;
        var acceptedTransitionsChecked = 0;
        var timerTransitionsChecked = 0;
        var duplicateReceiptsChecked = 0;
        var concurrentPairsChecked = 0;
        var concurrentConflictReceiptsChecked = 0;
        var pendingInstances = 0;
        var replayed = 0;
        using var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) };

        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(instances, new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (corpusInstance, cancellationToken) =>
            {
                var instanceId = $"{runId}-{corpusInstance.InstanceId}";
                var currentTicks = Stopwatch.GetTimestamp();
                var current = await GetJsonAsync($"/api/instances/{Uri.EscapeDataString(instanceId)}", cancellationToken);
                currentReadTicks.Add(Stopwatch.GetTimestamp() - currentTicks);

                var history = await GetJsonAsync($"/api/instances/{Uri.EscapeDataString(instanceId)}/history", cancellationToken);
                var receipts = await GetJsonAsync($"/api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages", cancellationToken);
                var replayTicksStart = Stopwatch.GetTimestamp();
                var replay = await GetJsonAsync($"/api/instances/{Uri.EscapeDataString(instanceId)}/replay", cancellationToken);
                replayTicks.Add(Stopwatch.GetTimestamp() - replayTicksStart);

                var historyItems = history.EnumerateArray().ToArray();
                var receiptItems = receipts.EnumerateArray().ToArray();
                Interlocked.Add(ref historiesChecked, historyItems.Length);
                Interlocked.Add(ref receiptsChecked, receiptItems.Length);
                Interlocked.Increment(ref replayed);

                var revision = current.GetProperty("revision").GetInt64();
                var state = current.GetProperty("currentState").GetString();
                if (historyItems.LongLength != revision)
                {
                    failures.Enqueue($"{instanceId}: history count {historyItems.Length} differs from revision {revision}.");
                }
                for (var index = 0; index < historyItems.Length; index++)
                {
                    if (historyItems[index].GetProperty("revisionBefore").GetInt64() != index
                        || historyItems[index].GetProperty("revisionAfter").GetInt64() != index + 1
                        || index > 0 && historyItems[index].GetProperty("previousState").GetString()
                            != historyItems[index - 1].GetProperty("newState").GetString())
                    {
                        failures.Enqueue($"{instanceId}: accepted history does not form a contiguous revision/state chain.");
                        break;
                    }
                }
                if (receiptItems.Length != deliveriesByInstance[corpusInstance.InstanceId].Length)
                {
                    failures.Enqueue($"{instanceId}: receipt count {receiptItems.Length} differs from corpus delivery count {deliveriesByInstance[corpusInstance.InstanceId].Length}.");
                }
                var receiptsByDeliveryId = receiptItems.ToDictionary(
                    item => item.GetProperty("deliveryId").GetString()!,
                    StringComparer.Ordinal);
                foreach (var pair in deliveriesByInstance[corpusInstance.InstanceId]
                             .Where(item => item.ConcurrentGroup is not null)
                             .GroupBy(item => item.ConcurrentGroup, StringComparer.Ordinal))
                {
                    var pairReceipts = pair.Where(item => receiptsByDeliveryId.ContainsKey(item.DeliveryId))
                        .Select(item => receiptsByDeliveryId[item.DeliveryId])
                        .ToArray();
                    var acceptedCount = pairReceipts.Count(item => item.GetProperty("status").GetString() == "Accepted");
                    var conflictCount = pairReceipts.Count(item =>
                        item.GetProperty("status").GetString() == "Stale"
                        && item.GetProperty("reasonCode").GetString() == "stale-revision");
                    if (pair.Count() != 2 || pairReceipts.Length != 2 || acceptedCount != 1 || conflictCount != 1)
                    {
                        failures.Enqueue($"{instanceId}: concurrent group {pair.Key} did not produce exactly one accepted input and one stale-revision conflict.");
                    }
                    else
                    {
                        Interlocked.Increment(ref concurrentPairsChecked);
                        Interlocked.Increment(ref concurrentConflictReceiptsChecked);
                    }
                }
                if (replay.GetProperty("outcome").GetString() != "Consistent"
                    || replay.GetProperty("stateReconstructionOutcome").GetString() != "Consistent"
                    || replay.GetProperty("reconstructedFinalState").GetString() != state
                    || replay.GetProperty("recordedRevision").GetInt64() != revision
                    || replay.GetProperty("inboundReceiptsExamined").GetInt32() != receiptItems.Length
                    || replay.GetProperty("acceptedTransitionsExamined").GetInt32() != historyItems.Length)
                {
                    failures.Enqueue($"{instanceId}: durable replay does not agree with current projection, history, and receipts.");
                }

                var accepted = receiptItems.Count(item => item.GetProperty("status").GetString() == "Accepted");
                Interlocked.Add(ref acceptedTransitionsChecked, accepted);
                var firedTimerCount = 0;
                if (corpusInstance.ScenarioProfile is "timed-external-wait" or "long-history-timed-wait")
                {
                    var timers = await GetJsonAsync(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/timers", cancellationToken);
                    var timerItems = timers.EnumerateArray().ToArray();
                    firedTimerCount = timerItems.Count(item => item.GetProperty("status").GetString() == "Fired");
                    if (timerItems.Length == 0)
                    {
                        failures.Enqueue($"{instanceId}: expected timer was not durably created for the external wait.");
                    }
                    Interlocked.Add(ref timerTransitionsChecked, firedTimerCount);
                    if (timerItems.Length > 1)
                    {
                        failures.Enqueue($"{instanceId}: expected one durable timer, found {timerItems.Length}.");
                    }
                }
                if (accepted + firedTimerCount != historyItems.Length)
                {
                    failures.Enqueue($"{instanceId}: accepted input receipts plus fired timers ({accepted + firedTimerCount}) differ from history count {historyItems.Length}.");
                }
                var repeatedReceipts = receiptItems.GroupBy(item => item.GetProperty("messageId").GetString(), StringComparer.Ordinal)
                    .Where(group => group.Count() > 1).ToArray();
                foreach (var duplicateGroup in repeatedReceipts)
                {
                    var duplicateCount = duplicateGroup.Count(item => item.GetProperty("status").GetString() == "Duplicate");
                    if (duplicateCount != duplicateGroup.Count() - 1)
                    {
                        failures.Enqueue($"{instanceId}: repeated message identity {duplicateGroup.Key} did not resolve to one original plus duplicate receipts.");
                    }
                    Interlocked.Add(ref duplicateReceiptsChecked, duplicateCount);
                }

                var terminalHistoryIndices = historyItems.Select((item, index) => (item, index))
                    .Where(pair => pair.item.GetProperty("newState").GetString() is "Completed" or "Cancelled" or "TimedOut")
                    .Select(pair => pair.index)
                    .ToArray();
                if (terminalHistoryIndices.Length > 0 && terminalHistoryIndices[^1] != historyItems.Length - 1)
                {
                    failures.Enqueue($"{instanceId}: history contains an accepted transition after a terminal state.");
                }

                if (replay.GetProperty("definitionId").GetString() != corpusInstance.DefinitionId
                    || replay.GetProperty("definitionVersion").GetString() != corpusInstance.DefinitionVersion)
                {
                    failures.Enqueue($"{instanceId}: replay is not bound to the corpus definition identity/version.");
                }

            });
        stopwatch.Stop();

        var sampleStride = Math.Max(1, instances.Length / sampleSize);
        var pendingSamples = instances.Where((_, index) => index % sampleStride == 0)
            .Take(sampleSize).ToArray();
        await Parallel.ForEachAsync(pendingSamples, new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (item, cancellationToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                var pending = await GetJsonAsync(
                    $"/api/instances/{Uri.EscapeDataString($"{runId}-{item.InstanceId}")}/pending-work",
                    cancellationToken);
                pendingReadTicks.Add(Stopwatch.GetTimestamp() - started);
                var timers = pending.GetProperty("timers").EnumerateArray().Count();
                if (timers > 0)
                {
                    Interlocked.Increment(ref pendingInstances);
                }
            });

        if (duplicateReceiptsChecked != expectedDuplicateReceipts)
        {
            failures.Enqueue($"Verified duplicate receipts {duplicateReceiptsChecked} differ from selected-scenario corpus duplicates {expectedDuplicateReceipts}.");
        }
        if (concurrentPairsChecked != expectedConcurrentPairs)
        {
            failures.Enqueue($"Verified concurrent pairs {concurrentPairsChecked} differ from selected-scenario corpus pairs {expectedConcurrentPairs}.");
        }

        var report = new Gate15CorrectnessReport(
            seed,
            scenario,
            instances.Length,
            deliveries.Length,
            instances.Length,
            receiptsChecked,
            historiesChecked,
            acceptedTransitionsChecked,
            timerTransitionsChecked,
            duplicateReceiptsChecked,
            concurrentPairsChecked,
            concurrentConflictReceiptsChecked,
            replayed,
            pendingSamples.Length,
            pendingInstances,
            stopwatch.Elapsed.TotalSeconds,
            Distribution(currentReadTicks),
            Distribution(replayTicks),
            Distribution(pendingReadTicks),
            failures.ToArray(),
            corpusValidation);
        File.WriteAllBytes(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, Gate15Corpus.JsonOptionsIndented));
        Console.WriteLine(JsonSerializer.Serialize(report, Gate15Corpus.JsonOptionsIndented));
        return failures.IsEmpty ? 0 : 1;

        async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(path, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new HttpRequestException($"GET {path} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return document.RootElement.Clone();
        }
    }

    private static LatencyDistribution Distribution(IEnumerable<long> ticks)
    {
        var durations = ticks.Select(value => value * 1000d / Stopwatch.Frequency).Order().ToArray();
        if (durations.Length == 0)
        {
            return new LatencyDistribution(0, 0, 0, 0, 0, 0);
        }
        return new LatencyDistribution(
            durations.Length,
            durations.Average(),
            Percentile(durations, 0.50),
            Percentile(durations, 0.95),
            Percentile(durations, 0.99),
            durations[^1]);
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)];

    private sealed record LatencyDistribution(
        int Count,
        double MeanMilliseconds,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds);

    private sealed record Gate15CorrectnessReport(
        int Seed,
        string Scenario,
        int CorpusInstances,
        int CorpusDeliveries,
        int InstancesChecked,
        int ReceiptsChecked,
        int HistoriesChecked,
        int AcceptedTransitionsChecked,
        int TimerTransitionsChecked,
        int DuplicateReceiptsChecked,
        int ConcurrentPairsChecked,
        int ConcurrentConflictReceiptsChecked,
        int InstancesReplayed,
        int PendingWorkQuerySampleSize,
        int PendingWorkInstancesObserved,
        double TotalCheckElapsedSeconds,
        LatencyDistribution CurrentStateQueryLatency,
        LatencyDistribution HistoricalReplayLatency,
        LatencyDistribution PendingWorkQueryLatency,
        IReadOnlyList<string> InvariantFailures,
        Gate15Validation CorpusValidation);
}
