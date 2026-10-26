using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaReleaseMaterial(
    string Product,
    string TargetVersion,
    string Environment,
    string ReleaseCategory,
    string ChangeSize,
    bool AuthenticationChange,
    bool CustomerDataImpact,
    string ReleaseSummary,
    string ReleaseOwner,
    DateTimeOffset PlannedDeploymentAt);

public sealed record AsteriaReleaseSnapshot(
    string ReleaseId,
    string WorkflowInstanceId,
    string DefinitionId,
    string DefinitionVersion,
    AsteriaReleaseMaterial Material,
    int AssessmentGeneration,
    string MaterialDigest,
    string CurrentAssessmentWorkId,
    string Status,
    AsteriaAssessmentWork Work);

public sealed record AsteriaAssessmentWork(
    string WorkId,
    string ReleaseId,
    int Generation,
    string MaterialDigest,
    string WorkflowInstanceId,
    string DefinitionId,
    string DefinitionVersion,
    long? ParentRevision,
    string ExpectedOperationId,
    string DecisionIdempotencyKey,
    string BusinessEffectiveAt,
    DateTimeOffset KnownAt,
    string? DecisionRequestFingerprint,
    string? DecisionRequestJson,
    string? EvaluationId,
    string SelectedPolicyVersionsJson,
    string? OriginalResponseJson,
    string? OriginalEvidenceJson,
    string Status,
    string RecoveryMetadataJson);

