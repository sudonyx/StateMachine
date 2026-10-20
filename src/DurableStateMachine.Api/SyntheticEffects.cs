using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Api;

public sealed record StartEffectRequest(
    string OperationId,
    string Capability,
    string? CustomerReference = null,
    string? AssetId = null,
    string? OfferReference = null,
    string? MessageTemplate = null);

public sealed record SyntheticEffectRequest(
    string OperationId,
    string Capability,
    string? CustomerReference,
    string? AssetId,
    string? OfferReference,
    string? MessageTemplate,
    string? ProviderOperationId = null);

public sealed record ProviderEffectResult(
    string ProviderIdentity,
    string Outcome,
    string? EffectId,
    JsonElement Evidence,
    DateTimeOffset OccurredAt);

public sealed record EffectAttemptResponse(
    string AttemptId,
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string TransportOutcome,
    string ProviderOutcome,
    string? ProviderEffectId,
    DateTimeOffset? ProviderOccurredAt,
    JsonElement? ProviderEvidence,
    string LocalPersistenceOutcome,
    string ObservationStatus,
    string? ObservationReceiptId,
    string? Error,
    string? HarnessExecutionId = null,
    int? HarnessExecutionState = null,
    int? HarnessResponseStatusCode = null,
    bool? HarnessResponseTruncated = null,
    int? HarnessRejectionReason = null,
    string? HarnessFailureCode = null,
    string? HarnessFailureMessage = null,
    IReadOnlyList<string>? HarnessAttemptIds = null,
    IReadOnlyList<string>? HarnessEvidenceIds = null,
    int? IdempotencyDecision = null,
    int? ReplayDecision = null,
    string? OutcomeConfidence = null,
    string? WorkflowTransitionId = null,
    string? WorkflowStateBefore = null,
    string? WorkflowStateAfter = null,
    long? WorkflowRevisionAfter = null);

public sealed record EffectOperationResponse(
    string InstanceId,
    string OperationId,
    string DefinitionId,
    string DefinitionVersion,
    long ExecutionRevision,
    string Capability,
    string ProviderIdentity,
    string ObservationType,
    string RequestDigest,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<EffectAttemptResponse> Attempts,
    string? HarnessProviderId = null,
    string? ApiDescriptionVersionId = null,
    string? HarnessOperationKey = null,
    string? HarnessExecutionId = null,
    string? HarnessIdempotencyKey = null,
    int? HarnessExecutionState = null,
    int? OperationReplaySafety = null,
    int? IdempotencyDecision = null,
    int? ReplayDecision = null,
    string? OutcomeConfidence = null);

public sealed record ProviderRequestResponse(
    string Id,
    string OperationId,
    string Capability,
    string Outcome,
    string? EffectId,
    JsonElement? Evidence,
    DateTimeOffset RequestedAt,
    DateTimeOffset? EffectOccurredAt);

public sealed class EffectFaultPlan
{
    private readonly HashSet<(string OperationId, string Point)> _faults = [];
    private readonly Dictionary<(string OperationId, string Point), EffectPauseBoundary> _pauses = [];
    private readonly object _sync = new();

    public static readonly string[] SupportedPoints =
    [
        "BeforeLocalIntentCommit",
        "AfterLocalCommitBeforeProvider",
        "AfterRequestTransmissionBeforeProviderExecution",
        "AfterProviderCommitBeforeResponse",
        "AfterProviderResponseBeforeLocalPersistence",
        "BeforeObservationAcceptance",
        "AfterObservationCommitBeforeLocalPersistence",
        "ProviderConfirmedFailure",
        "AfterLocalIntentCommitBeforeHarness",
        "AfterHarnessResponseBeforeLocalPersistence"
    ];

