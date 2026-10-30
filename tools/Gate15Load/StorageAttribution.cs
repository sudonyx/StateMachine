using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Gate15Load;

internal static class Gate15StorageAttribution
{
    private const int DefaultSampleCount = 512;
    private const int InstanceCount = DefaultSampleCount;
    private const string InstancePrefix = "gate15-storage-instance-";
    private const string FixedOccurredAt = "2026-01-01T00:00:00Z";
    private const string BaselineState = "Active";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, string> FixedPayload =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["storageExperiment"] = "gate15",
            ["value"] = "fixed-payload-v1"
        };

    public static async Task<int> RunAsync(string[] args)
    {
        var outcome = Program.Option(args, "--outcome", "").ToLowerInvariant();
        var baseUrl = Program.Option(args, "--base-url", "");
        var databasePath = Path.GetFullPath(Program.Option(args, "--database", ""));
        var runId = Program.Option(args, "--run-id", "");
        var reportPath = Program.Option(args, "--report", "");
        var sampleCount = int.Parse(Program.Option(args, "--sample-count", DefaultSampleCount.ToString()));

        if (outcome is not ("accepted" or "rejected" or "duplicate" or "stale"))
        {
            throw new ArgumentException("--outcome must be accepted, rejected, duplicate, or stale.");
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Supply an HTTP --base-url for the isolated State Machine API.");
        }
        if (!Path.IsPathFullyQualified(databasePath) || !File.Exists(databasePath))
        {
            throw new FileNotFoundException("The isolated API database must already exist.", databasePath);
        }
        if (string.IsNullOrWhiteSpace(runId)
            || runId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("--run-id must contain only ASCII letters, digits, and hyphens.");
        }
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("Supply a per-run --report path.");
        }
        if (sampleCount is < 128 or > InstanceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleCount),
                $"Sample count must be between 128 and {InstanceCount} for the isolated instance population.");
        }

        var instancePrefix = InstancePrefix;
        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };

        await EnsureHealthyAsync(client);
        var initialCounts = await ReadRowCountsAsync(databasePath, instancePrefix: null);
        if (initialCounts.Instances != 0
            || initialCounts.InboundMessages != 0
            || initialCounts.InputAttempts != 0
            || initialCounts.TransitionHistory != 0
            || initialCounts.Timers != 0)
        {
            throw new InvalidDataException(
                "Storage attribution requires a fresh isolated database with no existing instances, receipts, attempts, histories, or timers.");
        }

        await RegisterDefinitionAsync(client);
        var instanceIds = Enumerable.Range(0, InstanceCount)
            .Select(index => $"{instancePrefix}{index:D4}")
            .ToArray();
        await ForEachAsync(instanceIds, 8, async (instanceId, cancellationToken) =>
        {
            using var response = await client.PostAsJsonAsync("/api/instances", new
            {
                id = instanceId,
                definitionId = Gate15Corpus.DefinitionId,
                definitionVersion = Gate15Corpus.DefinitionVersion,
                facts = new Dictionary<string, object?>()
            }, JsonOptions, cancellationToken);
            await EnsureSuccessAsync(response, $"Creating baseline instance '{instanceId}'");
        });

        await ForEachAsync(instanceIds, 8, async (instanceId, cancellationToken) =>
        {
            var baselineRequest = CreateRequest(
                SetupMessageId(instanceId),
                SetupDeliveryId(instanceId),
                "BeginWork",
                expectedRevision: 0);
            var status = await SubmitAsync(client, instanceId, baselineRequest, cancellationToken);
            if (status != "Accepted")
            {
                throw new InvalidDataException(
                    $"Baseline BeginWork for '{instanceId}' returned '{status}', expected Accepted.");
            }
        });

        var baseline = await CaptureSnapshotAsync(databasePath, instancePrefix);
        AssertBaseline(baseline, instanceIds.Length);

        await ForEachAsync(Enumerable.Range(0, sampleCount), 8, async (index, cancellationToken) =>
        {
            var instanceId = instanceIds[index];
            var request = CreateMeasuredRequest(outcome, instanceId);
            var actualStatus = await SubmitAsync(client, instanceId, request, cancellationToken);
            var expectedStatus = OutcomeStatus(outcome);
            if (actualStatus != expectedStatus)
            {
                throw new InvalidDataException(
                    $"Measured input {index} returned '{actualStatus}', expected '{expectedStatus}' for {outcome}.");
            }
        });

        var final = await CaptureSnapshotAsync(databasePath, instancePrefix);
        var verification = await VerifyEvidenceAsync(client, instanceIds, sampleCount, outcome);
        AssertRowCountDeltas(baseline.RowCounts, final.RowCounts, sampleCount, outcome);
        if (verification.ExpectedOutcomeReceipts != sampleCount
            || verification.ReplayConsistentInstances != InstanceCount)
        {
            throw new InvalidDataException(
                $"Persisted receipt/replay verification failed for {outcome}: " +
                $"{verification.ExpectedOutcomeReceipts}/{sampleCount} outcomes, " +
                $"{verification.ReplayConsistentInstances}/{InstanceCount} replays.");
        }

        var databaseDeltaBytes = final.DatabaseBytes - baseline.DatabaseBytes;
        var walDeltaBytes = final.WalBytes - baseline.WalBytes;
        var baselineCombinedBytes = baseline.DatabaseBytes + baseline.WalBytes;
        var finalCombinedBytes = final.DatabaseBytes + final.WalBytes;
        var combinedDeltaBytes = finalCombinedBytes - baselineCombinedBytes;
        var bytesPerDelivery = StorageAttributionMath.CalculateBytesPerMeasuredDelivery(
            baselineCombinedBytes,
            finalCombinedBytes,
            sampleCount);

        var report = new StorageAttributionReport(
            runId,
            outcome,
            databasePath,
            sampleCount,
            InstanceCount,
            baseline,
            final,
            databaseDeltaBytes,
            walDeltaBytes,
            combinedDeltaBytes,
            bytesPerDelivery,
            FixedPayload,
            verification,
            "Each fresh database starts with 512 equivalent Active instances, each having one accepted BeginWork setup receipt, attempt, and history row. Baseline is checkpointed with PRAGMA wal_checkpoint(TRUNCATE); each measured delivery is then written and the same checkpoint is applied before the final snapshot. Instance, setup receipt, setup attempt, and setup history rows are baseline prerequisites and are excluded by subtracting baseline bytes and row counts. WAL and DB deltas are both reported; shared pages and SQLite allocation granularity remain in the measured change.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllBytesAsync(
            reportPath,
            JsonSerializer.SerializeToUtf8Bytes(report, Gate15Corpus.JsonOptionsIndented));
        Console.WriteLine(JsonSerializer.Serialize(report, Gate15Corpus.JsonOptionsIndented));
        return 0;
    }

    private static async Task RegisterDefinitionAsync(HttpClient client)
    {
        using var definitionContent = new ByteArrayContent(await File.ReadAllBytesAsync(Gate15Corpus.DefinitionPath()));
        definitionContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync("/api/definitions", definitionContent);
        await EnsureSuccessAsync(response, "Registering the generic Gate 15 definition");
    }

    private static async Task EnsureHealthyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/health");
        await EnsureSuccessAsync(response, "Checking the isolated API health");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        }
    }

    private static async Task<string> SubmitAsync(
        HttpClient client,
        string instanceId,
        object request,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(instanceId)}/commands",
            request,
            JsonOptions,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
        {
            throw new HttpRequestException(
                $"Command submission for '{instanceId}' failed ({(int)response.StatusCode}): {body}");
        }
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("message").GetProperty("status").GetString()
               ?? throw new InvalidDataException($"Command submission for '{instanceId}' omitted message.status.");
    }

    private static object CreateMeasuredRequest(string outcome, string instanceId) =>
        outcome switch
        {
            "accepted" => CreateRequest(
                $"measure-message-{InstanceIndex(instanceId):D4}",
                $"measure-delivery-{InstanceIndex(instanceId):D4}",
                "CompleteWork",
                expectedRevision: 1),
            "rejected" => CreateRequest(
                $"measure-message-{InstanceIndex(instanceId):D4}",
                $"measure-delivery-{InstanceIndex(instanceId):D4}",
                "BeginWork",
                expectedRevision: 1),
            "stale" => CreateRequest(
                $"measure-message-{InstanceIndex(instanceId):D4}",
                $"measure-delivery-{InstanceIndex(instanceId):D4}",
                "CompleteWork",
                expectedRevision: 0),
            "duplicate" => CreateRequest(
                SetupMessageId(instanceId),
                SetupDeliveryId(instanceId),
                "BeginWork",
                expectedRevision: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static object CreateRequest(
        string messageId,
        string deliveryId,
        string type,
        long expectedRevision) =>
        new
        {
            messageId,
            deliveryId,
            type,
            expectedRevision,
            occurredAt = DateTimeOffset.Parse(FixedOccurredAt, System.Globalization.CultureInfo.InvariantCulture),
            payload = FixedPayload
        };

    private static string SetupMessageId(string instanceId) => $"setup-message-{InstanceIndex(instanceId):D4}";

    private static string SetupDeliveryId(string instanceId) => $"setup-delivery-{InstanceIndex(instanceId):D4}";

    private static int InstanceIndex(string instanceId)
    {
        var separator = instanceId.LastIndexOf('-');
        if (separator < 0 || !int.TryParse(instanceId.AsSpan(separator + 1), out var index))
        {
            throw new InvalidDataException($"Unexpected Gate 15 storage instance identifier '{instanceId}'.");
        }
        return index;
    }

    private static string OutcomeStatus(string outcome) =>
        outcome switch
        {
            "accepted" => "Accepted",
            "rejected" => "Rejected",
            "duplicate" => "Duplicate",
            "stale" => "Stale",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static async Task<StorageSnapshot> CaptureSnapshotAsync(string databasePath, string instancePrefix)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        var journalMode = await ExecuteScalarStringAsync(connection, "PRAGMA journal_mode;");
        var checkpoint = await ReadCheckpointAsync(connection);
        if (checkpoint.Busy != 0)
        {
            throw new InvalidOperationException(
                $"SQLite checkpoint was blocked (busy={checkpoint.Busy}) for '{databasePath}'.");
        }
        var rowCounts = await ReadRowCountsAsync(connection, instancePrefix);
        var pageSize = await ExecuteScalarInt64Async(connection, "PRAGMA page_size;");
        var pageCount = await ExecuteScalarInt64Async(connection, "PRAGMA page_count;");
        var freeListCount = await ExecuteScalarInt64Async(connection, "PRAGMA freelist_count;");
        await connection.CloseAsync();

        return new StorageSnapshot(
            journalMode,
            new FileInfo(databasePath).Length,
            File.Exists(databasePath + "-wal") ? new FileInfo(databasePath + "-wal").Length : 0,
            pageSize,
            pageCount,
            freeListCount,
            checkpoint,
            rowCounts);
    }

    private static async Task<CheckpointResult> ReadCheckpointAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new CheckpointResult(0, 0, 0);
        }
        return new CheckpointResult(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static async Task<StorageRowCounts> ReadRowCountsAsync(string databasePath, string? instancePrefix)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 30
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        return await ReadRowCountsAsync(connection, instancePrefix);
    }

    private static async Task<StorageRowCounts> ReadRowCountsAsync(
        SqliteConnection connection,
        string? instancePrefix)
    {
        var instances = await CountAsync(connection, "Instances", instancePrefix);
        var inboundMessages = await CountAsync(connection, "InboundMessages", instancePrefix);
        var inputAttempts = await CountAsync(connection, "InputAttempts", instancePrefix);
        var history = await CountAsync(connection, "TransitionHistory", instancePrefix);
        var timers = await CountAsync(connection, "Timers", instancePrefix);
        var outcomes = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = instancePrefix is null
                ? """
                SELECT Status, COUNT(*)
                FROM InboundMessages
                GROUP BY Status;
                """
                : """
                  SELECT Status, COUNT(*)
                  FROM InboundMessages
                  WHERE substr(InstanceId, 1, length($prefix)) = $prefix
                  GROUP BY Status;
                  """;
            if (instancePrefix is not null)
            {
                command.Parameters.AddWithValue("$prefix", instancePrefix);
            }
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                outcomes.Add(reader.GetString(0), reader.GetInt64(1));
            }
        }
        return new StorageRowCounts(instances, inboundMessages, inputAttempts, history, timers, outcomes);
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string tableName,
        string? instancePrefix)
    {
        var (table, instanceColumn) = tableName switch
        {
            "Instances" => ("Instances", "Id"),
            "InboundMessages" or "InputAttempts" or "TransitionHistory" or "Timers" =>
                (tableName, "InstanceId"),
            _ => throw new ArgumentOutOfRangeException(nameof(tableName))
        };
        await using var command = connection.CreateCommand();
        command.CommandText = instancePrefix is null
            ? $"SELECT COUNT(*) FROM {table};"
            : $"""
              SELECT COUNT(*)
              FROM {table}
              WHERE substr({instanceColumn}, 1, length($prefix)) = $prefix;
              """;
        if (instancePrefix is not null)
        {
            command.Parameters.AddWithValue("$prefix", instancePrefix);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ExecuteScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
               ?? throw new InvalidDataException($"SQLite returned no value for '{sql}'.");
    }

    private static async Task<long> ExecuteScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<StorageVerification> VerifyEvidenceAsync(
        HttpClient client,
        IReadOnlyList<string> instanceIds,
        int sampleCount,
        string outcome)
    {
        long expectedOutcomes = 0;
        long replayed = 0;
        var failures = new ConcurrentQueue<string>();
        await ForEachAsync(instanceIds, 8, async (instanceId, cancellationToken) =>
        {
            using var receiptsResponse = await client.GetAsync(
                $"/api/instances/{Uri.EscapeDataString(instanceId)}/inbound-messages", cancellationToken);
            using var receipts = await ReadJsonAsync(receiptsResponse, $"Reading receipts for '{instanceId}'");
            var receiptItems = receipts.RootElement.EnumerateArray().ToArray();
            var measuredReceipts = receiptItems.Where(item =>
                outcome == "duplicate"
                    ? item.GetProperty("messageId").GetString() == SetupMessageId(instanceId)
                    : item.GetProperty("deliveryId").GetString() == $"measure-delivery-{InstanceIndex(instanceId):D4}")
            .ToArray();
            var expectedStatus = OutcomeStatus(outcome);
            var measuredCount = outcome == "duplicate"
                ? measuredReceipts.Count(item => item.GetProperty("status").GetString() == "Duplicate")
                : measuredReceipts.Count(item => item.GetProperty("status").GetString() == expectedStatus);
            if (measuredCount != 1)
            {
                failures.Enqueue($"{instanceId}: found {measuredCount} persisted {expectedStatus} measured receipts.");
            }
            else
            {
                Interlocked.Increment(ref expectedOutcomes);
            }
            if (receiptItems.Count(item =>
                    item.GetProperty("messageId").GetString() == SetupMessageId(instanceId)
                    && item.GetProperty("status").GetString() == "Accepted") != 1)
            {
                failures.Enqueue($"{instanceId}: baseline setup receipt is not uniquely Accepted.");
            }
            if (outcome == "duplicate" && measuredReceipts.Length != 2)
            {
                failures.Enqueue($"{instanceId}: exact duplicate message identity did not produce exactly two receipts.");
            }

            using var historyResponse = await client.GetAsync(
                $"/api/instances/{Uri.EscapeDataString(instanceId)}/history", cancellationToken);
            using var history = await ReadJsonAsync(historyResponse, $"Reading history for '{instanceId}'");
            var historyItems = history.RootElement.EnumerateArray().ToArray();
            var expectedHistoryCount = outcome == "accepted" ? 2 : 1;
            if (historyItems.Length != expectedHistoryCount
                || !HasContiguousHistory(historyItems))
            {
                failures.Enqueue($"{instanceId}: history count/state chain did not match the measured outcome.");
            }

            using var currentResponse = await client.GetAsync(
                $"/api/instances/{Uri.EscapeDataString(instanceId)}", cancellationToken);
            using var current = await ReadJsonAsync(currentResponse, $"Reading instance '{instanceId}'");
            var currentState = outcome == "accepted" ? "Completed" : BaselineState;
            var revision = outcome == "accepted" ? 2 : 1;
            if (current.RootElement.GetProperty("currentState").GetString() != currentState
                || current.RootElement.GetProperty("revision").GetInt64() != revision)
            {
                failures.Enqueue($"{instanceId}: current state/revision did not match the measured outcome.");
            }

            using var replayResponse = await client.GetAsync(
                $"/api/instances/{Uri.EscapeDataString(instanceId)}/replay", cancellationToken);
            using var replay = await ReadJsonAsync(replayResponse, $"Replaying instance '{instanceId}'");
            if (replay.RootElement.GetProperty("outcome").GetString() != "Consistent"
                || replay.RootElement.GetProperty("stateReconstructionOutcome").GetString() != "Consistent"
                || replay.RootElement.GetProperty("reconstructedFinalState").GetString() != currentState
                || replay.RootElement.GetProperty("recordedRevision").GetInt64() != revision
                || replay.RootElement.GetProperty("inboundReceiptsExamined").GetInt32() != receiptItems.Length
                || replay.RootElement.GetProperty("acceptedTransitionsExamined").GetInt32() != historyItems.Length)
            {
                failures.Enqueue($"{instanceId}: durable replay disagrees with receipts, history, or current state.");
            }
            else
            {
                Interlocked.Increment(ref replayed);
            }
        });

        if (!failures.IsEmpty)
        {
            throw new InvalidDataException(
                $"Storage attribution verification failed: {string.Join(" | ", failures.Take(10))}");
        }
        if (expectedOutcomes != sampleCount)
        {
            throw new InvalidDataException(
                $"Expected {sampleCount} persisted {OutcomeStatus(outcome)} receipts; found {expectedOutcomes}.");
        }
        return new StorageVerification(expectedOutcomes, replayed, failures.ToArray());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{operation} failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        }
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    }

    private static bool HasContiguousHistory(IReadOnlyList<JsonElement> items)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index].GetProperty("revisionBefore").GetInt64() != index
                || items[index].GetProperty("revisionAfter").GetInt64() != index + 1
                || index > 0 && items[index].GetProperty("previousState").GetString()
                    != items[index - 1].GetProperty("newState").GetString())
            {
                return false;
            }
        }
        return true;
    }

    private static void AssertBaseline(StorageSnapshot snapshot, int instanceCount)
    {
        var rows = snapshot.RowCounts;
        if (rows.Instances != instanceCount
            || rows.InboundMessages != instanceCount
            || rows.InputAttempts != instanceCount
            || rows.TransitionHistory != instanceCount
            || rows.Timers != 0
            || rows.ReceiptOutcomes.GetValueOrDefault("Accepted") != instanceCount
            || rows.ReceiptOutcomes.Values.Sum() != instanceCount)
        {
            throw new InvalidDataException(
                $"Equivalent setup baseline failed: {JsonSerializer.Serialize(rows, JsonOptions)}");
        }
    }

    private static void AssertRowCountDeltas(
        StorageRowCounts baseline,
        StorageRowCounts final,
        int sampleCount,
        string outcome)
    {
        var expectedAttempts = outcome is "accepted" or "rejected" ? sampleCount : 0;
        var expectedHistory = outcome == "accepted" ? sampleCount : 0;
        if (final.Instances != baseline.Instances
            || final.InboundMessages - baseline.InboundMessages != sampleCount
            || final.InputAttempts - baseline.InputAttempts != expectedAttempts
            || final.TransitionHistory - baseline.TransitionHistory != expectedHistory
            || final.Timers != baseline.Timers)
        {
            throw new InvalidDataException(
                $"Persisted row deltas for {outcome} did not match expectations. " +
                $"Baseline={JsonSerializer.Serialize(baseline, JsonOptions)}; " +
                $"Final={JsonSerializer.Serialize(final, JsonOptions)}.");
        }
        var expectedOutcomes = OutcomeStatus(outcome);
        if (final.ReceiptOutcomes.GetValueOrDefault(expectedOutcomes)
                - baseline.ReceiptOutcomes.GetValueOrDefault(expectedOutcomes) != sampleCount)
        {
            throw new InvalidDataException($"SQLite receipt outcome row counts did not confirm {sampleCount} {expectedOutcomes} records.");
        }
    }

    private static Task ForEachAsync<T>(
        IEnumerable<T> items,
        int degreeOfParallelism,
        Func<T, CancellationToken, Task> action) =>
        Parallel.ForEachAsync(
            items,
            new ParallelOptions { MaxDegreeOfParallelism = degreeOfParallelism },
            (item, cancellationToken) => new ValueTask(action(item, cancellationToken)));
}

