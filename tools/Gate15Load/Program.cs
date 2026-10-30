using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gate15Load;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault() ?? "help";
            var output = Option(args, "--output", Path.Combine("artifacts", "gate15"));
            var seed = int.Parse(Option(args, "--seed", Gate15Corpus.DefaultSeed.ToString()));
            var instanceCount = int.Parse(Option(args, "--instances", Gate15Corpus.DefaultInstanceCount.ToString()));
            var deliveryCount = int.Parse(Option(args, "--deliveries", Gate15Corpus.DefaultDeliveryCount.ToString()));
            var historyDays = int.Parse(Option(args, "--history-days", Gate15Corpus.HistoryDays.ToString()));
            var profile = Option(args, "--profile", Gate15Corpus.DefaultProfile);

            switch (command)
            {
                case "generate":
                    Gate15Corpus.Generate(output, seed, instanceCount, deliveryCount, historyDays, profile);
                    Console.WriteLine($"Generated {instanceCount} instances and {deliveryCount} deliveries in {Path.GetFullPath(output)}.");
                    return 0;
                case "validate":
                    Console.WriteLine(JsonSerializer.Serialize(Gate15Corpus.Validate(output, seed, instanceCount, deliveryCount, historyDays, profile),
                        Gate15Corpus.JsonOptionsIndented));
                    return 0;
                case "determinism":
                    Gate15Corpus.VerifyDeterminism(seed, instanceCount, deliveryCount, historyDays, profile);
                    Console.WriteLine("All generated file hashes match across independent output directories.");
                    return 0;
                case "run":
                    return await Gate15Runner.RunAsync(args, output, seed, instanceCount, deliveryCount, historyDays, profile);
                case "verify":
                    return await Gate15Verifier.RunAsync(args, output, seed, instanceCount, deliveryCount, historyDays, profile);
                case "storage-attribution":
                    return await Gate15StorageAttribution.RunAsync(args);
                default:
                    Console.WriteLine("Commands: generate | validate | determinism | run | verify | storage-attribution");
                    return command == "help" ? 0 : 2;
            }

        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    internal static string Option(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0)
        {
            return fallback;
        }
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option {name} requires a value.");
        }
        return args[index + 1];
    }

    internal static bool MatchesScenario(string scenarioProfile, string selectedScenario) =>
        selectedScenario switch
        {
            "all" => true,
            "ordinary" => scenarioProfile is "pending" or "terminal-completed" or "timed-external-wait",
            "adversarial" => scenarioProfile is "competing-transition" or "cancelled",
            "long-history" => scenarioProfile is "long-history-pending" or "long-history-timed-wait",
            _ => false,
        };
}

public sealed record Gate15Instance(
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("definition_id")] string DefinitionId,
    [property: JsonPropertyName("definition_version")] string DefinitionVersion,
    [property: JsonPropertyName("business_created_at")] DateTimeOffset BusinessCreatedAt,
    [property: JsonPropertyName("facts")] SortedDictionary<string, object?> Facts,
    [property: JsonPropertyName("scenario_profile")] string ScenarioProfile);

public sealed record Gate15Delivery(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("input_type")] string InputType,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset DeliveredAt,
    [property: JsonPropertyName("expected_revision")] long ExpectedRevision,
    [property: JsonPropertyName("payload")] SortedDictionary<string, object?> Payload,
    [property: JsonPropertyName("concurrent_group")] string? ConcurrentGroup);

public sealed record Gate15Manifest(
    int Seed,
    int InstanceCount,
    int DeliveryCount,
    int HistoryDays,
    int DuplicateDeliveries,
    int LateDeliveries,
    int ConcurrentPairs,
    IReadOnlyDictionary<string, int> ScenarioCounts,
    string DefinitionId,
    string DefinitionVersion,
    string WorkloadProfile,
    string DefinitionSha256,
    IReadOnlyDictionary<string, string> Sha256);

public sealed record Gate15TimerSchedule(
    string InstanceId,
    DateTimeOffset TimerDueAt,
    DateTimeOffset SimulatedDowntimeStart,
    DateTimeOffset SimulatedDowntimeEnd);

public sealed record Gate15ConcurrentGroup(
    string GroupId,
    string InstanceId,
    string FirstDeliveryId,
    string SecondDeliveryId);

public sealed record Gate15Validation(
    int Instances,
    int Deliveries,
    int ExactDuplicateDeliveries,
    int LateDeliveries,
    int ConcurrentPairs,
    int DistinctBusinessDays,
    bool StableOrdering,
    bool NoAnswerColumns,
    bool ManifestMatches);