public sealed class AsteriaReleaseRegistry : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public AsteriaReleaseRegistry(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A registry database path is required.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS AsteriaReleases (
                ReleaseId TEXT NOT NULL CONSTRAINT PK_AsteriaReleases PRIMARY KEY,
                WorkflowInstanceId TEXT NOT NULL CONSTRAINT UX_AsteriaReleases_Workflow UNIQUE,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                CanonicalMaterialJson TEXT NOT NULL,
                AssessmentGeneration INTEGER NOT NULL CHECK (AssessmentGeneration = 1),
                MaterialDigest TEXT NOT NULL,
                CurrentAssessmentWorkId TEXT NOT NULL,
                Status TEXT NOT NULL,
                LeaseToken TEXT NULL,
                LeaseExpiresAt INTEGER NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AsteriaAssessmentWorks (
                WorkId TEXT NOT NULL CONSTRAINT PK_AsteriaAssessmentWorks PRIMARY KEY,
                ReleaseId TEXT NOT NULL REFERENCES AsteriaReleases(ReleaseId),
                Generation INTEGER NOT NULL,
                MaterialDigest TEXT NOT NULL,
                WorkflowInstanceId TEXT NOT NULL,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                ParentRevision INTEGER NULL,
                ExpectedOperationId TEXT NOT NULL,
                DecisionIdempotencyKey TEXT NOT NULL CONSTRAINT UX_AsteriaWork_Idempotency UNIQUE,
                BusinessEffectiveAt TEXT NOT NULL,
                KnownAt TEXT NOT NULL,
                DecisionRequestFingerprint TEXT NULL,
                DecisionRequestJson TEXT NULL,
                EvaluationId TEXT NULL,
                SelectedPolicyVersionsJson TEXT NOT NULL,
                OriginalResponseJson TEXT NULL,
                OriginalEvidenceJson TEXT NULL,
                Status TEXT NOT NULL,
                RecoveryMetadataJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                CONSTRAINT UX_AsteriaWork_ReleaseGeneration UNIQUE (ReleaseId, Generation)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AsteriaReleaseSnapshot> CreateReleaseAsync(
        string releaseId,
        AsteriaReleaseMaterial material,
        DateTimeOffset now,
        string definitionId = "asteria-release-governance",
        string definitionVersion = "2.0",
        CancellationToken cancellationToken = default)
    {
        ValidateMaterial(releaseId, material);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionVersion);
        await InitializeAsync(cancellationToken);

        var canonicalMaterialJson = CanonicalMaterialJson(material);
        var materialDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalMaterialJson)));
        var normalizedNow = now.ToUniversalTime();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var prior = await LoadAsync(connection, transaction, releaseId, cancellationToken);
        if (prior is not null)
        {
            if (prior.Work.DefinitionId != definitionId ||
                prior.Work.DefinitionVersion != definitionVersion ||
                prior.MaterialDigest != materialDigest ||
                !string.Equals(CanonicalMaterialJson(prior.Material), canonicalMaterialJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Release identity '{releaseId}' already exists with different canonical material or definition identity.");
            }

            await transaction.CommitAsync(cancellationToken);
            return prior;
        }

        var workflowInstanceId = "asteria-" + Guid.NewGuid().ToString("N");
        var workId = Guid.NewGuid().ToString("N");
        var generation = 1;
        var expectedOperationId = StableIdentifier("asteria-assessment-", releaseId, generation);
        var idempotencyKey = StableIdentifier("ast-", releaseId, generation);
        var businessEffectiveAt = material.PlannedDeploymentAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var releaseStatus = "Registered";
        var workStatus = "Registered";

        await using (var insertRelease = connection.CreateCommand())
        {
            insertRelease.Transaction = transaction;
            insertRelease.CommandText = """
                INSERT INTO AsteriaReleases
                    (ReleaseId, WorkflowInstanceId, DefinitionId, DefinitionVersion, CanonicalMaterialJson,
                     AssessmentGeneration, MaterialDigest, CurrentAssessmentWorkId, Status, CreatedAt, UpdatedAt)
                VALUES ($releaseId, $instanceId, $definitionId, $definitionVersion, $materialJson,
                        1, $materialDigest, $workId, $status, $now, $now);
                """;
            insertRelease.Parameters.AddWithValue("$releaseId", releaseId);
            insertRelease.Parameters.AddWithValue("$instanceId", workflowInstanceId);
            insertRelease.Parameters.AddWithValue("$definitionId", definitionId);
            insertRelease.Parameters.AddWithValue("$definitionVersion", definitionVersion);
            insertRelease.Parameters.AddWithValue("$materialJson", canonicalMaterialJson);
            insertRelease.Parameters.AddWithValue("$materialDigest", materialDigest);
            insertRelease.Parameters.AddWithValue("$workId", workId);
            insertRelease.Parameters.AddWithValue("$status", releaseStatus);
            insertRelease.Parameters.AddWithValue("$now", normalizedNow.ToString("O", CultureInfo.InvariantCulture));
            await insertRelease.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertWork = connection.CreateCommand())
        {
            insertWork.Transaction = transaction;
            insertWork.CommandText = """
                INSERT INTO AsteriaAssessmentWorks
                    (WorkId, ReleaseId, Generation, MaterialDigest, WorkflowInstanceId, DefinitionId,
                     DefinitionVersion, ExpectedOperationId, DecisionIdempotencyKey, BusinessEffectiveAt,
                     KnownAt, SelectedPolicyVersionsJson, Status, RecoveryMetadataJson, CreatedAt, UpdatedAt)
                VALUES ($workId, $releaseId, 1, $materialDigest, $instanceId, $definitionId,
                        $definitionVersion, $operationId, $idempotencyKey, $effectiveAt,
                        $knownAt, '[]', $status, '{}', $now, $now);
                """;
            insertWork.Parameters.AddWithValue("$workId", workId);
            insertWork.Parameters.AddWithValue("$releaseId", releaseId);
            insertWork.Parameters.AddWithValue("$materialDigest", materialDigest);
            insertWork.Parameters.AddWithValue("$instanceId", workflowInstanceId);
            insertWork.Parameters.AddWithValue("$definitionId", definitionId);
            insertWork.Parameters.AddWithValue("$definitionVersion", definitionVersion);
            insertWork.Parameters.AddWithValue("$operationId", expectedOperationId);
            insertWork.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
            insertWork.Parameters.AddWithValue("$effectiveAt", businessEffectiveAt);
            insertWork.Parameters.AddWithValue("$knownAt", normalizedNow.ToString("O", CultureInfo.InvariantCulture));
            insertWork.Parameters.AddWithValue("$status", workStatus);
            insertWork.Parameters.AddWithValue("$now", normalizedNow.ToString("O", CultureInfo.InvariantCulture));
            await insertWork.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetReleaseAsync(releaseId, cancellationToken)
            ?? throw new InvalidOperationException("The newly committed release could not be read back.");
    }

    public async Task<AsteriaReleaseSnapshot?> GetReleaseAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await LoadAsync(connection, transaction: null, releaseId, cancellationToken);
    }

    public async Task<bool> AcquireLeaseAsync(
        string releaseId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be positive.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleases
            SET LeaseToken = $token, LeaseExpiresAt = $expiresAt
            WHERE ReleaseId = $releaseId
              AND Status <> 'Accepted'
              AND (LeaseToken = $token OR LeaseExpiresAt IS NULL OR LeaseExpiresAt <= $now);
            """;
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$expiresAt", now.Add(duration).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task ReleaseLeaseAsync(
        string releaseId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleases
            SET LeaseToken = NULL, LeaseExpiresAt = NULL
            WHERE ReleaseId = $releaseId AND LeaseToken = $token;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$token", leaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RenewLeaseAsync(
        string releaseId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleases
            SET LeaseExpiresAt = $expiresAt
            WHERE ReleaseId = $releaseId
              AND LeaseToken = $token
              AND LeaseExpiresAt > $now;
            """;
        command.Parameters.AddWithValue("$expiresAt", now.Add(duration).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task RecordParentRevisionAsync(
        string releaseId,
        long revision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        await UpdateWorkAsync(releaseId, """
            UPDATE AsteriaAssessmentWorks
            SET ParentRevision = COALESCE(ParentRevision, $revision), UpdatedAt = $now
            WHERE ReleaseId = $releaseId
              AND (ParentRevision IS NULL OR ParentRevision = $revision);
            """,
            now,
            command => command.Parameters.AddWithValue("$revision", revision),
            cancellationToken);
        await AdvanceStatusAsync(releaseId, "Submitted", now, cancellationToken);
    }

    public async Task RecordDecisionRequestAsync(
        string releaseId,
        string requestFingerprint,
        string requestJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Release '{releaseId}' does not exist.");
        if (current.Work.DecisionRequestJson is not null)
        {
            if (current.Work.DecisionRequestFingerprint != requestFingerprint ||
                !string.Equals(current.Work.DecisionRequestJson, requestJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A different request cannot reuse this assessment work identity.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AsteriaAssessmentWorks
            SET DecisionRequestFingerprint = $fingerprint,
                DecisionRequestJson = $requestJson,
                Status = 'DecisionRequested',
                UpdatedAt = $now
            WHERE ReleaseId = $releaseId AND DecisionRequestJson IS NULL;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$fingerprint", requestFingerprint);
        command.Parameters.AddWithValue("$requestJson", requestJson);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Assessment request persistence lost a concurrent update.");
        }

        await UpdateReleaseStatusAsync(connection, transaction, releaseId, "DecisionRequested", now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordDecisionResponseAsync(
        string releaseId,
        string? evaluationId,
        string selectedPolicyVersionsJson,
        string originalResponseJson,
        string? originalEvidenceJson,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (status is not ("DecisionReceived" or "NoApplicablePolicy"))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Decision response status is invalid.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Release '{releaseId}' does not exist.");
        if (current.Work.OriginalResponseJson is not null)
        {
            if (!string.Equals(current.Work.OriginalResponseJson, originalResponseJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A stored Decision API response is immutable and cannot be replaced by a later result.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (current.Work.DecisionRequestJson is null)
        {
            throw new InvalidOperationException("The exact Decision API request must be durable before storing its response.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AsteriaAssessmentWorks
            SET EvaluationId = $evaluationId,
                SelectedPolicyVersionsJson = $selectedVersions,
                OriginalResponseJson = $responseJson,
                OriginalEvidenceJson = $evidenceJson,
                Status = $status,
                UpdatedAt = $now
            WHERE ReleaseId = $releaseId AND OriginalResponseJson IS NULL;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$evaluationId", (object?)evaluationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$selectedVersions", selectedPolicyVersionsJson);
        command.Parameters.AddWithValue("$responseJson", originalResponseJson);
        command.Parameters.AddWithValue("$evidenceJson", (object?)originalEvidenceJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Decision response persistence lost a concurrent update.");
        }

        await UpdateReleaseStatusAsync(connection, transaction, releaseId, status, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AdvanceStatusAsync(
        string releaseId,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await LoadAsync(connection, transaction, releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Release '{releaseId}' does not exist.");
        if (StatusRank(status) < StatusRank(current.Work.Status))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AsteriaAssessmentWorks SET Status = $status, UpdatedAt = $now
            WHERE ReleaseId = $releaseId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"Assessment work for release '{releaseId}' does not exist.");
        }
        await UpdateReleaseStatusAsync(connection, transaction, releaseId, status, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static int StatusRank(string status) => status switch
    {
        "Registered" => 0,
        "WorkflowCreated" => 1,
        "Submitted" => 2,
        "OperationRegistered" => 3,
        "DecisionRequested" => 4,
        "DecisionReceived" or "NoApplicablePolicy" => 5,
        "ObservationSubmitted" => 6,
        "Accepted" => 7,
        _ => throw new InvalidDataException($"Unknown Asteria assessment status '{status}'.")
    };

    public async Task UpdateRecoveryMetadataAsync(
        string releaseId,
        string recoveryMetadataJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(recoveryMetadataJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Recovery metadata must be a JSON object.", nameof(recoveryMetadataJson));
        }

        await UpdateWorkAsync(releaseId, """
            UPDATE AsteriaAssessmentWorks
            SET RecoveryMetadataJson = $metadata, UpdatedAt = $now
            WHERE ReleaseId = $releaseId;
            """,
            now,
            command => command.Parameters.AddWithValue("$metadata", recoveryMetadataJson),
            cancellationToken);
    }

    private async Task UpdateWorkAsync(
        string releaseId,
        string sql,
        DateTimeOffset now,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        bind(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Assessment work update did not affect exactly one row.");
        }
    }

    private static async Task UpdateReleaseStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string releaseId,
        string status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AsteriaReleases SET Status = $status, UpdatedAt = $now
            WHERE ReleaseId = $releaseId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"Release '{releaseId}' does not exist.");
        }
    }

    private static async Task<AsteriaReleaseSnapshot?> LoadAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string releaseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.ReleaseId, r.WorkflowInstanceId, r.DefinitionId, r.DefinitionVersion,
                   r.CanonicalMaterialJson, r.AssessmentGeneration, r.MaterialDigest,
                   r.CurrentAssessmentWorkId, r.Status,
                   w.WorkId, w.ReleaseId, w.Generation, w.MaterialDigest, w.WorkflowInstanceId,
                   w.DefinitionId, w.DefinitionVersion, w.ParentRevision, w.ExpectedOperationId,
                   w.DecisionIdempotencyKey, w.BusinessEffectiveAt, w.KnownAt,
                   w.DecisionRequestFingerprint, w.DecisionRequestJson, w.EvaluationId,
                   w.SelectedPolicyVersionsJson, w.OriginalResponseJson, w.OriginalEvidenceJson,
                   w.Status, w.RecoveryMetadataJson
            FROM AsteriaReleases r
            JOIN AsteriaAssessmentWorks w ON w.WorkId = r.CurrentAssessmentWorkId
            WHERE r.ReleaseId = $releaseId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var material = JsonSerializer.Deserialize<AsteriaReleaseMaterial>(reader.GetString(4), JsonOptions)
            ?? throw new InvalidDataException("The canonical release material is invalid.");
        var work = new AsteriaAssessmentWork(
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt32(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetInt64(16),
            reader.GetString(17),
            reader.GetString(18),
            reader.GetString(19),
            DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.GetString(24),
            reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.GetString(27),
            reader.GetString(28));
        return new AsteriaReleaseSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            material,
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            work);
    }

    public static string CanonicalMaterialJson(AsteriaReleaseMaterial material)
    {
        ValidateMaterial("canonical-material", material);
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authenticationChange"] = material.AuthenticationChange,
            ["changeSize"] = material.ChangeSize,
            ["customerDataImpact"] = material.CustomerDataImpact,
            ["environment"] = material.Environment,
            ["plannedDeploymentAt"] = material.PlannedDeploymentAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["product"] = material.Product,
            ["releaseCategory"] = material.ReleaseCategory,
            ["releaseOwner"] = material.ReleaseOwner,
            ["releaseSummary"] = material.ReleaseSummary,
            ["targetVersion"] = material.TargetVersion
        };
        return JsonSerializer.Serialize(canonical, JsonOptions);
    }

    private static void ValidateMaterial(string releaseId, AsteriaReleaseMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentException.ThrowIfNullOrWhiteSpace(material.Product);
        ArgumentException.ThrowIfNullOrWhiteSpace(material.TargetVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(material.ReleaseSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(material.ReleaseOwner);
        if (material.Environment is not ("development" or "staging" or "production" or "unmanaged-production"))
        {
            throw new ArgumentException("Environment is not declared by AST-POL-001.", nameof(material));
        }
        if (material.ReleaseCategory is not ("Standard" or "Emergency"))
        {
            throw new ArgumentException("Release category is not declared by AST-POL-001.", nameof(material));
        }
        if (material.ChangeSize is not ("Small" or "Medium" or "Large"))
        {
            throw new ArgumentException("Change size is not declared by AST-POL-001.", nameof(material));
        }
        if (material.PlannedDeploymentAt == default)
        {
            throw new ArgumentException("A planned deployment timestamp is required.", nameof(material));
        }
    }

    private static string StableIdentifier(string prefix, string releaseId, int generation)
    {
        var input = Encoding.UTF8.GetBytes($"{releaseId}\0{generation.ToString(CultureInfo.InvariantCulture)}");
        return prefix + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
