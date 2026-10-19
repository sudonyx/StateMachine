using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrionGate9Corpus;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var root = FindRepositoryRoot();
            var command = args.FirstOrDefault() ?? "validate";
            var seed = GetOption(args, "--seed", 20261019);
            var definition = GetOption(args, "--definition", Path.Combine(root, "definitions", "orion-v4.json"));
            var corpusPath = GetOption(args, "--output", Path.Combine(root, "test-data", "orion-gate9-deliveries.ndjson"));
            switch (command)
            {
                case "generate":
                    var generated = CorpusGenerator.Serialize(CorpusGenerator.Build(seed, definition));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(corpusPath))!);
                    File.WriteAllBytes(corpusPath, generated);
                    Console.WriteLine($"Wrote 5,000 deliveries for 300 applications (seed={seed}).");
                    return 0;
                case "validate":
                    var bytes = File.ReadAllBytes(corpusPath);
                    var records = CorpusGenerator.Deserialize(bytes);
                    var report = CorpusValidator.Validate(records, bytes, seed, definition);
                    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
                case "run":
                    return await CorpusRunner.RunAsync(args, root);
                default:
                    throw new ArgumentException("Usage: OrionGate9Corpus [generate|validate|run] [options]");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static T GetOption<T>(string[] args, string name, T fallback)
    {
        var position = Array.IndexOf(args, name);
        if (position < 0)
        {
            return fallback;
        }
        if (position + 1 >= args.Length)
        {
            throw new ArgumentException($"Option {name} requires a value.");
        }
        return (T)Convert.ChangeType(args[position + 1], typeof(T), CultureInfo.InvariantCulture);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(Environment.CurrentDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "definitions", "orion-v4.json")))
            {
                return current.FullName;
            }
        }
        throw new DirectoryNotFoundException("Run this command from within the StateMachine repository.");
    }
}

public sealed record DeliveryRecord(
    [property: JsonPropertyName("delivery_id")] string DeliveryId,
    [property: JsonPropertyName("application_id")] string ApplicationId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("delivered_at")] DateTimeOffset DeliveredAt,
    [property: JsonPropertyName("payload")] Dictionary<string, object?> Payload,
    [property: JsonPropertyName("input_kind")] string InputKind,
    [property: JsonPropertyName("input_type")] string InputType,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("operation_id")] string? OperationId,
    [property: JsonPropertyName("observation_id")] string? ObservationId,
    [property: JsonPropertyName("expected_revision")] long ExpectedRevision,
    [property: JsonPropertyName("facts")] Dictionary<string, object?> Facts,
    [property: JsonPropertyName("evidence")] Dictionary<string, object?> Evidence,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("race_group")] string? RaceGroup,
    [property: JsonPropertyName("instance_facts")] Dictionary<string, object?> InstanceFacts);

public sealed record CorpusValidationReport(
    int Seed,
    int Records,
    int Applications,
    IReadOnlyList<string> EventVocabulary,
    IReadOnlyList<string> InputVocabulary,
    int ExactDuplicateAdditionalDeliveries,
    int SemanticDuplicateAdditionalDeliveries,
    int LateDeliveries,
    int RacePairs,
    int MalformedPayloads,
    double MalformedPayloadRatio,
    int CancelledApplications,
    int PreCancellationEventsDeliveredAfterCancellation,
    bool Reproducible);

