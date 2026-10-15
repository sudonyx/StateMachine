using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Persistence;

public sealed class MachineDbContext(DbContextOptions<MachineDbContext> options) : DbContext(options)
{
    public DbSet<DefinitionRecord> Definitions => Set<DefinitionRecord>();
    public DbSet<InstanceRecord> Instances => Set<InstanceRecord>();
    public DbSet<InputAttemptRecord> InputAttempts => Set<InputAttemptRecord>();
    public DbSet<TransitionHistoryRecord> TransitionHistory => Set<TransitionHistoryRecord>();

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DefinitionRecord>().HasKey(record => new { record.Id, record.Version });
        modelBuilder.Entity<InstanceRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InputAttemptRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<InputAttemptRecord>().HasIndex(record => new { record.InstanceId, record.ReceivedAt, record.Id });
        modelBuilder.Entity<TransitionHistoryRecord>().HasKey(record => record.Id);
        modelBuilder.Entity<TransitionHistoryRecord>().HasIndex(record => new { record.InstanceId, record.RevisionAfter });
        modelBuilder.Entity<TransitionHistoryRecord>().HasIndex(record => record.AttemptId).IsUnique();
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
