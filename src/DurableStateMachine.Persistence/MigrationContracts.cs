namespace DurableStateMachine.Persistence;

public sealed record DefinitionMigrationPlan(
    string Id,
    string Version,
    string SourceDefinitionId,
    string SourceDefinitionVersion,
    string TargetDefinitionId,
    string TargetDefinitionVersion,
    IReadOnlyList<string> SupportedSourceStates,
    IReadOnlyList<MigrationStateCorrespondence> StateCorrespondences,
    IReadOnlyList<string> RequiredTargetFacts,
    IReadOnlyList<string> AmendmentReferences);

public sealed record MigrationStateCorrespondence(
    string SourceState,
    string TargetState,
    string BusinessMeaning);

public sealed record MigrationRequest(
    string MigrationId,
    string SourceDefinitionId,
    string SourceDefinitionVersion,
    string TargetDefinitionId,
    string TargetDefinitionVersion,
    long ExpectedRevision,
    string AuthorisationReason);

public sealed record MigrationEligibility(
    string InstanceId,
    string Status,
    string? PlanId,
    string? PlanVersion,
    string SourceDefinitionId,
    string SourceDefinitionVersion,
    string TargetDefinitionId,
    string TargetDefinitionVersion,
    string SourceState,
    string? TargetState,
    long CurrentRevision,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> OutstandingWorkFindings);

public sealed record MigrationExecutionResult(MigrationRecord Migration, MachineInstanceDetails Instance);
