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
    AsteriaAssessmentWork Work,
    string RegistryState = "Operational");

public sealed record AsteriaAmendmentSnapshot(
    string AmendmentId,
    string ReleaseId,
    string RequestFingerprint,
    int ExpectedBaseGeneration,
    int NewGeneration,
    string OldWorkflowInstanceId,
    string NewWorkflowInstanceId,
    string Phase,
    string? LastError);

public sealed record AsteriaSupersessionAttempt(
    string ReleaseId,
    string AmendmentId,
    long ExpectedRevision,
    string OperationId,
    string MessageId);

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
            DefaultTimeout = 30,
            ForeignKeys = true
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
            CREATE TABLE IF NOT EXISTS AsteriaGenerations (
                ReleaseId TEXT NOT NULL REFERENCES AsteriaReleases(ReleaseId),
                Generation INTEGER NOT NULL CHECK (Generation > 0),
                CanonicalMaterialJson TEXT NOT NULL,
                MaterialDigest TEXT NOT NULL,
                WorkflowInstanceId TEXT NOT NULL UNIQUE,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                AssessmentWorkId TEXT NOT NULL UNIQUE REFERENCES AsteriaAssessmentWorks(WorkId),
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (ReleaseId, Generation),
                UNIQUE (ReleaseId, Generation, AssessmentWorkId)
            );
            CREATE TABLE IF NOT EXISTS AsteriaReleasePointers (
                ReleaseId TEXT NOT NULL PRIMARY KEY REFERENCES AsteriaReleases(ReleaseId),
                CurrentGeneration INTEGER NOT NULL,
                CurrentAssessmentWorkId TEXT NOT NULL REFERENCES AsteriaAssessmentWorks(WorkId),
                Status TEXT NOT NULL,
                RegistryState TEXT NOT NULL CHECK (
                    RegistryState IN ('Operational', 'AmendmentPending', 'ActivationIncomplete')),
                LeaseToken TEXT NULL,
                LeaseExpiresAt INTEGER NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (ReleaseId, CurrentGeneration, CurrentAssessmentWorkId)
                    REFERENCES AsteriaGenerations(ReleaseId, Generation, AssessmentWorkId)
            );
            CREATE TABLE IF NOT EXISTS AsteriaAmendments (
                AmendmentId TEXT NOT NULL,
                ReleaseId TEXT NOT NULL REFERENCES AsteriaReleases(ReleaseId),
                RequestFingerprint TEXT NOT NULL,
                ExpectedBaseGeneration INTEGER NOT NULL,
                NewGeneration INTEGER NOT NULL,
                OldWorkflowInstanceId TEXT NOT NULL,
                NewWorkflowInstanceId TEXT NOT NULL UNIQUE,
                NewWorkId TEXT NOT NULL UNIQUE REFERENCES AsteriaAssessmentWorks(WorkId),
                Phase TEXT NOT NULL CHECK (Phase IN (
                    'AmendmentReserved', 'NewInstanceProvisioned',
                    'OldInstanceSuperseded', 'NewGenerationActivated')),
                LastError TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ReleaseId, AmendmentId),
                UNIQUE (ReleaseId, NewGeneration),
                FOREIGN KEY (ReleaseId, ExpectedBaseGeneration)
                    REFERENCES AsteriaGenerations(ReleaseId, Generation),
                FOREIGN KEY (ReleaseId, NewGeneration, NewWorkId)
                    REFERENCES AsteriaGenerations(ReleaseId, Generation, AssessmentWorkId)
            );
            CREATE TABLE IF NOT EXISTS AsteriaSupersessionAttempts (
                ReleaseId TEXT NOT NULL,
                AmendmentId TEXT NOT NULL,
                ExpectedRevision INTEGER NOT NULL CHECK (ExpectedRevision >= 0),
                OperationId TEXT NOT NULL UNIQUE,
                MessageId TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                PRIMARY KEY (ReleaseId, AmendmentId, ExpectedRevision),
                FOREIGN KEY (ReleaseId, AmendmentId)
                    REFERENCES AsteriaAmendments(ReleaseId, AmendmentId)
            );
            INSERT OR IGNORE INTO AsteriaGenerations
                (ReleaseId, Generation, CanonicalMaterialJson, MaterialDigest, WorkflowInstanceId,
                 DefinitionId, DefinitionVersion, AssessmentWorkId, CreatedAt)
            SELECT r.ReleaseId, r.AssessmentGeneration, r.CanonicalMaterialJson, r.MaterialDigest,
                   r.WorkflowInstanceId, r.DefinitionId, r.DefinitionVersion, w.WorkId, r.CreatedAt
            FROM AsteriaReleases r
            JOIN AsteriaAssessmentWorks w ON w.WorkId = r.CurrentAssessmentWorkId;
            INSERT OR IGNORE INTO AsteriaReleasePointers
                (ReleaseId, CurrentGeneration, CurrentAssessmentWorkId, Status, RegistryState,
                 LeaseToken, LeaseExpiresAt, CreatedAt, UpdatedAt)
            SELECT ReleaseId, AssessmentGeneration, CurrentAssessmentWorkId, Status, 'Operational',
                   LeaseToken, LeaseExpiresAt, CreatedAt, UpdatedAt
            FROM AsteriaReleases;
            CREATE TRIGGER IF NOT EXISTS TR_AsteriaGenerations_NoUpdate
            BEFORE UPDATE ON AsteriaGenerations
            BEGIN
                SELECT RAISE(ABORT, 'Asteria generation snapshots are immutable');
            END;
            CREATE TRIGGER IF NOT EXISTS TR_AsteriaGenerations_NoDelete
            BEFORE DELETE ON AsteriaGenerations
            BEGIN
                SELECT RAISE(ABORT, 'Asteria generation snapshots are immutable');
            END;
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

        await using (var insertGeneration = connection.CreateCommand())
        {
            insertGeneration.Transaction = transaction;
            insertGeneration.CommandText = """
                INSERT INTO AsteriaGenerations
                    (ReleaseId, Generation, CanonicalMaterialJson, MaterialDigest, WorkflowInstanceId,
                     DefinitionId, DefinitionVersion, AssessmentWorkId, CreatedAt)
                VALUES ($releaseId, 1, $materialJson, $materialDigest, $instanceId,
                        $definitionId, $definitionVersion, $workId, $now);
                """;
            insertGeneration.Parameters.AddWithValue("$releaseId", releaseId);
            insertGeneration.Parameters.AddWithValue("$materialJson", canonicalMaterialJson);
            insertGeneration.Parameters.AddWithValue("$materialDigest", materialDigest);
            insertGeneration.Parameters.AddWithValue("$instanceId", workflowInstanceId);
            insertGeneration.Parameters.AddWithValue("$definitionId", definitionId);
            insertGeneration.Parameters.AddWithValue("$definitionVersion", definitionVersion);
            insertGeneration.Parameters.AddWithValue("$workId", workId);
            insertGeneration.Parameters.AddWithValue("$now", normalizedNow.ToString("O", CultureInfo.InvariantCulture));
            await insertGeneration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertPointer = connection.CreateCommand())
        {
            insertPointer.Transaction = transaction;
            insertPointer.CommandText = """
                INSERT INTO AsteriaReleasePointers
                    (ReleaseId, CurrentGeneration, CurrentAssessmentWorkId, Status, RegistryState, CreatedAt, UpdatedAt)
                VALUES ($releaseId, 1, $workId, $status, 'Operational', $now, $now);
                """;
            insertPointer.Parameters.AddWithValue("$releaseId", releaseId);
            insertPointer.Parameters.AddWithValue("$workId", workId);
            insertPointer.Parameters.AddWithValue("$status", releaseStatus);
            insertPointer.Parameters.AddWithValue("$now", normalizedNow.ToString("O", CultureInfo.InvariantCulture));
            await insertPointer.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<AsteriaReleaseSnapshot?> GetGenerationAsync(
        string releaseId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.ReleaseId, g.WorkflowInstanceId, g.DefinitionId, g.DefinitionVersion,
                   g.CanonicalMaterialJson, g.Generation, g.MaterialDigest, g.AssessmentWorkId,
                   w.Status, p.RegistryState,
                   w.WorkId, w.ReleaseId, w.Generation, w.MaterialDigest, w.WorkflowInstanceId,
                   w.DefinitionId, w.DefinitionVersion, w.ParentRevision, w.ExpectedOperationId,
                   w.DecisionIdempotencyKey, w.BusinessEffectiveAt, w.KnownAt,
                   w.DecisionRequestFingerprint, w.DecisionRequestJson, w.EvaluationId,
                   w.SelectedPolicyVersionsJson, w.OriginalResponseJson, w.OriginalEvidenceJson,
                   w.Status, w.RecoveryMetadataJson
            FROM AsteriaGenerations g
            JOIN AsteriaAssessmentWorks w ON w.WorkId = g.AssessmentWorkId
            JOIN AsteriaReleasePointers p ON p.ReleaseId = g.ReleaseId
            WHERE g.ReleaseId = $releaseId AND g.Generation = $generation;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$generation", generation);
        return await ReadSnapshotAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AsteriaReleaseSnapshot>> ListGenerationsAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var generations = new List<int>();
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Generation
                FROM AsteriaGenerations
                WHERE ReleaseId = $releaseId
                ORDER BY Generation;
                """;
            command.Parameters.AddWithValue("$releaseId", releaseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                generations.Add(reader.GetInt32(0));
            }
        }

        var snapshots = new List<AsteriaReleaseSnapshot>(generations.Count);
        foreach (var generation in generations)
        {
            snapshots.Add(await GetGenerationAsync(releaseId, generation, cancellationToken)
                ?? throw new InvalidDataException(
                    $"Asteria generation {generation} for release '{releaseId}' disappeared while listing."));
        }
        return snapshots;
    }

    public async Task<IReadOnlyList<AsteriaAmendmentSnapshot>> ListAmendmentsAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var amendments = new List<AsteriaAmendmentSnapshot>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT AmendmentId, ReleaseId, RequestFingerprint, ExpectedBaseGeneration,
                   NewGeneration, OldWorkflowInstanceId, NewWorkflowInstanceId, Phase, LastError
            FROM AsteriaAmendments
            WHERE ReleaseId = $releaseId
            ORDER BY CreatedAt, AmendmentId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            amendments.Add(new AsteriaAmendmentSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return amendments;
    }

    public async Task<AsteriaAmendmentSnapshot> ReserveAmendmentAsync(
        string releaseId,
        string amendmentId,
        int expectedBaseGeneration,
        AsteriaReleaseMaterial material,
        string observedOldState,
        string amendmentLeaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        string definitionId = "asteria-release-governance",
        string definitionVersion = "3.0",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(amendmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(amendmentLeaseToken);
        ValidateMaterial(releaseId, material);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionVersion);
        if (expectedBaseGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedBaseGeneration));
        }
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        await InitializeAsync(cancellationToken);
        var canonicalMaterialJson = CanonicalMaterialJson(material);
        var materialDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalMaterialJson)));
        var requestFingerprint = AmendmentFingerprint(
            releaseId, amendmentId, expectedBaseGeneration, canonicalMaterialJson, definitionId, definitionVersion);
        var normalizedNow = now.ToUniversalTime();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await ReadAmendmentAsync(connection, transaction, releaseId, amendmentId, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Amendment identity '{amendmentId}' was already used with a different payload or expected base generation.");
            }
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var current = await LoadAsync(connection, transaction, releaseId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
        if (current.AssessmentGeneration != expectedBaseGeneration)
        {
            throw new InvalidOperationException(
                $"Expected base generation {expectedBaseGeneration}, but release '{releaseId}' is at generation {current.AssessmentGeneration}.");
        }
        if (current.RegistryState != "Operational")
        {
            throw new InvalidOperationException(
                $"Release '{releaseId}' admission is suspended in state '{current.RegistryState}'.");
        }
        if (current.DefinitionVersion != "3.0")
        {
            throw new InvalidOperationException(
                $"Generation {expectedBaseGeneration} uses immutable definition {current.DefinitionId}@{current.DefinitionVersion}, which has no supersession transition.");
        }
        var eligibleStates = new HashSet<string>(StringComparer.Ordinal)
        {
            "Draft", "UnderAssessment", "RequirementsIdentified",
            "ReviewObligationsIdentified", "AssessmentUnresolved"
        };
        if (!eligibleStates.Contains(observedOldState))
        {
            throw new InvalidOperationException(
                $"Asteria generation in state '{observedOldState}' is not eligible for pre-deployment amendment.");
        }
        if (!string.Equals(current.WorkflowInstanceId, current.Work.WorkflowInstanceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Current generation workflow identity is inconsistent.");
        }

        await using (var leaseCheck = connection.CreateCommand())
        {
            leaseCheck.Transaction = transaction;
            leaseCheck.CommandText = """
                SELECT LeaseExpiresAt FROM AsteriaReleasePointers
                WHERE ReleaseId = $releaseId;
                """;
            leaseCheck.Parameters.AddWithValue("$releaseId", releaseId);
            var value = await leaseCheck.ExecuteScalarAsync(cancellationToken);
            if (value is long expiresAt && expiresAt > normalizedNow.ToUnixTimeSeconds())
            {
                throw new InvalidOperationException(
                    $"Asteria release '{releaseId}' is being processed by an active coordinator.");
            }
        }

        var newGeneration = await GetNextGenerationAsync(connection, transaction, releaseId, cancellationToken);
        var workflowInstanceId = StableIdentifier("asteria-", releaseId, newGeneration);
        var workId = StableIdentifier("asteria-work-", releaseId, newGeneration);
        var expectedOperationId = StableIdentifier("asteria-assessment-", releaseId, newGeneration);
        var idempotencyKey = StableIdentifier("ast-", releaseId, newGeneration);
        var businessEffectiveAt = material.PlannedDeploymentAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var timestamp = normalizedNow.ToString("O", CultureInfo.InvariantCulture);

        await using (var insertWork = connection.CreateCommand())
        {
            insertWork.Transaction = transaction;
            insertWork.CommandText = """
                INSERT INTO AsteriaAssessmentWorks
                    (WorkId, ReleaseId, Generation, MaterialDigest, WorkflowInstanceId, DefinitionId,
                     DefinitionVersion, ExpectedOperationId, DecisionIdempotencyKey, BusinessEffectiveAt,
                     KnownAt, SelectedPolicyVersionsJson, Status, RecoveryMetadataJson, CreatedAt, UpdatedAt)
                VALUES ($workId, $releaseId, $generation, $materialDigest, $instanceId, $definitionId,
                        $definitionVersion, $operationId, $idempotencyKey, $effectiveAt,
                        $knownAt, '[]', 'Reserved', '{}', $now, $now);
                """;
            insertWork.Parameters.AddWithValue("$workId", workId);
            insertWork.Parameters.AddWithValue("$releaseId", releaseId);
            insertWork.Parameters.AddWithValue("$generation", newGeneration);
            insertWork.Parameters.AddWithValue("$materialDigest", materialDigest);
            insertWork.Parameters.AddWithValue("$instanceId", workflowInstanceId);
            insertWork.Parameters.AddWithValue("$definitionId", definitionId);
            insertWork.Parameters.AddWithValue("$definitionVersion", definitionVersion);
            insertWork.Parameters.AddWithValue("$operationId", expectedOperationId);
            insertWork.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
            insertWork.Parameters.AddWithValue("$effectiveAt", businessEffectiveAt);
            insertWork.Parameters.AddWithValue("$knownAt", timestamp);
            insertWork.Parameters.AddWithValue("$now", timestamp);
            await insertWork.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertGeneration = connection.CreateCommand())
        {
            insertGeneration.Transaction = transaction;
            insertGeneration.CommandText = """
                INSERT INTO AsteriaGenerations
                    (ReleaseId, Generation, CanonicalMaterialJson, MaterialDigest, WorkflowInstanceId,
                     DefinitionId, DefinitionVersion, AssessmentWorkId, CreatedAt)
                VALUES ($releaseId, $generation, $materialJson, $materialDigest, $instanceId,
                        $definitionId, $definitionVersion, $workId, $now);
                """;
            insertGeneration.Parameters.AddWithValue("$releaseId", releaseId);
            insertGeneration.Parameters.AddWithValue("$generation", newGeneration);
            insertGeneration.Parameters.AddWithValue("$materialJson", canonicalMaterialJson);
            insertGeneration.Parameters.AddWithValue("$materialDigest", materialDigest);
            insertGeneration.Parameters.AddWithValue("$instanceId", workflowInstanceId);
            insertGeneration.Parameters.AddWithValue("$definitionId", definitionId);
            insertGeneration.Parameters.AddWithValue("$definitionVersion", definitionVersion);
            insertGeneration.Parameters.AddWithValue("$workId", workId);
            insertGeneration.Parameters.AddWithValue("$now", timestamp);
            await insertGeneration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertAmendment = connection.CreateCommand())
        {
            insertAmendment.Transaction = transaction;
            insertAmendment.CommandText = """
                INSERT INTO AsteriaAmendments
                    (AmendmentId, ReleaseId, RequestFingerprint, ExpectedBaseGeneration, NewGeneration,
                     OldWorkflowInstanceId, NewWorkflowInstanceId, NewWorkId, Phase, CreatedAt, UpdatedAt)
                VALUES ($amendmentId, $releaseId, $fingerprint, $baseGeneration, $newGeneration,
                        $oldInstanceId, $newInstanceId, $workId, 'AmendmentReserved', $now, $now);
                """;
            insertAmendment.Parameters.AddWithValue("$amendmentId", amendmentId);
            insertAmendment.Parameters.AddWithValue("$releaseId", releaseId);
            insertAmendment.Parameters.AddWithValue("$fingerprint", requestFingerprint);
            insertAmendment.Parameters.AddWithValue("$baseGeneration", expectedBaseGeneration);
            insertAmendment.Parameters.AddWithValue("$newGeneration", newGeneration);
            insertAmendment.Parameters.AddWithValue("$oldInstanceId", current.WorkflowInstanceId);
            insertAmendment.Parameters.AddWithValue("$newInstanceId", workflowInstanceId);
            insertAmendment.Parameters.AddWithValue("$workId", workId);
            insertAmendment.Parameters.AddWithValue("$now", timestamp);
            await insertAmendment.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var suspend = connection.CreateCommand())
        {
            suspend.Transaction = transaction;
            suspend.CommandText = """
                UPDATE AsteriaReleasePointers
                SET RegistryState = 'AmendmentPending', LeaseToken = $token,
                    LeaseExpiresAt = $leaseExpires, UpdatedAt = $now
                WHERE ReleaseId = $releaseId AND CurrentGeneration = $baseGeneration
                  AND RegistryState = 'Operational'
                  AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= $nowEpoch);
                """;
            suspend.Parameters.AddWithValue("$token", amendmentLeaseToken);
            suspend.Parameters.AddWithValue("$leaseExpires", normalizedNow.Add(leaseDuration).ToUnixTimeSeconds());
            suspend.Parameters.AddWithValue("$now", timestamp);
            suspend.Parameters.AddWithValue("$releaseId", releaseId);
            suspend.Parameters.AddWithValue("$baseGeneration", expectedBaseGeneration);
            suspend.Parameters.AddWithValue("$nowEpoch", normalizedNow.ToUnixTimeSeconds());
            if (await suspend.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Amendment reservation lost a concurrent registry update.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new AsteriaAmendmentSnapshot(
            amendmentId, releaseId, requestFingerprint, expectedBaseGeneration, newGeneration,
            current.WorkflowInstanceId, workflowInstanceId, "AmendmentReserved", null);
    }

    public async Task<AsteriaAmendmentSnapshot?> GetAmendmentAsync(
        string releaseId,
        string amendmentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadAmendmentAsync(connection, null, releaseId, amendmentId, cancellationToken);
    }

    public async Task<AsteriaSupersessionAttempt> GetOrCreateSupersessionAttemptAsync(
        string releaseId,
        string amendmentId,
        long expectedRevision,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var amendment = await ReadAmendmentAsync(connection, transaction, releaseId, amendmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria amendment '{amendmentId}' does not exist.");
        await AssertAmendmentLeaseAsync(connection, transaction, releaseId, leaseToken, now, cancellationToken);
        if (amendment.Phase != "NewInstanceProvisioned")
        {
            throw new InvalidOperationException(
                $"A supersession attempt cannot be allocated during phase '{amendment.Phase}'.");
        }

        var existing = await ReadSupersessionAttemptAsync(
            connection, transaction, releaseId, amendmentId, expectedRevision, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        var operationId = StableTextIdentifier(
            "asteria-supersession-op-", releaseId, amendmentId,
            expectedRevision.ToString(CultureInfo.InvariantCulture));
        var messageId = StableTextIdentifier(
            "asteria-supersession-msg-", releaseId, amendmentId,
            expectedRevision.ToString(CultureInfo.InvariantCulture));
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AsteriaSupersessionAttempts
                (ReleaseId, AmendmentId, ExpectedRevision, OperationId, MessageId, CreatedAt)
            VALUES ($releaseId, $amendmentId, $revision, $operationId, $messageId, $createdAt);
            """;
        insert.Parameters.AddWithValue("$releaseId", releaseId);
        insert.Parameters.AddWithValue("$amendmentId", amendmentId);
        insert.Parameters.AddWithValue("$revision", expectedRevision);
        insert.Parameters.AddWithValue("$operationId", operationId);
        insert.Parameters.AddWithValue("$messageId", messageId);
        insert.Parameters.AddWithValue("$createdAt", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AsteriaSupersessionAttempt(
            releaseId, amendmentId, expectedRevision, operationId, messageId);
    }

    public async Task<IReadOnlyList<AsteriaSupersessionAttempt>> ListSupersessionAttemptsAsync(
        string releaseId,
        string amendmentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ReleaseId, AmendmentId, ExpectedRevision, OperationId, MessageId
            FROM AsteriaSupersessionAttempts
            WHERE ReleaseId = $releaseId AND AmendmentId = $amendmentId
            ORDER BY ExpectedRevision;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$amendmentId", amendmentId);
        var attempts = new List<AsteriaSupersessionAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            attempts.Add(new AsteriaSupersessionAttempt(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4)));
        }
        return attempts;
    }

    public async Task<bool> AcquireAmendmentLeaseAsync(
        string releaseId,
        string amendmentId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleasePointers
            SET LeaseToken = $token, LeaseExpiresAt = $expires, UpdatedAt = $updated
            WHERE ReleaseId = $releaseId
              AND RegistryState IN ('AmendmentPending', 'ActivationIncomplete')
              AND (LeaseToken = $token OR LeaseExpiresAt IS NULL OR LeaseExpiresAt <= $now)
              AND EXISTS (
                  SELECT 1 FROM AsteriaAmendments a
                  WHERE a.ReleaseId = $releaseId AND a.AmendmentId = $amendmentId
                    AND a.Phase <> 'NewGenerationActivated');
            """;
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$expires", now.Add(duration).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$updated", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$amendmentId", amendmentId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> RenewAmendmentLeaseAsync(
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
            UPDATE AsteriaReleasePointers SET LeaseExpiresAt = $expires
            WHERE ReleaseId = $releaseId AND LeaseToken = $token
              AND RegistryState IN ('AmendmentPending', 'ActivationIncomplete')
              AND LeaseExpiresAt > $now;
            """;
        command.Parameters.AddWithValue("$expires", now.Add(duration).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task AssertAmendmentAdmissionAsync(
        string releaseId,
        string amendmentId,
        int expectedBaseGeneration,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleasePointers
            SET LeaseExpiresAt = $expires
            WHERE ReleaseId = $releaseId AND CurrentGeneration = $baseGeneration
              AND LeaseToken = $token
              AND LeaseExpiresAt > $now
              AND RegistryState IN ('AmendmentPending', 'ActivationIncomplete')
              AND EXISTS (
                  SELECT 1 FROM AsteriaAmendments a
                  WHERE a.ReleaseId = $releaseId AND a.AmendmentId = $amendmentId
                    AND a.ExpectedBaseGeneration = $baseGeneration
                    AND a.Phase <> 'NewGenerationActivated');
            """;
        command.Parameters.AddWithValue("$expires", now.Add(duration).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$baseGeneration", expectedBaseGeneration);
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$amendmentId", amendmentId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                $"Amendment '{amendmentId}' no longer has current-generation admission for base {expectedBaseGeneration}.");
        }
    }

    public async Task ReleaseAmendmentLeaseAsync(
        string releaseId,
        string leaseToken,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleasePointers SET LeaseToken = NULL, LeaseExpiresAt = NULL
            WHERE ReleaseId = $releaseId AND LeaseToken = $token
              AND RegistryState IN ('AmendmentPending', 'ActivationIncomplete');
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$token", leaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AssertCoordinatorAdmissionAsync(
        string releaseId,
        int generation,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM AsteriaReleasePointers
            WHERE ReleaseId = $releaseId AND CurrentGeneration = $generation
              AND RegistryState = 'Operational' AND LeaseToken = $token
              AND LeaseExpiresAt > $now;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException(
                $"Coordinator admission for release '{releaseId}' generation {generation} is no longer current.");
        }
    }

    public async Task AdvanceAmendmentPhaseAsync(
        string releaseId,
        string amendmentId,
        string leaseToken,
        string phase,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var next = phase switch
        {
            "NewInstanceProvisioned" => "AmendmentReserved",
            "OldInstanceSuperseded" => "NewInstanceProvisioned",
            _ => throw new ArgumentOutOfRangeException(nameof(phase), "Only recoverable intermediate phases may be advanced directly.")
        };
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var amendment = await ReadAmendmentAsync(connection, transaction, releaseId, amendmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria amendment '{amendmentId}' does not exist.");
        await AssertAmendmentLeaseAsync(connection, transaction, releaseId, leaseToken, now, cancellationToken);
        if (amendment.Phase == phase)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (amendment.Phase != next)
        {
            throw new InvalidOperationException(
                $"Amendment phase '{amendment.Phase}' cannot advance to '{phase}'.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaAmendments SET Phase = $phase, UpdatedAt = $now
            WHERE ReleaseId = $releaseId AND AmendmentId = $amendmentId AND Phase = $previous;
            """;
        update.Parameters.AddWithValue("$phase", phase);
        update.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$releaseId", releaseId);
        update.Parameters.AddWithValue("$amendmentId", amendmentId);
        update.Parameters.AddWithValue("$previous", next);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Amendment phase update lost a concurrent registry change.");
        }
        if (phase == "OldInstanceSuperseded")
        {
            await using var fence = connection.CreateCommand();
            fence.Transaction = transaction;
            fence.CommandText = """
                UPDATE AsteriaReleasePointers SET RegistryState = 'ActivationIncomplete', UpdatedAt = $now
                WHERE ReleaseId = $releaseId AND RegistryState = 'AmendmentPending';
                """;
            fence.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            fence.Parameters.AddWithValue("$releaseId", releaseId);
            if (await fence.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Release admission could not be fenced after supersession.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ActivateAmendedGenerationAsync(
        string releaseId,
        string amendmentId,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var amendment = await ReadAmendmentAsync(connection, transaction, releaseId, amendmentId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria amendment '{amendmentId}' does not exist.");
        if (amendment.Phase == "NewGenerationActivated")
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (amendment.Phase != "OldInstanceSuperseded")
        {
            throw new InvalidOperationException("The old generation must be superseded before activation.");
        }
        await AssertAmendmentLeaseAsync(connection, transaction, releaseId, leaseToken, now, cancellationToken);

        var timestamp = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await using (var updateWork = connection.CreateCommand())
        {
            updateWork.Transaction = transaction;
            updateWork.CommandText = """
                UPDATE AsteriaAssessmentWorks SET Status = 'Registered', UpdatedAt = $now
                WHERE WorkId = $workId AND Status = 'Reserved';
                """;
            updateWork.Parameters.AddWithValue("$now", timestamp);
            updateWork.Parameters.AddWithValue("$workId", await GetNewWorkIdAsync(
                connection, transaction, releaseId, amendmentId, cancellationToken));
            if (await updateWork.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Reserved assessment work was not available for activation.");
            }
        }
        await using (var updatePointer = connection.CreateCommand())
        {
            updatePointer.Transaction = transaction;
            updatePointer.CommandText = """
                UPDATE AsteriaReleasePointers
                SET CurrentGeneration = $generation, CurrentAssessmentWorkId = $workId,
                    Status = 'Registered', RegistryState = 'Operational',
                    LeaseToken = NULL, LeaseExpiresAt = NULL, UpdatedAt = $now
                WHERE ReleaseId = $releaseId AND CurrentGeneration = $baseGeneration
                  AND RegistryState = 'ActivationIncomplete';
                """;
            updatePointer.Parameters.AddWithValue("$generation", amendment.NewGeneration);
            updatePointer.Parameters.AddWithValue("$workId", await GetNewWorkIdAsync(
                connection, transaction, releaseId, amendmentId, cancellationToken));
            updatePointer.Parameters.AddWithValue("$now", timestamp);
            updatePointer.Parameters.AddWithValue("$releaseId", releaseId);
            updatePointer.Parameters.AddWithValue("$baseGeneration", amendment.ExpectedBaseGeneration);
            if (await updatePointer.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Current-generation activation lost its fenced base pointer.");
            }
        }
        await using (var updateAmendment = connection.CreateCommand())
        {
            updateAmendment.Transaction = transaction;
            updateAmendment.CommandText = """
                UPDATE AsteriaAmendments SET Phase = 'NewGenerationActivated', UpdatedAt = $now
                WHERE ReleaseId = $releaseId AND AmendmentId = $amendmentId
                  AND Phase = 'OldInstanceSuperseded';
                """;
            updateAmendment.Parameters.AddWithValue("$now", timestamp);
            updateAmendment.Parameters.AddWithValue("$releaseId", releaseId);
            updateAmendment.Parameters.AddWithValue("$amendmentId", amendmentId);
            if (await updateAmendment.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Amendment activation record could not be committed.");
            }
        }
        await transaction.CommitAsync(cancellationToken);
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
            UPDATE AsteriaReleasePointers
            SET LeaseToken = $token, LeaseExpiresAt = $expiresAt
            WHERE ReleaseId = $releaseId
              AND RegistryState = 'Operational'
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
            UPDATE AsteriaReleasePointers
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
            UPDATE AsteriaReleasePointers
            SET LeaseExpiresAt = $expiresAt
            WHERE ReleaseId = $releaseId
              AND RegistryState = 'Operational'
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
            WHERE WorkId = (SELECT CurrentAssessmentWorkId FROM AsteriaReleasePointers
                            WHERE ReleaseId = $releaseId AND RegistryState = 'Operational')
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
            WHERE WorkId = (SELECT CurrentAssessmentWorkId FROM AsteriaReleasePointers
                            WHERE ReleaseId = $releaseId AND RegistryState = 'Operational')
              AND DecisionRequestJson IS NULL;
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
            WHERE WorkId = (SELECT CurrentAssessmentWorkId FROM AsteriaReleasePointers
                            WHERE ReleaseId = $releaseId AND RegistryState = 'Operational')
              AND OriginalResponseJson IS NULL;
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
            WHERE WorkId = (SELECT CurrentAssessmentWorkId FROM AsteriaReleasePointers
                            WHERE ReleaseId = $releaseId AND RegistryState = 'Operational');
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
            WHERE WorkId = (SELECT CurrentAssessmentWorkId FROM AsteriaReleasePointers
                            WHERE ReleaseId = $releaseId AND RegistryState = 'Operational');
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
            UPDATE AsteriaReleasePointers SET Status = $status, UpdatedAt = $now
            WHERE ReleaseId = $releaseId AND RegistryState = 'Operational';
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
            SELECT r.ReleaseId, g.WorkflowInstanceId, g.DefinitionId, g.DefinitionVersion,
                   g.CanonicalMaterialJson, p.CurrentGeneration, g.MaterialDigest,
                   p.CurrentAssessmentWorkId, p.Status, p.RegistryState,
                   w.WorkId, w.ReleaseId, w.Generation, w.MaterialDigest, w.WorkflowInstanceId,
                   w.DefinitionId, w.DefinitionVersion, w.ParentRevision, w.ExpectedOperationId,
                   w.DecisionIdempotencyKey, w.BusinessEffectiveAt, w.KnownAt,
                   w.DecisionRequestFingerprint, w.DecisionRequestJson, w.EvaluationId,
                   w.SelectedPolicyVersionsJson, w.OriginalResponseJson, w.OriginalEvidenceJson,
                   w.Status, w.RecoveryMetadataJson
            FROM AsteriaReleases r
            JOIN AsteriaReleasePointers p ON p.ReleaseId = r.ReleaseId
            JOIN AsteriaGenerations g
              ON g.ReleaseId = p.ReleaseId AND g.Generation = p.CurrentGeneration
            JOIN AsteriaAssessmentWorks w ON w.WorkId = p.CurrentAssessmentWorkId
            WHERE r.ReleaseId = $releaseId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        return await ReadSnapshotAsync(command, cancellationToken);
    }

    private static async Task<AsteriaReleaseSnapshot?> ReadSnapshotAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var material = JsonSerializer.Deserialize<AsteriaReleaseMaterial>(reader.GetString(4), JsonOptions)
            ?? throw new InvalidDataException("The canonical release material is invalid.");
        var work = new AsteriaAssessmentWork(
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt32(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.GetString(18),
            reader.GetString(19),
            reader.GetString(20),
            DateTimeOffset.Parse(reader.GetString(21), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.IsDBNull(23) ? null : reader.GetString(23),
            reader.IsDBNull(24) ? null : reader.GetString(24),
            reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26),
            reader.IsDBNull(27) ? null : reader.GetString(27),
            reader.GetString(28),
            reader.GetString(29));
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
            work,
            reader.GetString(9));
    }

    private static async Task<int> GetNextGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string releaseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(Generation), 0) + 1 FROM AsteriaGenerations WHERE ReleaseId = $releaseId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<AsteriaAmendmentSnapshot?> ReadAmendmentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string releaseId,
        string amendmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT AmendmentId, ReleaseId, RequestFingerprint, ExpectedBaseGeneration, NewGeneration,
                   OldWorkflowInstanceId, NewWorkflowInstanceId, Phase, LastError
            FROM AsteriaAmendments WHERE ReleaseId = $releaseId AND AmendmentId = $amendmentId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$amendmentId", amendmentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AsteriaAmendmentSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8))
            : null;
    }

    private static async Task<AsteriaSupersessionAttempt?> ReadSupersessionAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string releaseId,
        string amendmentId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ReleaseId, AmendmentId, ExpectedRevision, OperationId, MessageId
            FROM AsteriaSupersessionAttempts
            WHERE ReleaseId = $releaseId AND AmendmentId = $amendmentId AND ExpectedRevision = $revision;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$amendmentId", amendmentId);
        command.Parameters.AddWithValue("$revision", expectedRevision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AsteriaSupersessionAttempt(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4))
            : null;
    }

    private static async Task AssertAmendmentLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string releaseId,
        string leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM AsteriaReleasePointers
            WHERE ReleaseId = $releaseId AND LeaseToken = $token
              AND LeaseExpiresAt > $now
              AND RegistryState IN ('AmendmentPending', 'ActivationIncomplete');
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$token", leaseToken);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException("The amendment coordinator no longer holds the release fence.");
        }
    }

    private static async Task<string> GetNewWorkIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string releaseId,
        string amendmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT NewWorkId FROM AsteriaAmendments
            WHERE ReleaseId = $releaseId AND AmendmentId = $amendmentId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$amendmentId", amendmentId);
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidDataException("Reserved amendment work identity is missing.");
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

    private static string StableTextIdentifier(string prefix, params string[] components)
    {
        var input = Encoding.UTF8.GetBytes(string.Join('\0', components));
        return prefix + Convert.ToHexStringLower(SHA256.HashData(input));
    }

    private static string AmendmentFingerprint(
        string releaseId,
        string amendmentId,
        int expectedBaseGeneration,
        string canonicalMaterialJson,
        string definitionId,
        string definitionVersion)
    {
        var input = string.Join('\0',
            releaseId,
            amendmentId,
            expectedBaseGeneration.ToString(CultureInfo.InvariantCulture),
            canonicalMaterialJson,
            definitionId,
            definitionVersion);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
