using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DurableStateMachine.AsteriaClient;

public sealed class AsteriaDeadlineCoordinator(
    AsteriaReleaseRegistry releaseRegistry,
    AsteriaActivityLedger activityLedger,
    AsteriaDeadlineLedger deadlineLedger,
    AsteriaStateMachineClient stateMachine,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AsteriaDeadlineSnapshot> EnsureVerificationEscalationAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken = default)
    {
        EnsureAcceptedRelease(release);
        if (release.DefinitionVersion != "5.0")
        {
            throw new InvalidOperationException("Asteria verification deadlines require workflow definition V5.");
        }
        var dueAt = release.Material.PlannedDeploymentAt.ToUniversalTime().AddHours(-48);
        var deadlineId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0VerificationEscalation", "deadline");
        return await deadlineLedger.EnsureAsync(
            new AsteriaDeadlineReservation(
                deadlineId,
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                release.WorkflowInstanceId,
                "VerificationEscalation",
                dueAt,
                release.Work.WorkId),
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<AsteriaDeadlineSnapshot> EnsureDeploymentInvestigationAsync(
        AsteriaReleaseSnapshot release,
        CancellationToken cancellationToken = default)
    {
        EnsureAcceptedRelease(release);
        if (release.DefinitionVersion != "5.0")
        {
            throw new InvalidOperationException("Asteria deployment deadlines require workflow definition V5.");
        }
        var history = await stateMachine.ListTransitionHistoryAsync(
            release.WorkflowInstanceId, cancellationToken);
        var deploymentStart = history.SingleOrDefault(item =>
            GetString(item, "transitionId") == "deployment-start-from-ready" &&
            GetString(item, "event") == "AsteriaDeploymentStarted");
        if (deploymentStart.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Asteria deployment investigation deadline requires the accepted deployment-start history record.");
        }
        var originId = GetString(deploymentStart, "id");
        var dueAt = DateTimeOffset.Parse(GetString(deploymentStart, "occurredAt")).ToUniversalTime()
            .AddMinutes(30);
        var deadlineId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0DeploymentInvestigation", "deadline");
        return await deadlineLedger.EnsureAsync(
            new AsteriaDeadlineReservation(
                deadlineId,
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                release.WorkflowInstanceId,
                "DeploymentInvestigation",
                dueAt,
                originId),
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<AsteriaDeadlineSnapshot>> ProcessDueAsync(
        string releaseId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var generations = await releaseRegistry.ListGenerationsAsync(releaseId, cancellationToken);
        var dueDeadlines = new List<AsteriaDeadlineSnapshot>();
        foreach (var generation in generations)
        {
            var deadlines = await deadlineLedger.ListGenerationAsync(
                releaseId, generation.AssessmentGeneration, cancellationToken);
            dueDeadlines.AddRange(deadlines.Where(item =>
                item.Status == "Pending" && item.DueAt <= now));
        }

        var processed = new List<AsteriaDeadlineSnapshot>(dueDeadlines.Count);
        foreach (var deadline in dueDeadlines.OrderBy(item => item.DueAt))
        {
            var currentRelease = await releaseRegistry.GetReleaseAsync(releaseId, cancellationToken)
                ?? throw new KeyNotFoundException($"Asteria release '{releaseId}' does not exist.");
            string outcome;
            object evidence;
            if (currentRelease.AssessmentGeneration != deadline.Generation ||
                currentRelease.MaterialDigest != deadline.MaterialDigest ||
                currentRelease.WorkflowInstanceId != deadline.WorkflowInstanceId)
            {
                outcome = "Superseded";
                evidence = new
                {
                    deadline.DeadlineId,
                    deadline.DeadlineKind,
                    deadline.DueAt,
                    currentGeneration = currentRelease.AssessmentGeneration,
                    currentMaterialDigest = currentRelease.MaterialDigest,
                    currentWorkflowInstanceId = currentRelease.WorkflowInstanceId
                };
            }
            else if (deadline.DeadlineKind == "VerificationEscalation")
            {
                (outcome, evidence) = await EvaluateVerificationDeadlineAsync(
                    currentRelease, deadline, cancellationToken);
            }
            else
            {
                (outcome, evidence) = await EvaluateDeploymentDeadlineAsync(
                    currentRelease, deadline, cancellationToken);
            }

            var evidenceJson = JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            processed.Add(await deadlineLedger.ProcessAsync(
                deadline.DeadlineId,
                outcome,
                evidenceJson,
                now,
                cancellationToken));
        }
        return processed;
    }

    private async Task<(string Outcome, object Evidence)> EvaluateVerificationDeadlineAsync(
        AsteriaReleaseSnapshot release,
        AsteriaDeadlineSnapshot deadline,
        CancellationToken cancellationToken)
    {
        if (release.Work.OriginalResponseJson is null ||
            release.Work.Status != "Accepted")
        {
            return ("Unresolved", new
            {
                deadline.DeadlineId,
                deadline.DueAt,
                assessmentStatus = release.Work.Status,
                reason = "The accepted policy response is unavailable."
            });
        }
        var assessment = AsteriaDecisionApiClient.ValidateResponse(
            release.Work.OriginalResponseJson, release);
        var applicability = AsteriaDecisionApiClient.ReadObligationApplicabilities(assessment);
        var obligationStatuses = new List<object>();
        var incomplete = false;
        var unresolved = false;
        foreach (var item in new[]
                 {
                     ("FunctionalVerification", applicability.FunctionalVerification),
                     ("SecurityVerification", applicability.SecurityVerification)
                 })
        {
            if (item.Item2 == "NotRequired")
            {
                obligationStatuses.Add(new
                {
                    concernId = item.Item1,
                    applicability = item.Item2,
                    outcome = "NotRequired"
                });
                continue;
            }
            if (item.Item2 == "Unresolved")
            {
                unresolved = true;
                obligationStatuses.Add(new
                {
                    concernId = item.Item1,
                    applicability = item.Item2,
                    outcome = "Unresolved"
                });
                continue;
            }
            var workId = StableId(
                $"{release.ReleaseId}\0{release.AssessmentGeneration}\0{item.Item1}\0initial", "work");
            var activity = await activityLedger.GetAsync(workId, cancellationToken);
            var passed = activity is
            {
                Status: "Accepted",
                ValidatedOutcome: "Passed",
                ObservationReceiptId: not null,
                AcceptedHistoryId: not null
            };
            incomplete |= !passed;
            obligationStatuses.Add(new
            {
                concernId = item.Item1,
                applicability = item.Item2,
                activityStatus = activity?.Status ?? "Missing",
                validatedOutcome = activity?.ValidatedOutcome,
                complete = passed
            });
        }

        var outcome = incomplete
            ? "EscalationRecorded"
            : unresolved
                ? "Unresolved"
                : "NoEscalationRequired";
        return (outcome, new
        {
            deadline.DeadlineId,
            deadline.DueAt,
            processedAtUtc = _timeProvider.GetUtcNow(),
            releaseId = release.ReleaseId,
            generation = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            assessmentEvidenceDigest = AsteriaActivityLedger.Fingerprint(assessment.OriginalResponseJson),
            outcome = outcome switch
            {
                "EscalationRecorded" => "Escalation recorded; no obligation was waived or automatically decided.",
                "Unresolved" => "Applicability remained unresolved; no escalation decision was inferred.",
                _ => "No mandatory verification remained incomplete."
            },
            obligations = obligationStatuses
        });
    }

    private async Task<(string Outcome, object Evidence)> EvaluateDeploymentDeadlineAsync(
        AsteriaReleaseSnapshot release,
        AsteriaDeadlineSnapshot deadline,
        CancellationToken cancellationToken)
    {
        var workId = StableId(
            $"{release.ReleaseId}\0{release.AssessmentGeneration}\0Deployment", "work");
        var activity = await activityLedger.GetAsync(workId, cancellationToken);
        var authoritativeResultAccepted = activity is
        {
            Status: "Accepted",
            ValidatedOutcome: not null,
            ObservationReceiptId: not null,
            AcceptedHistoryId: not null
        };
        return (
            authoritativeResultAccepted ? "ResultAlreadyAccepted" : "InvestigationRecorded",
            new
            {
                deadline.DeadlineId,
                deadline.DueAt,
                processedAtUtc = _timeProvider.GetUtcNow(),
                releaseId = release.ReleaseId,
                generation = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest,
                deadline.OriginId,
                activityStatus = activity?.Status ?? "Missing",
                validatedOutcome = activity?.ValidatedOutcome,
                investigationRecorded = !authoritativeResultAccepted,
                description = authoritativeResultAccepted
                    ? "An authoritative deployment result was accepted before deadline processing."
                    : "Investigation due was recorded; this is not evidence that an intervention occurred or that deployment failed."
            });
    }

    private static void EnsureAcceptedRelease(AsteriaReleaseSnapshot release)
    {
        if (release.RegistryState != "Operational" ||
            release.Status != "Accepted" ||
            release.Work.Status != "Accepted" ||
            release.Work.Generation != release.AssessmentGeneration ||
            release.Work.MaterialDigest != release.MaterialDigest ||
            release.Work.WorkflowInstanceId != release.WorkflowInstanceId)
        {
            throw new InvalidOperationException("Asteria deadline scheduling requires the current accepted assessment.");
        }
    }

    private static string StableId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static string GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) &&
        item.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()!
            : throw new InvalidDataException($"State Machine history is missing '{property}'.");
}