public static class CorpusGenerator
{
    public const int DefaultSeed = 20261019;
    public const int ApplicationCount = 300;
    public const int DeliveryCount = 5000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<DeliveryRecord> Build(int seed, string definitionPath)
    {
        var random = new Random(seed);
        var records = new List<DeliveryRecord>(DeliveryCount);
        var nextNumber = 1;
        var perApplicationClock = new DateTimeOffset[ApplicationCount + 1];
        var firstSubmitByApp = new Dictionary<int, DeliveryRecord>();

        DeliveryRecord Add(
            int applicationNumber,
            string eventType,
            string inputType,
            long revision,
            DateTimeOffset occurredAt,
            DateTimeOffset deliveredAt,
            string inputKind = "Command",
            Dictionary<string, object?>? payload = null,
            Dictionary<string, object?>? facts = null,
            Dictionary<string, object?>? evidence = null,
            string? correlationId = null,
            string? source = null,
            string? raceGroup = null,
            string? eventId = null)
        {
            var logicalId = eventId ?? $"event-{nextNumber:00000}";
            var record = new DeliveryRecord(
                $"delivery-{nextNumber:00000}",
                $"orion-app-{applicationNumber:000}",
                eventType,
                logicalId,
                occurredAt,
                deliveredAt,
                payload ?? [],
                inputKind,
                inputType,
                logicalId,
                inputKind == "Command" ? $"command-op-{logicalId}" : null,
                inputKind == "Observation" ? $"observation-{logicalId}" : null,
                revision,
                facts ?? [],
                evidence ?? [],
                correlationId,
                source,
                raceGroup,
                InstanceFactsFor(applicationNumber));
            nextNumber++;
            records.Add(record);
            return record;
        }

        for (var app = 1; app <= ApplicationCount; app++)
        {
            var current = Start.AddMinutes(app * 3);
            perApplicationClock[app] = current;
            var submit = Add(app, "Submit", "SubmitApplication", 0, current, current.AddSeconds(1),
                payload: new Dictionary<string, object?> { ["applicationReference"] = $"OR-{app:00000}" });
            firstSubmitByApp.Add(app, submit);
            current = current.AddSeconds(10);

            if (app <= 100)
            {
                var operationId = $"documents-{app:000}";
                if (app <= 50)
                {
                    Add(app, "DocumentsReceived", "DocumentPackageReceived", 1,
                        current.AddSeconds(-5), current.AddSeconds(2),
                        inputKind: "Observation",
                        correlationId: operationId,
                        source: "orion-document-service");
                    current = current.AddSeconds(10);
                }
                var cancel = Add(app, "Cancel", "CancelApplication", 1, current, current.AddSeconds(1));
                Add(app, "DocumentsReceived", "DocumentPackageReceived", 1,
                    cancel.OccurredAt.AddSeconds(-1), cancel.DeliveredAt.AddSeconds(2),
                    inputKind: "Observation",
                    evidence: new Dictionary<string, object?> { ["documentSetDigest"] = $"sha256:late-docs-{app:000}" },
                    correlationId: operationId,
                    source: "orion-document-service");
                current = current.AddSeconds(10);
            }
            else if (app <= 200)
            {
                var raceGroup = app <= 175 ? $"race-{app - 100:000}" : null;
                Add(app, "IssueOffer", "IssueOffer", 1, current, current.AddMilliseconds(20), raceGroup: raceGroup);
                if (raceGroup is not null)
                {
                    Add(app, "ReferForReview", "ReferForReview", 1,
                        current.AddMilliseconds(10), current.AddMilliseconds(70), raceGroup: raceGroup);
                }
                current = current.AddSeconds(10);
                Add(app, "DeclineOffer", "DeclineOffer", 2, current, current.AddSeconds(1));
                current = current.AddSeconds(10);
            }
            else
            {
                Add(app, "IssueOffer", "IssueOffer", 1, current, current.AddSeconds(1));
                current = current.AddSeconds(10);
                Add(app, "AcceptOffer", "AcceptOffer", 2, current, current.AddSeconds(1),
                    facts: new Dictionary<string, object?>
                    {
                        ["offerValidAtEvaluation"] = true,
                        ["offerEvaluationTime"] = current.ToString("O", CultureInfo.InvariantCulture),
                    });
                current = current.AddSeconds(10);
                var reservationId = $"reservation-{app:000}";
                Add(app, "EquipmentReserved", "EquipmentReserved", 3, current, current.AddSeconds(1),
                    inputKind: "Observation",
                    evidence: new Dictionary<string, object?>
                    {
                        ["reservationId"] = reservationId,
                        ["assetId"] = $"asset-{app:000}",
                    },
                    correlationId: reservationId,
                    source: "orion-asset-service");
                current = current.AddSeconds(10);
                var notificationId = $"notification-{app:000}";
                Add(app, "CustomerNotified", "CustomerNotified", 4, current, current.AddSeconds(1),
                    inputKind: "Observation",
                    evidence: new Dictionary<string, object?>
                    {
                        ["deliveryId"] = notificationId,
                        ["messageDigest"] = $"sha256:notice-{app:000}",
                    },
                    correlationId: notificationId,
                    source: "orion-notification-service");
                current = current.AddSeconds(10);
            }
            perApplicationClock[app] = current;
        }

        var exactSources = records.Where(record => record.InputType == "SubmitApplication").Take(125)
            .Concat(records.Where(record => record.InputType == "EquipmentReserved").Take(25))
            .ToArray();
        for (var index = 0; index < 150; index++)
        {
            var original = exactSources[index];
            records.Add(original with
            {
                DeliveryId = $"delivery-{nextNumber:00000}",
                DeliveredAt = original.DeliveredAt.AddDays(2).AddMilliseconds(index),
            });
            nextNumber++;
        }

        for (var index = 1; index <= 100; index++)
        {
            var original = firstSubmitByApp[index];
            Add(index, "Submit", "SubmitApplication", 0, original.OccurredAt.AddMilliseconds(1),
                perApplicationClock[index].AddMinutes(1),
                payload: new Dictionary<string, object?>(original.Payload));
        }

        for (var index = 0; index < 100; index++)
        {
            var app = index + 101;
            var later = records.First(record => record.ApplicationId == $"orion-app-{app:000}"
                                                && record.InputType == "DeclineOffer");
            Add(app, "IssueOffer", "IssueOffer", 1, later.OccurredAt.AddMinutes(-1),
                later.OccurredAt.AddMinutes(2),
                payload: new Dictionary<string, object?> { ["lateRequest"] = true });
        }

        var terminalInputs = new (string Event, string Input)[]
        {
            ("Submit", "SubmitApplication"),
            ("DocumentsReceived", "ReceiveDocuments"),
            ("ReferForReview", "ReferForReview"),
            ("Approve", "ApproveAssessment"),
            ("IssueOffer", "IssueOffer"),
            ("AcceptOffer", "AcceptOffer"),
            ("DeclineOffer", "DeclineOffer"),
            ("Reject", "RejectApplication"),
            ("Cancel", "CancelApplication"),
        };
        while (records.Count < DeliveryCount)
        {
            var app = random.Next(1, ApplicationCount + 1);
            var (eventType, inputType) = terminalInputs[random.Next(terminalInputs.Length)];
            var occurred = perApplicationClock[app].AddSeconds(records.Count % 3600);
            var finalRevision = app <= 100 ? 2 : app <= 200 ? 3 : 5;
            Add(app, eventType, inputType, finalRevision, occurred, occurred.AddSeconds(1),
                payload: new Dictionary<string, object?> { ["requestReference"] = $"follow-up-{records.Count:00000}" });
        }

        return records.OrderBy(record => record.DeliveredAt).ThenBy(record => record.DeliveryId, StringComparer.Ordinal).ToArray();
    }

