using System.Text.Json;
using DurableStateMachine.Persistence;

namespace DurableStateMachine.Api;

public static class OrionMigrationPlans
{
    private static readonly DefinitionMigrationPlan Orion4To5Draft = new(
        "orion-4-to-5-early-draft",
        "1",
        "orion-equipment-lease",
        "4.0",
        "orion-equipment-lease",
        "5.0",
        ["Draft"],
        [
            new("Draft", "Draft", "An application not yet submitted remains an unsubmitted draft."),
            new("AwaitingDocuments", "AwaitingDocuments", "The application is waiting for required documents."),
            new("AssessmentReady", "AssessmentReady", "Required documents are complete and assessment can begin."),
            new("ManualReview", "SpecialistAssessment", "The same reviewer assessment under the amended business term."),
            new("AwaitingOfferAcceptance", "AwaitingOfferAcceptance", "A customer offer is outstanding."),
            new("AssetReservationPending", "AssetReservationPending", "Equipment reservation is pending."),
            new("CustomerNotificationPending", "CustomerNotificationPending", "Customer notification is pending."),
            new("Approved", "Approved", "The application reached its approved terminal outcome."),
            new("Rejected", "Rejected", "The application reached its rejected terminal outcome."),
            new("Cancelled", "Cancelled", "The application reached its cancelled terminal outcome."),
            new("OfferExpired", "OfferExpired", "The offer expired."),
            new("OfferDeclined", "OfferDeclined", "The customer declined the offer.")
        ],
        [],
        ["ORN-AMD-001", "ORN-AMD-002", "ORN-AMD-003", "ORN-AMD-004"]);

    public static DefinitionMigrationPlan? Resolve(MigrationRequest request) =>
        request.SourceDefinitionId == Orion4To5Draft.SourceDefinitionId &&
        request.SourceDefinitionVersion == Orion4To5Draft.SourceDefinitionVersion &&
        request.TargetDefinitionId == Orion4To5Draft.TargetDefinitionId &&
        request.TargetDefinitionVersion == Orion4To5Draft.TargetDefinitionVersion
            ? Orion4To5Draft
            : null;
}

public sealed record MigrationResponse(
    string Id,
    string InstanceId,
    string MigrationId,
    string PlanId,
    string PlanVersion,
    string SourceDefinitionId,
    string SourceDefinitionVersion,
    string SourceDefinitionDigest,
    string TargetDefinitionId,
    string TargetDefinitionVersion,
    string TargetDefinitionDigest,
    string SourceState,
    string TargetState,
    long ExpectedRevision,
    long SourceRevision,
    long TargetRevision,
    string AuthorisationContext,
    string AuthorisationReason,
    IReadOnlyList<string> EligibilityFindings,
    IReadOnlyList<string> OutstandingWorkFindings,
    IReadOnlyList<string> AmendmentReferences,
    string Status,
    string Outcome,
    string Explanation,
    DateTimeOffset CommittedAt)
{
    public static MigrationResponse From(MigrationRecord record) =>
        new(record.Id, record.InstanceId, record.MigrationId, record.PlanId, record.PlanVersion,
            record.SourceDefinitionId, record.SourceDefinitionVersion, record.SourceDefinitionDigest,
            record.TargetDefinitionId, record.TargetDefinitionVersion, record.TargetDefinitionDigest,
            record.SourceState, record.TargetState, record.ExpectedRevision, record.SourceRevision, record.TargetRevision,
            record.AuthorisationContext, record.AuthorisationReason,
            JsonSerializer.Deserialize<string[]>(record.EligibilityFindingsJson, JsonDefaults.Options) ?? [],
            JsonSerializer.Deserialize<string[]>(record.OutstandingWorkFindingsJson, JsonDefaults.Options) ?? [],
            JsonSerializer.Deserialize<string[]>(record.AmendmentReferencesJson, JsonDefaults.Options) ?? [],
            record.Status, record.Outcome, record.Explanation, record.CommittedAt);
}