    public void Arm(string operationId, string point)
    {
        if (!SupportedPoints.Contains(point, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported Gate 6 fault point '{point}'.");
        }
        lock (_sync)
        {
            _faults.Add((operationId, point));
        }
    }

    public bool Consume(string operationId, string point)
    {
        lock (_sync)
        {
            return _faults.Remove((operationId, point));
        }
    }

    public void ArmPause(string operationId, string point)
    {
        if (!SupportedPoints.Contains(point, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported Gate 6 fault point '{point}'.", nameof(point));
        }
        lock (_sync)
        {
            _pauses[(operationId, point)] = new EffectPauseBoundary();
        }
    }

    public async Task WaitIfPausedAsync(string operationId, string point, CancellationToken cancellationToken)
    {
        EffectPauseBoundary? boundary;
        lock (_sync)
        {
            boundary = _pauses.TryGetValue((operationId, point), out var pause) ? pause : null;
        }
        if (boundary is not null)
        {
            boundary.Reached.TrySetResult();
            await boundary.Release.Task.WaitAsync(cancellationToken);
        }
    }

    public bool IsPauseReached(string operationId, string point)
    {
        lock (_sync)
        {
            return _pauses.TryGetValue((operationId, point), out var boundary) &&
                boundary.Reached.Task.IsCompleted;
        }
    }

    private sealed class EffectPauseBoundary
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class EffectInterruptionException(string message) : Exception(message);

public sealed class SyntheticProviderStore(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory = Path.GetFullPath(
        Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR")
        ?? configuration["DURABLE_STATE_MACHINE_PROVIDER_DB_DIR"]
        ?? "synthetic-provider-data");

    public static string GetProviderIdentity(string capability) => capability switch
    {
        "offer-document" => "synthetic-offer-provider",
        "equipment-reservation" => "synthetic-reservation-provider",
        "customer-notification" => "synthetic-notification-provider",
        _ => throw new ArgumentException($"Unsupported external effect capability '{capability}'.")
    };

    public async Task<ProviderEffectResult> ExecuteAsync(
        SyntheticEffectRequest request,
        bool forceFailure,
        CancellationToken cancellationToken,
        Action? afterRequestReceived = null,
        Action? afterProviderCommit = null)
    {
        var provider = GetProviderIdentity(request.Capability);
        var providerOperationId = request.ProviderOperationId ?? request.OperationId;
        await using var connection = await OpenAsync(request.Capability, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        afterRequestReceived?.Invoke();
        var now = DateTimeOffset.UtcNow;

        if (request.Capability == "offer-document")
        {
            var previous = await ReadIdempotentOfferAsync(connection, transaction, providerOperationId, cancellationToken);
            if (previous is not null)
            {
                await InsertRequestAsync(connection, transaction, request, "Succeeded", previous.Value.EffectId,
                    previous.Value.Evidence, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                afterProviderCommit?.Invoke();
                return new ProviderEffectResult(provider, "Succeeded", previous.Value.EffectId,
                    previous.Value.Evidence, previous.Value.OccurredAt);
            }
        }

        if (forceFailure)
        {
            var failedEvidence = JsonSerializer.SerializeToElement(new
            {
                operationId = request.OperationId,
                reason = "controlled-synthetic-provider-failure"
            }, JsonOptions);
            await InsertRequestAsync(connection, transaction, request, "Failed", null, failedEvidence,
                now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            afterProviderCommit?.Invoke();
            return new ProviderEffectResult(provider, "Failed", null, failedEvidence, now);
        }

        if (request.Capability == "equipment-reservation")
        {
            await using var existingReservation = connection.CreateCommand();
            existingReservation.Transaction = transaction;
            existingReservation.CommandText = """
                SELECT Id, OperationId, EvidenceJson, CreatedAt
                FROM ProviderEffects
                WHERE Capability = 'equipment-reservation' AND AssetId = $assetId
                LIMIT 1
                """;
            existingReservation.Parameters.AddWithValue("$assetId", request.AssetId!);
            await using var reader = await existingReservation.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var existingEffectId = reader.GetString(0);
                var existingOperationId = reader.GetString(1);
                var existingEvidence = ParseElement(reader.GetString(2));
                var existingAt = new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero);
                await reader.DisposeAsync();
                var sameOperation = string.Equals(existingOperationId, providerOperationId, StringComparison.Ordinal);
                var resultEvidence = sameOperation
                    ? existingEvidence
                    : JsonSerializer.SerializeToElement(new
                    {
                        assetId = request.AssetId,
                        existingReservationId = existingEffectId,
                        existingOperationId
                    }, JsonOptions);
                var resultOutcome = sameOperation ? "Succeeded" : "Refused";
                await InsertRequestAsync(connection, transaction, request, resultOutcome, existingEffectId,
                    resultEvidence, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                afterProviderCommit?.Invoke();
                return new ProviderEffectResult(provider, resultOutcome, existingEffectId, resultEvidence,
                    sameOperation ? existingAt : now);
            }
        }

        var effectId = $"{request.Capability}-{Guid.NewGuid():N}";
        var evidence = CreateEvidence(request);
        var evidenceJson = evidence.GetRawText();
        var idempotencyKey = request.Capability == "offer-document" ? providerOperationId : null;
        await using (var addEffect = connection.CreateCommand())
        {
            addEffect.Transaction = transaction;
            addEffect.CommandText = """
                INSERT INTO ProviderEffects (Id, OperationId, Capability, AssetId, IdempotencyKey, EvidenceJson, CreatedAt)
                VALUES ($id, $operationId, $capability, $assetId, $idempotencyKey, $evidence, $createdAt)
                """;
            addEffect.Parameters.AddWithValue("$id", effectId);
            addEffect.Parameters.AddWithValue("$operationId", providerOperationId);
            addEffect.Parameters.AddWithValue("$capability", request.Capability);
            addEffect.Parameters.AddWithValue("$assetId", (object?)request.AssetId ?? DBNull.Value);
            addEffect.Parameters.AddWithValue("$idempotencyKey", (object?)idempotencyKey ?? DBNull.Value);
            addEffect.Parameters.AddWithValue("$evidence", evidenceJson);
            addEffect.Parameters.AddWithValue("$createdAt", now.UtcTicks);
            await addEffect.ExecuteNonQueryAsync(cancellationToken);
        }
        await InsertRequestAsync(connection, transaction, request, "Succeeded", effectId, evidence, now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        afterProviderCommit?.Invoke();
        return new ProviderEffectResult(provider, "Succeeded", effectId, evidence, now);
    }

    public async Task<IReadOnlyList<ProviderRequestResponse>> ListRequestsAsync(
        string providerIdentity,
        string? operationId,
        CancellationToken cancellationToken)
    {
        var capability = GetCapability(providerIdentity);
        await using var connection = await OpenAsync(capability, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request.Id, request.OperationId, request.Capability, request.Outcome,
                   request.EffectId, request.EvidenceJson, request.RequestedAt, effect.CreatedAt
            FROM ProviderRequests AS request
            LEFT JOIN ProviderEffects AS effect ON effect.Id = request.EffectId
            WHERE ($operationId IS NULL OR request.OperationId = $operationId)
            ORDER BY request.RequestedAt, request.Id
            """;
        command.Parameters.AddWithValue("$operationId", (object?)operationId ?? DBNull.Value);
        var results = new List<ProviderRequestResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ProviderRequestResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : ParseElement(reader.GetString(5)),
                new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
                reader.IsDBNull(7) ? null : new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero)));
        }
        return results;
    }

    private async Task<SqliteConnection> OpenAsync(string capability, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{capability}.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS ProviderRequests (
                Id TEXT NOT NULL PRIMARY KEY,
                OperationId TEXT NOT NULL,
                Capability TEXT NOT NULL,
                RequestJson TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                EffectId TEXT NULL,
                EvidenceJson TEXT NULL,
                RequestedAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ProviderRequests_OperationId
                ON ProviderRequests (OperationId, RequestedAt);
            CREATE TABLE IF NOT EXISTS ProviderEffects (
                Id TEXT NOT NULL PRIMARY KEY,
                OperationId TEXT NOT NULL,
                Capability TEXT NOT NULL,
                AssetId TEXT NULL,
                IdempotencyKey TEXT NULL,
                EvidenceJson TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ProviderEffects_IdempotencyKey
                ON ProviderEffects (IdempotencyKey);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ProviderEffects_AssetId
                ON ProviderEffects (AssetId);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task<(string EffectId, JsonElement Evidence, DateTimeOffset OccurredAt)?>
        ReadIdempotentOfferAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string operationId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, EvidenceJson, CreatedAt
            FROM ProviderEffects
            WHERE IdempotencyKey = $operationId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return (reader.GetString(0), ParseElement(reader.GetString(1)),
            new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero));
    }

    private static async Task InsertRequestAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyntheticEffectRequest request,
        string outcome,
        string? effectId,
        JsonElement evidence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ProviderRequests (Id, OperationId, Capability, RequestJson, Outcome, EffectId, EvidenceJson, RequestedAt)
            VALUES ($id, $operationId, $capability, $request, $outcome, $effectId, $evidence, $requestedAt)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$operationId", request.OperationId);
        command.Parameters.AddWithValue("$capability", request.Capability);
        command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, JsonOptions));
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$effectId", (object?)effectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", evidence.GetRawText());
        command.Parameters.AddWithValue("$requestedAt", now.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static JsonElement CreateEvidence(SyntheticEffectRequest request) => request.Capability switch
    {
        "offer-document" => JsonSerializer.SerializeToElement(new
        {
            documentId = $"offer-document-{Guid.NewGuid():N}",
            offerDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                request.OfferReference ?? request.OperationId))).ToLowerInvariant()}"
        }, JsonOptions),
        "equipment-reservation" => JsonSerializer.SerializeToElement(new
        {
            reservationId = $"reservation-{Guid.NewGuid():N}",
            assetId = request.AssetId
        }, JsonOptions),
        "customer-notification" => JsonSerializer.SerializeToElement(new
        {
            deliveryId = $"delivery-{Guid.NewGuid():N}",
            messageDigest = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                request.MessageTemplate ?? request.OperationId))).ToLowerInvariant()}"
        }, JsonOptions),
        _ => throw new ArgumentException($"Unsupported capability '{request.Capability}'.")
    };

    private static string GetCapability(string providerIdentity) => providerIdentity switch
    {
        "synthetic-offer-provider" => "offer-document",
        "synthetic-reservation-provider" => "equipment-reservation",
        "synthetic-notification-provider" => "customer-notification",
        _ => throw new ArgumentException($"Unknown synthetic provider '{providerIdentity}'.")
    };

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

public sealed class EffectCoordinator(
    MachineDbContext db,
    MachineStore machineStore,
    SyntheticProviderStore providers,
    EffectFaultPlan faults)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EffectOperationResponse> RequestAsync(
        string instanceId,
        StartEffectRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequest(request);
        var provider = SyntheticProviderStore.GetProviderIdentity(normalized.Capability);
        var observation = normalized.Capability switch
        {
            "offer-document" => "OfferDocumentGenerated",
            "equipment-reservation" => "EquipmentReserved",
            "customer-notification" => "CustomerNotified",
            _ => throw new ArgumentException($"Unsupported capability '{normalized.Capability}'.")
        };
        var requestJson = JsonSerializer.Serialize(normalized, JsonOptions);
        var requestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));

        ThrowIfArmed(normalized.OperationId, "BeforeLocalIntentCommit");
        var intent = await machineStore.CreateEffectIntentAsync(
            instanceId,
            normalized.OperationId,
            normalized.Capability,
            provider,
            observation,
            requestJson,
            requestDigest,
            now,
            cancellationToken);
        if (intent.AlreadyRequested)
        {
            return await GetResponseAsync(instanceId, normalized.OperationId, cancellationToken);
        }

        ThrowIfArmed(normalized.OperationId, "AfterLocalCommitBeforeProvider");
        await faults.WaitIfPausedAsync(
            normalized.OperationId, "AfterLocalCommitBeforeProvider", cancellationToken);
        await ExecuteAttemptAsync(intent.Operation, normalized, cancellationToken);
        return await GetResponseAsync(instanceId, normalized.OperationId, cancellationToken);
    }

    public async Task<EffectOperationResponse> ResumeAsync(
        string instanceId,
        string operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var operation = await machineStore.GetEffectOperationAsync(instanceId, operationId, cancellationToken);
        var latest = await db.EffectAttempts.AsNoTracking()
            .Where(attempt => attempt.EffectOperationId == operation.Id)
            .OrderByDescending(attempt => attempt.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest?.ProviderOutcome == "Succeeded" &&
            latest.LocalPersistenceOutcome == "Persisted" &&
            latest.ObservationStatus == "NotSubmitted")
        {
            var evidence = ParseElement(latest.ProviderEvidenceJson!);
            await AcceptObservationAsync(operation, latest, evidence,
                latest.ProviderOccurredAt ?? latest.CompletedAt ?? now, now, cancellationToken);
        }
        else if (latest?.ObservationStatus == "Accepted" || latest?.ObservationStatus is "Stale" or "Untrusted" or "Unexpected")
        {
            return await GetResponseAsync(instanceId, operationId, cancellationToken);
        }
        else if (latest is null || latest.ProviderOutcome == "Failed")
        {
            var request = JsonSerializer.Deserialize<SyntheticEffectRequest>(operation.RequestJson, JsonOptions)
                ?? throw new InvalidOperationException($"Stored request for operation '{operationId}' is invalid.");
            await ExecuteAttemptAsync(operation, request, cancellationToken);
        }
        else if (latest.ProviderOutcome == "Unknown")
        {
            var request = JsonSerializer.Deserialize<SyntheticEffectRequest>(operation.RequestJson, JsonOptions)
                ?? throw new InvalidOperationException($"Stored request for operation '{operationId}' is invalid.");
            if (request.Capability == "customer-notification")
            {
                throw new DefinitionConflictException(
                    "The notification provider does not guarantee idempotency; replaying an uncertain request may deliver twice.");
            }
            await ExecuteAttemptAsync(operation, request, cancellationToken);
        }

        return await GetResponseAsync(instanceId, operationId, cancellationToken);
    }

    public async Task<EffectOperationResponse> GetResponseAsync(
        string instanceId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var operation = await machineStore.GetEffectOperationAsync(instanceId, operationId, cancellationToken);
        var attempts = await machineStore.ListEffectAttemptsAsync(operation.Id, cancellationToken);
        var responses = attempts.Select(ToResponse).ToArray();
        var status = operation.Status;
        if (attempts.LastOrDefault() is { TransportOutcome: "AwaitingResponse", ProviderOutcome: "Unknown" })
        {
            status = "OutcomeUnknown";
        }
        return new EffectOperationResponse(
            operation.InstanceId,
            operation.OperationId,
            operation.DefinitionId,
            operation.DefinitionVersion,
            operation.ExecutionRevision,
            operation.Capability,
            operation.ProviderIdentity,
            operation.ObservationType,
            operation.RequestDigest,
            status,
            operation.RequestedAt,
            operation.UpdatedAt,
            responses,
            operation.HarnessProviderId,
            operation.ApiDescriptionVersionId,
            operation.HarnessOperationKey,
            operation.HarnessExecutionId,
            operation.HarnessIdempotencyKey,
            operation.HarnessExecutionState,
            operation.OperationReplaySafety,
            operation.IdempotencyDecision,
            operation.ReplayDecision,
            operation.OutcomeConfidence);
    }

    public Task<IReadOnlyList<EffectOperationRecord>> ListAsync(
        string instanceId,
        CancellationToken cancellationToken) =>
        machineStore.ListEffectOperationsAsync(instanceId, cancellationToken);

    public Task<IReadOnlyList<ProviderRequestResponse>> ListProviderRequestsAsync(
        string providerIdentity,
        string? operationId,
        CancellationToken cancellationToken) =>
        providers.ListRequestsAsync(providerIdentity, operationId, cancellationToken);

    private async Task ExecuteAttemptAsync(
        EffectOperationRecord operation,
        SyntheticEffectRequest request,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        if (db.Entry(operation).State == EntityState.Detached)
        {
            db.Attach(operation);
        }
        var previousAttempts = await db.EffectAttempts.AsNoTracking()
            .CountAsync(attempt => attempt.EffectOperationId == operation.Id, cancellationToken);
        var attempt = new EffectAttemptRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            EffectOperationId = operation.Id,
            AttemptNumber = previousAttempts + 1,
            StartedAt = startedAt,
            TransportOutcome = "AwaitingResponse",
            ProviderOutcome = "Unknown",
            LocalPersistenceOutcome = "Pending",
            ObservationStatus = "NotSubmitted"
        };
        operation.Status = "AttemptInitiated";
        operation.UpdatedAt = startedAt;
        db.EffectAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        var result = await providers.ExecuteAsync(
            request with { ProviderOperationId = $"{operation.InstanceId}:{operation.OperationId}" },
            faults.Consume(request.OperationId, "ProviderConfirmedFailure"),
            cancellationToken,
            afterRequestReceived: () => ThrowIfArmed(
                request.OperationId, "AfterRequestTransmissionBeforeProviderExecution"),
            afterProviderCommit: () => ThrowIfArmed(
                request.OperationId, "AfterProviderCommitBeforeResponse"));
        ThrowIfArmed(request.OperationId, "AfterProviderResponseBeforeLocalPersistence");

        attempt.CompletedAt = DateTimeOffset.UtcNow;
        attempt.TransportOutcome = "ResponseReceived";
        attempt.ProviderOutcome = result.Outcome;
        attempt.ProviderEffectId = result.EffectId;
        attempt.ProviderOccurredAt = result.OccurredAt;
        attempt.ProviderEvidenceJson = result.Evidence.GetRawText();
        attempt.LocalPersistenceOutcome = "Persisted";
        operation.Status = result.Outcome switch
        {
            "Succeeded" => "ProviderSucceeded",
            "Failed" => "ProviderFailed",
            "Refused" => "ProviderRefused",
            _ => throw new InvalidOperationException($"Unsupported provider result '{result.Outcome}'.")
        };
        operation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        if (result.Outcome == "Succeeded")
        {
            ThrowIfArmed(request.OperationId, "BeforeObservationAcceptance");
            await AcceptObservationAsync(operation, attempt, result.Evidence, result.OccurredAt,
                DateTimeOffset.UtcNow, cancellationToken);
        }
    }

    private async Task AcceptObservationAsync(
        EffectOperationRecord operation,
        EffectAttemptRecord attempt,
        JsonElement evidence,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        var evidenceValues = evidence.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        var result = await machineStore.SubmitInputAsync(
            operation.InstanceId,
            "Observation",
            $"effect-result-{operation.OperationId}",
            null,
            null,
            $"observation-effect-{operation.OperationId}",
            operation.ObservationType,
            operation.ExecutionRevision,
            operation.OperationId,
            operation.ProviderIdentity,
            occurredAt,
            payload: new Dictionary<string, JsonElement>
            {
                ["effectId"] = JsonSerializer.SerializeToElement(attempt.ProviderEffectId, JsonOptions)
            },
            facts: null,
            evidence: evidenceValues,
            decisionEvidence: null,
            factsProvenance: null,
            trustedSource: true,
            now: receivedAt,
            cancellationToken: cancellationToken);

        ThrowIfArmed(operation.OperationId, "AfterObservationCommitBeforeLocalPersistence");
        var accepted = result.Message.Status == "Accepted";
        InputAttemptRecord? acceptedAttempt = result.Submission?.Attempt;
        if (result.Message.Status == "Duplicate" && result.Message.AttemptId is not null)
        {
            acceptedAttempt = await machineStore.GetAttemptAsync(
                operation.InstanceId, result.Message.AttemptId, cancellationToken);
            accepted = acceptedAttempt.Status == "Accepted";
        }
        var persistedAttempt = await db.EffectAttempts.SingleAsync(
            record => record.Id == attempt.Id, cancellationToken);
        var persistedOperation = await db.EffectOperations.SingleAsync(
            record => record.Id == operation.Id, cancellationToken);
        persistedAttempt.ObservationStatus = result.Message.Status;
        persistedAttempt.ObservationReceiptId = result.Message.Id;
        persistedAttempt.WorkflowTransitionId = acceptedAttempt?.TransitionId;
        persistedAttempt.WorkflowStateBefore = acceptedAttempt?.PreviousState;
        persistedAttempt.WorkflowStateAfter = acceptedAttempt?.TargetState;
        persistedAttempt.WorkflowRevisionAfter = acceptedAttempt?.RevisionAfter;
        persistedOperation.Status = accepted ? "ObservationAccepted" : "ObservationRefused";
        persistedOperation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static SyntheticEffectRequest NormalizeRequest(StartEffectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 96)
        {
            throw new ArgumentException("Operation ID must contain between 1 and 96 characters.");
        }
        if (request.Capability is not ("offer-document" or "equipment-reservation" or "customer-notification"))
        {
            throw new ArgumentException("Capability must be offer-document, equipment-reservation, or customer-notification.");
        }
        if (request.Capability == "equipment-reservation" && string.IsNullOrWhiteSpace(request.AssetId))
        {
            throw new ArgumentException("Equipment reservation requires a fictional assetId.");
        }
        if (request.Capability is "offer-document" or "customer-notification" &&
            string.IsNullOrWhiteSpace(request.CustomerReference))
        {
            throw new ArgumentException($"{request.Capability} requires a fictional customerReference.");
        }
        if (request.Capability == "offer-document" && string.IsNullOrWhiteSpace(request.OfferReference))
        {
            throw new ArgumentException("Offer-document generation requires a fictional offerReference.");
        }
        if (request.Capability == "customer-notification" && string.IsNullOrWhiteSpace(request.MessageTemplate))
        {
            throw new ArgumentException("Customer notification requires a synthetic messageTemplate.");
        }
        return new SyntheticEffectRequest(
            request.OperationId,
            request.Capability,
            request.CustomerReference,
            request.AssetId,
            request.OfferReference,
            request.MessageTemplate);
    }

    private static EffectAttemptResponse ToResponse(EffectAttemptRecord attempt)
    {
        JsonElement? evidence = attempt.ProviderEvidenceJson is null
            ? null
            : ParseElement(attempt.ProviderEvidenceJson);
        return new EffectAttemptResponse(
            attempt.Id,
            attempt.AttemptNumber,
            attempt.StartedAt,
            attempt.CompletedAt,
            attempt.TransportOutcome,
            attempt.ProviderOutcome,
            attempt.ProviderEffectId,
            attempt.ProviderOccurredAt,
            evidence,
            attempt.LocalPersistenceOutcome,
            attempt.ObservationStatus,
            attempt.ObservationReceiptId,
            attempt.Error,
            attempt.HarnessExecutionId,
            attempt.HarnessExecutionState,
            attempt.HarnessResponseStatusCode,
            attempt.HarnessResponseTruncated,
            attempt.HarnessRejectionReason,
            attempt.HarnessFailureCode,
            attempt.HarnessFailureMessage,
            DeserializeStringList(attempt.HarnessAttemptIdsJson),
            DeserializeStringList(attempt.HarnessEvidenceIdsJson),
            attempt.IdempotencyDecision,
            attempt.ReplayDecision,
            attempt.OutcomeConfidence,
            attempt.WorkflowTransitionId,
            attempt.WorkflowStateBefore,
            attempt.WorkflowStateAfter,
            attempt.WorkflowRevisionAfter);
    }

    private static IReadOnlyList<string>? DeserializeStringList(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<string[]>(json, JsonOptions);

    private void ThrowIfArmed(string operationId, string point)
    {
        if (faults.Consume(operationId, point))
        {
            throw new EffectInterruptionException(
                $"Controlled Gate 6 interruption at '{point}' for operation '{operationId}'.");
        }
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