public static class Gate15Corpus
{
    public const int DefaultSeed = 20261029;
    public const int DefaultInstanceCount = 10_000;
    public const int DefaultDeliveryCount = 100_000;
    public const int HistoryDays = 180;
    public const string DefinitionId = "gate15-generic-load";
    public const string DefinitionVersion = "1.0";
    public const string DefaultProfile = "generic-durable-command-mix";
    private static readonly DateTimeOffset HistoryStart = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] DataFiles =
    [
        "instances.ndjson",
        "deliveries.ndjson",
        "timer-schedule.ndjson",
        "concurrent-groups.ndjson",
    ];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly JsonSerializerOptions JsonOptionsIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Generate(
        string directory,
        int seed,
        int instanceCount,
        int deliveryCount,
        int historyDays = HistoryDays,
        string profile = DefaultProfile)
    {
        ValidateParameters(instanceCount, deliveryCount, historyDays, profile);
        Directory.CreateDirectory(directory);

        var duplicateCount = (int)Math.Ceiling(deliveryCount * 0.02);
        var lateCount = (int)Math.Ceiling(deliveryCount * 0.01);
        var pairCount = (int)Math.Ceiling(deliveryCount * 0.005);
        var cancelledCount = Math.Max(1, instanceCount / 10);
        var completedCount = Math.Max(1, instanceCount / 10);
        if (cancelledCount + pairCount + completedCount >= instanceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount),
                "The instance count must leave distinct instances for all lifecycle profiles.");
        }

        var random = new Random(seed);
        var instances = new Gate15Instance[instanceCount];
        var perInstanceDeliveries = new List<Gate15Delivery>[instanceCount];
        var nextId = 1;
        for (var index = 0; index < instanceCount; index++)
        {
            var baseDays = instanceCount == 1 ? 0 : (double)index / (instanceCount - 1) * (historyDays - 62);
            var createdAt = HistoryStart.AddDays(baseDays);
            var longHistory = index % 5 == 0 || index == instanceCount - 1;
            var isCancellation = index >= instanceCount - cancelledCount;
            var isCompetition = index < pairCount;
            var isCompleted = !isCancellation && !isCompetition
                              && index < pairCount + completedCount;
            var isWaiting = !isCancellation && !isCompetition && !isCompleted && index % 4 == 0;
            var scenarioProfile = isCancellation ? "cancelled"
                : isCompetition ? "competing-transition"
                : isCompleted ? "terminal-completed"
                : isWaiting ? longHistory ? "long-history-timed-wait" : "timed-external-wait"
                : longHistory ? "long-history-pending" : "pending";
            instances[index] = new Gate15Instance(
                InstanceId(index + 1),
                DefinitionId,
                DefinitionVersion,
                createdAt,
                new SortedDictionary<string, object?>(StringComparer.Ordinal),
                scenarioProfile);
            perInstanceDeliveries[index] = [];
            if (index < pairCount)
            {
                var groupId = $"race-{index + 1:000000}";
                var competingAt = createdAt.AddSeconds(1);
                var begin = Add(index, "Begin", "BeginWork", $"begin-race-{index + 1:000000}",
                    competingAt, competingAt.AddMilliseconds(10), 0, groupId, "begin");
                var cancel = Add(index, "Cancel", "CancelWork", $"cancel-race-{index + 1:000000}",
                    competingAt, competingAt.AddMilliseconds(20), 0, groupId, "cancel");
                perInstanceDeliveries[index].Add(begin);
                perInstanceDeliveries[index].Add(cancel);
            }
            else if (isCancellation)
            {
                perInstanceDeliveries[index].Add(Add(index, "Cancel", "CancelWork",
                    $"cancel-{index + 1:000000}", createdAt, createdAt.AddSeconds(1), 0));
            }
            else
            {
                perInstanceDeliveries[index].Add(Add(index, "Begin", "BeginWork", "begin",
                    createdAt, createdAt.AddSeconds(1), 0));
                if (isCompleted)
                {
                    perInstanceDeliveries[index].Add(Add(index, "Complete", "CompleteWork",
                        $"complete-{index + 1:000000}", createdAt.AddSeconds(2), createdAt.AddSeconds(3), 1));
                }
                else if (isWaiting)
                {
                    perInstanceDeliveries[index].Add(Add(index, "WaitForExternal", "WaitForExternal",
                        $"wait-{index + 1:000000}", createdAt.AddSeconds(2), createdAt.AddSeconds(3), 1));
                }
            }
        }

        var duplicateSources = perInstanceDeliveries.SelectMany(items => items)
            .Take(duplicateCount)
            .ToArray();
        if (duplicateSources.Length != duplicateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryCount), "There are too few logical inputs for the duplicate quota.");
        }
        foreach (var original in duplicateSources)
        {
            var group = Array.FindIndex(instances, instance => instance.InstanceId == original.InstanceId);
            perInstanceDeliveries[group].Add(original with
            {
                DeliveryId = DeliveryId(nextId++),
                DeliveredAt = original.DeliveredAt.AddDays(1),
                ConcurrentGroup = null,
            });
        }

        for (var index = 0; index < lateCount; index++)
        {
            var instanceIndex = instanceCount - cancelledCount + index % cancelledCount;
            var original = perInstanceDeliveries[instanceIndex][0];
            perInstanceDeliveries[instanceIndex].Add(Add(
                instanceIndex,
                "Begin",
                "BeginWork",
                $"late-{index + 1:000000}",
                original.OccurredAt.AddMilliseconds(-500),
                original.OccurredAt.AddDays(20).AddSeconds(index % 60),
                1));
        }

        var concurrentGroups = new List<Gate15ConcurrentGroup>(pairCount);
        for (var pair = 0; pair < pairCount; pair++)
        {
            var instanceIndex = pair;
            var first = perInstanceDeliveries[instanceIndex].First(item => item.ConcurrentGroup is not null);
            var second = perInstanceDeliveries[instanceIndex].Last(item => item.ConcurrentGroup is not null);
            var groupId = $"race-{pair + 1:000000}";
            concurrentGroups.Add(new Gate15ConcurrentGroup(
                groupId, instances[instanceIndex].InstanceId, first.DeliveryId, second.DeliveryId));
        }

        var currentCount = perInstanceDeliveries.Sum(items => items.Count);
        var remaining = deliveryCount - currentCount;
        if (remaining < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryCount),
                $"Delivery count {deliveryCount} is too small for the required duplicate, late, and concurrent cases.");
        }

        var rotation = random.Next(instanceCount);
        for (var index = 0; index < remaining; index++)
        {
            var instanceIndex = index == 0 ? instanceCount - 1 : (index + rotation) % instanceCount;
            var created = instances[instanceIndex].BusinessCreatedAt;
            var longHistory = instanceIndex % 5 == 0 || instanceIndex == instanceCount - 1;
            var spanDays = longHistory ? 60 : 1;
            var ordinal = perInstanceDeliveries[instanceIndex].Count;
            var elapsedDays = instanceIndex == instanceCount - 1 && ordinal <= 2
                ? spanDays
                : (double)((ordinal % 12) + 1) / 12 * spanDays;
            var occurrence = created.AddDays(elapsedDays).AddSeconds(ordinal);
            var delivery = occurrence.AddSeconds(1);
            perInstanceDeliveries[instanceIndex].Add(Add(
                instanceIndex,
                "Begin",
                "BeginWork",
                $"repeat-{index + 1:000000}",
                occurrence,
                delivery,
                1));
        }

        var deliveries = perInstanceDeliveries.SelectMany(items => items)
            .OrderBy(item => item.DeliveredAt)
            .ThenBy(item => item.DeliveryId, StringComparer.Ordinal)
            .ToArray();
        var downtimeStart = HistoryStart.AddDays(historyDays / 2);
        var downtimeEnd = downtimeStart.AddDays(2);
        var schedules = instances.Select((instance, index) => (instance, index))
            .Where(item => item.instance.ScenarioProfile is "timed-external-wait" or "long-history-timed-wait")
            .Select(item => new Gate15TimerSchedule(
                item.instance.InstanceId,
                item.instance.BusinessCreatedAt.AddDays(7),
                downtimeStart,
                downtimeEnd)).ToArray();

        WriteNdjson(Path.Combine(directory, DataFiles[0]), instances);
        WriteNdjson(Path.Combine(directory, DataFiles[1]), deliveries);
        WriteNdjson(Path.Combine(directory, DataFiles[2]), schedules);
        WriteNdjson(Path.Combine(directory, DataFiles[3]), concurrentGroups);

        var digests = DataFiles.ToDictionary(
            name => name,
            name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))).ToLowerInvariant(),
            StringComparer.Ordinal);
        var definitionDigest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FindDefinitionPath())))
            .ToLowerInvariant();
        var manifest = new Gate15Manifest(seed, instanceCount, deliveryCount, historyDays,
            duplicateCount, lateCount, pairCount,
            instances.GroupBy(item => item.ScenarioProfile, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            DefinitionId, DefinitionVersion, profile, definitionDigest, digests);
        File.WriteAllBytes(Path.Combine(directory, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptionsIndented));

        Gate15Delivery Add(
            int instanceIndex,
            string eventType,
            string inputType,
            string logicalId,
            DateTimeOffset occurredAt,
            DateTimeOffset deliveredAt,
            long expectedRevision,
            string? groupId = null,
            string? pairSuffix = null)
        {
            var messageId = logicalId.StartsWith("submit", StringComparison.Ordinal)
                ? $"logical-{instances[instanceIndex].InstanceId}-submit"
                : $"logical-{instances[instanceIndex].InstanceId}-{logicalId}";
            return new Gate15Delivery(
                DeliveryId(nextId++),
                instances[instanceIndex].InstanceId,
                eventType,
                inputType,
                messageId,
                occurredAt,
                deliveredAt,
                expectedRevision,
                new SortedDictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["source"] = "gate15-synthetic",
                    ["sequence"] = logicalId,
                    ["pair"] = pairSuffix,
                },
                groupId);
        }
    }

    public static Gate15Validation Validate(
        string directory,
        int seed,
        int instanceCount,
        int deliveryCount,
        int historyDays = HistoryDays,
        string profile = DefaultProfile)
    {
        ValidateParameters(instanceCount, deliveryCount, historyDays, profile);
        var paths = DataFiles.Select(name => Path.Combine(directory, name)).ToArray();
        var instances = ReadNdjson<Gate15Instance>(paths[0]);
        var deliveries = ReadNdjson<Gate15Delivery>(paths[1]);
        var timers = ReadNdjson<Gate15TimerSchedule>(paths[2]);
        var declaredGroups = ReadNdjson<Gate15ConcurrentGroup>(paths[3]);
        var manifest = JsonSerializer.Deserialize<Gate15Manifest>(
                           File.ReadAllBytes(Path.Combine(directory, "manifest.json")), JsonOptions)
                       ?? throw new InvalidDataException("Manifest was empty.");
        Require(instances.Count == instanceCount && instances.Select(item => item.InstanceId).Distinct(StringComparer.Ordinal).Count() == instanceCount,
            "Instance count or uniqueness requirement failed.");
        Require(deliveries.Count == deliveryCount
                && deliveries.Select(item => item.DeliveryId).Distinct(StringComparer.Ordinal).Count() == deliveryCount,
            "Delivery count or uniqueness requirement failed.");
        var known = instances.Select(item => item.InstanceId).ToHashSet(StringComparer.Ordinal);
        Require(deliveries.All(item => known.Contains(item.InstanceId)), "A delivery references an unknown instance.");
        Require(deliveries.All(item => (item.EventType, item.InputType) is ("Begin", "BeginWork")
                                       or ("Cancel", "CancelWork")
                                       or ("Complete", "CompleteWork")
                                       or ("WaitForExternal", "WaitForExternal")),
            "Corpus contains an event/input mapping outside the selected Orion definition.");
        ValidateDefinition(deliveries, instances);
        Require(deliveries.All(item => item.ExpectedRevision >= 0
                                       && System.Text.RegularExpressions.Regex.IsMatch(item.DeliveryId, "^delivery-[0-9]{6}$")
                                       && System.Text.RegularExpressions.Regex.IsMatch(item.InstanceId, "^gate15-generic-[0-9]{6}$")
                                       && item.MessageId.StartsWith("logical-", StringComparison.Ordinal)
                                       && item.MessageId.Length <= 128
                                       && item.OccurredAt.Offset == TimeSpan.Zero && item.DeliveredAt.Offset == TimeSpan.Zero),
            "Input identity, revision, or timestamp formatting is invalid.");
        var ordered = instances.SequenceEqual(instances.OrderBy(item => item.InstanceId, StringComparer.Ordinal))
                      && deliveries.SequenceEqual(deliveries.OrderBy(item => item.DeliveredAt)
                          .ThenBy(item => item.DeliveryId, StringComparer.Ordinal))
                      && timers.SequenceEqual(timers.OrderBy(item => item.InstanceId, StringComparer.Ordinal))
                      && declaredGroups.SequenceEqual(declaredGroups.OrderBy(item => item.GroupId, StringComparer.Ordinal));
        Require(ordered, "Deliveries are not in stable canonical order.");

        var repeated = deliveries.GroupBy(item => item.MessageId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).ToArray();
        var exactDuplicates = repeated.Sum(group => group.Count() - 1);
        Require(exactDuplicates >= (int)Math.Ceiling(deliveryCount * 0.02), "Required exact duplicate count was not met.");
        foreach (var group in repeated)
        {
            var original = LogicalFingerprint(group.First());
            Require(group.All(item => LogicalFingerprint(item) == original),
                $"Message identity {group.Key} has conflicting logical content.");
        }

        var late = deliveries.GroupBy(item => item.InstanceId, StringComparer.Ordinal).Sum(group =>
        {
            DateTimeOffset? latestOccurrence = null;
            var count = 0;
            foreach (var item in group.OrderBy(item => item.DeliveredAt).ThenBy(item => item.DeliveryId, StringComparer.Ordinal))
            {
                if (latestOccurrence.HasValue && item.OccurredAt < latestOccurrence.Value
                    && item.DeliveredAt > latestOccurrence.Value)
                {
                    count++;
                }
                latestOccurrence = latestOccurrence.HasValue && latestOccurrence.Value > item.OccurredAt
                    ? latestOccurrence : item.OccurredAt;
            }
            return count;
        });
        Require(late >= (int)Math.Ceiling(deliveryCount * 0.01), "Required demonstrably late-delivery count was not met.");

        var raceGroups = deliveries.Where(item => item.ConcurrentGroup is not null)
            .GroupBy(item => item.ConcurrentGroup!, StringComparer.Ordinal).ToArray();
        foreach (var group in raceGroups)
        {
            var pair = group.ToArray();
            Require(pair.Length == 2 && pair[0].InstanceId == pair[1].InstanceId
                    && pair[0].ExpectedRevision == pair[1].ExpectedRevision
                    && pair[0].MessageId != pair[1].MessageId
                    && pair.Select(item => item.EventType).Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "Begin", "Cancel" })
                    && (pair[0].DeliveredAt - pair[1].DeliveredAt).Duration() <= TimeSpan.FromMilliseconds(100),
                $"Concurrent group {group.Key} is not a two-command shared-revision race.");
        }
        Require(raceGroups.Length >= (int)Math.Ceiling(deliveryCount * 0.005), "Required concurrent-pair count was not met.");
        var declaredGroupMap = declaredGroups.ToDictionary(item => item.GroupId, StringComparer.Ordinal);
        Require(declaredGroupMap.Count == raceGroups.Length && raceGroups.All(group =>
        {
            if (!declaredGroupMap.TryGetValue(group.Key, out var declared))
            {
                return false;
            }
            var pair = group.OrderBy(item => item.DeliveryId, StringComparer.Ordinal).ToArray();
            return declared.InstanceId == pair[0].InstanceId
                   && declared.FirstDeliveryId == pair[0].DeliveryId
                   && declared.SecondDeliveryId == pair[1].DeliveryId;
        }), "Concurrent group manifest references do not match delivery records.");

        var cancellationCount = Math.Max(1, instanceCount / 10);
        var categories = instances.GroupBy(item => item.ScenarioProfile, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Require(categories.GetValueOrDefault("cancelled") == cancellationCount
                && categories.GetValueOrDefault("competing-transition") == raceGroups.Length
                && categories.GetValueOrDefault("terminal-completed") > 0
                && categories.GetValueOrDefault("timed-external-wait") > 0
                && categories.GetValueOrDefault("long-history-timed-wait") > 0
                && categories.GetValueOrDefault("pending") > 0
                && categories.GetValueOrDefault("long-history-pending") > 0,
            "Lifecycle scenario categories are missing or incorrectly distributed.");
        var waitingCount = categories.GetValueOrDefault("timed-external-wait")
                           + categories.GetValueOrDefault("long-history-timed-wait");
        Require(timers.Count == waitingCount
                && timers.All(timer => known.Contains(timer.InstanceId)
                                       && timer.SimulatedDowntimeEnd > timer.SimulatedDowntimeStart),
            "Timer schedule count or instance references are invalid.");
        Require(timers.Any(timer => timer.TimerDueAt < timer.SimulatedDowntimeStart)
                && timers.Any(timer => timer.TimerDueAt > timer.SimulatedDowntimeEnd),
            "Timer schedule must include deadlines on both sides of simulated downtime.");

        var businessDays = deliveries.Select(item => item.OccurredAt.UtcDateTime.Date).Distinct().Count();
        Require(deliveries.Min(item => item.OccurredAt) <= HistoryStart.AddDays(1)
                && deliveries.Max(item => item.OccurredAt) >= HistoryStart.AddDays(historyDays - 5),
            "Business occurrence timestamps do not span the configured history duration.");
        var forbidden = new[] { "expected_state", "expected_result", "accepted", "rejected", "answer" };
        var noAnswers = File.ReadAllText(paths[0]).ContainsAny(forbidden) == false
                        && File.ReadAllText(paths[1]).ContainsAny(forbidden) == false
                        && File.ReadAllText(paths[2]).ContainsAny(forbidden) == false
                        && File.ReadAllText(paths[3]).ContainsAny(forbidden) == false;
        Require(noAnswers, "Corpus contains a prohibited answer column.");

        var hashes = DataFiles.ToDictionary(
            name => name,
            name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))).ToLowerInvariant(),
            StringComparer.Ordinal);
        var manifestMatches = manifest.Seed == seed && manifest.InstanceCount == instanceCount
                              && manifest.DeliveryCount == deliveryCount && manifest.HistoryDays == historyDays
                              && manifest.DefinitionId == DefinitionId && manifest.DefinitionVersion == DefinitionVersion
                              && manifest.WorkloadProfile == profile
                              && manifest.ScenarioCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                  .SequenceEqual(categories.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                              && manifest.DefinitionSha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(FindDefinitionPath())))
                                  .ToLowerInvariant()
                              && manifest.Sha256.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                  .SequenceEqual(hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Require(manifestMatches, "Manifest parameters or SHA-256 digests do not match generated files.");
        return new Gate15Validation(instances.Count, deliveries.Count, exactDuplicates, late, raceGroups.Length,
            businessDays, ordered, noAnswers, manifestMatches);
    }

    public static void VerifyDeterminism(
        int seed,
        int instanceCount,
        int deliveryCount,
        int historyDays = HistoryDays,
        string profile = DefaultProfile)
    {
        var first = Path.Combine(Path.GetTempPath(), $"gate15-a-{Guid.NewGuid():N}");
        var second = Path.Combine(Path.GetTempPath(), $"gate15-b-{Guid.NewGuid():N}");
        try
        {
            Generate(first, seed, instanceCount, deliveryCount, historyDays, profile);
            Generate(second, seed, instanceCount, deliveryCount, historyDays, profile);
            foreach (var name in DataFiles.Append("manifest.json"))
            {
                var firstHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(first, name)));
                var secondHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(second, name)));
                Require(CryptographicOperations.FixedTimeEquals(firstHash, secondHash),
                    $"Determinism check failed for {name}.");
            }
        }
        finally
        {
            Directory.Delete(first, true);
            Directory.Delete(second, true);
        }
    }

    public static List<T> ReadNdjson<T>(string path)
    {
        var result = new List<T>();
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            try
            {
                result.Add(JsonSerializer.Deserialize<T>(line, JsonOptions)
                           ?? throw new InvalidDataException($"Line {lineNumber} was null."));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Invalid NDJSON at {path}:{lineNumber}.", exception);
            }
        }
        return result;
    }

    private static string LogicalFingerprint(Gate15Delivery item) =>
        JsonSerializer.Serialize(new
        {
            item.InstanceId,
            item.EventType,
            item.InputType,
            item.MessageId,
            item.OccurredAt,
            item.ExpectedRevision,
            item.Payload,
        }, JsonOptions);

    private static void WriteNdjson<T>(string path, IEnumerable<T> records)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.NewLine = "\n";
        foreach (var record in records)
        {
            writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
        }
    }

    private static string InstanceId(int number) => $"gate15-generic-{number:000000}";
    private static string DeliveryId(int number) => $"delivery-{number:000000}";

    private static void ValidateDefinition(IReadOnlyList<Gate15Delivery> deliveries, IReadOnlyList<Gate15Instance> instances)
    {
        using var definition = JsonDocument.Parse(File.ReadAllBytes(FindDefinitionPath()));
        var root = definition.RootElement;
        Require(root.GetProperty("id").GetString() == DefinitionId
                && root.GetProperty("version").GetString() == DefinitionVersion,
            "Selected workflow definition identity/version mismatch.");
        var events = root.GetProperty("events").EnumerateArray().Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        var commands = root.GetProperty("inputPolicy").GetProperty("commands").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!,
                item => item.GetProperty("event").GetString()!, StringComparer.Ordinal);
        Require(deliveries.All(item => events.Contains(item.EventType)
                                       && commands.TryGetValue(item.InputType, out var eventType)
                                       && eventType == item.EventType),
            "Corpus event/input types do not match the selected definition.");
        Require(instances.All(item => item.DefinitionId == DefinitionId
                                      && item.DefinitionVersion == DefinitionVersion
                                      && item.Facts.Count == 0),
            "Instance definition binding or authority-free facts are invalid.");
    }

    private static string FindDefinitionPath()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
            {
                var candidates = new[]
                {
                    Path.Combine(current.FullName, "tools", "Gate15Load", "definitions", "gate15-generic-load-v1.json"),
                    Path.Combine(current.FullName, "definitions", "gate15-generic-load-v1.json"),
                };
                var definition = candidates.FirstOrDefault(File.Exists);
                if (definition is not null)
                {
                    return definition;
                }
            }
        }
        throw new DirectoryNotFoundException("Could not locate the Gate 15 generic workflow definition.");
    }

    public static string DefinitionPath() => FindDefinitionPath();

    private static void ValidateParameters(int instances, int deliveries, int historyDays, string profile)
    {
        if (instances < 100 || deliveries < instances || historyDays < 62)
        {
            throw new ArgumentOutOfRangeException(nameof(instances),
                "At least 100 instances, one delivery per instance, and a 62-day history are required.");
        }
        if (profile != DefaultProfile)
        {
            throw new ArgumentException($"Unsupported workload profile '{profile}'. The supported profile is '{DefaultProfile}'.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

internal static class StringExtensions
{
    public static bool ContainsAny(this string value, IEnumerable<string> tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
}

internal static class Gate15Runner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        var runId = Program.Option(args, "--run-id", "gate15");
        var concurrency = int.Parse(Program.Option(args, "--concurrency", "4"));
        var scenario = Program.Option(args, "--scenario", "all");
        var checkpointPath = Program.Option(args, "--checkpoint", Path.Combine(corpusDirectory, "runner-checkpoint.json"));
        var reportPath = Program.Option(args, "--report", Path.Combine(corpusDirectory, "run-results.json"));
        var databasePath = Path.GetFullPath(Program.Option(args, "--database", ""));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Supply an HTTP --base-url for a separately started, isolated StateMachine API.");
        }
        if (concurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrency), "Concurrency must be between 1 and 32.");
        }
        if (scenario is not ("all" or "ordinary" or "adversarial" or "long-history"))
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), "Scenario must be all, ordinary, adversarial, or long-history.");
        }
        var validation = Gate15Corpus.Validate(corpusDirectory, seed, instanceCount, deliveryCount, historyDays, profile);
        var instances = Gate15Corpus.ReadNdjson<Gate15Instance>(Path.Combine(corpusDirectory, "instances.ndjson"));
        var deliveries = Gate15Corpus.ReadNdjson<Gate15Delivery>(Path.Combine(corpusDirectory, "deliveries.ndjson"));
        var selectedInstances = instances.Where(instance => Program.MatchesScenario(instance.ScenarioProfile, scenario)).ToArray();
        var selectedIds = selectedInstances.Select(instance => instance.InstanceId).ToHashSet(StringComparer.Ordinal);
        var selectedDeliveries = deliveries.Where(item => selectedIds.Contains(item.InstanceId)).ToArray();
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("The isolated runtime database does not exist.", databasePath);
        }
        var initialDatabaseBytes = new FileInfo(databasePath).Length;
        var manifestDigest = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(Path.Combine(corpusDirectory, "manifest.json")))).ToLowerInvariant();
        var existingCheckpoint = File.Exists(checkpointPath)
            ? JsonSerializer.Deserialize<RunnerCheckpoint>(File.ReadAllBytes(checkpointPath), JsonOptions)
              ?? throw new InvalidDataException("Runner checkpoint is empty.")
            : new RunnerCheckpoint(runId, seed, manifestDigest, databasePath, [], scenario);
        if (existingCheckpoint.RunId != runId || existingCheckpoint.Seed != seed
            || existingCheckpoint.ManifestDigest != manifestDigest
            || !string.Equals(existingCheckpoint.DatabasePath, databasePath, StringComparison.OrdinalIgnoreCase)
            || existingCheckpoint.Scenario != scenario)
        {
            throw new InvalidDataException("Checkpoint belongs to another run, corpus, or database.");
        }
        var completed = existingCheckpoint.CompletedInstances.ToHashSet(StringComparer.Ordinal);
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var statuses = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var httpFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var transportFailures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var client = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        var trustedIngestionKey = Program.Option(args, "--trusted-ingestion-key", "");
        if (!string.IsNullOrEmpty(trustedIngestionKey))
        {
            client.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", trustedIngestionKey);
        }

        var definitionPath = Gate15Corpus.DefinitionPath();
        using (var definitionContent = new ByteArrayContent(await File.ReadAllBytesAsync(definitionPath)))
        {
            definitionContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var registration = await client.PostAsync("/api/definitions", definitionContent);
            if (!registration.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Gate 15 workload definition registration failed ({(int)registration.StatusCode}): {await registration.Content.ReadAsStringAsync()}");
            }
        }

        var instanceCreationClock = Stopwatch.StartNew();
        foreach (var instance in selectedInstances)
        {
            if (completed.Contains(instance.InstanceId))
            {
                continue;
            }
            var response = await client.PostAsJsonAsync("/api/instances", new
            {
                id = $"{runId}-{instance.InstanceId}",
                definitionId = instance.DefinitionId,
                definitionVersion = instance.DefinitionVersion,
                facts = instance.Facts,
            }, JsonOptions);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                using var inspect = await client.GetAsync($"/api/instances/{Uri.EscapeDataString($"{runId}-{instance.InstanceId}")}");
                inspect.EnsureSuccessStatusCode();
            }
            else if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Instance creation failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
            }
        }
        instanceCreationClock.Stop();
        long? databaseBytesBeforeDeliveries = File.Exists(databasePath) ? new FileInfo(databasePath).Length : null;

        var deliveryGroups = selectedDeliveries.GroupBy(item => item.InstanceId, StringComparer.Ordinal)
            .Where(group => !completed.Contains(group.Key))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(deliveryGroups, new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (group, cancellationToken) =>
            {
                var instanceId = $"{runId}-{group.Key}";
                var ordered = group.OrderBy(item => item.DeliveredAt).ThenBy(item => item.DeliveryId, StringComparer.Ordinal).ToArray();
                var groupFailed = false;
                for (var index = 0; index < ordered.Length; index++)
                {
                    var item = ordered[index];
                    if (item.ConcurrentGroup is not null)
                    {
                        var pair = ordered.Where(candidate => candidate.ConcurrentGroup == item.ConcurrentGroup).ToArray();
                        if (pair.Length != 2)
                        {
                            throw new InvalidDataException($"Concurrent group {item.ConcurrentGroup} is incomplete.");
                        }
                        if (item.DeliveryId != pair[0].DeliveryId)
                        {
                            continue;
                        }
                        var results = await Task.WhenAll(pair.Select(candidate => Submit(instanceId, candidate, cancellationToken)));
                        groupFailed |= results.Any(success => !success);
                        index = Array.FindLastIndex(ordered, candidate => candidate.ConcurrentGroup == item.ConcurrentGroup);
                        continue;
                    }
                    groupFailed |= !await Submit(instanceId, item, cancellationToken);
                }
                if (!groupFailed)
                {
                    lock (completed)
                    {
                        completed.Add(group.Key);
                        if (completed.Count % 50 == 0 || completed.Count == selectedInstances.Length)
                        {
                            var checkpoint = new RunnerCheckpoint(runId, seed, manifestDigest, databasePath,
                                completed.Order(StringComparer.Ordinal).ToArray(), scenario);
                            var temporaryPath = checkpointPath + ".tmp";
                            File.WriteAllBytes(temporaryPath,
                                JsonSerializer.SerializeToUtf8Bytes(checkpoint, Gate15Corpus.JsonOptionsIndented));
                            File.Move(temporaryPath, checkpointPath, overwrite: true);
                        }
                    }
                }
            });
        stopwatch.Stop();
        var elapsed = stopwatch.Elapsed.TotalSeconds;
        JsonElement? evaluationMetrics = null;
        if (!string.IsNullOrEmpty(trustedIngestionKey))
        {
            using var metricsResponse = await client.GetAsync("/api/test-controls/evaluation-metrics");
            if (!metricsResponse.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Evaluation metrics retrieval failed ({(int)metricsResponse.StatusCode}): {await metricsResponse.Content.ReadAsStringAsync()}");
            }
            using var metricsDocument = JsonDocument.Parse(await metricsResponse.Content.ReadAsStreamAsync());
            evaluationMetrics = metricsDocument.RootElement.Clone();
        }
        var report = new RunnerReport(
            runId,
            seed,
            selectedInstances.Length,
            selectedDeliveries.Length,
            statuses.Values.Sum(),
            concurrency,
            scenario,
            instanceCreationClock.Elapsed.TotalSeconds,
            elapsed,
            elapsed > 0 ? statuses.Values.Sum() / elapsed : 0,
            attempts.Values.Sum(),
            statuses.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value),
            httpFailures.ToArray(),
            transportFailures.ToArray(),
            databasePath,
            File.Exists(databasePath) ? new FileInfo(databasePath).Length : null,
            File.Exists(databasePath + "-wal") ? new FileInfo(databasePath + "-wal").Length : 0,
            initialDatabaseBytes,
            databaseBytesBeforeDeliveries,
            evaluationMetrics,
            validation);
        File.WriteAllBytes(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, Gate15Corpus.JsonOptionsIndented));
        Console.WriteLine(JsonSerializer.Serialize(report, Gate15Corpus.JsonOptionsIndented));
        return httpFailures.IsEmpty && transportFailures.IsEmpty ? 0 : 1;

        async Task<bool> Submit(string instanceId, Gate15Delivery item, CancellationToken cancellationToken)
        {
            var route = item.InputType;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                attempts.AddOrUpdate(item.DeliveryId, 1, (_, value) => value + 1);
                try
                {
                    using var response = await client.PostAsJsonAsync(
                        $"/api/instances/{Uri.EscapeDataString(instanceId)}/commands",
                        new
                        {
                            messageId = item.MessageId,
                            deliveryId = item.DeliveryId,
                            type = route,
                            expectedRevision = item.ExpectedRevision,
                            occurredAt = item.OccurredAt,
                            payload = item.Payload,
                        }, JsonOptions, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (response.StatusCode == HttpStatusCode.Conflict)
                        {
                            using var conflict = JsonDocument.Parse(body);
                            var conflictStatus = conflict.RootElement.GetProperty("message").GetProperty("status").GetString()
                                                 ?? "ConcurrencyConflict";
                            statuses.AddOrUpdate(conflictStatus, 1, (_, value) => value + 1);
                            return true;
                        }
                        httpFailures.Enqueue($"{item.DeliveryId} HTTP {(int)response.StatusCode}: {body}");
                        return false;
                    }
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    var status = document.RootElement.GetProperty("message").GetProperty("status").GetString() ?? "Unknown";
                    statuses.AddOrUpdate(status, 1, (_, value) => value + 1);
                    return true;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    if (attempt == 2)
                    {
                        transportFailures.Enqueue($"{item.DeliveryId} transport failure after retries: {exception.Message}");
                        return false;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken);
                }
            }
            return false;
        }
    }

    private sealed record RunnerCheckpoint(
        string RunId,
        int Seed,
        string ManifestDigest,
        string DatabasePath,
        IReadOnlyList<string> CompletedInstances,
        string Scenario = "all");
    private sealed record RunnerReport(
        string RunId,
        int Seed,
        int Instances,
        int IntendedDeliveries,
        int CompletedDeliveryRecords,
        int Concurrency,
        string Scenario,
        double InstanceCreationElapsedSeconds,
        double ElapsedSeconds,
        double CompletedDeliveriesPerSecond,
        int HttpAttempts,
        IReadOnlyDictionary<string, int> BusinessOutcomes,
        IReadOnlyList<string> HttpFailures,
        IReadOnlyList<string> TransportFailures,
        string DatabasePath,
        long? DatabaseBytesAfterRun,
        long DatabaseWalBytesAfterRun,
        long DatabaseBytesBeforeInstanceCreation,
        long? DatabaseBytesBeforeDeliveries,
        JsonElement? EvaluationMetrics,
        Gate15Validation CorpusValidation);
}