    public static byte[] Serialize(IReadOnlyList<DeliveryRecord> records)
    {
        var builder = new StringBuilder(records.Count * 300);
        foreach (var record in records)
        {
            builder.Append(JsonSerializer.Serialize(record, JsonOptions)).Append('\n');
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static IReadOnlyList<DeliveryRecord> Deserialize(byte[] bytes)
    {
        var records = new List<DeliveryRecord>();
        var lineNumber = 0;
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            try
            {
                records.Add(JsonSerializer.Deserialize<DeliveryRecord>(line, JsonOptions)
                            ?? throw new InvalidDataException($"line {lineNumber}: null record"));
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"line {lineNumber}: invalid NDJSON", exception);
            }
        }
        return records;
    }

    private static Dictionary<string, object?> InstanceFactsFor(int app) => new()
    {
        ["documentsComplete"] = app > 100,
        ["riskScore"] = 72,
        ["equipmentValue"] = 42000,
        ["checksComplete"] = true,
        ["eligibilityDecision"] = "Eligible",
        ["cancellationPermitted"] = true,
    };
}

public static class CorpusValidator
{
    public static CorpusValidationReport Validate(
        IReadOnlyList<DeliveryRecord> records,
        byte[] sourceBytes,
        int seed,
        string definitionPath)
    {
        Require(records.Count == CorpusGenerator.DeliveryCount,
            $"Expected 5,000 records; found {records.Count}.");
        Require(records.Select(record => record.ApplicationId).Distinct(StringComparer.Ordinal).Count()
                == CorpusGenerator.ApplicationCount,
            "Expected exactly 300 distinct application IDs.");
        Require(records.Select(record => record.DeliveryId).Distinct(StringComparer.Ordinal).Count() == records.Count,
            "Delivery IDs must be unique.");

        using var definition = JsonDocument.Parse(File.ReadAllText(definitionPath));
        var root = definition.RootElement;
        var supportedEvents = root.GetProperty("events").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var supportedInputs = new HashSet<string>(StringComparer.Ordinal);
        var requiredEvidence = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var kind in new[] { "commands", "observations" })
        {
            foreach (var mapping in root.GetProperty("inputPolicy").GetProperty(kind).EnumerateArray())
            {
                var name = mapping.GetProperty("name").GetString()!;
                supportedInputs.Add(name);
                requiredEvidence[name] = mapping.TryGetProperty("requiredEvidence", out var evidence)
                    ? evidence.EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
            }
        }
        foreach (var record in records)
        {
            Require(supportedEvents.Contains(record.EventType), $"Unsupported event type '{record.EventType}'.");
            Require(supportedInputs.Contains(record.InputType), $"Unsupported input type '{record.InputType}'.");
            Require(record.ExpectedRevision >= 0, $"Invalid expected revision for {record.DeliveryId}.");
            Require(record.Payload is not null, $"Missing payload for {record.DeliveryId}.");
        }

