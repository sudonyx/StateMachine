using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaApprovalDecision(
    string DecisionId,
    string WorkId,
    string ReleaseId,
    int Generation,
    string MaterialDigest,
    string WorkflowInstanceId,
    string ConcernId,
    string ActivityId,
    string ExpectedOperationId,
    string SubmissionId,
    string IdempotencyKey,
    string PrincipalId,
    string AuthorityRole,
    string AuthorityRegistryVersion,
    string Decision,
    DateTimeOffset DecisionAt,
    string EvidenceReference,
    string EvidenceDigest,
    string RequestFingerprint,
    string DecisionJson,
    DateTimeOffset RecordedAt);

public sealed class AsteriaApprovalRegistry
{
    private readonly string _connectionString;

    public AsteriaApprovalRegistry(string databasePath)
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
            CREATE TABLE IF NOT EXISTS AsteriaApprovalDecisions (
                DecisionId TEXT NOT NULL PRIMARY KEY,
                WorkId TEXT NOT NULL UNIQUE REFERENCES AsteriaActivities(WorkId),
                ReleaseId TEXT NOT NULL,
                Generation INTEGER NOT NULL,
                MaterialDigest TEXT NOT NULL,
                WorkflowInstanceId TEXT NOT NULL,
                ConcernId TEXT NOT NULL,
                ActivityId TEXT NOT NULL,
                ExpectedOperationId TEXT NOT NULL UNIQUE,
                SubmissionId TEXT NOT NULL UNIQUE,
                IdempotencyKey TEXT NOT NULL UNIQUE,
                PrincipalId TEXT NOT NULL,
                AuthorityRole TEXT NOT NULL,
                AuthorityRegistryVersion TEXT NOT NULL,
                Decision TEXT NOT NULL CHECK (Decision IN ('Approved', 'Rejected', 'Unresolved')),
                DecisionAt TEXT NOT NULL,
                EvidenceReference TEXT NOT NULL,
                EvidenceDigest TEXT NOT NULL,
                RequestFingerprint TEXT NOT NULL,
                DecisionJson TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                FOREIGN KEY (ReleaseId, Generation)
                    REFERENCES AsteriaGenerations(ReleaseId, Generation)
            );
            CREATE TRIGGER IF NOT EXISTS TR_AsteriaApprovalDecisions_NoUpdate
                BEFORE UPDATE ON AsteriaApprovalDecisions
                BEGIN
                    SELECT RAISE(ABORT, 'Asteria approval decisions are immutable');
                END;
            CREATE TRIGGER IF NOT EXISTS TR_AsteriaApprovalDecisions_NoDelete
                BEFORE DELETE ON AsteriaApprovalDecisions
                BEGIN
                    SELECT RAISE(ABORT, 'Asteria approval decisions are immutable');
                END;
            CREATE TABLE IF NOT EXISTS AsteriaApprovalConflicts (
                ConflictId TEXT NOT NULL PRIMARY KEY,
                WorkId TEXT NULL REFERENCES AsteriaActivities(WorkId),
                SubmissionId TEXT NOT NULL,
                PrincipalId TEXT NOT NULL,
                RequestFingerprint TEXT NOT NULL,
                RequestJson TEXT NOT NULL,
                Reason TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                UNIQUE (SubmissionId, PrincipalId, RequestFingerprint)
            );
            CREATE TABLE IF NOT EXISTS AsteriaApprovalRefusals (
                RefusalId TEXT NOT NULL PRIMARY KEY,
                PrincipalId TEXT NOT NULL,
                ReleaseId TEXT NULL,
                ConcernId TEXT NULL,
                RequestFingerprint TEXT NOT NULL,
                Reason TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                UNIQUE (PrincipalId, RequestFingerprint, Reason)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AsteriaApprovalDecision> ReserveDecisionAsync(
        AsteriaApprovalDecision proposed,
        string requestJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var existing = await FindDecisionAsync(
            connection,
            transaction,
            proposed,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestFingerprint == proposed.RequestFingerprint &&
                existing.WorkId == proposed.WorkId &&
                existing.IdempotencyKey == proposed.IdempotencyKey &&
                existing.PrincipalId == proposed.PrincipalId &&
                existing.AuthorityRole == proposed.AuthorityRole &&
                existing.AuthorityRegistryVersion == proposed.AuthorityRegistryVersion)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
            await RecordConflictAsync(
                connection,
                transaction,
                existing.WorkId,
                proposed.SubmissionId,
                proposed.PrincipalId,
                proposed.RequestFingerprint,
                requestJson,
                "Approval submission identity was reused with a different binding or decision.",
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria approval submission conflicts with an immutable decision.");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AsteriaApprovalDecisions (
                DecisionId, WorkId, ReleaseId, Generation, MaterialDigest, WorkflowInstanceId,
                ConcernId, ActivityId, ExpectedOperationId, SubmissionId, IdempotencyKey,
                PrincipalId, AuthorityRole, AuthorityRegistryVersion, Decision, DecisionAt,
                EvidenceReference, EvidenceDigest, RequestFingerprint, DecisionJson, RecordedAt)
            VALUES (
                $decisionId, $workId, $releaseId, $generation, $materialDigest, $workflowInstanceId,
                $concernId, $activityId, $operationId, $submissionId, $idempotencyKey,
                $principalId, $authorityRole, $registryVersion, $decision, $decisionAt,
                $evidenceReference, $evidenceDigest, $requestFingerprint, $decisionJson, $recordedAt);
            """;
        AddDecisionParameters(insert, proposed, now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetByWorkIdAsync(proposed.WorkId, cancellationToken)
            ?? throw new InvalidDataException("Asteria approval decision disappeared after persistence.");
    }

    public async Task<AsteriaApprovalDecision?> GetByWorkIdAsync(
        string workId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AsteriaApprovalDecisions WHERE WorkId = $workId;";
        command.Parameters.AddWithValue("$workId", workId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDecision(reader) : null;
    }

    public async Task<IReadOnlyList<AsteriaApprovalDecision>> ListGenerationAsync(
        string releaseId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM AsteriaApprovalDecisions
            WHERE ReleaseId = $releaseId AND Generation = $generation
            ORDER BY DecisionAt, DecisionId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$generation", generation);
        var decisions = new List<AsteriaApprovalDecision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            decisions.Add(ReadDecision(reader));
        }
        return decisions;
    }

    public async Task<IReadOnlyList<JsonElement>> ListConflictEvidenceAsync(
        string releaseId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var evidence = new List<JsonElement>();
        await using (var conflicts = connection.CreateCommand())
        {
            conflicts.CommandText = """
                SELECT c.ConflictId, c.WorkId, c.SubmissionId, c.PrincipalId,
                       c.RequestFingerprint, c.RequestJson, c.Reason, c.RecordedAt
                FROM AsteriaApprovalConflicts c
                JOIN AsteriaActivities a ON a.WorkId = c.WorkId
                WHERE a.ReleaseId = $releaseId AND a.Generation = $generation
                ORDER BY c.RecordedAt, c.ConflictId;
                """;
            conflicts.Parameters.AddWithValue("$releaseId", releaseId);
            conflicts.Parameters.AddWithValue("$generation", generation);
            await using var reader = await conflicts.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                using var item = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    kind = "Conflict",
                    conflictId = reader.GetString(0),
                    workId = reader.GetString(1),
                    submissionId = reader.GetString(2),
                    principalId = reader.GetString(3),
                    requestFingerprint = reader.GetString(4),
                    requestJson = reader.GetString(5),
                    reason = reader.GetString(6),
                    recordedAt = reader.GetString(7)
                }));
                evidence.Add(item.RootElement.Clone());
            }
        }
        await using (var refusals = connection.CreateCommand())
        {
            refusals.CommandText = """
                SELECT RefusalId, PrincipalId, ConcernId, RequestFingerprint, Reason, RecordedAt
                FROM AsteriaApprovalRefusals
                WHERE ReleaseId = $releaseId
                ORDER BY RecordedAt, RefusalId;
                """;
            refusals.Parameters.AddWithValue("$releaseId", releaseId);
            await using var reader = await refusals.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                using var item = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    kind = "Refusal",
                    refusalId = reader.GetString(0),
                    principalId = reader.GetString(1),
                    concernId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    requestFingerprint = reader.GetString(3),
                    reason = reader.GetString(4),
                    recordedAt = reader.GetString(5)
                }));
                evidence.Add(item.RootElement.Clone());
            }
        }
        return evidence;
    }

    public async Task RecordRefusalAsync(
        string principalId,
        string? releaseId,
        string? concernId,
        string requestFingerprint,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO AsteriaApprovalRefusals (
                RefusalId, PrincipalId, ReleaseId, ConcernId, RequestFingerprint, Reason, RecordedAt)
            VALUES ($id, $principalId, $releaseId, $concernId, $fingerprint, $reason, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$principalId", principalId);
        command.Parameters.AddWithValue("$releaseId", (object?)releaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$concernId", (object?)concernId ?? DBNull.Value);
        command.Parameters.AddWithValue("$fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AsteriaApprovalDecision?> FindDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AsteriaApprovalDecision proposed,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM AsteriaApprovalDecisions
            WHERE WorkId = $workId
               OR (ReleaseId = $releaseId AND Generation = $generation
                   AND ConcernId = $concernId AND SubmissionId = $submissionId)
               OR IdempotencyKey = $idempotencyKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$workId", proposed.WorkId);
        command.Parameters.AddWithValue("$releaseId", proposed.ReleaseId);
        command.Parameters.AddWithValue("$generation", proposed.Generation);
        command.Parameters.AddWithValue("$concernId", proposed.ConcernId);
        command.Parameters.AddWithValue("$submissionId", proposed.SubmissionId);
        command.Parameters.AddWithValue("$idempotencyKey", proposed.IdempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDecision(reader) : null;
    }

    private static async Task RecordConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workId,
        string submissionId,
        string principalId,
        string fingerprint,
        string requestJson,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO AsteriaApprovalConflicts (
                ConflictId, WorkId, SubmissionId, PrincipalId, RequestFingerprint,
                RequestJson, Reason, RecordedAt)
            VALUES ($id, $workId, $submissionId, $principalId, $fingerprint, $requestJson, $reason, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$workId", workId);
        command.Parameters.AddWithValue("$submissionId", submissionId);
        command.Parameters.AddWithValue("$principalId", principalId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$requestJson", requestJson);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddDecisionParameters(SqliteCommand command, AsteriaApprovalDecision value, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$decisionId", value.DecisionId);
        command.Parameters.AddWithValue("$workId", value.WorkId);
        command.Parameters.AddWithValue("$releaseId", value.ReleaseId);
        command.Parameters.AddWithValue("$generation", value.Generation);
        command.Parameters.AddWithValue("$materialDigest", value.MaterialDigest);
        command.Parameters.AddWithValue("$workflowInstanceId", value.WorkflowInstanceId);
        command.Parameters.AddWithValue("$concernId", value.ConcernId);
        command.Parameters.AddWithValue("$activityId", value.ActivityId);
        command.Parameters.AddWithValue("$operationId", value.ExpectedOperationId);
        command.Parameters.AddWithValue("$submissionId", value.SubmissionId);
        command.Parameters.AddWithValue("$idempotencyKey", value.IdempotencyKey);
        command.Parameters.AddWithValue("$principalId", value.PrincipalId);
        command.Parameters.AddWithValue("$authorityRole", value.AuthorityRole);
        command.Parameters.AddWithValue("$registryVersion", value.AuthorityRegistryVersion);
        command.Parameters.AddWithValue("$decision", value.Decision);
        command.Parameters.AddWithValue("$decisionAt", value.DecisionAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$evidenceReference", value.EvidenceReference);
        command.Parameters.AddWithValue("$evidenceDigest", value.EvidenceDigest);
        command.Parameters.AddWithValue("$requestFingerprint", value.RequestFingerprint);
        command.Parameters.AddWithValue("$decisionJson", value.DecisionJson);
        command.Parameters.AddWithValue("$recordedAt", now.ToUniversalTime().ToString("O"));
    }

    private static AsteriaApprovalDecision ReadDecision(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            DateTimeOffset.Parse(reader.GetString(15), System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            DateTimeOffset.Parse(reader.GetString(20), System.Globalization.CultureInfo.InvariantCulture));

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
