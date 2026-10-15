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
                MessageId TEXT NOT NULL,
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DefinitionRecord>().HasKey(record => new { record.Id, record.Version });
        modelBuilder.Entity<InstanceRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InputAttemptRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InputAttemptRecord>().HasIndex(record => new { record.InstanceId, record.ReceivedAt, record.Id });
        modelBuilder.Entity<TransitionHistoryRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<TransitionHistoryRecord>().HasIndex(record => new { record.InstanceId, record.RevisionAfter });
        modelBuilder.Entity<TransitionHistoryRecord>().HasIndex(record => record.AttemptId).IsUnique();
        modelBuilder.Entity<InboundMessageRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InboundMessageRecord>().HasIndex(record => new { record.InstanceId, record.ReceivedAt, record.Id });
        modelBuilder.Entity<InboundMessageRecord>().HasIndex(record => new { record.InstanceId, record.InputKind, record.MessageId });
        modelBuilder.Entity<ExpectedOperationRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<ExpectedOperationRecord>().HasIndex(record => new { record.InstanceId, record.OperationId }).IsUnique();
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
    public required string MessageId { get; set; }
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