        var identityGroups = records.GroupBy(record => record.EventId, StringComparer.Ordinal).ToArray();
        var exactDuplicates = identityGroups.Where(group => group.Count() > 1).Sum(group => group.Count() - 1);
        Require(exactDuplicates >= 150, $"Expected 150 additional exact duplicates; found {exactDuplicates}.");
        foreach (var group in identityGroups.Where(group => group.Count() > 1))
        {
            var first = group.First();
            Require(group.All(record => EquivalentMessage(first, record)),
                $"Event ID '{group.Key}' is reused with conflicting immutable content.");
        }

        var semanticDuplicates = records
            .GroupBy(SemanticFingerprint, StringComparer.Ordinal)
            .Where(group => group.Select(record => record.EventId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Sum(group => group.Select(record => record.EventId).Distinct(StringComparer.Ordinal).Count() - 1);
        Require(semanticDuplicates >= 100,
            $"Expected 100 semantic duplicate requests with new event IDs; found {semanticDuplicates}.");

        var lateDeliveries = records.GroupBy(record => record.ApplicationId, StringComparer.Ordinal).Sum(group =>
        {
            DateTimeOffset? latestOccurrence = null;
            var count = 0;
            foreach (var record in group.OrderBy(record => record.DeliveredAt))
            {
                if (latestOccurrence.HasValue && record.OccurredAt < latestOccurrence.Value
                    && record.DeliveredAt > latestOccurrence.Value)
                {
                    count++;
                }
                latestOccurrence = latestOccurrence.HasValue
                    ? Max(latestOccurrence.Value, record.OccurredAt)
                    : record.OccurredAt;
            }
            return count;
        });
        Require(lateDeliveries >= 100, $"Expected 100 late deliveries; found {lateDeliveries}.");

        var raceGroups = records.Where(record => record.RaceGroup is not null)
            .GroupBy(record => record.RaceGroup!, StringComparer.Ordinal).ToArray();
        foreach (var group in raceGroups)
        {
            var pair = group.ToArray();
            Require(pair.Length == 2, $"Race group '{group.Key}' must contain two deliveries.");
            Require(pair[0].ApplicationId == pair[1].ApplicationId,
                $"Race group '{group.Key}' targets different applications.");
            Require(pair[0].ExpectedRevision == pair[1].ExpectedRevision,
                $"Race group '{group.Key}' targets different revisions.");
            Require((pair[0].DeliveredAt - pair[1].DeliveredAt).Duration() <= TimeSpan.FromMilliseconds(100),
                $"Race group '{group.Key}' exceeds the 100ms delivery window.");
        }
        Require(raceGroups.Length >= 75, $"Expected 75 race pairs; found {raceGroups.Length}.");

        var malformedPayloads = records.Count(record =>
            requiredEvidence.TryGetValue(record.InputType, out var required)
            && required.Count != 0
            && !required.IsSubsetOf(record.Evidence.Keys));
        var malformedRatio = (double)malformedPayloads / records.Count;
        Require(malformedRatio is >= 0.009 and <= 0.011,
            $"Malformed payload ratio must be about 1%; found {malformedRatio:P2}.");

        var cancellations = records.Where(record => record.EventType == "Cancel")
            .GroupBy(record => record.ApplicationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var preCancellationLateEvents = records.Count(record =>
            record.EventType == "DocumentsReceived"
            && cancellations.TryGetValue(record.ApplicationId, out var appCancellations)
            && appCancellations.Any(cancel => record.OccurredAt < cancel.OccurredAt
                                              && record.DeliveredAt > cancel.DeliveredAt));
        Require(cancellations.Count > 0, "No cancellation cases were found.");
        Require(preCancellationLateEvents >= 100,
            $"Expected 100 pre-cancellation observations delivered later; found {preCancellationLateEvents}.");

        var regenerated = CorpusGenerator.Serialize(CorpusGenerator.Build(seed, definitionPath));
        var reproducible = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(sourceBytes), SHA256.HashData(regenerated));
        Require(reproducible, "Corpus regeneration differs from the checked-in bytes.");

        return new CorpusValidationReport(
            seed,
            records.Count,
            records.Select(record => record.ApplicationId).Distinct(StringComparer.Ordinal).Count(),
            records.Select(record => record.EventType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            records.Select(record => record.InputType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            exactDuplicates,
            semanticDuplicates,
            lateDeliveries,
            raceGroups.Length,
            malformedPayloads,
            malformedRatio,
            cancellations.Count,
            preCancellationLateEvents,
            reproducible);
    }

    private static bool EquivalentMessage(DeliveryRecord first, DeliveryRecord second) =>
        LogicalMessageFingerprint(first) == LogicalMessageFingerprint(second);

    private static string LogicalMessageFingerprint(DeliveryRecord record) =>
        JsonSerializer.Serialize(new
        {
            record.ApplicationId,
            record.EventType,
            record.OccurredAt,
            record.Payload,
            record.InputKind,
            record.InputType,
            record.ExpectedRevision,
            record.Facts,
            record.Evidence,
            record.CorrelationId,
            record.Source,
            record.RaceGroup,
            record.InstanceFacts,
        });

    private static string SemanticFingerprint(DeliveryRecord record) =>
        JsonSerializer.Serialize(new
        {
            record.ApplicationId,
            record.EventType,
            record.InputKind,
            record.InputType,
            record.ExpectedRevision,
            record.Payload,
            record.Facts,
            record.Evidence,
            record.CorrelationId,
            record.Source,
        });

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left > right ? left : right;

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