public static class StorageAttributionMath
{
    public static double CalculateBytesPerMeasuredDelivery(
        long baselineCombinedBytes,
        long finalCombinedBytes,
        int measuredDeliveries)
    {
        if (measuredDeliveries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(measuredDeliveries), "Measured deliveries must be positive.");
        }
        if (baselineCombinedBytes < 0 || finalCombinedBytes < baselineCombinedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(finalCombinedBytes),
                "Final combined storage must be at least the non-negative baseline size.");
        }
        return (double)(finalCombinedBytes - baselineCombinedBytes) / measuredDeliveries;
    }
}

internal sealed record StorageSnapshot(
    string JournalMode,
    long DatabaseBytes,
    long WalBytes,
    long PageSize,
    long PageCount,
    long FreeListPages,
    CheckpointResult Checkpoint,
    StorageRowCounts RowCounts);

internal sealed record CheckpointResult(int Busy, int LogFrames, int CheckpointedFrames);

internal sealed record StorageRowCounts(
    long Instances,
    long InboundMessages,
    long InputAttempts,
    long TransitionHistory,
    long Timers,
    IReadOnlyDictionary<string, long> ReceiptOutcomes);

internal sealed record StorageVerification(
    long ExpectedOutcomeReceipts,
    long ReplayConsistentInstances,
    IReadOnlyList<string> Failures);

internal sealed record StorageAttributionReport(
    string RunId,
    string Outcome,
    string DatabasePath,
    int SampleCount,
    int BaselineInstanceCount,
    StorageSnapshot Baseline,
    StorageSnapshot Final,
    long DatabaseDeltaBytes,
    long WalDeltaBytes,
    long CombinedStorageDeltaBytes,
    double EstimatedBytesPerMeasuredDelivery,
    IReadOnlyDictionary<string, string> Payload,
    StorageVerification Verification,
    string Methodology);
