using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Persistence;

public sealed class MachineDbContext(DbContextOptions<MachineDbContext> options) : DbContext(options)
{
    public DbSet<DefinitionRecord> Definitions => Set<DefinitionRecord>();
    public DbSet<InstanceRecord> Instances => Set<InstanceRecord>();
    public DbSet<InputAttemptRecord> InputAttempts => Set<InputAttemptRecord>();
    public DbSet<TransitionHistoryRecord> TransitionHistory => Set<TransitionHistoryRecord>();
    public DbSet<InboundMessageRecord> InboundMessages => Set<InboundMessageRecord>();
    public DbSet<ExpectedOperationRecord> ExpectedOperations => Set<ExpectedOperationRecord>();
    public DbSet<EffectOperationRecord> EffectOperations => Set<EffectOperationRecord>();
    public DbSet<EffectAttemptRecord> EffectAttempts => Set<EffectAttemptRecord>();
    public DbSet<TimerRecord> Timers => Set<TimerRecord>();

    public async Task EnsureGate4SchemaAsync(CancellationToken cancellationToken = default)
    {
        var connection = Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "PRAGMA table_info('InputAttempts')";
                await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    columns.Add(reader.GetString(1));
                }
            }

            if (!columns.Contains(nameof(InputAttemptRecord.EvaluationEvidenceJson)))
            {
                await using var addEvidence = connection.CreateCommand();
                addEvidence.CommandText = "ALTER TABLE InputAttempts ADD COLUMN EvaluationEvidenceJson TEXT NOT NULL DEFAULT '{}'";
                await addEvidence.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!columns.Contains(nameof(InputAttemptRecord.EvaluationEvidenceDigest)))
            {
                await using var addDigest = connection.CreateCommand();
                addDigest.CommandText = "ALTER TABLE InputAttempts ADD COLUMN EvaluationEvidenceDigest TEXT NOT NULL DEFAULT ''";
                await addDigest.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task EnsureGate5SchemaAsync(CancellationToken cancellationToken = default)
    {
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS InboundMessages (
                Id TEXT NOT NULL CONSTRAINT PK_InboundMessages PRIMARY KEY,
                InstanceId TEXT NOT NULL,
                DeliveryId TEXT NULL,
                MessageId TEXT NOT NULL,
                OperationId TEXT NULL,
                ObservationId TEXT NULL,
                InputKind TEXT NOT NULL,
                InputType TEXT NOT NULL,
                Authority TEXT NOT NULL DEFAULT 'unknown',
                CorrelationId TEXT NULL,
                ClaimedSource TEXT NULL,
                ExpectedRevision INTEGER NULL,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                RevisionAtReceipt INTEGER NOT NULL,
                InputEnvelopeJson TEXT NOT NULL DEFAULT '{{}}',
                PayloadJson TEXT NOT NULL,
                EvidenceJson TEXT NOT NULL,
                PayloadDigest TEXT NOT NULL,
                Status TEXT NOT NULL,
                ReasonCode TEXT NOT NULL,
                Explanation TEXT NOT NULL,
                AcceptedEvent TEXT NULL,
                AttemptId TEXT NULL,
                ReceivedAt INTEGER NOT NULL,
                OccurredAt INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS IX_InboundMessages_InstanceId_ReceivedAt
                ON InboundMessages (InstanceId, ReceivedAt, Id);
            CREATE INDEX IF NOT EXISTS IX_InboundMessages_InstanceId_Kind_MessageId
                ON InboundMessages (InstanceId, InputKind, MessageId);
            CREATE TABLE IF NOT EXISTS ExpectedOperations (
                Id TEXT NOT NULL CONSTRAINT PK_ExpectedOperations PRIMARY KEY,
                InstanceId TEXT NOT NULL,
                OperationId TEXT NOT NULL,
                ObservationType TEXT NOT NULL,
                SourceIdentity TEXT NOT NULL,
                ExpectedAt INTEGER NOT NULL DEFAULT 0,
                ExpectedRevision INTEGER NOT NULL,
                RequiredEvidenceJson TEXT NOT NULL,
                Status TEXT NOT NULL,
                ResultDigest TEXT NULL,
                ResolvedAt INTEGER NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_ExpectedOperations_InstanceId_OperationId
                ON ExpectedOperations (InstanceId, OperationId);
            """, cancellationToken);
        var connection = Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "PRAGMA table_info('InboundMessages')";
                await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    columns.Add(reader.GetString(1));
                }
            }
            if (!columns.Contains(nameof(InboundMessageRecord.InputEnvelopeJson)))
            {
                await using var addEnvelope = connection.CreateCommand();
                addEnvelope.CommandText = "ALTER TABLE InboundMessages ADD COLUMN InputEnvelopeJson TEXT NOT NULL DEFAULT '{}'";
                await addEnvelope.ExecuteNonQueryAsync(cancellationToken);
            }
            if (!columns.Contains(nameof(InboundMessageRecord.Authority)))
            {
                await using var addAuthority = connection.CreateCommand();
                addAuthority.CommandText = "ALTER TABLE InboundMessages ADD COLUMN Authority TEXT NOT NULL DEFAULT 'unknown'";
                await addAuthority.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var (column, declaration) in new[]
                     {
                         (nameof(InboundMessageRecord.DeliveryId), "TEXT NULL"),
                         (nameof(InboundMessageRecord.OperationId), "TEXT NULL"),
                         (nameof(InboundMessageRecord.ObservationId), "TEXT NULL"),
                     })
            {
                if (columns.Contains(column))
                {
                    continue;
                }
                await using var addIdentity = connection.CreateCommand();
                addIdentity.CommandText = $"ALTER TABLE InboundMessages ADD COLUMN {column} {declaration}";
                await addIdentity.ExecuteNonQueryAsync(cancellationToken);
                columns.Add(column);
            }
            await using (var createDeliveryIndex = connection.CreateCommand())
            {
                createDeliveryIndex.CommandText = """
                    CREATE INDEX IF NOT EXISTS IX_InboundMessages_InstanceId_DeliveryId
                        ON InboundMessages (InstanceId, DeliveryId)
                    """;
                await createDeliveryIndex.ExecuteNonQueryAsync(cancellationToken);
            }
            var operationColumns = new HashSet<string>(StringComparer.Ordinal);
            await using (var inspect = connection.CreateCommand())
            {
                inspect.CommandText = "PRAGMA table_info('ExpectedOperations')";
                await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    operationColumns.Add(reader.GetString(1));
                }
            }
            if (!operationColumns.Contains(nameof(ExpectedOperationRecord.ExpectedAt)))
            {
                await using var addExpectedAt = connection.CreateCommand();
                addExpectedAt.CommandText = "ALTER TABLE ExpectedOperations ADD COLUMN ExpectedAt INTEGER NOT NULL DEFAULT 0";
                await addExpectedAt.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task EnsureGate6SchemaAsync(CancellationToken cancellationToken = default)
    {
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS EffectOperations (
                Id TEXT NOT NULL CONSTRAINT PK_EffectOperations PRIMARY KEY,
                InstanceId TEXT NOT NULL,
                OperationId TEXT NOT NULL,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                ExecutionRevision INTEGER NOT NULL,
                Capability TEXT NOT NULL,
                ProviderIdentity TEXT NOT NULL,
                ObservationType TEXT NOT NULL,
                RequestJson TEXT NOT NULL,
                RequestDigest TEXT NOT NULL,
                Status TEXT NOT NULL,
                RequestedAt INTEGER NOT NULL,
                UpdatedAt INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_EffectOperations_InstanceId_OperationId
                ON EffectOperations (InstanceId, OperationId);
            CREATE TABLE IF NOT EXISTS EffectAttempts (
                Id TEXT NOT NULL CONSTRAINT PK_EffectAttempts PRIMARY KEY,
                EffectOperationId TEXT NOT NULL,
                AttemptNumber INTEGER NOT NULL,
                StartedAt INTEGER NOT NULL,
                CompletedAt INTEGER NULL,
                TransportOutcome TEXT NOT NULL,
                ProviderOutcome TEXT NOT NULL,
                ProviderEffectId TEXT NULL,
                ProviderOccurredAt INTEGER NULL,
                ProviderEvidenceJson TEXT NULL,
                LocalPersistenceOutcome TEXT NOT NULL,
                ObservationStatus TEXT NOT NULL,
                ObservationReceiptId TEXT NULL,
                Error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_EffectAttempts_EffectOperationId_AttemptNumber
                ON EffectAttempts (EffectOperationId, AttemptNumber);
            """, cancellationToken);

        var connection = Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }
        try
        {
            var columns = new HashSet<string>(StringComparer.Ordinal);
            await using var inspect = connection.CreateCommand();
            inspect.CommandText = "PRAGMA table_info('EffectAttempts')";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
            await reader.DisposeAsync();
            if (!columns.Contains(nameof(EffectAttemptRecord.ProviderOccurredAt)))
            {
                await using var addOccurredAt = connection.CreateCommand();
                addOccurredAt.CommandText = "ALTER TABLE EffectAttempts ADD COLUMN ProviderOccurredAt INTEGER NULL";
                await addOccurredAt.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task EnsureGate7SchemaAsync(CancellationToken cancellationToken = default)
    {
        var additions = new Dictionary<string, string[]>
        {
            [nameof(EffectOperationRecord.HarnessProviderId)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectOperationRecord.CredentialBindingId)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectOperationRecord.ApiDescriptionVersionId)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectOperationRecord.HarnessOperationKey)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectOperationRecord.HarnessExecutionId)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectOperationRecord.HarnessIdempotencyKey)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectOperationRecord.HarnessExecutionState)] = ["EffectOperations", "INTEGER NULL"],
            [nameof(EffectOperationRecord.OperationReplaySafety)] = ["EffectOperations", "INTEGER NULL"],
            [nameof(EffectOperationRecord.IdempotencyDecision)] = ["EffectOperations", "INTEGER NULL"],
            [nameof(EffectOperationRecord.ReplayDecision)] = ["EffectOperations", "INTEGER NULL"],
            [nameof(EffectOperationRecord.OutcomeConfidence)] = ["EffectOperations", "TEXT NULL"],
            [nameof(EffectAttemptRecord.HarnessExecutionId)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.HarnessExecutionState)] = ["EffectAttempts", "INTEGER NULL"],
            [nameof(EffectAttemptRecord.HarnessResponseStatusCode)] = ["EffectAttempts", "INTEGER NULL"],
            [nameof(EffectAttemptRecord.HarnessResponseTruncated)] = ["EffectAttempts", "INTEGER NULL"],
            [nameof(EffectAttemptRecord.HarnessRejectionReason)] = ["EffectAttempts", "INTEGER NULL"],
            [nameof(EffectAttemptRecord.HarnessFailureCode)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.HarnessFailureMessage)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.HarnessAttemptIdsJson)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.HarnessEvidenceIdsJson)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.IdempotencyDecision)] = ["EffectAttempts", "INTEGER NULL"],
            [nameof(EffectAttemptRecord.ReplayDecision)] = ["EffectAttempts", "INTEGER NULL"],
            [nameof(EffectAttemptRecord.OutcomeConfidence)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.WorkflowTransitionId)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.WorkflowStateBefore)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.WorkflowStateAfter)] = ["EffectAttempts", "TEXT NULL"],
            [nameof(EffectAttemptRecord.WorkflowRevisionAfter)] = ["EffectAttempts", "INTEGER NULL"]
        };

        var connection = Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
        {
            await connection.OpenAsync(cancellationToken);
        }
        try
        {
            foreach (var (columnName, definition) in additions)
            {
                var tableName = definition[0];
                var columns = new HashSet<string>(StringComparer.Ordinal);
                await using (var inspect = connection.CreateCommand())
                {
                    inspect.CommandText = $"PRAGMA table_info('{tableName}')";
                    await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
                if (columns.Contains(columnName))
                {
                    continue;
                }
                await using var add = connection.CreateCommand();
                add.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition[1]}";
                await add.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            if (closeWhenDone)
            {
                await connection.CloseAsync();
            }
        }
    }

    public Task EnsureGate8SchemaAsync(CancellationToken cancellationToken = default) =>
        Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Timers (
                Id TEXT NOT NULL CONSTRAINT PK_Timers PRIMARY KEY,
                InstanceId TEXT NOT NULL,
                DefinitionId TEXT NOT NULL,
                DefinitionVersion TEXT NOT NULL,
                TimerDeclarationId TEXT NOT NULL,
                OriginTransitionId TEXT NOT NULL,
                OriginTransitionHistoryId TEXT NOT NULL,
                OriginEvent TEXT NOT NULL,
                OriginRevision INTEGER NOT NULL,
                TriggeredAt INTEGER NOT NULL,
                CalculatedAt INTEGER NOT NULL,
                DueAt INTEGER NOT NULL,
                Status TEXT NOT NULL,
                ResultReason TEXT NULL,
                ProcessedAt INTEGER NULL,
                AttemptId TEXT NULL,
                ResultTransitionId TEXT NULL,
                ActionOperationId TEXT NULL,
                EvidenceJson TEXT NOT NULL DEFAULT '{{}}',
                ProcessingEvidenceJson TEXT NOT NULL DEFAULT '{{}}'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Timers_InstanceId_Declaration_OriginTransition
                ON Timers (InstanceId, TimerDeclarationId, OriginTransitionHistoryId);
            CREATE INDEX IF NOT EXISTS IX_Timers_Status_DueAt_Id
                ON Timers (Status, DueAt, Id);
            CREATE INDEX IF NOT EXISTS IX_Timers_InstanceId_DueAt_Id
                ON Timers (InstanceId, DueAt, Id);
            """, cancellationToken);

    public Task EnsureGate9SchemaAsync(CancellationToken cancellationToken = default) =>
        Database.ExecuteSqlRawAsync("""
                CREATE UNIQUE INDEX IF NOT EXISTS UX_TransitionHistory_InstanceId_RevisionAfter
                    ON TransitionHistory (InstanceId, RevisionAfter);
                """, cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DefinitionRecord>().HasKey(record => new { record.Id, record.Version });
        modelBuilder.Entity<InstanceRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InputAttemptRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InputAttemptRecord>().HasIndex(record => new { record.InstanceId, record.ReceivedAt, record.Id });
        modelBuilder.Entity<TransitionHistoryRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<TransitionHistoryRecord>()
            .HasIndex(record => new { record.InstanceId, record.RevisionAfter })
            .IsUnique().HasDatabaseName("UX_TransitionHistory_InstanceId_RevisionAfter");
        modelBuilder.Entity<TransitionHistoryRecord>().HasIndex(record => record.AttemptId).IsUnique();
        modelBuilder.Entity<InboundMessageRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InboundMessageRecord>().HasIndex(record => new { record.InstanceId, record.ReceivedAt, record.Id });
        modelBuilder.Entity<InboundMessageRecord>().HasIndex(record => new { record.InstanceId, record.InputKind, record.MessageId });
        modelBuilder.Entity<InboundMessageRecord>().HasIndex(record => new { record.InstanceId, record.DeliveryId })
            .HasDatabaseName("IX_InboundMessages_InstanceId_DeliveryId");
        modelBuilder.Entity<ExpectedOperationRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<ExpectedOperationRecord>().HasIndex(record => new { record.InstanceId, record.OperationId }).IsUnique();
        modelBuilder.Entity<EffectOperationRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<EffectOperationRecord>().HasIndex(record => new { record.InstanceId, record.OperationId }).IsUnique();
        modelBuilder.Entity<EffectAttemptRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<EffectAttemptRecord>().HasIndex(record => new { record.EffectOperationId, record.AttemptNumber }).IsUnique();
        modelBuilder.Entity<TimerRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<TimerRecord>()
            .HasIndex(record => new { record.InstanceId, record.TimerDeclarationId, record.OriginTransitionHistoryId })
            .IsUnique();
        modelBuilder.Entity<TimerRecord>().HasIndex(record => new { record.Status, record.DueAt, record.Id });
        modelBuilder.Entity<TimerRecord>().HasIndex(record => new { record.InstanceId, record.DueAt, record.Id });
        modelBuilder.Entity<DefinitionRecord>().Property(record => record.RegisteredAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<InstanceRecord>().Property(record => record.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<InstanceRecord>().Property(record => record.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<InputAttemptRecord>().Property(record => record.ReceivedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<TransitionHistoryRecord>().Property(record => record.OccurredAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<InboundMessageRecord>().Property(record => record.ReceivedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<InboundMessageRecord>().Property(record => record.OccurredAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<ExpectedOperationRecord>().Property(record => record.ResolvedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<ExpectedOperationRecord>().Property(record => record.ExpectedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<EffectOperationRecord>().Property(record => record.RequestedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<EffectOperationRecord>().Property(record => record.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<EffectAttemptRecord>().Property(record => record.StartedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<EffectAttemptRecord>().Property(record => record.CompletedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<EffectAttemptRecord>().Property(record => record.ProviderOccurredAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        modelBuilder.Entity<TimerRecord>().Property(record => record.TriggeredAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<TimerRecord>().Property(record => record.CalculatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<TimerRecord>().Property(record => record.DueAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        modelBuilder.Entity<TimerRecord>().Property(record => record.ProcessedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
    }
}

public sealed class DefinitionRecord
{
    public required string Id { get; set; }
    public required string Version { get; set; }
    public required string LanguageVersion { get; set; }
    public required string DefinitionJson { get; set; }
    public required string ContentDigest { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}

public sealed class InstanceRecord
{
    public required string Id { get; set; }
    public required string DefinitionId { get; set; }
    public required string DefinitionVersion { get; set; }
    public required string CurrentState { get; set; }
    public required string FactsJson { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InputAttemptRecord
{
    public required string Id { get; set; }
    public required string InstanceId { get; set; }
    public required string Event { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public required string Status { get; set; }
    public string? TransitionId { get; set; }
    public required string PreviousState { get; set; }
    public string? TargetState { get; set; }
    public long RevisionBefore { get; set; }
    public long RevisionAfter { get; set; }
    public required string ReasonCode { get; set; }
    public required string Explanation { get; set; }
    public required string GuardFindingsJson { get; set; }
    public required string EvaluationEvidenceJson { get; set; }
    public required string EvaluationEvidenceDigest { get; set; }
}

public sealed class TransitionHistoryRecord
{
    public required string Id { get; set; }
    public required string InstanceId { get; set; }
    public required string AttemptId { get; set; }
    public required string TransitionId { get; set; }
    public required string DefinitionId { get; set; }
    public required string DefinitionVersion { get; set; }
    public required string PreviousState { get; set; }
    public required string NewState { get; set; }
    public required string Event { get; set; }
    public long RevisionBefore { get; set; }
    public long RevisionAfter { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class InboundMessageRecord
{
    public required string Id { get; set; }
    public required string InstanceId { get; set; }
    public string? DeliveryId { get; set; }
    public required string MessageId { get; set; }
    public string? OperationId { get; set; }
    public string? ObservationId { get; set; }
    public required string InputKind { get; set; }
    public required string InputType { get; set; }
    public required string Authority { get; set; }
    public string? CorrelationId { get; set; }
    public string? ClaimedSource { get; set; }
    public long? ExpectedRevision { get; set; }
    public required string DefinitionId { get; set; }
    public required string DefinitionVersion { get; set; }
    public long RevisionAtReceipt { get; set; }
    public required string InputEnvelopeJson { get; set; }
    public required string PayloadJson { get; set; }
    public required string EvidenceJson { get; set; }
    public required string PayloadDigest { get; set; }
    public required string Status { get; set; }
    public required string ReasonCode { get; set; }
    public required string Explanation { get; set; }
    public string? AcceptedEvent { get; set; }
    public string? AttemptId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
}

public sealed class ExpectedOperationRecord
{
    public required string Id { get; set; }
    public required string InstanceId { get; set; }
    public required string OperationId { get; set; }
    public required string ObservationType { get; set; }
    public required string SourceIdentity { get; set; }
    public DateTimeOffset ExpectedAt { get; set; }
    public long ExpectedRevision { get; set; }
    public required string RequiredEvidenceJson { get; set; }
    public required string Status { get; set; }
    public string? ResultDigest { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class EffectOperationRecord
{
    public required string Id { get; set; }
    public required string InstanceId { get; set; }
    public required string OperationId { get; set; }
    public required string DefinitionId { get; set; }
    public required string DefinitionVersion { get; set; }
    public long ExecutionRevision { get; set; }
    public required string Capability { get; set; }
    public required string ProviderIdentity { get; set; }
    public required string ObservationType { get; set; }
    public required string RequestJson { get; set; }
    public required string RequestDigest { get; set; }
    public required string Status { get; set; }
    public string? HarnessProviderId { get; set; }
    public string? CredentialBindingId { get; set; }
    public string? ApiDescriptionVersionId { get; set; }
    public string? HarnessOperationKey { get; set; }
    public string? HarnessExecutionId { get; set; }
    public string? HarnessIdempotencyKey { get; set; }
    public int? HarnessExecutionState { get; set; }
    public int? OperationReplaySafety { get; set; }
    public int? IdempotencyDecision { get; set; }
    public int? ReplayDecision { get; set; }
    public string? OutcomeConfidence { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EffectAttemptRecord
{
    public required string Id { get; set; }
    public required string EffectOperationId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string TransportOutcome { get; set; }
    public required string ProviderOutcome { get; set; }
    public string? ProviderEffectId { get; set; }
    public DateTimeOffset? ProviderOccurredAt { get; set; }
    public string? ProviderEvidenceJson { get; set; }
    public required string LocalPersistenceOutcome { get; set; }
    public required string ObservationStatus { get; set; }
    public string? ObservationReceiptId { get; set; }
    public string? Error { get; set; }
    public string? HarnessExecutionId { get; set; }
    public int? HarnessExecutionState { get; set; }
    public int? HarnessResponseStatusCode { get; set; }
    public bool? HarnessResponseTruncated { get; set; }
    public int? HarnessRejectionReason { get; set; }
    public string? HarnessFailureCode { get; set; }
    public string? HarnessFailureMessage { get; set; }
    public string? HarnessAttemptIdsJson { get; set; }
    public string? HarnessEvidenceIdsJson { get; set; }
    public int? IdempotencyDecision { get; set; }
    public int? ReplayDecision { get; set; }
    public string? OutcomeConfidence { get; set; }
    public string? WorkflowTransitionId { get; set; }
    public string? WorkflowStateBefore { get; set; }
    public string? WorkflowStateAfter { get; set; }
    public long? WorkflowRevisionAfter { get; set; }
}

public sealed class TimerRecord
{
    public required string Id { get; set; }
    public required string InstanceId { get; set; }
    public required string DefinitionId { get; set; }
    public required string DefinitionVersion { get; set; }
    public required string TimerDeclarationId { get; set; }
    public required string OriginTransitionId { get; set; }
    public required string OriginTransitionHistoryId { get; set; }
    public required string OriginEvent { get; set; }
    public long OriginRevision { get; set; }
    public DateTimeOffset TriggeredAt { get; set; }
    public DateTimeOffset CalculatedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public required string Status { get; set; }
    public string? ResultReason { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? AttemptId { get; set; }
    public string? ResultTransitionId { get; set; }
    public string? ActionOperationId { get; set; }
    public required string EvidenceJson { get; set; }
    public required string ProcessingEvidenceJson { get; set; }
}
