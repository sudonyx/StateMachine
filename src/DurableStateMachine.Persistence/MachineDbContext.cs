using Microsoft.EntityFrameworkCore;

namespace DurableStateMachine.Persistence;

public sealed class MachineDbContext(DbContextOptions<MachineDbContext> options) : DbContext(options)
{
    public DbSet<DefinitionRecord> Definitions => Set<DefinitionRecord>();
    public DbSet<InstanceRecord> Instances => Set<InstanceRecord>();
    public DbSet<InputAttemptRecord> InputAttempts => Set<InputAttemptRecord>();
    public DbSet<TransitionHistoryRecord> TransitionHistory => Set<TransitionHistoryRecord>();

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
