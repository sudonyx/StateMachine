using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaDeadlineReservation(
    string DeadlineId,
    string ReleaseId,
    int Generation,
    string MaterialDigest,
    string WorkflowInstanceId,
    string DeadlineKind,
    DateTimeOffset DueAt,
    string OriginId);

public sealed record AsteriaDeadlineSnapshot(
    string DeadlineId,
    string ReleaseId,
    int Generation,
    string MaterialDigest,
    string WorkflowInstanceId,
    string DeadlineKind,
    DateTimeOffset DueAt,
    string OriginId,
    string Status,
    DateTimeOffset? ProcessedAt,
    string? ProcessingOutcome,
    string? EvidenceJson,
    DateTimeOffset CreatedAt);

public sealed class AsteriaDeadlineLedger : IAsyncDisposable
{
    private readonly string _connectionString;

    public AsteriaDeadlineLedger(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AsteriaDeadlines (
                DeadlineId TEXT NOT NULL PRIMARY KEY,
                ReleaseId TEXT NOT NULL,
                Generation INTEGER NOT NULL CHECK (Generation > 0),
                MaterialDigest TEXT NOT NULL,
                WorkflowInstanceId TEXT NOT NULL,
                DeadlineKind TEXT NOT NULL CHECK (
                    DeadlineKind IN ('VerificationEscalation', 'DeploymentInvestigation')),
                DueAt TEXT NOT NULL,
                OriginId TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Pending', 'Processed')),
                ProcessedAt TEXT NULL,
                ProcessingOutcome TEXT NULL,
                EvidenceJson TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UNIQUE (ReleaseId, Generation, DeadlineKind),
                FOREIGN KEY (ReleaseId, Generation)
                    REFERENCES AsteriaGenerations(ReleaseId, Generation)
            );
            CREATE INDEX IF NOT EXISTS IX_AsteriaDeadlines_Due
                ON AsteriaDeadlines(Status, DueAt);
            CREATE TABLE IF NOT EXISTS AsteriaDeadlineConflicts (
                ConflictId TEXT NOT NULL PRIMARY KEY,
                DeadlineId TEXT NOT NULL REFERENCES AsteriaDeadlines(DeadlineId),
                ProposedJson TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                UNIQUE (DeadlineId, ProposedJson)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AsteriaDeadlineSnapshot> EnsureAsync(
        AsteriaDeadlineReservation reservation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateReservation(reservation);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindAsync(
            connection,
            transaction,
            reservation.ReleaseId,
            reservation.Generation,
            reservation.DeadlineKind,
            cancellationToken);
        if (existing is not null)
        {
            if (Matches(existing, reservation))
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
            await RecordConflictAsync(connection, transaction, existing.DeadlineId, reservation, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria deadline identity was reused with conflicting bindings.");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AsteriaDeadlines (
                DeadlineId, ReleaseId, Generation, MaterialDigest, WorkflowInstanceId,
                DeadlineKind, DueAt, OriginId, Status, CreatedAt)
            VALUES (
                $deadlineId, $releaseId, $generation, $materialDigest, $workflowInstanceId,
                $kind, $dueAt, $originId, 'Pending', $createdAt);
            """;
        AddReservationParameters(insert, reservation);
        insert.Parameters.AddWithValue("$createdAt", now.ToUniversalTime().ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(reservation.DeadlineId, cancellationToken)
            ?? throw new InvalidDataException("Asteria deadline disappeared after reservation.");
    }

    public async Task<AsteriaDeadlineSnapshot> ProcessAsync(
        string deadlineId,
        string processingOutcome,
        string evidenceJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deadlineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingOutcome);
        using var evidence = JsonDocument.Parse(evidenceJson);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await GetWithinTransactionAsync(connection, transaction, deadlineId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria deadline '{deadlineId}' does not exist.");
        var processedAt = now.ToUniversalTime();
        var supportedOutcomes = current.DeadlineKind switch
        {
            "VerificationEscalation" =>
                new[] { "EscalationRecorded", "NoEscalationRequired", "Superseded", "Unresolved" },
            "DeploymentInvestigation" =>
                new[] { "InvestigationRecorded", "ResultAlreadyAccepted", "Superseded", "Unresolved" },
            _ => throw new InvalidDataException($"Unsupported Asteria deadline kind '{current.DeadlineKind}'.")
        };
        if (!supportedOutcomes.Contains(processingOutcome, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Outcome '{processingOutcome}' is not valid for Asteria deadline '{current.DeadlineKind}'.");
        }
        if (current.Status == "Processed")
        {
            if (current.ProcessingOutcome == processingOutcome &&
                current.EvidenceJson == evidenceJson)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            await RecordProcessingConflictAsync(
                connection, transaction, current, processingOutcome, evidenceJson, processedAt, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria deadline has conflicting processing evidence.");
        }
        if (processedAt < current.DueAt)
        {
            throw new InvalidOperationException("Asteria deadline cannot be processed before its immutable UTC due instant.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaDeadlines
            SET Status = 'Processed', ProcessedAt = $processedAt,
                ProcessingOutcome = $outcome, EvidenceJson = $evidence
            WHERE DeadlineId = $deadlineId AND Status = 'Pending';
            """;
        update.Parameters.AddWithValue("$processedAt", processedAt.ToString("O"));
        update.Parameters.AddWithValue("$outcome", processingOutcome);
        update.Parameters.AddWithValue("$evidence", evidenceJson);
        update.Parameters.AddWithValue("$deadlineId", deadlineId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(deadlineId, cancellationToken)
            ?? throw new InvalidDataException("Asteria deadline disappeared after processing.");
    }

    public async Task<AsteriaDeadlineSnapshot?> GetAsync(
        string deadlineId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await GetWithinTransactionAsync(connection, null, deadlineId, cancellationToken);
    }

    public async Task<IReadOnlyList<AsteriaDeadlineSnapshot>> ListGenerationAsync(
        string releaseId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM AsteriaDeadlines
            WHERE ReleaseId = $releaseId AND Generation = $generation
            ORDER BY DueAt, DeadlineId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$generation", generation);
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AsteriaDeadlineSnapshot>> ListDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM AsteriaDeadlines
            WHERE Status = 'Pending' AND DueAt <= $now
            ORDER BY DueAt, DeadlineId;
            """;
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        return await ReadAllAsync(command, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<AsteriaDeadlineSnapshot?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string releaseId,
        int generation,
        string kind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM AsteriaDeadlines
            WHERE ReleaseId = $releaseId AND Generation = $generation AND DeadlineKind = $kind;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$kind", kind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task<AsteriaDeadlineSnapshot?> GetWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deadlineId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM AsteriaDeadlines WHERE DeadlineId = $deadlineId;";
        command.Parameters.AddWithValue("$deadlineId", deadlineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task<IReadOnlyList<AsteriaDeadlineSnapshot>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<AsteriaDeadlineSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(Read(reader));
        }
        return results;
    }

    private static AsteriaDeadlineSnapshot Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            DateTimeOffset.Parse(reader.GetString(6)),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12)));

    private static void AddReservationParameters(SqliteCommand command, AsteriaDeadlineReservation value)
    {
        command.Parameters.AddWithValue("$deadlineId", value.DeadlineId);
        command.Parameters.AddWithValue("$releaseId", value.ReleaseId);
        command.Parameters.AddWithValue("$generation", value.Generation);
        command.Parameters.AddWithValue("$materialDigest", value.MaterialDigest);
        command.Parameters.AddWithValue("$workflowInstanceId", value.WorkflowInstanceId);
        command.Parameters.AddWithValue("$kind", value.DeadlineKind);
        command.Parameters.AddWithValue("$dueAt", value.DueAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$originId", value.OriginId);
    }

    private static async Task RecordConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string existingDeadlineId,
        AsteriaDeadlineReservation proposed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(proposed);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO AsteriaDeadlineConflicts (
                ConflictId, DeadlineId, ProposedJson, RecordedAt)
            VALUES ($conflictId, $deadlineId, $payload, $recordedAt);
            """;
        command.Parameters.AddWithValue("$conflictId", AsteriaActivityLedger.Fingerprint(payload));
        command.Parameters.AddWithValue("$deadlineId", existingDeadlineId);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$recordedAt", now.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecordProcessingConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AsteriaDeadlineSnapshot existing,
        string outcome,
        string evidenceJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { outcome, evidenceJson });
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO AsteriaDeadlineConflicts (
                ConflictId, DeadlineId, ProposedJson, RecordedAt)
            VALUES ($conflictId, $deadlineId, $payload, $recordedAt);
            """;
        command.Parameters.AddWithValue("$conflictId", AsteriaActivityLedger.Fingerprint(payload));
        command.Parameters.AddWithValue("$deadlineId", existing.DeadlineId);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$recordedAt", now.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool Matches(
        AsteriaDeadlineSnapshot existing,
        AsteriaDeadlineReservation proposed) =>
        existing.DeadlineId == proposed.DeadlineId &&
        existing.MaterialDigest == proposed.MaterialDigest &&
        existing.WorkflowInstanceId == proposed.WorkflowInstanceId &&
        existing.DueAt == proposed.DueAt.ToUniversalTime() &&
        existing.OriginId == proposed.OriginId;

    private static void ValidateReservation(AsteriaDeadlineReservation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.DeadlineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.ReleaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.MaterialDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.WorkflowInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.OriginId);
        if (value.Generation <= 0 ||
            value.DeadlineKind is not ("VerificationEscalation" or "DeploymentInvestigation"))
        {
            throw new ArgumentException("Asteria deadline has an invalid generation or kind.", nameof(value));
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
