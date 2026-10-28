using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.AsteriaClient;

public sealed record AsteriaActivityReservation(
    string WorkId,
    string ActivityId,
    string ReleaseId,
    int Generation,
    string MaterialDigest,
    string WorkflowInstanceId,
    string DefinitionId,
    string DefinitionVersion,
    string DefinitionDigest,
    string ConcernId,
    string ActivityType,
    string AttemptId,
    string ExpectedOperationId,
    long ParentRevision,
    long ConcernRevision,
    string SubmissionId,
    string IdempotencyKey,
    string OriginalRequestJson,
    string? ProviderIdentity,
    string? ApiDescriptionVersionId,
    string? OperationKey,
    string? AuthorityPrincipalId,
    string? AuthorityRole,
    string? AuthorityRegistryVersion);

public sealed record AsteriaActivitySnapshot(
    string WorkId,
    string ActivityId,
    string ReleaseId,
    int Generation,
    string MaterialDigest,
    string WorkflowInstanceId,
    string DefinitionId,
    string DefinitionVersion,
    string DefinitionDigest,
    string ConcernId,
    string ActivityType,
    string AttemptId,
    string ExpectedOperationId,
    long ParentRevision,
    long ConcernRevision,
    string SubmissionId,
    string IdempotencyKey,
    string RequestFingerprint,
    string OriginalRequestJson,
    string? ProviderIdentity,
    string? ApiDescriptionVersionId,
    string? OperationKey,
    string? AuthorityPrincipalId,
    string? AuthorityRole,
    string? AuthorityRegistryVersion,
    string Status,
    string? HarnessExecutionId,
    string? OriginalResponseJson,
    string? ResponseDigest,
    string? ValidatedOutcome,
    string? ValidatedEvidenceDigest,
    string? ObservationReceiptId,
    string? AcceptedAttemptId,
    string? AcceptedHistoryId,
    string? RecoveryDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AsteriaActivityProviderObservation(
    string ObservationId,
    string ObservationKind,
    string ResponseJson,
    string ResponseDigest,
    DateTimeOffset RecordedAt);

public sealed record AsteriaActivityConflict(
    string ConflictId,
    string WorkId,
    string ConflictKind,
    string ConflictingFingerprint,
    string ConflictingPayloadJson,
    DateTimeOffset RecordedAt);

public sealed class AsteriaActivityLedger : IAsyncDisposable
{
    private readonly string _connectionString;

    public AsteriaActivityLedger(string databasePath)
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
            CREATE TABLE IF NOT EXISTS AsteriaActivities (
                WorkId TEXT NOT NULL PRIMARY KEY,
                ActivityId TEXT NOT NULL,
                ReleaseId TEXT NOT NULL,
                Generation INTEGER NOT NULL CHECK (Generation > 0),
                MaterialDigest TEXT NOT NULL,
                WorkflowInstanceId TEXT NOT NULL,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                DefinitionDigest TEXT NOT NULL,
                ConcernId TEXT NOT NULL,
                ActivityType TEXT NOT NULL,
                AttemptId TEXT NOT NULL,
                ExpectedOperationId TEXT NOT NULL UNIQUE,
                ParentRevision INTEGER NOT NULL CHECK (ParentRevision >= 0),
                ConcernRevision INTEGER NOT NULL CHECK (ConcernRevision >= 0),
                SubmissionId TEXT NOT NULL UNIQUE,
                IdempotencyKey TEXT NOT NULL UNIQUE,
                RequestFingerprint TEXT NOT NULL,
                OriginalRequestJson TEXT NOT NULL,
                ProviderIdentity TEXT NULL,
                ApiDescriptionVersionId TEXT NULL,
                OperationKey TEXT NULL,
                AuthorityPrincipalId TEXT NULL,
                AuthorityRole TEXT NULL,
                AuthorityRegistryVersion TEXT NULL,
                Status TEXT NOT NULL CHECK (Status IN (
                    'Reserved', 'OperationRegistered', 'DispatchPending',
                    'Dispatched', 'ResponsePersisted', 'ObservationSubmitted',
                    'Accepted', 'OutcomeUnknown', 'Refused', 'Conflicting')),
                HarnessExecutionId TEXT NULL,
                OriginalResponseJson TEXT NULL,
                ResponseDigest TEXT NULL,
                ValidatedOutcome TEXT NULL,
                ValidatedEvidenceDigest TEXT NULL,
                ObservationReceiptId TEXT NULL,
                AcceptedAttemptId TEXT NULL,
                AcceptedHistoryId TEXT NULL,
                RecoveryDetail TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UNIQUE (ReleaseId, Generation, ActivityType, AttemptId),
                UNIQUE (ReleaseId, Generation, ActivityId),
                FOREIGN KEY (ReleaseId, Generation)
                    REFERENCES AsteriaGenerations(ReleaseId, Generation)
            );
            CREATE INDEX IF NOT EXISTS IX_AsteriaActivities_Generation_Status
                ON AsteriaActivities(ReleaseId, Generation, Status, CreatedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_AsteriaActivities_HarnessExecution
                ON AsteriaActivities(HarnessExecutionId)
                WHERE HarnessExecutionId IS NOT NULL;
            CREATE TABLE IF NOT EXISTS AsteriaActivityConflicts (
                ConflictId TEXT NOT NULL PRIMARY KEY,
                WorkId TEXT NOT NULL REFERENCES AsteriaActivities(WorkId),
                ConflictKind TEXT NOT NULL,
                ConflictingFingerprint TEXT NOT NULL,
                ConflictingPayloadJson TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                UNIQUE (WorkId, ConflictKind, ConflictingFingerprint)
            );
            CREATE TABLE IF NOT EXISTS AsteriaActivityDispatchClaims (
                WorkId TEXT NOT NULL PRIMARY KEY REFERENCES AsteriaActivities(WorkId),
                ClaimedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AsteriaActivityProviderObservations (
                WorkId TEXT NOT NULL REFERENCES AsteriaActivities(WorkId),
                ObservationId TEXT NOT NULL,
                ObservationKind TEXT NOT NULL,
                ResponseJson TEXT NOT NULL,
                ResponseDigest TEXT NOT NULL,
                RecordedAt TEXT NOT NULL,
                PRIMARY KEY (WorkId, ObservationId)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AsteriaActivitySnapshot> ReserveAsync(
        AsteriaActivityReservation reservation,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateReservation(reservation);
        await InitializeAsync(cancellationToken);
        var normalizedNow = now.ToUniversalTime().ToString("O");
        var fingerprint = Fingerprint(reservation.OriginalRequestJson);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await FindByLogicalAttemptAsync(
            connection, transaction, reservation, cancellationToken);
        if (existing is not null)
        {
            if (ReservationMatches(existing, reservation, fingerprint))
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                existing.WorkId,
                "reservation-identity-conflict",
                fingerprint,
                reservation.OriginalRequestJson,
                normalizedNow,
                cancellationToken);
            if (CanMarkConflicting(existing.Status))
            {
                await SetStatusAsync(
                    connection, transaction, existing.WorkId, "Conflicting", normalizedNow, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Asteria activity attempt '{reservation.ActivityType}/{reservation.AttemptId}' was reused with conflicting binding or request data.");
        }

        var collidingWorkId = await FindIdentityCollisionAsync(
            connection, transaction, reservation, cancellationToken);
        if (collidingWorkId is not null)
        {
            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                collidingWorkId,
                "stable-identity-conflict",
                fingerprint,
                reservation.OriginalRequestJson,
                normalizedNow,
                cancellationToken);
            var collision = await ReadByWorkIdAsync(
                connection, transaction, collidingWorkId, cancellationToken)
                ?? throw new InvalidDataException("Asteria activity identity collision disappeared.");
            if (CanMarkConflicting(collision.Status))
            {
                await SetStatusAsync(
                    connection, transaction, collidingWorkId, "Conflicting", normalizedNow, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Asteria activity identity '{reservation.WorkId}' conflicts with an existing durable activity.");
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AsteriaActivities (
                WorkId, ActivityId, ReleaseId, Generation, MaterialDigest,
                WorkflowInstanceId, DefinitionId, DefinitionVersion, DefinitionDigest,
                ConcernId, ActivityType, AttemptId, ExpectedOperationId, ParentRevision,
                ConcernRevision, SubmissionId, IdempotencyKey, RequestFingerprint,
                OriginalRequestJson, ProviderIdentity, ApiDescriptionVersionId, OperationKey,
                AuthorityPrincipalId, AuthorityRole, AuthorityRegistryVersion,
                Status, CreatedAt, UpdatedAt)
            VALUES (
                $workId, $activityId, $releaseId, $generation, $materialDigest,
                $workflowInstanceId, $definitionId, $definitionVersion, $definitionDigest,
                $concernId, $activityType, $attemptId, $expectedOperationId, $parentRevision,
                $concernRevision, $submissionId, $idempotencyKey, $requestFingerprint,
                $requestJson, $providerIdentity, $apiDescriptionVersionId, $operationKey,
                $authorityPrincipalId, $authorityRole, $authorityRegistryVersion,
                'Reserved', $now, $now);
            """;
        AddReservationParameters(insert, reservation, fingerprint, normalizedNow);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(reservation.WorkId, cancellationToken)
            ?? throw new InvalidDataException("Reserved Asteria activity could not be read back.");
    }

    public async Task<AsteriaActivitySnapshot> MarkExpectedOperationRegisteredAsync(
        string workId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await AdvanceStatusAsync(
            workId,
            "Reserved",
            "OperationRegistered",
            now,
            cancellationToken);

    public async Task<AsteriaActivitySnapshot> MarkDispatchPendingAsync(
        string workId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await AdvanceStatusAsync(
            workId,
            "OperationRegistered",
            "DispatchPending",
            now,
            cancellationToken);

    public async Task<(AsteriaActivitySnapshot Activity, bool ShouldDispatch)> MarkDispatchStartedAsync(
        string workId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == "DispatchPending")
        {
            await using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                INSERT OR IGNORE INTO AsteriaActivityDispatchClaims (WorkId, ClaimedAt)
                VALUES ($workId, $claimedAt);
                """;
            claim.Parameters.AddWithValue("$workId", workId);
            claim.Parameters.AddWithValue("$claimedAt", nowText);
            var shouldDispatch = await claim.ExecuteNonQueryAsync(cancellationToken) == 1;
            await SetStatusAsync(
                connection, transaction, workId, "Dispatched", nowText, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (
                await GetAsync(workId, cancellationToken)
                    ?? throw new InvalidDataException("Asteria activity disappeared after dispatch was claimed."),
                shouldDispatch);
        }
        if (current.Status is "Dispatched" or "ResponsePersisted" or
            "ObservationSubmitted" or "Accepted" or "OutcomeUnknown")
        {
            await transaction.CommitAsync(cancellationToken);
            return (current, false);
        }
        throw new InvalidOperationException(
            $"Asteria activity '{workId}' cannot begin dispatch while in status '{current.Status}'.");
    }

    public async Task<AsteriaActivitySnapshot> RecordHarnessExecutionAsync(
        string workId,
        string harnessExecutionId,
        string providerIdentity,
        string apiDescriptionVersionId,
        string operationKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessExecutionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiDescriptionVersionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.ProviderIdentity != providerIdentity ||
            current.ApiDescriptionVersionId != apiDescriptionVersionId ||
            current.OperationKey != operationKey)
        {
            throw new InvalidDataException("Harness execution does not match the activity's persisted provider binding.");
        }
        if (current.Status is not ("DispatchPending" or "Dispatched" or "Accepted"))
        {
            throw new InvalidOperationException(
                $"Harness execution identity cannot be recorded while activity is in status '{current.Status}'.");
        }
        if (current.HarnessExecutionId is not null)
        {
            if (current.HarnessExecutionId == harnessExecutionId)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                workId,
                "harness-execution-conflict",
                Fingerprint(harnessExecutionId),
                harnessExecutionId,
                now.ToUniversalTime().ToString("O"),
                cancellationToken);
            if (CanMarkConflicting(current.Status))
            {
                await SetStatusAsync(
                    connection, transaction, workId, "Conflicting",
                    now.ToUniversalTime().ToString("O"), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria activity was associated with a conflicting Harness execution identity.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET HarnessExecutionId = $executionId,
                Status = CASE WHEN Status = 'Accepted' THEN Status ELSE 'Dispatched' END,
                UpdatedAt = $now
            WHERE WorkId = $workId AND HarnessExecutionId IS NULL;
            """;
        update.Parameters.AddWithValue("$executionId", harnessExecutionId);
        update.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O"));
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after recording Harness execution.");
    }

    public async Task<AsteriaActivitySnapshot> RecordOriginalResponseAsync(
        string workId,
        string originalResponseJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalResponseJson);
        using var parsedResponse = System.Text.Json.JsonDocument.Parse(originalResponseJson);
        var digest = Fingerprint(originalResponseJson);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.OriginalResponseJson is not null)
        {
            if (current.OriginalResponseJson == originalResponseJson)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                workId,
                "provider-response-conflict",
                digest,
                originalResponseJson,
                nowText,
                cancellationToken);
            if (CanMarkConflicting(current.Status))
            {
                await SetStatusAsync(connection, transaction, workId, "Conflicting", nowText, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria activity received a conflicting provider response.");
        }
        if (current.Status is not ("DispatchPending" or "Dispatched" or "Accepted"))
        {
            throw new InvalidOperationException(
                $"Provider response cannot be archived while activity is in status '{current.Status}'.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET OriginalResponseJson = $response, ResponseDigest = $digest,
                Status = CASE WHEN Status = 'Accepted' THEN Status ELSE 'ResponsePersisted' END,
                UpdatedAt = $now
            WHERE WorkId = $workId AND OriginalResponseJson IS NULL;
            """;
        update.Parameters.AddWithValue("$response", originalResponseJson);
        update.Parameters.AddWithValue("$digest", digest);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after archiving provider response.");
    }

    public async Task<AsteriaActivitySnapshot> RecordCoordinatorResponseAsync(
        string workId,
        string responseJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responseJson);
        using var parsedResponse = System.Text.Json.JsonDocument.Parse(responseJson);
        var digest = Fingerprint(responseJson);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.OriginalResponseJson is not null)
        {
            if (current.OriginalResponseJson == responseJson)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                workId,
                "coordinator-response-conflict",
                digest,
                responseJson,
                nowText,
                cancellationToken);
            if (CanMarkConflicting(current.Status))
            {
                await SetStatusAsync(connection, transaction, workId, "Conflicting", nowText, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria activity received a conflicting coordinator response.");
        }
        if (current.Status != "OperationRegistered")
        {
            throw new InvalidOperationException(
                $"Coordinator response cannot be recorded while activity is in status '{current.Status}'.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET OriginalResponseJson = $response, ResponseDigest = $digest,
                Status = 'ResponsePersisted', UpdatedAt = $now
            WHERE WorkId = $workId AND OriginalResponseJson IS NULL;
            """;
        update.Parameters.AddWithValue("$response", responseJson);
        update.Parameters.AddWithValue("$digest", digest);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after archiving coordinator response.");
    }

    public async Task RecordProviderObservationAsync(
        string workId,
        string observationId,
        string observationKind,
        string responseJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationKind);
        ArgumentNullException.ThrowIfNull(responseJson);
        using var parsedResponse = System.Text.Json.JsonDocument.Parse(responseJson);
        var digest = Fingerprint(responseJson);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        await using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT ObservationKind, ResponseJson, ResponseDigest
            FROM AsteriaActivityProviderObservations
            WHERE WorkId = $workId AND ObservationId = $observationId;
            """;
        existing.Parameters.AddWithValue("$workId", workId);
        existing.Parameters.AddWithValue("$observationId", observationId);
        await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) == observationKind &&
                reader.GetString(1) == responseJson &&
                reader.GetString(2) == digest)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            await reader.DisposeAsync();
            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                workId,
                "provider-observation-conflict",
                digest,
                responseJson,
                now.ToUniversalTime().ToString("O"),
                cancellationToken);
            if (CanMarkConflicting(current.Status))
            {
                await SetStatusAsync(
                    connection, transaction, workId, "Conflicting",
                    now.ToUniversalTime().ToString("O"), cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria provider observation identity was reused with conflicting content.");
        }
        await reader.DisposeAsync();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO AsteriaActivityProviderObservations (
                WorkId, ObservationId, ObservationKind, ResponseJson, ResponseDigest, RecordedAt)
            VALUES ($workId, $observationId, $kind, $response, $digest, $recordedAt);
            """;
        insert.Parameters.AddWithValue("$workId", workId);
        insert.Parameters.AddWithValue("$observationId", observationId);
        insert.Parameters.AddWithValue("$kind", observationKind);
        insert.Parameters.AddWithValue("$response", responseJson);
        insert.Parameters.AddWithValue("$digest", digest);
        insert.Parameters.AddWithValue("$recordedAt", now.ToUniversalTime().ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AsteriaActivitySnapshot> RecordValidatedOutcomeAsync(
        string workId,
        string outcome,
        string evidenceDigest,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceDigest);
        if (!IsSha256Fingerprint(evidenceDigest))
        {
            throw new ArgumentException(
                "Validated evidence digest must use the canonical sha256:<lowercase-hex> format.",
                nameof(evidenceDigest));
        }
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        var supportedOutcomes = current.ConcernId switch
        {
            "FunctionalVerification" or "SecurityVerification" =>
                new[] { "Passed", "Failed", "Indeterminate" },
            "ReleaseReviewApproval" or "EmergencyApproval" =>
                new[] { "Approved", "Rejected", "Unresolved" },
            "Deployment" => new[] { "Succeeded", "Failed", "Indeterminate" },
            "HealthVerification" => new[] { "Passed", "Failed", "Indeterminate" },
            "Readiness" => new[] { "Ready" },
            _ => throw new InvalidDataException(
                $"Asteria activity concern '{current.ConcernId}' has no supported terminal outcomes.")
        };
        if (!supportedOutcomes.Contains(outcome, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Outcome '{outcome}' is not valid for Asteria concern '{current.ConcernId}'.");
        }
        if (current.ValidatedOutcome is not null)
        {
            if (current.ValidatedOutcome == outcome &&
                current.ValidatedEvidenceDigest == evidenceDigest)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            await RecordConflictWithinTransactionAsync(
                connection,
                transaction,
                workId,
                "validated-outcome-conflict",
                Fingerprint($"{outcome}\0{evidenceDigest}"),
                System.Text.Json.JsonSerializer.Serialize(new { outcome, evidenceDigest }),
                nowText,
                cancellationToken);
            if (CanMarkConflicting(current.Status))
            {
                await SetStatusAsync(connection, transaction, workId, "Conflicting", nowText, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Asteria activity has a conflicting validated outcome.");
        }
        if (current.Status != "ResponsePersisted" ||
            current.OriginalResponseJson is null ||
            current.ResponseDigest is null)
        {
            throw new InvalidOperationException(
                $"Activity status '{current.Status}' has no archived provider response to validate.");
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET ValidatedOutcome = $outcome, ValidatedEvidenceDigest = $evidenceDigest,
                UpdatedAt = $now
            WHERE WorkId = $workId AND ValidatedOutcome IS NULL;
            """;
        update.Parameters.AddWithValue("$outcome", outcome);
        update.Parameters.AddWithValue("$evidenceDigest", evidenceDigest);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after storing validated outcome.");
    }

    public async Task<AsteriaActivitySnapshot> MarkOutcomeUnknownAsync(
        string workId,
        string recoveryDetail,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryDetail);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == "OutcomeUnknown" && current.RecoveryDetail == recoveryDetail)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }
        var unresolvedDispatch =
            (current.Status is "DispatchPending" or "Dispatched") &&
            current.OriginalResponseJson is null;
        var unresolvedArchivedResponse =
            current.Status == "ResponsePersisted" &&
            current.OriginalResponseJson is not null &&
            current.ValidatedOutcome is null;
        if (!unresolvedDispatch && !unresolvedArchivedResponse)
        {
            throw new InvalidOperationException(
                $"Activity status '{current.Status}' cannot be changed to OutcomeUnknown.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET Status = 'OutcomeUnknown', RecoveryDetail = $detail, UpdatedAt = $now
            WHERE WorkId = $workId;
            """;
        update.Parameters.AddWithValue("$detail", recoveryDetail);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after recording unknown outcome.");
    }

    public async Task<AsteriaActivitySnapshot> ResolveOutcomeUnknownAsync(
        string workId,
        string reconciledResponseJson,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reconciledResponseJson);
        using var parsedResponse = System.Text.Json.JsonDocument.Parse(reconciledResponseJson);
        var responseDigest = Fingerprint(reconciledResponseJson);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == "ResponsePersisted" &&
            current.OriginalResponseJson is not null &&
            current.ValidatedOutcome is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }
        if (current.Status is not ("DispatchPending" or "Dispatched" or "OutcomeUnknown") ||
            current.ValidatedOutcome is not null)
        {
            throw new InvalidOperationException(
                $"Activity status '{current.Status}' cannot be reconciled from an external provider observation.");
        }
        await using var observation = connection.CreateCommand();
        observation.Transaction = transaction;
        observation.CommandText = """
            SELECT COUNT(*) FROM AsteriaActivityProviderObservations
            WHERE WorkId = $workId AND ResponseDigest = $digest;
            """;
        observation.Parameters.AddWithValue("$workId", workId);
        observation.Parameters.AddWithValue("$digest", responseDigest);
        if (Convert.ToInt64(await observation.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            throw new InvalidDataException("Asteria provider reconciliation response was not durably archived first.");
        }
        var storedResponse = current.OriginalResponseJson ?? reconciledResponseJson;
        var storedDigest = current.ResponseDigest ?? responseDigest;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET OriginalResponseJson = $response, ResponseDigest = $digest,
                Status = 'ResponsePersisted', UpdatedAt = $now
            WHERE WorkId = $workId;
            """;
        update.Parameters.AddWithValue("$response", storedResponse);
        update.Parameters.AddWithValue("$digest", storedDigest);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after provider reconciliation.");
    }

    public async Task<AsteriaActivitySnapshot> MarkRefusedAsync(
        string workId,
        string refusalDetail,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refusalDetail);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == "Refused" && current.RecoveryDetail == refusalDetail)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }
        if (current.Status != "ResponsePersisted" ||
            current.OriginalResponseJson is null ||
            current.ValidatedOutcome is not null)
        {
            throw new InvalidOperationException(
                $"Activity status '{current.Status}' cannot be marked Refused.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET Status = 'Refused', RecoveryDetail = $detail, UpdatedAt = $now
            WHERE WorkId = $workId AND Status = 'ResponsePersisted';
            """;
        update.Parameters.AddWithValue("$detail", refusalDetail);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Asteria activity refusal lost a concurrent status update.");
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after recording refusal.");
    }

    public async Task<AsteriaActivitySnapshot> MarkObservationSubmittedAsync(
        string workId,
        string receiptId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.ObservationReceiptId is not null)
        {
            if (current.ObservationReceiptId == receiptId)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            throw new InvalidOperationException("Asteria activity observation receipt identity cannot be replaced.");
        }
        if (current.Status != "ResponsePersisted" ||
            current.ValidatedOutcome is null ||
            current.ValidatedEvidenceDigest is null)
        {
            throw new InvalidOperationException(
                $"Activity status '{current.Status}' has no validated outcome for State Machine observation.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET Status = 'ObservationSubmitted', ObservationReceiptId = $receiptId, UpdatedAt = $now
            WHERE WorkId = $workId AND ObservationReceiptId IS NULL;
            """;
        update.Parameters.AddWithValue("$receiptId", receiptId);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after observation submission.");
    }

    public async Task<AsteriaActivitySnapshot> MarkAcceptedAsync(
        string workId,
        string receiptId,
        string attemptId,
        string historyId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyId);
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == "Accepted")
        {
            if (current.ObservationReceiptId == receiptId &&
                current.AcceptedAttemptId == attemptId &&
                current.AcceptedHistoryId == historyId)
            {
                await transaction.CommitAsync(cancellationToken);
                return current;
            }
            throw new InvalidOperationException("Accepted Asteria activity history references are immutable.");
        }
        if (current.Status != "ObservationSubmitted" || current.ObservationReceiptId != receiptId)
        {
            throw new InvalidOperationException(
                $"Activity status '{current.Status}' or receipt identity does not permit acceptance.");
        }
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE AsteriaActivities
            SET Status = 'Accepted', AcceptedAttemptId = $attemptId,
                AcceptedHistoryId = $historyId, UpdatedAt = $now
            WHERE WorkId = $workId AND Status = 'ObservationSubmitted';
            """;
        update.Parameters.AddWithValue("$attemptId", attemptId);
        update.Parameters.AddWithValue("$historyId", historyId);
        update.Parameters.AddWithValue("$now", nowText);
        update.Parameters.AddWithValue("$workId", workId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Asteria activity acceptance lost a concurrent status update.");
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Accepted Asteria activity could not be read back.");
    }

    public async Task<AsteriaActivitySnapshot?> GetAsync(
        string workId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM AsteriaActivities WHERE WorkId = $workId;";
        command.Parameters.AddWithValue("$workId", workId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    public async Task<IReadOnlyList<AsteriaActivitySnapshot>> ListGenerationAsync(
        string releaseId,
        int generation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM AsteriaActivities
            WHERE ReleaseId = $releaseId AND Generation = $generation
            ORDER BY CreatedAt, WorkId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        command.Parameters.AddWithValue("$generation", generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var activities = new List<AsteriaActivitySnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            activities.Add(ReadSnapshot(reader));
        }
        return activities;
    }

    public async Task<IReadOnlyList<AsteriaActivityProviderObservation>> ListProviderObservationsAsync(
        string workId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ObservationId, ObservationKind, ResponseJson, ResponseDigest, RecordedAt
            FROM AsteriaActivityProviderObservations
            WHERE WorkId = $workId
            ORDER BY RecordedAt, ObservationId;
            """;
        command.Parameters.AddWithValue("$workId", workId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var observations = new List<AsteriaActivityProviderObservation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            observations.Add(new AsteriaActivityProviderObservation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return observations;
    }

    public async Task<IReadOnlyList<AsteriaActivityConflict>> ListConflictsAsync(
        string workId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ConflictId, WorkId, ConflictKind, ConflictingFingerprint,
                   ConflictingPayloadJson, RecordedAt
            FROM AsteriaActivityConflicts
            WHERE WorkId = $workId
            ORDER BY RecordedAt, ConflictId;
            """;
        command.Parameters.AddWithValue("$workId", workId);
        var results = new List<AsteriaActivityConflict>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AsteriaActivityConflict(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }
        return results;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static string Fingerprint(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsSha256Fingerprint(string value) =>
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    private async Task<AsteriaActivitySnapshot> AdvanceStatusAsync(
        string workId,
        string expectedStatus,
        string nextStatus,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nowText = now.ToUniversalTime().ToString("O");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var current = await ReadByWorkIdAsync(connection, transaction, workId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        if (current.Status == nextStatus || current.Status is "Dispatched" or "ResponsePersisted" or "ObservationSubmitted" or "Accepted")
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }
        if (current.Status != expectedStatus)
        {
            throw new InvalidOperationException(
                $"Asteria activity '{workId}' cannot advance from '{current.Status}' to '{nextStatus}'.");
        }
        await SetStatusAsync(connection, transaction, workId, nextStatus, nowText, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(workId, cancellationToken)
            ?? throw new InvalidDataException("Asteria activity disappeared after status transition.");
    }

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

    private static void ValidateReservation(AsteriaActivityReservation value)
    {
        foreach (var (name, field) in new (string, string?)[]
        {
            (nameof(value.WorkId), value.WorkId),
            (nameof(value.ActivityId), value.ActivityId),
            (nameof(value.ReleaseId), value.ReleaseId),
            (nameof(value.MaterialDigest), value.MaterialDigest),
            (nameof(value.WorkflowInstanceId), value.WorkflowInstanceId),
            (nameof(value.DefinitionId), value.DefinitionId),
            (nameof(value.DefinitionVersion), value.DefinitionVersion),
            (nameof(value.DefinitionDigest), value.DefinitionDigest),
            (nameof(value.ConcernId), value.ConcernId),
            (nameof(value.ActivityType), value.ActivityType),
            (nameof(value.AttemptId), value.AttemptId),
            (nameof(value.ExpectedOperationId), value.ExpectedOperationId),
            (nameof(value.SubmissionId), value.SubmissionId),
            (nameof(value.IdempotencyKey), value.IdempotencyKey),
            (nameof(value.OriginalRequestJson), value.OriginalRequestJson)
        })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field, name);
        }
        if (value.Generation < 1 || value.ParentRevision < 0 || value.ConcernRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Generation and concern/parent revisions must be non-negative.");
        }
        if (value.ProviderIdentity is not null &&
            (string.IsNullOrWhiteSpace(value.ApiDescriptionVersionId) || string.IsNullOrWhiteSpace(value.OperationKey)))
        {
            throw new ArgumentException("External activities require a complete provider, description, and operation binding.", nameof(value));
        }
        if (value.AuthorityRole is not null &&
            (string.IsNullOrWhiteSpace(value.AuthorityPrincipalId) ||
             string.IsNullOrWhiteSpace(value.AuthorityRegistryVersion)))
        {
            throw new ArgumentException("Approval activities require a principal and authority-registry version.", nameof(value));
        }
        using var parsedRequest = System.Text.Json.JsonDocument.Parse(value.OriginalRequestJson);
    }

    private static bool ReservationMatches(
        AsteriaActivitySnapshot existing,
        AsteriaActivityReservation requested,
        string fingerprint) =>
        existing.WorkId == requested.WorkId &&
        existing.ActivityId == requested.ActivityId &&
        existing.MaterialDigest == requested.MaterialDigest &&
        existing.WorkflowInstanceId == requested.WorkflowInstanceId &&
        existing.DefinitionId == requested.DefinitionId &&
        existing.DefinitionVersion == requested.DefinitionVersion &&
        existing.DefinitionDigest == requested.DefinitionDigest &&
        existing.ConcernId == requested.ConcernId &&
        existing.ExpectedOperationId == requested.ExpectedOperationId &&
        existing.ParentRevision == requested.ParentRevision &&
        existing.ConcernRevision == requested.ConcernRevision &&
        existing.SubmissionId == requested.SubmissionId &&
        existing.IdempotencyKey == requested.IdempotencyKey &&
        existing.RequestFingerprint == fingerprint &&
        existing.ProviderIdentity == requested.ProviderIdentity &&
        existing.ApiDescriptionVersionId == requested.ApiDescriptionVersionId &&
        existing.OperationKey == requested.OperationKey &&
        existing.AuthorityPrincipalId == requested.AuthorityPrincipalId &&
        existing.AuthorityRole == requested.AuthorityRole &&
        existing.AuthorityRegistryVersion == requested.AuthorityRegistryVersion;

    private static void AddReservationParameters(
        SqliteCommand command,
        AsteriaActivityReservation value,
        string fingerprint,
        string now)
    {
        command.Parameters.AddWithValue("$workId", value.WorkId);
        command.Parameters.AddWithValue("$activityId", value.ActivityId);
        command.Parameters.AddWithValue("$releaseId", value.ReleaseId);
        command.Parameters.AddWithValue("$generation", value.Generation);
        command.Parameters.AddWithValue("$materialDigest", value.MaterialDigest);
        command.Parameters.AddWithValue("$workflowInstanceId", value.WorkflowInstanceId);
        command.Parameters.AddWithValue("$definitionId", value.DefinitionId);
        command.Parameters.AddWithValue("$definitionVersion", value.DefinitionVersion);
        command.Parameters.AddWithValue("$definitionDigest", value.DefinitionDigest);
        command.Parameters.AddWithValue("$concernId", value.ConcernId);
        command.Parameters.AddWithValue("$activityType", value.ActivityType);
        command.Parameters.AddWithValue("$attemptId", value.AttemptId);
        command.Parameters.AddWithValue("$expectedOperationId", value.ExpectedOperationId);
        command.Parameters.AddWithValue("$parentRevision", value.ParentRevision);
        command.Parameters.AddWithValue("$concernRevision", value.ConcernRevision);
        command.Parameters.AddWithValue("$submissionId", value.SubmissionId);
        command.Parameters.AddWithValue("$idempotencyKey", value.IdempotencyKey);
        command.Parameters.AddWithValue("$requestFingerprint", fingerprint);
        command.Parameters.AddWithValue("$requestJson", value.OriginalRequestJson);
        AddNullable(command, "$providerIdentity", value.ProviderIdentity);
        AddNullable(command, "$apiDescriptionVersionId", value.ApiDescriptionVersionId);
        AddNullable(command, "$operationKey", value.OperationKey);
        AddNullable(command, "$authorityPrincipalId", value.AuthorityPrincipalId);
        AddNullable(command, "$authorityRole", value.AuthorityRole);
        AddNullable(command, "$authorityRegistryVersion", value.AuthorityRegistryVersion);
        command.Parameters.AddWithValue("$now", now);
    }

    private static async Task<string?> FindIdentityCollisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AsteriaActivityReservation value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT WorkId FROM AsteriaActivities
            WHERE WorkId = $workId OR ActivityId = $activityId
               OR ExpectedOperationId = $operationId OR SubmissionId = $submissionId
               OR IdempotencyKey = $idempotencyKey
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$workId", value.WorkId);
        command.Parameters.AddWithValue("$activityId", value.ActivityId);
        command.Parameters.AddWithValue("$operationId", value.ExpectedOperationId);
        command.Parameters.AddWithValue("$submissionId", value.SubmissionId);
        command.Parameters.AddWithValue("$idempotencyKey", value.IdempotencyKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<AsteriaActivitySnapshot?> FindByLogicalAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AsteriaActivityReservation value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM AsteriaActivities
            WHERE ReleaseId = $releaseId AND Generation = $generation
              AND ActivityType = $activityType AND AttemptId = $attemptId;
            """;
        command.Parameters.AddWithValue("$releaseId", value.ReleaseId);
        command.Parameters.AddWithValue("$generation", value.Generation);
        command.Parameters.AddWithValue("$activityType", value.ActivityType);
        command.Parameters.AddWithValue("$attemptId", value.AttemptId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    private static async Task<AsteriaActivitySnapshot?> ReadByWorkIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM AsteriaActivities WHERE WorkId = $workId;";
        command.Parameters.AddWithValue("$workId", workId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSnapshot(reader) : null;
    }

    private static async Task RecordConflictWithinTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workId,
        string kind,
        string fingerprint,
        string payloadJson,
        string recordedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO AsteriaActivityConflicts
                (ConflictId, WorkId, ConflictKind, ConflictingFingerprint, ConflictingPayloadJson, RecordedAt)
            VALUES ($id, $workId, $kind, $fingerprint, $payload, $recordedAt);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$workId", workId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$recordedAt", recordedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string workId,
        string status,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE AsteriaActivities SET Status = $status, UpdatedAt = $now
            WHERE WorkId = $workId;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$workId", workId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new KeyNotFoundException($"Asteria activity '{workId}' does not exist.");
        }
    }

    private static bool CanMarkConflicting(string status) =>
        status is not ("Accepted" or "OutcomeUnknown");

    private static AsteriaActivitySnapshot ReadSnapshot(SqliteDataReader reader) =>
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
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18),
            NullableString(reader, 19),
            NullableString(reader, 20),
            NullableString(reader, 21),
            NullableString(reader, 22),
            NullableString(reader, 23),
            NullableString(reader, 24),
            reader.GetString(25),
            NullableString(reader, 26),
            NullableString(reader, 27),
            NullableString(reader, 28),
            NullableString(reader, 29),
            NullableString(reader, 30),
            NullableString(reader, 31),
            NullableString(reader, 32),
            NullableString(reader, 33),
            NullableString(reader, 34),
            DateTimeOffset.Parse(reader.GetString(35), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(36), System.Globalization.CultureInfo.InvariantCulture));

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
}
