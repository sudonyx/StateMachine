using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using DurableStateMachine.Api;
using DurableStateMachine.AsteriaClient;
using DurableStateMachine.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class AsteriaMilestone3BTests
{
    private const string TrustedKey = "asteria-milestone3b-test-ingestion-key";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 16, 0, 0, TimeSpan.Zero);
    private static readonly AsteriaReleaseMaterial Material = new(
        "AsteriaPortal",
        "4.2.0",
        "production",
        "Standard",
        "Medium",
        false,
        true,
        "Initial synthetic release assessment",
        "release-owner@example.invalid",
        new DateTimeOffset(2027, 3, 15, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Registry_derives_digest_and_enforces_release_identity_idempotently()
    {
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var first = await registry.CreateReleaseAsync("AST-REGISTRY-001", Material, Now);
        var repeated = await registry.CreateReleaseAsync("AST-REGISTRY-001", Material, Now.AddMinutes(1));

        Assert.Equal(1, first.AssessmentGeneration);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first.MaterialDigest);
        Assert.Equal(first.MaterialDigest, repeated.MaterialDigest);
        Assert.Equal(first.WorkflowInstanceId, repeated.WorkflowInstanceId);
        Assert.Equal(first.Work.WorkId, repeated.Work.WorkId);
        Assert.Equal(first.Work.KnownAt, repeated.Work.KnownAt);

        var changed = Material with { TargetVersion = "4.2.1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CreateReleaseAsync("AST-REGISTRY-001", changed, Now));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.CreateReleaseAsync("AST-REGISTRY-002", Material with { Environment = "unknown" }, Now));
    }

    [Fact]
    public async Task Activity_ledger_persists_validated_acceptance_across_restart_and_retries()
    {
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var release = await registry.CreateReleaseAsync("AST-ACTIVITY-001", Material, Now);
        var reservation = CreateActivityReservation(release, "functional");

        await using (var ledger = new AsteriaActivityLedger(path))
        {
            var reserved = await ledger.ReserveAsync(reservation, Now);
            var repeatedReservation = await ledger.ReserveAsync(reservation, Now.AddMinutes(1));
            Assert.Equal(reserved.WorkId, repeatedReservation.WorkId);
            Assert.Equal("Reserved", repeatedReservation.Status);
            Assert.Equal(AsteriaActivityLedger.Fingerprint(reservation.OriginalRequestJson), reserved.RequestFingerprint);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ledger.MarkObservationSubmittedAsync(reserved.WorkId, "receipt-activity-1", Now));

            _ = await ledger.MarkExpectedOperationRegisteredAsync(reserved.WorkId, Now);
            _ = await ledger.MarkDispatchPendingAsync(reserved.WorkId, Now);
            var dispatchStarted = await ledger.MarkDispatchStartedAsync(reserved.WorkId, Now);
            Assert.True(dispatchStarted.ShouldDispatch);
            Assert.Equal("Dispatched", dispatchStarted.Activity.Status);
            var dispatched = await ledger.RecordHarnessExecutionAsync(
                reserved.WorkId,
                "harness-execution-1",
                reservation.ProviderIdentity!,
                reservation.ApiDescriptionVersionId!,
                reservation.OperationKey!,
                Now);
            var repeatedDispatch = await ledger.RecordHarnessExecutionAsync(
                reserved.WorkId,
                "harness-execution-1",
                reservation.ProviderIdentity!,
                reservation.ApiDescriptionVersionId!,
                reservation.OperationKey!,
                Now.AddMinutes(1));
            Assert.Equal("Dispatched", dispatched.Status);
            Assert.Equal(dispatched.HarnessExecutionId, repeatedDispatch.HarnessExecutionId);

            const string response = """{"status":"passed"}""";
            var persistedResponse = await ledger.RecordOriginalResponseAsync(reserved.WorkId, response, Now);
            var repeatedResponse = await ledger.RecordOriginalResponseAsync(reserved.WorkId, response, Now.AddMinutes(1));
            Assert.Equal("ResponsePersisted", persistedResponse.Status);
            Assert.Equal(persistedResponse.ResponseDigest, repeatedResponse.ResponseDigest);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ledger.RecordValidatedOutcomeAsync(
                    reserved.WorkId,
                    "Approved",
                    AsteriaActivityLedger.Fingerprint("evidence"),
                    Now));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                ledger.RecordValidatedOutcomeAsync(reserved.WorkId, "Passed", "not-a-digest", Now));

            var validated = await ledger.RecordValidatedOutcomeAsync(
                reserved.WorkId,
                "Passed",
                AsteriaActivityLedger.Fingerprint("validated evidence"),
                Now);
            var repeatedValidation = await ledger.RecordValidatedOutcomeAsync(
                reserved.WorkId,
                "Passed",
                AsteriaActivityLedger.Fingerprint("validated evidence"),
                Now.AddMinutes(1));
            Assert.Equal(validated.ValidatedOutcome, repeatedValidation.ValidatedOutcome);

            var submitted = await ledger.MarkObservationSubmittedAsync(
                reserved.WorkId, "receipt-activity-1", Now);
            var repeatedSubmission = await ledger.MarkObservationSubmittedAsync(
                reserved.WorkId, "receipt-activity-1", Now.AddMinutes(1));
            Assert.Equal("ObservationSubmitted", submitted.Status);
            Assert.Equal(submitted.ObservationReceiptId, repeatedSubmission.ObservationReceiptId);

            var accepted = await ledger.MarkAcceptedAsync(
                reserved.WorkId, "receipt-activity-1", "attempt-accepted-1", "history-accepted-1", Now);
            var repeatedAcceptance = await ledger.MarkAcceptedAsync(
                reserved.WorkId, "receipt-activity-1", "attempt-accepted-1", "history-accepted-1", Now.AddMinutes(1));
            Assert.Equal("Accepted", accepted.Status);
            Assert.Equal(accepted.AcceptedHistoryId, repeatedAcceptance.AcceptedHistoryId);
        }

        await using var restartedLedger = new AsteriaActivityLedger(path);
        var restored = await restartedLedger.GetAsync(reservation.WorkId);
        Assert.NotNull(restored);
        Assert.Equal("Accepted", restored.Status);
        Assert.Equal("Passed", restored.ValidatedOutcome);
        Assert.Equal("harness-execution-1", restored.HarnessExecutionId);
        Assert.Equal("history-accepted-1", restored.AcceptedHistoryId);
    }

    [Fact]
    public async Task Activity_ledger_preserves_unknown_outcome_and_rejects_conflicts_and_dangling_generation()
    {
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var release = await registry.CreateReleaseAsync("AST-ACTIVITY-002", Material, Now);
        var reservation = CreateActivityReservation(release, "security");
        AsteriaActivitySnapshot reserved;
        await using (var ledger = new AsteriaActivityLedger(path))
        {
            reserved = await ledger.ReserveAsync(reservation, Now);
            _ = await ledger.MarkExpectedOperationRegisteredAsync(reserved.WorkId, Now);
            _ = await ledger.MarkDispatchPendingAsync(reserved.WorkId, Now);
            _ = await ledger.MarkDispatchStartedAsync(reserved.WorkId, Now);
        }

        await using var restartedLedger = new AsteriaActivityLedger(path);
        var afterRestart = (await restartedLedger.GetAsync(reserved.WorkId))!;
        Assert.Equal("Dispatched", afterRestart.Status);
        Assert.Null(afterRestart.HarnessExecutionId);
        var unknown = await restartedLedger.MarkOutcomeUnknownAsync(
            reserved.WorkId, "Harness response was lost after dispatch.", Now);
        var repeatedUnknown = await restartedLedger.MarkOutcomeUnknownAsync(
            reserved.WorkId, "Harness response was lost after dispatch.", Now.AddMinutes(1));
        Assert.Equal("OutcomeUnknown", unknown.Status);
        Assert.Equal(unknown.RecoveryDetail, repeatedUnknown.RecoveryDetail);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedLedger.RecordOriginalResponseAsync(reserved.WorkId, """{"status":"passed"}""", Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restartedLedger.ReserveAsync(
                reservation with { OriginalRequestJson = """{"provider":"changed"}""" },
                Now.AddMinutes(2)));
        Assert.Equal("OutcomeUnknown", (await restartedLedger.GetAsync(reserved.WorkId))!.Status);

        var invalidGeneration = reservation with
        {
            WorkId = "work-invalid-generation",
            ActivityId = "activity-invalid-generation",
            Generation = 2,
            ActivityType = "SecurityVerificationRetry",
            AttemptId = "attempt-invalid-generation",
            ExpectedOperationId = "operation-invalid-generation",
            SubmissionId = "submission-invalid-generation",
            IdempotencyKey = "idempotency-invalid-generation"
        };
        await Assert.ThrowsAsync<SqliteException>(() => restartedLedger.ReserveAsync(invalidGeneration, Now));
    }

    [Fact]
    public async Task Activity_ledger_concurrent_exact_reservations_share_one_durable_work_item()
    {
        var path = NewDatabasePath();
        try
        {
            await using var registry = new AsteriaReleaseRegistry(path);
            var release = await registry.CreateReleaseAsync("AST-ACTIVITY-CONCURRENT-001", Material, Now);
            await using var ledger = new AsteriaActivityLedger(path);
            var reservation = CreateActivityReservation(release, "functional-concurrent");

            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => ledger.ReserveAsync(reservation, Now)));

            Assert.All(results, result => Assert.Equal(reservation.WorkId, result.WorkId));
            Assert.Single(await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration));
            _ = await ledger.MarkExpectedOperationRegisteredAsync(reservation.WorkId, Now);
            _ = await ledger.MarkDispatchPendingAsync(reservation.WorkId, Now);

            var dispatchClaims = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => ledger.MarkDispatchStartedAsync(reservation.WorkId, Now)));

            Assert.Single(dispatchClaims, claim => claim.ShouldDispatch);
            Assert.All(dispatchClaims, claim => Assert.Equal("Dispatched", claim.Activity.Status));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                var filePath = path + suffix;
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
    }

    [Fact]
    public async Task Registry_renews_active_lease_and_allows_takeover_after_release()
    {
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        _ = await registry.CreateReleaseAsync("AST-LEASE-001", Material, Now);
        Assert.True(await registry.AcquireLeaseAsync(
            "AST-LEASE-001", "first-coordinator", Now, TimeSpan.FromSeconds(30)));
        Assert.False(await registry.AcquireLeaseAsync(
            "AST-LEASE-001", "second-coordinator", Now.AddSeconds(10), TimeSpan.FromSeconds(30)));

        Assert.True(await registry.RenewLeaseAsync(
            "AST-LEASE-001", "first-coordinator", Now.AddSeconds(20), TimeSpan.FromSeconds(30)));
        Assert.False(await registry.AcquireLeaseAsync(
            "AST-LEASE-001", "second-coordinator", Now.AddSeconds(40), TimeSpan.FromSeconds(30)));

        await registry.ReleaseLeaseAsync("AST-LEASE-001", "first-coordinator");
        Assert.True(await registry.AcquireLeaseAsync(
            "AST-LEASE-001", "second-coordinator", Now.AddSeconds(40), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Decision_request_uses_published_schema_without_workflow_only_fields()
    {
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-REQUEST-001", Material, Now);
        var (json, fingerprint) = AsteriaDecisionApiClient.CreateCanonicalRequest(release);
        using var request = JsonDocument.Parse(json);
        var root = request.RootElement;

        Assert.Equal("AST-REQUEST-001", root.GetProperty("releaseId").GetString());
        Assert.Equal(1, root.GetProperty("assessmentGeneration").GetInt32());
        Assert.Equal(release.MaterialDigest, root.GetProperty("materialDigest").GetString());
        Assert.Equal("2027-03-15", root.GetProperty("businessEffectiveAt").GetString());
        Assert.Equal(release.Work.KnownAt, root.GetProperty("knownAt").GetDateTimeOffset());
        Assert.Equal(9, root.GetProperty("facts").EnumerateObject().Count());
        Assert.False(root.TryGetProperty("workflowInstanceId", out _));
        Assert.False(root.TryGetProperty("expectedOperationId", out _));
        Assert.False(root.TryGetProperty("parentRevision", out _));
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Asteria_v2_maps_only_safe_assessment_outcomes()
    {
        var v1 = TestSupport.LoadWorkflow("asteria-release-governance-v1.json");
        var v2 = TestSupport.LoadWorkflow("asteria-release-governance-v2.json");

        Assert.Equal("1.0", v1.Version);
        Assert.Equal("2.0", v2.Version);
        Assert.Contains("AsteriaAssessmentObserved", v2.InputPolicy!.Observations.Keys);
        Assert.Contains(v2.Transitions, transition => transition.Id == "asteria-assessment-prohibited" && transition.Target == "Prohibited");
        Assert.Contains(v2.Transitions, transition => transition.Id == "asteria-assessment-unresolved" && transition.Target == "AssessmentUnresolved");
        Assert.Contains(v2.Transitions, transition => transition.Target == "RequirementsIdentified");
        Assert.Contains(v2.Transitions, transition => transition.Target == "ReviewObligationsIdentified");
        Assert.DoesNotContain(v2.Transitions, transition =>
            transition.Target is "PreparingForDeployment" or "ReadyForDeployment" or
                "DeploymentInProgress" or "PostDeploymentVerification" or "Completed");
    }

    [Theory]
    [InlineData("RequirementsDetermined", "RequirementsIdentified")]
    [InlineData("AdditionalReviewRequired", "ReviewObligationsIdentified")]
    [InlineData("Prohibited", "Prohibited")]
    [InlineData("Unresolved", "AssessmentUnresolved")]
    public async Task Valid_policy_outcome_is_bound_to_expected_operation_and_preserves_meaning(
        string outcome,
        string expectedState)
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync($"AST-OUTCOME-{outcome}", Material, Now);
        var handler = new DecisionApiHandler(snapshot => CreatePersistedResponse(snapshot, outcome));
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);

        var completed = await coordinator.ResumeAsync(release.ReleaseId);
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{completed.WorkflowInstanceId}");
        var operations = await stateHost.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{completed.WorkflowInstanceId}/expected-operations");

        Assert.Equal(expectedState, instance.GetProperty("currentState").GetString());
        Assert.NotNull(operations);
        var operation = operations.Single(item =>
            item.GetProperty("operationId").GetString() == completed.Work.ExpectedOperationId);
        Assert.Equal("Resolved", operation.GetProperty("status").GetString());
        Assert.Equal(1, operation.GetProperty("expectedRevision").GetInt64());
        Assert.Equal("Accepted", completed.Status);
        Assert.NotNull(completed.Work.OriginalResponseJson);
        Assert.NotNull(completed.Work.OriginalEvidenceJson);
        Assert.Equal(expectedState, instance.GetProperty("currentState").GetString());
        Assert.False(instance.GetProperty("currentState").GetString() is
            "ReadyForDeployment" or "DeploymentInProgress" or "Completed");
        Assert.Single(handler.Requests);
        Assert.Equal(completed.Work.DecisionIdempotencyKey, handler.Requests[0].IdempotencyKey);
    }

    [Fact]
    public async Task V4_assessment_records_four_generation_bound_applicability_concerns()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync(
            "AST-V4-APPLICABILITY-001",
            Material,
            Now,
            "asteria-release-governance",
            "4.0");
        var handler = new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired"));
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v4.json"));
        var coordinator = new AsteriaAssessmentCoordinator(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);

        var assessed = await coordinator.ResumeAsync(release.ReleaseId);
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}");
        var concerns = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}/concerns");
        var operations = await stateHost.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{assessed.WorkflowInstanceId}/expected-operations")
            ?? throw new InvalidDataException("Expected operations were not returned.");

        Assert.Equal("4.0", instance.GetProperty("definitionVersion").GetString());
        Assert.Equal("ReviewObligationsIdentified", instance.GetProperty("currentState").GetString());
        Assert.Equal(2, instance.GetProperty("revision").GetInt64());
        Assert.Equal("Required", ConcernState(concerns, "FunctionalVerification"));
        Assert.Equal("Required", ConcernState(concerns, "SecurityVerification"));
        Assert.Equal("Required", ConcernState(concerns, "ReleaseReviewApproval"));
        Assert.Equal("NotRequired", ConcernState(concerns, "EmergencyApproval"));

        var applicabilityOperations = operations
            .Where(item => item.GetProperty("observationType").GetString() is
                "AsteriaFunctionalVerificationApplicability" or
                "AsteriaSecurityVerificationApplicability" or
                "AsteriaReleaseReviewApprovalApplicability" or
                "AsteriaEmergencyApprovalApplicability")
            .ToArray();
        Assert.Equal(4, applicabilityOperations.Length);
        Assert.All(applicabilityOperations, operation =>
        {
            Assert.Equal("Resolved", operation.GetProperty("status").GetString());
            Assert.Equal(1, operation.GetProperty("expectedRevision").GetInt64());
        });
        Assert.Equal(2, concerns.GetProperty("parentRevision").GetInt64());

        var amendmentCoordinator = new AsteriaAmendmentCoordinator(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            definitionJson,
            TimeProvider.System);
        var amendment = await amendmentCoordinator.AmendAsync(
            release.ReleaseId,
            "AST-V4-APPLICABILITY-AMEND-001",
            1,
            Material with { TargetVersion = "4.3.1" });
        var oldGeneration = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}");
        var newGeneration = await registry.GetReleaseAsync(release.ReleaseId)
            ?? throw new InvalidDataException("Replacement generation was not activated.");
        var newInstance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{newGeneration.WorkflowInstanceId}");
        Assert.Equal("NewGenerationActivated", amendment.Phase);
        Assert.Equal("Superseded", oldGeneration.GetProperty("currentState").GetString());
        Assert.Equal("4.0", newGeneration.DefinitionVersion);
        Assert.Equal(2, newGeneration.AssessmentGeneration);
        Assert.Equal("Draft", newInstance.GetProperty("currentState").GetString());

        static string? ConcernState(JsonElement projection, string concernId) =>
            projection.GetProperty("concerns").EnumerateArray()
                .Single(item => item.GetProperty("concernId").GetString() == concernId)
                .GetProperty("currentState").GetString();
    }

    [Fact]
    public async Task V4_no_policy_marks_all_applicabilities_unresolved_without_completion()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync(
            "AST-V4-UNRESOLVED-001",
            Material,
            Now,
            "asteria-release-governance",
            "4.0");
        using var decisionHttp = new HttpClient(new DecisionApiHandler(CreateNoPolicyResponse))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v4.json"));
        var coordinator = new AsteriaAssessmentCoordinator(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);

        var assessed = await coordinator.ResumeAsync(release.ReleaseId);
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}");
        var concerns = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}/concerns");

        Assert.Null(assessed.Work.EvaluationId);
        Assert.Equal("AssessmentUnresolved", instance.GetProperty("currentState").GetString());
        Assert.Equal("ApplicabilityUnresolved", ConcernState(concerns, "FunctionalVerification"));
        Assert.Equal("ApplicabilityUnresolved", ConcernState(concerns, "SecurityVerification"));
        Assert.Equal("ApplicabilityUnresolved", ConcernState(concerns, "ReleaseReviewApproval"));
        Assert.Equal("ApplicabilityUnresolved", ConcernState(concerns, "EmergencyApproval"));

        static string? ConcernState(JsonElement projection, string concernId) =>
            projection.GetProperty("concerns").EnumerateArray()
                .Single(item => item.GetProperty("concernId").GetString() == concernId)
                .GetProperty("currentState").GetString();
    }

    [Fact]
    public async Task V4_readiness_guard_refuses_incomplete_obligations_and_public_command()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync(
            "AST-V4-READINESS-BLOCKED-001",
            Material,
            Now,
            "asteria-release-governance",
            "4.0");
        using var decisionHttp = new HttpClient(new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired")))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v4.json"));
        var coordinator = new AsteriaAssessmentCoordinator(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);
        var assessed = await coordinator.ResumeAsync(release.ReleaseId);
        var instanceUrl = $"/api/instances/{assessed.WorkflowInstanceId}";
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(instanceUrl);
        var revision = instance.GetProperty("revision").GetInt64();

        using var publicCommand = await stateHost.Client.PostAsJsonAsync(
            $"{instanceUrl}/commands",
            new { messageId = "asteria-public-readiness", type = "AsteriaReadinessConfirmed", expectedRevision = revision });
        using var publicResult = JsonDocument.Parse(await publicCommand.Content.ReadAsStringAsync());
        Assert.Equal(
            "Unexpected",
            publicResult.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal(
            "unexpected-command",
            publicResult.RootElement.GetProperty("message").GetProperty("reasonCode").GetString());

        const string operationId = "asteria-readiness-incomplete-operation";
        const string messageId = "asteria-readiness-incomplete-message";
        string[] requiredEvidence =
        [
            "releaseId",
            "assessmentGeneration",
            "materialDigest",
            "assessmentEvidenceDigest",
            "allObligationsEvidenceDigest",
            "readinessWorkId"
        ];
        using var registrationRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{instanceUrl}/expected-operations");
        registrationRequest.Headers.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        registrationRequest.Content = JsonContent.Create(new
        {
            operationId,
            observationType = "AsteriaReadinessConfirmed",
            sourceIdentity = "AsteriaAssessmentCoordinator",
            expectedRevision = revision,
            requiredEvidence
        });
        using var registration = await stateHost.Client.SendAsync(registrationRequest);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        using var readinessRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{instanceUrl}/observations");
        readinessRequest.Headers.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        readinessRequest.Content = JsonContent.Create(new
        {
            messageId,
            deliveryId = messageId,
            operationId,
            observationId = "asteria-readiness-incomplete-observation",
            type = "AsteriaReadinessConfirmed",
            expectedRevision = revision,
            correlationId = operationId,
            source = "AsteriaAssessmentCoordinator",
            payload = new { },
            facts = new { },
            evidence = new
            {
                releaseId = assessed.ReleaseId,
                assessmentGeneration = assessed.AssessmentGeneration,
                materialDigest = assessed.MaterialDigest,
                assessmentEvidenceDigest = "assessment",
                allObligationsEvidenceDigest = "incomplete",
                readinessWorkId = "readiness-incomplete"
            }
        });
        using var readiness = await stateHost.Client.SendAsync(readinessRequest);
        using var readinessResult = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal(
            "Rejected",
            readinessResult.RootElement.GetProperty("message").GetProperty("status").GetString());
        var finalInstance = await stateHost.Client.GetFromJsonAsync<JsonElement>(instanceUrl);
        Assert.Equal("ReviewObligationsIdentified", finalInstance.GetProperty("currentState").GetString());
    }

    [Theory]
    [InlineData("FunctionalVerification", "verify-functional", "Passed")]
    [InlineData("FunctionalVerification", "verify-functional", "Failed")]
    [InlineData("FunctionalVerification", "verify-functional", "Indeterminate")]
    [InlineData("SecurityVerification", "verify-security", "Passed")]
    [InlineData("SecurityVerification", "verify-security", "Failed")]
    [InlineData("SecurityVerification", "verify-security", "Indeterminate")]
    public async Task V5_verification_uses_distinct_harness_bindings_and_accepts_bound_business_result(
        string concernId,
        string operationKey,
        string businessResult)
    {
        using var stateHost = new StateMachineTestHost();
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var release = await registry.CreateReleaseAsync(
            "AST-V5-VERIFICATION-001", Material, Now, "asteria-release-governance", "5.0");
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));
        using var decisionHttp = new HttpClient(new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired")))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var assessment = new AsteriaAssessmentCoordinator(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);
        var assessed = await assessment.ResumeAsync(release.ReleaseId);

        var providerId = concernId == "FunctionalVerification"
            ? "11111111-1111-4111-8111-111111111111"
            : "33333333-3333-4333-8333-333333333333";
        var apiDescriptionId = concernId == "FunctionalVerification"
            ? "22222222-2222-4222-8222-222222222222"
            : "44444444-4444-4444-8444-444444444444";
        var handler = new VerificationHarnessHandler(request =>
        {
            var body = request.GetProperty("body");
            return JsonSerializer.Serialize(new
            {
                releaseId = body.GetProperty("releaseId").GetString(),
                assessmentGeneration = body.GetProperty("assessmentGeneration").GetInt32(),
                materialDigest = body.GetProperty("materialDigest").GetString(),
                workflowInstanceId = body.GetProperty("workflowInstanceId").GetString(),
                activityId = body.GetProperty("activityId").GetString(),
                expectedOperationId = body.GetProperty("expectedOperationId").GetString(),
                providerId,
                apiDescriptionVersionId = apiDescriptionId,
                operationKey,
                result = businessResult,
                evidenceId = "controlled-functional-evidence-1"
            });
        });
        using var harnessHttp = new HttpClient(handler) { BaseAddress = new Uri("http://harness.test/") };
        await using var ledger = new AsteriaActivityLedger(path);
        var verification = new AsteriaVerificationCoordinator(
            registry,
            ledger,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            harnessHttp,
            definitionJson,
            new Dictionary<string, AsteriaHarnessOperationBinding>(StringComparer.Ordinal)
            {
                [concernId] = new(
                    $"synthetic-{concernId}", providerId, apiDescriptionId, operationKey)
            },
            TimeProvider.System);

        var result = await verification.ExecuteAsync(assessed.ReleaseId, concernId);
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}");
        var concerns = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{assessed.WorkflowInstanceId}/concerns");

        Assert.Equal("Accepted", result.Status);
        Assert.Equal(businessResult, result.ValidatedOutcome);
        Assert.NotNull(result.HarnessExecutionId);
        Assert.NotNull(result.AcceptedAttemptId);
        Assert.NotNull(result.AcceptedHistoryId);
        Assert.Equal("ReviewObligationsIdentified", instance.GetProperty("currentState").GetString());
        Assert.Equal(2, instance.GetProperty("revision").GetInt64());
        Assert.Equal(businessResult, ConcernState(concerns, concernId));
        Assert.Equal(3, ConcernRevision(concerns, concernId));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task V5_verification_never_retries_ambiguous_harness_dispatch()
    {
        using var stateHost = new StateMachineTestHost();
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var release = await registry.CreateReleaseAsync(
            "AST-V5-VERIFICATION-UNKNOWN-001", Material, Now, "asteria-release-governance", "5.0");
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));
        using var decisionHttp = new HttpClient(new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired")))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var assessment = new AsteriaAssessmentCoordinator(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);
        var assessed = await assessment.ResumeAsync(release.ReleaseId);
        var handler = new VerificationHarnessHandler(
            _ => throw new HttpRequestException("Controlled lost dispatch acknowledgement."));
        using var harnessHttp = new HttpClient(handler) { BaseAddress = new Uri("http://harness.test/") };
        await using var ledger = new AsteriaActivityLedger(path);
        var verification = new AsteriaVerificationCoordinator(
            registry,
            ledger,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            harnessHttp,
            definitionJson,
            new Dictionary<string, AsteriaHarnessOperationBinding>(StringComparer.Ordinal)
            {
                ["FunctionalVerification"] = new(
                    "synthetic-functional-provider",
                    "11111111-1111-4111-8111-111111111111",
                    "22222222-2222-4222-8222-222222222222",
                    "verify-functional")
            },
            TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            verification.ExecuteAsync(assessed.ReleaseId, "FunctionalVerification"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verification.ExecuteAsync(assessed.ReleaseId, "FunctionalVerification"));
        var activity = Assert.Single(await ledger.ListGenerationAsync(assessed.ReleaseId, 1));

        Assert.Equal("OutcomeUnknown", activity.Status);
        Assert.Null(activity.ValidatedOutcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("malformed-json")]
    [InlineData("wrong-release")]
    [InlineData("wrong-generation")]
    [InlineData("wrong-activity")]
    [InlineData("unsupported-result")]
    [InlineData("missing-evidence")]
    public async Task V5_verification_refuses_malformed_or_mismatched_provider_evidence(string invalidCase)
    {
        using var stateHost = new StateMachineTestHost();
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var release = await registry.CreateReleaseAsync(
            $"AST-V5-VERIFICATION-INVALID-{invalidCase}",
            Material,
            Now,
            "asteria-release-governance",
            "5.0");
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));
        using var decisionHttp = new HttpClient(new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired")))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var stateMachine = new AsteriaStateMachineClient(stateHost.Client, TrustedKey);
        var assessment = new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);
        var assessed = await assessment.ResumeAsync(release.ReleaseId);
        const string providerId = "11111111-1111-4111-8111-111111111111";
        const string apiDescriptionId = "22222222-2222-4222-8222-222222222222";
        var handler = new VerificationHarnessHandler(request =>
        {
            var body = request.GetProperty("body");
            if (invalidCase == "malformed-json")
            {
                return "{";
            }
            var response = new JsonObject
            {
                ["releaseId"] = body.GetProperty("releaseId").GetString(),
                ["assessmentGeneration"] = body.GetProperty("assessmentGeneration").GetInt32(),
                ["materialDigest"] = body.GetProperty("materialDigest").GetString(),
                ["workflowInstanceId"] = body.GetProperty("workflowInstanceId").GetString(),
                ["activityId"] = body.GetProperty("activityId").GetString(),
                ["expectedOperationId"] = body.GetProperty("expectedOperationId").GetString(),
                ["result"] = "Passed",
                ["evidenceId"] = "controlled-verification-evidence"
            };
            switch (invalidCase)
            {
                case "wrong-release":
                    response["releaseId"] = "AST-OTHER-RELEASE";
                    break;
                case "wrong-generation":
                    response["assessmentGeneration"] = 2;
                    break;
                case "wrong-activity":
                    response["activityId"] = Guid.NewGuid().ToString();
                    break;
                case "unsupported-result":
                    response["result"] = "Skipped";
                    break;
                case "missing-evidence":
                    response.Remove("evidenceId");
                    break;
            }
            return response.ToJsonString();
        });
        using var harnessHttp = new HttpClient(handler) { BaseAddress = new Uri("http://harness.test/") };
        await using var ledger = new AsteriaActivityLedger(path);
        var verification = new AsteriaVerificationCoordinator(
            registry,
            ledger,
            stateMachine,
            harnessHttp,
            definitionJson,
            new Dictionary<string, AsteriaHarnessOperationBinding>(StringComparer.Ordinal)
            {
                ["FunctionalVerification"] = new(
                    "synthetic-functional-provider",
                    providerId,
                    apiDescriptionId,
                    "verify-functional")
            },
            TimeProvider.System);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            verification.ExecuteAsync(assessed.ReleaseId, "FunctionalVerification"));

        var activity = Assert.Single(await ledger.ListGenerationAsync(assessed.ReleaseId, 1));
        Assert.Equal("Refused", activity.Status);
        Assert.Null(activity.ValidatedOutcome);
        Assert.Equal(1, handler.RequestCount);
        var concerns = await stateMachine.GetConcernStatesAsync(assessed.WorkflowInstanceId);
        Assert.Equal("Requested", concerns["FunctionalVerification"].CurrentState);
    }

    [Fact]
    public async Task Approval_gateway_authenticates_roles_and_preserves_immutable_decisions()
    {
        using var stateHost = new StateMachineTestHost();
        var path = NewDatabasePath();
        await using var releaseRegistry = new AsteriaReleaseRegistry(path);
        var release = await releaseRegistry.CreateReleaseAsync(
            "AST-V5-APPROVAL-001", Material, Now, "asteria-release-governance", "5.0");
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));
        using var decisionHttp = new HttpClient(new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired")))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var assessment = new AsteriaAssessmentCoordinator(
            releaseRegistry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System);
        var assessed = await assessment.ResumeAsync(release.ReleaseId);

        await using var activityLedger = new AsteriaActivityLedger(path);
        await activityLedger.InitializeAsync();
        var approvalRegistry = new AsteriaApprovalRegistry(path);
        await approvalRegistry.InitializeAsync();
        const string reviewerToken = "synthetic-reviewer-token-with-more-than-32-characters";
        const string emergencyToken = "synthetic-emergency-token-with-more-than-32-characters";
        var credentialRegistry = new AsteriaApprovalCredentialRegistry(
            "synthetic-authority-v1",
            [
                new("reviewer-1", "ReleaseReviewer", reviewerToken),
                new("emergency-authoriser-1", "EmergencyReleaseAuthoriser", emergencyToken)
            ]);
        var stateMachine = new AsteriaStateMachineClient(stateHost.Client, TrustedKey);
        var gateway = new AsteriaApprovalGateway(
            releaseRegistry,
            activityLedger,
            approvalRegistry,
            credentialRegistry,
            stateMachine,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = definitionJson });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        gateway.MapEndpoints(app);
        await app.StartAsync();
        var gatewayHttp = app.GetTestClient();

        using var unauthenticated = await gatewayHttp.PostAsJsonAsync(
            "/approvals",
            new { releaseId = assessed.ReleaseId });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var spoofedRequest = new HttpRequestMessage(HttpMethod.Post, "/approvals")
        {
            Content = JsonContent.Create(new
            {
                releaseId = assessed.ReleaseId,
                assessmentGeneration = assessed.AssessmentGeneration,
                materialDigest = assessed.MaterialDigest,
                concernId = "ReleaseReviewApproval",
                submissionId = "spoofed-submission",
                idempotencyKey = "spoofed-idempotency",
                decision = "Approved",
                evidenceReference = "controlled-review",
                principalId = "spoofed-principal",
                role = "ReleaseReviewer"
            })
        };
        spoofedRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reviewerToken);
        using var spoofed = await gatewayHttp.SendAsync(spoofedRequest);
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);

        var wrongRoleRequest = new AsteriaApprovalSubmission(
            assessed.ReleaseId,
            assessed.AssessmentGeneration,
            assessed.MaterialDigest,
            "EmergencyApproval",
            "wrong-role-submission",
            "wrong-role-idempotency",
            "Approved",
            "controlled-emergency-review");
        using var wrongRole = new HttpRequestMessage(HttpMethod.Post, "/approvals")
        {
            Content = JsonContent.Create(wrongRoleRequest)
        };
        wrongRole.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reviewerToken);
        using var forbidden = await gatewayHttp.SendAsync(wrongRole);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var submission = new AsteriaApprovalSubmission(
            assessed.ReleaseId,
            assessed.AssessmentGeneration,
            assessed.MaterialDigest,
            "ReleaseReviewApproval",
            "review-submission-1",
            "review-idempotency-1",
            "Approved",
            "controlled-review-evidence-1");
        using var approvalRequest = new HttpRequestMessage(HttpMethod.Post, "/approvals")
        {
            Content = JsonContent.Create(submission)
        };
        approvalRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reviewerToken);
        using var approved = await gatewayHttp.SendAsync(approvalRequest);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var accepted = await approved.Content.ReadFromJsonAsync<AsteriaApprovalResponse>();
        Assert.NotNull(accepted);
        Assert.Equal("Accepted", accepted.Activity.Status);
        Assert.Equal("reviewer-1", accepted.Decision.PrincipalId);
        Assert.Equal("ReleaseReviewer", accepted.Decision.AuthorityRole);
        Assert.Equal("synthetic-authority-v1", accepted.Decision.AuthorityRegistryVersion);
        Assert.Equal("Approved", accepted.Decision.Decision);

        var retry = new AsteriaApprovalSubmission(
            assessed.ReleaseId,
            assessed.AssessmentGeneration,
            assessed.MaterialDigest,
            "ReleaseReviewApproval",
            "review-submission-1",
            "review-idempotency-1",
            "Approved",
            "controlled-review-evidence-1");
        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, "/approvals")
        {
            Content = JsonContent.Create(retry)
        };
        retryRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reviewerToken);
        using var retryResponse = await gatewayHttp.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        var repeated = await retryResponse.Content.ReadFromJsonAsync<AsteriaApprovalResponse>();
        Assert.NotNull(repeated);
        Assert.Equal(accepted.Decision.DecisionId, repeated.Decision.DecisionId);
        Assert.Equal(accepted.Decision.DecisionAt, repeated.Decision.DecisionAt);

        var conflict = retry with { Decision = "Rejected" };
        using var conflictRequest = new HttpRequestMessage(HttpMethod.Post, "/approvals")
        {
            Content = JsonContent.Create(conflict)
        };
        conflictRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reviewerToken);
        using var conflictResponse = await gatewayHttp.SendAsync(conflictRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        var concernStates = await stateMachine.GetConcernStatesAsync(assessed.WorkflowInstanceId);
        Assert.Equal("Approved", concernStates["ReleaseReviewApproval"].CurrentState);
        Assert.Equal("NotRequired", concernStates["EmergencyApproval"].CurrentState);
        Assert.Equal(1, (await activityLedger.ListGenerationAsync(assessed.ReleaseId, 1))
            .Count(activity => activity.Status == "Accepted"));
    }

    [Theory]
    [InlineData(false, "Passed", "none", false, false, false, "Succeeded")]
    [InlineData(true, "Passed", "none", false, false, false, "Succeeded")]
    [InlineData(false, "Failed", "none", false, false, false, "Succeeded")]
    [InlineData(false, "Indeterminate", "none", false, false, false, "Succeeded")]
    [InlineData(false, "Passed", "wrong-deployment", false, false, false, "Succeeded")]
    [InlineData(false, "Passed", "wrong-version", false, false, false, "Succeeded")]
    [InlineData(false, "Passed", "none", true, false, false, "Succeeded")]
    [InlineData(false, "Passed", "none", false, true, false, "Succeeded")]
    [InlineData(true, "Passed", "none", false, false, true, "Succeeded")]
    [InlineData(false, "Passed", "none", false, false, false, "Failed")]
    [InlineData(false, "Passed", "none", false, false, false, "Pending")]
    public async Task Readiness_and_post_deployment_results_preserve_current_generation_evidence(
        bool loseDeploymentAcknowledgement,
        string healthOutcome,
        string healthMismatch,
        bool cancelAfterDeployment,
        bool cancelAtReadiness,
        bool restartAfterLostDeploymentAcknowledgement,
        string deploymentTerminalStatus)
    {
        using var stateHost = new StateMachineTestHost();
        var path = NewDatabasePath();
        await using var releaseRegistry = new AsteriaReleaseRegistry(path);
        var release = await releaseRegistry.CreateReleaseAsync(
            "AST-V5-READINESS-001", Material, Now, "asteria-release-governance", "5.0");
        await using var activityLedger = new AsteriaActivityLedger(path);
        await activityLedger.InitializeAsync();
        await using var deadlineLedger = new AsteriaDeadlineLedger(path);
        await deadlineLedger.InitializeAsync();
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));
        using var decisionHttp = new HttpClient(new DecisionApiHandler(snapshot =>
        {
            var response = JsonNode.Parse(
                CreatePersistedResponse(snapshot, "AdditionalReviewRequired"))!;
            response["emergencyApproval"]!["status"] = "Required";
            return response.ToJsonString();
        }))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var stateMachine = new AsteriaStateMachineClient(stateHost.Client, TrustedKey);
        var deadlineCoordinator = new AsteriaDeadlineCoordinator(
            releaseRegistry,
            activityLedger,
            deadlineLedger,
            stateMachine,
            new FixedTimeProvider(new DateTimeOffset(2027, 3, 16, 0, 0, 0, TimeSpan.Zero)));
        var assessment = new AsteriaAssessmentCoordinator(
            releaseRegistry,
            stateMachine,
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson,
            TimeProvider.System,
            deadlineCoordinator);
        var assessed = await assessment.ResumeAsync(release.ReleaseId);
        var verificationDeadline = Assert.Single(await deadlineLedger.ListGenerationAsync(
            assessed.ReleaseId, assessed.AssessmentGeneration));
        Assert.Equal("VerificationEscalation", verificationDeadline.DeadlineKind);
        Assert.Equal(Material.PlannedDeploymentAt.AddHours(-48), verificationDeadline.DueAt);
        var bindings = new Dictionary<string, AsteriaHarnessOperationBinding>(StringComparer.Ordinal)
        {
            ["FunctionalVerification"] = new(
                "synthetic-functional-provider",
                "11111111-1111-4111-8111-111111111111",
                "22222222-2222-4222-8222-222222222222",
                "verify-functional"),
            ["SecurityVerification"] = new(
                "synthetic-security-provider",
                "33333333-3333-4333-8333-333333333333",
                "44444444-4444-4444-8444-444444444444",
                "verify-security")
        };
        var harnessHandler = new VerificationHarnessHandler(request =>
        {
            var body = request.GetProperty("body");
            var security = request.GetProperty("operationKey").GetString() == "verify-security";
            return JsonSerializer.Serialize(new
            {
                releaseId = body.GetProperty("releaseId").GetString(),
                assessmentGeneration = body.GetProperty("assessmentGeneration").GetInt32(),
                materialDigest = body.GetProperty("materialDigest").GetString(),
                workflowInstanceId = body.GetProperty("workflowInstanceId").GetString(),
                activityId = body.GetProperty("activityId").GetString(),
                expectedOperationId = body.GetProperty("expectedOperationId").GetString(),
                providerId = security
                    ? "33333333-3333-4333-8333-333333333333"
                    : "11111111-1111-4111-8111-111111111111",
                apiDescriptionVersionId = security
                    ? "44444444-4444-4444-8444-444444444444"
                    : "22222222-2222-4222-8222-222222222222",
                operationKey = security ? "verify-security" : "verify-functional",
                result = "Passed",
                evidenceId = $"controlled-{(security ? "security" : "functional")}-evidence"
            });
        });
        using var harnessHttp = new HttpClient(harnessHandler) { BaseAddress = new Uri("http://harness.test/") };
        var verification = new AsteriaVerificationCoordinator(
            releaseRegistry,
            activityLedger,
            stateMachine,
            harnessHttp,
            definitionJson,
            bindings,
            TimeProvider.System);
        _ = await verification.ExecuteAsync(assessed.ReleaseId, "FunctionalVerification");
        _ = await verification.ExecuteAsync(assessed.ReleaseId, "SecurityVerification");

        var approvalRegistry = new AsteriaApprovalRegistry(path);
        await approvalRegistry.InitializeAsync();
        var credentialRegistry = new AsteriaApprovalCredentialRegistry(
            "synthetic-authority-v1",
            [
                new("reviewer-1", "ReleaseReviewer", "synthetic-reviewer-token-with-more-than-32-characters"),
                new("emergency-authoriser-1", "EmergencyReleaseAuthoriser", "synthetic-emergency-token-with-more-than-32-characters")
            ]);
        Assert.True(credentialRegistry.TryAuthenticate(
            "synthetic-reviewer-token-with-more-than-32-characters", out var reviewer));
        Assert.NotNull(reviewer);
        Assert.True(credentialRegistry.TryAuthenticate(
            "synthetic-emergency-token-with-more-than-32-characters", out var emergencyAuthoriser));
        Assert.NotNull(emergencyAuthoriser);
        var gateway = new AsteriaApprovalGateway(
            releaseRegistry,
            activityLedger,
            approvalRegistry,
            credentialRegistry,
            stateMachine,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = definitionJson });
        _ = await gateway.SubmitAsync(
            reviewer,
            new AsteriaApprovalSubmission(
                assessed.ReleaseId,
                assessed.AssessmentGeneration,
                assessed.MaterialDigest,
                "ReleaseReviewApproval",
                "readiness-review-submission",
                "readiness-review-idempotency",
                "Approved",
                "controlled-review-evidence"));
        _ = await gateway.SubmitAsync(
            emergencyAuthoriser,
            new AsteriaApprovalSubmission(
                assessed.ReleaseId,
                assessed.AssessmentGeneration,
                assessed.MaterialDigest,
                "EmergencyApproval",
                "readiness-emergency-submission",
                "readiness-emergency-idempotency",
                "Approved",
                "controlled-emergency-evidence"));

        var readiness = new AsteriaReadinessCoordinator(
            releaseRegistry,
            activityLedger,
            approvalRegistry,
            stateMachine,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = definitionJson },
            bindings,
            TimeProvider.System);
        var ready = await readiness.ConfirmAsync(assessed.ReleaseId);
        Assert.Equal("Accepted", ready.Status);
        Assert.Equal("Ready", ready.ValidatedOutcome);
        Assert.Equal("ReadyForDeployment", (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);

        var repeated = await readiness.ConfirmAsync(assessed.ReleaseId);
        Assert.Equal(ready.WorkId, repeated.WorkId);
        Assert.Equal(ready.ObservationReceiptId, repeated.ObservationReceiptId);
        Assert.Equal(ready.AcceptedHistoryId, repeated.AcceptedHistoryId);
        Assert.Equal(2, harnessHandler.RequestCount);
        if (cancelAtReadiness)
        {
            var cancellation = new AsteriaCancellationCoordinator(
                releaseRegistry, activityLedger, stateMachine);
            var cancelled = await cancellation.CancelAsync(assessed.ReleaseId);
            Assert.Equal("CancelledBeforeExternalDispatch", cancelled.Disposition);
            Assert.Equal("Cancelled", cancelled.ParentState);
            Assert.Equal("Requested", cancelled.CancellationIntentState);
            Assert.Equal(cancelled, await cancellation.CancelAsync(assessed.ReleaseId));
            Assert.DoesNotContain(
                await activityLedger.ListGenerationAsync(assessed.ReleaseId, assessed.AssessmentGeneration),
                item => item.ConcernId == "Deployment");
            return;
        }

        string? deploymentIdempotencyKey = null;
        var deploymentStatusLookups = 0;
        var deploymentMutationAttempts = 0;
        var deploymentHarness = new VerificationHarnessHandler(request =>
        {
            var operationKey = request.GetProperty("operationKey").GetString();
            if (operationKey == "requestDeployment")
            {
                deploymentMutationAttempts++;
                var body = request.GetProperty("body");
                deploymentIdempotencyKey = request.GetProperty("idempotencyKey").GetString();
                if (loseDeploymentAcknowledgement)
                {
                    throw new HttpRequestException("Controlled lost deployment acknowledgement.");
                }
                return JsonSerializer.Serialize(new
                {
                    deploymentId = "controlled-deployment-1",
                    idempotencyKey = deploymentIdempotencyKey,
                    releaseId = body.GetProperty("releaseId").GetString(),
                    assessmentGeneration = body.GetProperty("assessmentGeneration").GetInt32(),
                    materialDigest = body.GetProperty("materialDigest").GetString(),
                    product = body.GetProperty("product").GetString(),
                    targetVersion = body.GetProperty("targetVersion").GetString(),
                    environment = body.GetProperty("environment").GetString(),
                    status = "Pending",
                    lastUpdatedAt = Now.ToString("O")
                });
            }
            if (operationKey == "getDeploymentStatus" || operationKey == "lookupDeploymentByIdempotencyKey")
            {
                var lookupNumber = Interlocked.Increment(ref deploymentStatusLookups);
                if (restartAfterLostDeploymentAcknowledgement && lookupNumber == 1)
                {
                    throw new HttpRequestException("Controlled status lookup outage before coordinator restart.");
                }
                var status = deploymentTerminalStatus == "Pending"
                    ? "Pending"
                    : lookupNumber == 1
                        ? "Pending"
                        : deploymentTerminalStatus;
                return JsonSerializer.Serialize(new
                {
                    deploymentId = "controlled-deployment-1",
                    idempotencyKey = deploymentIdempotencyKey,
                    releaseId = assessed.ReleaseId,
                    assessmentGeneration = assessed.AssessmentGeneration,
                    materialDigest = assessed.MaterialDigest,
                    product = Material.Product,
                    targetVersion = Material.TargetVersion,
                    environment = Material.Environment,
                    status,
                    lastUpdatedAt = Now.AddMinutes(deploymentStatusLookups + 1).ToString("O"),
                    completedAt = status == "Succeeded" ? Now.AddMinutes(2).ToString("O") : null,
                    evidenceId = status == "Succeeded" ? "controlled-deployment-evidence-1" : null
                });
            }
            throw new InvalidOperationException($"Unexpected deployment operation '{operationKey}'.");
        }, HttpStatusCode.Accepted, request =>
            request.GetProperty("operationKey").GetString() == "requestDeployment"
                ? HttpStatusCode.Accepted
                : HttpStatusCode.OK);
        using var deploymentHarnessHttp = new HttpClient(deploymentHarness)
        {
            BaseAddress = new Uri("http://harness.test/")
        };
        var deploymentBindings = new AsteriaDeploymentHarnessBindings(
            new AsteriaHarnessOperationBinding(
                "synthetic-deployment-provider",
                "55555555-5555-4555-8555-555555555555",
                "66666666-6666-4666-8666-666666666666",
                "requestDeployment"),
            new AsteriaHarnessOperationBinding(
                "synthetic-deployment-provider",
                "55555555-5555-4555-8555-555555555555",
                "66666666-6666-4666-8666-666666666666",
                "getDeploymentStatus"),
            new AsteriaHarnessOperationBinding(
                "synthetic-deployment-provider",
                "55555555-5555-4555-8555-555555555555",
                "66666666-6666-4666-8666-666666666666",
                "lookupDeploymentByIdempotencyKey"));
        var deploymentCoordinator = new AsteriaDeploymentCoordinator(
            releaseRegistry,
            activityLedger,
            stateMachine,
            deploymentHarnessHttp,
            definitionJson,
            deploymentBindings,
            TimeProvider.System,
            deadlineCoordinator);
        if (deploymentTerminalStatus == "Pending")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                deploymentCoordinator.ExecuteAsync(assessed.ReleaseId));
            var unresolved = Assert.Single(
                await activityLedger.ListGenerationAsync(assessed.ReleaseId, assessed.AssessmentGeneration),
                item => item.ConcernId == "Deployment");
            Assert.Equal("OutcomeUnknown", unresolved.Status);
            Assert.Null(unresolved.ValidatedOutcome);
            Assert.Equal(1, deploymentMutationAttempts);
            Assert.Equal(100, deploymentStatusLookups);
            Assert.Equal(101, deploymentHarness.RequestCount);
            Assert.Equal(
                "DeploymentInProgress",
                (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);
            return;
        }
        AsteriaDeploymentResult deployed;
        if (restartAfterLostDeploymentAcknowledgement)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                deploymentCoordinator.ExecuteAsync(assessed.ReleaseId));
            var unknownDeployment = Assert.Single(
                await activityLedger.ListGenerationAsync(assessed.ReleaseId, assessed.AssessmentGeneration),
                item => item.ConcernId == "Deployment");
            Assert.Equal("OutcomeUnknown", unknownDeployment.Status);
            Assert.Equal(1, deploymentMutationAttempts);
            var restartedHarness = new VerificationHarnessHandler(request =>
            {
                var pathParameters = request.GetProperty("pathParameters");
                Assert.Equal(
                    "lookupDeploymentByIdempotencyKey",
                    request.GetProperty("operationKey").GetString());
                return JsonSerializer.Serialize(new
                {
                    deploymentId = "controlled-deployment-1",
                    idempotencyKey = pathParameters.GetProperty("idempotencyKey").GetString(),
                    releaseId = assessed.ReleaseId,
                    assessmentGeneration = assessed.AssessmentGeneration,
                    materialDigest = assessed.MaterialDigest,
                    product = Material.Product,
                    targetVersion = Material.TargetVersion,
                    environment = Material.Environment,
                    status = "Succeeded",
                    lastUpdatedAt = Now.AddMinutes(2).ToString("O"),
                    completedAt = Now.AddMinutes(2).ToString("O"),
                    evidenceId = "controlled-deployment-evidence-after-restart"
                });
            });
            using var restartedHarnessHttp = new HttpClient(restartedHarness)
            {
                BaseAddress = new Uri("http://harness.test/")
            };
            var restartedCoordinator = new AsteriaDeploymentCoordinator(
                releaseRegistry,
                activityLedger,
                stateMachine,
                restartedHarnessHttp,
                definitionJson,
                deploymentBindings,
                TimeProvider.System,
                deadlineCoordinator);
            deployed = await restartedCoordinator.ExecuteAsync(assessed.ReleaseId);
            Assert.Equal(1, restartedHarness.RequestCount);
        }
        else
        {
            deployed = await deploymentCoordinator.ExecuteAsync(assessed.ReleaseId);
        }
        if (deploymentTerminalStatus == "Failed")
        {
            Assert.Equal("Failed", deployed.Outcome);
            Assert.Equal("Accepted", deployed.Activity.Status);
            Assert.Equal(
                "DeploymentFailed",
                (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);
            var failedRetry = await deploymentCoordinator.ExecuteAsync(assessed.ReleaseId);
            Assert.Equal(deployed.Activity.ObservationReceiptId, failedRetry.Activity.ObservationReceiptId);
            Assert.Equal(3, deploymentHarness.RequestCount);
            return;
        }
        Assert.Equal("Succeeded", deployed.Outcome);
        Assert.Equal("Accepted", deployed.Activity.Status);
        Assert.Equal(1, deploymentMutationAttempts);
        Assert.Equal(restartAfterLostDeploymentAcknowledgement ? 1 : 2, deploymentStatusLookups);
        Assert.Equal("PostDeploymentVerification", (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);
        Assert.Equal(restartAfterLostDeploymentAcknowledgement ? 2 : 3, deploymentHarness.RequestCount);
        var deploymentRetry = await deploymentCoordinator.ExecuteAsync(assessed.ReleaseId);
        Assert.Equal(deployed.Activity.WorkId, deploymentRetry.Activity.WorkId);
        Assert.Equal(deployed.Activity.ObservationReceiptId, deploymentRetry.Activity.ObservationReceiptId);
        Assert.Equal(restartAfterLostDeploymentAcknowledgement ? 2 : 3, deploymentHarness.RequestCount);
        var investigationDeadline = await deadlineCoordinator.EnsureDeploymentInvestigationAsync(assessed);
        Assert.Equal("DeploymentInvestigation", investigationDeadline.DeadlineKind);
        var deploymentStart = Assert.Single(await stateMachine.ListTransitionHistoryAsync(assessed.WorkflowInstanceId),
            item => item.GetProperty("transitionId").GetString() == "deployment-start-from-ready");
        Assert.Equal(
            DateTimeOffset.Parse(deploymentStart.GetProperty("occurredAt").GetString()!).AddMinutes(30),
            investigationDeadline.DueAt);

        var healthHarness = new VerificationHarnessHandler(request =>
        {
            var query = request.GetProperty("queryParameters");
            return JsonSerializer.Serialize(new
            {
                deploymentId = healthMismatch == "wrong-deployment"
                    ? "controlled-other-deployment"
                    : query.GetProperty("deploymentId").GetString(),
                observedVersion = healthMismatch == "wrong-version"
                    ? "9.9.9"
                    : query.GetProperty("targetVersion").GetString(),
                environment = query.GetProperty("environment").GetString(),
                result = healthOutcome,
                checkedAt = Now.AddMinutes(3).ToString("O"),
                evidenceId = "controlled-health-evidence-1"
            });
        });
        using var healthHarnessHttp = new HttpClient(healthHarness)
        {
            BaseAddress = new Uri("http://harness.test/")
        };
        var healthCoordinator = new AsteriaHealthVerificationCoordinator(
            releaseRegistry,
            activityLedger,
            stateMachine,
            healthHarnessHttp,
            definitionJson,
            new AsteriaHarnessOperationBinding(
                "synthetic-health-provider",
                "77777777-7777-4777-8777-777777777777",
                "88888888-8888-4888-8888-888888888888",
                "verifyDeployedApplicationHealth"),
            TimeProvider.System);
        if (cancelAfterDeployment)
        {
            var cancellation = new AsteriaCancellationCoordinator(
                releaseRegistry, activityLedger, stateMachine);
            var cancelled = await cancellation.CancelAsync(assessed.ReleaseId);
            Assert.Equal(
                "CancellationIntentRecorded; external deployment is not cancelled or rolled back.",
                cancelled.Disposition);
            Assert.Equal("PostDeploymentVerification", cancelled.ParentState);
            Assert.Equal("Requested", cancelled.CancellationIntentState);
            Assert.Equal(cancelled, await cancellation.CancelAsync(assessed.ReleaseId));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                healthCoordinator.ExecuteAsync(assessed.ReleaseId));
            Assert.DoesNotContain(
                await activityLedger.ListGenerationAsync(assessed.ReleaseId, assessed.AssessmentGeneration),
                item => item.ConcernId == "HealthVerification");
            Assert.Equal(0, healthHarness.RequestCount);
            Assert.Equal(
                "PostDeploymentVerification",
                (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);
            return;
        }
        if (healthMismatch != "none")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                healthCoordinator.ExecuteAsync(assessed.ReleaseId));
            var refusedHealth = Assert.Single(
                await activityLedger.ListGenerationAsync(assessed.ReleaseId, assessed.AssessmentGeneration),
                item => item.ConcernId == "HealthVerification");
            Assert.Equal("Refused", refusedHealth.Status);
            Assert.Equal(
                "PostDeploymentVerification",
                (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                healthCoordinator.ExecuteAsync(assessed.ReleaseId));
            Assert.Equal(1, healthHarness.RequestCount);
            return;
        }
        var health = await healthCoordinator.ExecuteAsync(assessed.ReleaseId);
        Assert.Equal(healthOutcome, health.Outcome);
        Assert.Equal("Accepted", health.Activity.Status);
        var expectedFinalState = healthOutcome switch
        {
            "Passed" => "Completed",
            "Failed" => "HealthFailed",
            _ => "HealthUnresolved"
        };
        Assert.Equal(expectedFinalState, (await stateMachine.GetInstanceAsync(assessed.WorkflowInstanceId))!.CurrentState);
        Assert.Equal(healthOutcome, (await stateMachine.GetConcernStatesAsync(assessed.WorkflowInstanceId))
            .Single(item => item.Key == "HealthVerification").Value.CurrentState);
        var healthRetry = await healthCoordinator.ExecuteAsync(assessed.ReleaseId);
        Assert.Equal(health.Activity.WorkId, healthRetry.Activity.WorkId);
        Assert.Equal(health.Activity.ObservationReceiptId, healthRetry.Activity.ObservationReceiptId);
        Assert.Equal(1, healthHarness.RequestCount);
        var dueDeadlines = await deadlineCoordinator.ProcessDueAsync(assessed.ReleaseId);
        Assert.Equal(2, dueDeadlines.Count);
        Assert.Contains(dueDeadlines, item => item.ProcessingOutcome == "NoEscalationRequired");
        Assert.Contains(dueDeadlines, item => item.ProcessingOutcome == "ResultAlreadyAccepted");
        Assert.Empty(await deadlineCoordinator.ProcessDueAsync(assessed.ReleaseId));
    }

    [Fact]
    public async Task Cancellation_before_dispatch_is_terminal_and_idempotent()
    {
        using var stateHost = new StateMachineTestHost();
        var path = NewDatabasePath();
        await using var releaseRegistry = new AsteriaReleaseRegistry(path);
        var release = await releaseRegistry.CreateReleaseAsync(
            "AST-V5-CANCEL-BEFORE-DISPATCH-001", Material, Now, "asteria-release-governance", "5.0");
        var definitionJson = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));
        using var decisionHttp = new HttpClient(new DecisionApiHandler(
            snapshot => CreatePersistedResponse(snapshot, "AdditionalReviewRequired")))
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var stateMachine = new AsteriaStateMachineClient(stateHost.Client, TrustedKey);
        var assessment = await new AsteriaAssessmentCoordinator(
            releaseRegistry,
            stateMachine,
            new AsteriaDecisionApiClient(decisionHttp),
            definitionJson).ResumeAsync(release.ReleaseId);
        await using var activityLedger = new AsteriaActivityLedger(path);
        await activityLedger.InitializeAsync();
        var cancellation = new AsteriaCancellationCoordinator(
            releaseRegistry, activityLedger, stateMachine);

        var result = await cancellation.CancelAsync(assessment.ReleaseId);

        Assert.Equal("CancelledBeforeExternalDispatch", result.Disposition);
        Assert.Equal("Cancelled", result.ParentState);
        Assert.Equal("Requested", result.CancellationIntentState);
        var repeated = await cancellation.CancelAsync(assessment.ReleaseId);
        Assert.Equal(result, repeated);
        Assert.Empty(await activityLedger.ListGenerationAsync(
            assessment.ReleaseId, assessment.AssessmentGeneration));
    }

    [Fact]
    public async Task No_applicable_policy_is_durable_unresolved_without_fabricated_identifiers()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-NO-POLICY-001", Material, Now);
        var handler = new DecisionApiHandler(CreateNoPolicyResponse);
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);

        var completed = await coordinator.ResumeAsync(release.ReleaseId);
        using var originalResponse = JsonDocument.Parse(completed.Work.OriginalResponseJson!);
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{completed.WorkflowInstanceId}");

        Assert.Null(completed.Work.EvaluationId);
        Assert.Null(completed.Work.OriginalEvidenceJson);
        Assert.Equal("[]", completed.Work.SelectedPolicyVersionsJson);
        Assert.Equal("Accepted", completed.Work.Status);
        Assert.Null(originalResponse.RootElement.GetProperty("evaluationId").GetString());
        Assert.Equal("AssessmentUnresolved", instance.GetProperty("currentState").GetString());
        Assert.False(originalResponse.RootElement.GetProperty("deploymentAuthorised").GetBoolean());
        var repeated = await coordinator.ResumeAsync(release.ReleaseId);
        Assert.Equal(completed.Work.OriginalResponseJson, repeated.Work.OriginalResponseJson);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("releaseId")]
    [InlineData("generation")]
    [InlineData("digest")]
    public async Task Incorrect_decision_binding_is_refused_before_observation(string changedField)
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync($"AST-BINDING-{changedField}", Material, Now);
        var handler = new DecisionApiHandler(snapshot =>
        {
            var node = JsonNode.Parse(CreatePersistedResponse(snapshot, "RequirementsDetermined"))!;
            var binding = node["binding"]!.AsObject();
            switch (changedField)
            {
                case "releaseId":
                    binding["releaseId"] = "AST-WRONG-RELEASE";
                    break;
                case "generation":
                    binding["assessmentGeneration"] = 2;
                    break;
                case "digest":
                    binding["materialDigest"] = "sha256:" + new string('0', 64);
                    break;
            }
            return node.ToJsonString();
        });
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ResumeAsync(release.ReleaseId));
        var current = await registry.GetReleaseAsync(release.ReleaseId);
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{release.WorkflowInstanceId}");
        var operations = await stateHost.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{release.WorkflowInstanceId}/expected-operations");

        Assert.NotNull(current);
        Assert.Null(current.Work.OriginalResponseJson);
        Assert.Equal("UnderAssessment", instance.GetProperty("currentState").GetString());
        Assert.Equal("Expected", Assert.Single(operations!).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Persisted_decision_is_recovered_after_response_loss_using_same_key_and_payload()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-LOST-RESPONSE-001", Material, Now);
        var handler = new DecisionApiHandler(snapshot => CreatePersistedResponse(snapshot, "RequirementsDetermined"))
        {
            LoseFirstResponseAfterPersistence = true
        };
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);

        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.ResumeAsync(release.ReleaseId));
        var beforeRecovery = await registry.GetReleaseAsync(release.ReleaseId);
        Assert.Equal("DecisionRequested", beforeRecovery!.Work.Status);
        Assert.Null(beforeRecovery.Work.OriginalResponseJson);

        var recovered = await coordinator.ResumeAsync(release.ReleaseId);
        Assert.Equal("Accepted", recovered.Status);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].IdempotencyKey, handler.Requests[1].IdempotencyKey);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        Assert.NotNull(recovered.Work.EvaluationId);
    }

    [Fact]
    public async Task Accepted_observation_is_reconciled_after_coordinator_acknowledgement_is_lost()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-LOST-OBSERVATION-ACK-001", Material, Now);
        var decisionHandler = new DecisionApiHandler(snapshot =>
            CreatePersistedResponse(snapshot, "RequirementsDetermined"));
        using var decisionHttp = new HttpClient(decisionHandler) { BaseAddress = new Uri("http://decision.test/") };
        using var droppingClient = stateHost.CreateClient(new DropObservationAcknowledgementHandler());
        var firstCoordinator = new AsteriaAssessmentCoordinator(
            registry,
            new AsteriaStateMachineClient(droppingClient, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "asteria-release-governance-v2.json")));

        await Assert.ThrowsAsync<HttpRequestException>(() => firstCoordinator.ResumeAsync(release.ReleaseId));
        var pending = await registry.GetReleaseAsync(release.ReleaseId);
        Assert.Equal("ObservationSubmitted", pending!.Work.Status);
        Assert.NotNull(pending.Work.OriginalResponseJson);

        var recoveryCoordinator = CreateCoordinator(stateHost, registry, decisionHttp);
        var recovered = await recoveryCoordinator.ResumeAsync(release.ReleaseId);
        var receipts = await stateHost.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{recovered.WorkflowInstanceId}/inbound-messages");
        var operations = await stateHost.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{recovered.WorkflowInstanceId}/expected-operations");

        Assert.Equal("Accepted", recovered.Status);
        Assert.Single(decisionHandler.Requests);
        Assert.NotNull(receipts);
        Assert.Contains(receipts, item =>
            item.GetProperty("inputType").GetString() == AsteriaStateMachineClient.AssessmentObservation &&
            item.GetProperty("status").GetString() == "Accepted");
        Assert.Equal("Resolved", Assert.Single(operations!).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Changed_request_cannot_reuse_durable_assessment_work()
    {
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-CONFLICTING-REQUEST-001", Material, Now);
        var first = AsteriaDecisionApiClient.CreateCanonicalRequest(release);
        await registry.RecordDecisionRequestAsync(release.ReleaseId, first.Fingerprint, first.Json, Now);

        var changed = release with { Material = Material with { TargetVersion = "4.2.1" } };
        var second = AsteriaDecisionApiClient.CreateCanonicalRequest(changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.RecordDecisionRequestAsync(release.ReleaseId, second.Fingerprint, second.Json, Now));
    }

    [Fact]
    public async Task Existing_wrong_expected_operation_binding_is_not_reused()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-WRONG-OPERATION-001", Material, Now);
        var handler = new DecisionApiHandler(snapshot =>
            CreatePersistedResponse(snapshot, "RequirementsDetermined"));
        using var decisionHttp = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://decision.test/")
        };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);
        var prepared = await coordinator.PrepareAsync(release.ReleaseId, Material);
        using var badOperation = await stateHost.Client.PostAsJsonAsync(
            $"/api/instances/{prepared.WorkflowInstanceId}/expected-operations",
            new
            {
                operationId = prepared.Work.ExpectedOperationId,
                observationType = AsteriaStateMachineClient.AssessmentObservation,
                sourceIdentity = "untrusted-substitute",
                expectedRevision = prepared.Work.ParentRevision,
                requiredEvidence = Array.Empty<string>()
            });
        Assert.Equal(HttpStatusCode.Created, badOperation.StatusCode);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ResumeAsync(release.ReleaseId));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Wrong_workflow_and_stale_parent_revision_cannot_accept_assessment()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-WRONG-WORKFLOW-REVISION-001", Material, Now);
        var handler = new DecisionApiHandler(snapshot =>
            CreatePersistedResponse(snapshot, "RequirementsDetermined"));
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);
        var prepared = await coordinator.PrepareAsync(release.ReleaseId, Material);
        var stateMachine = new AsteriaStateMachineClient(stateHost.Client, TrustedKey);
        _ = await stateMachine.EnsureExpectedOperationAsync(prepared, prepared.Work.ParentRevision!.Value);
        var otherInstance = await stateMachine.EnsureInstanceAsync(prepared with
        {
            WorkflowInstanceId = "asteria-unrelated-" + Guid.NewGuid().ToString("N")
        });

        using var wrongWorkflow = await stateHost.Client.PostAsJsonAsync(
            $"/api/instances/{otherInstance.Id}/observations",
            new
            {
                messageId = "wrong-workflow-operation",
                operationId = prepared.Work.ExpectedOperationId,
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision = otherInstance.Revision,
                correlationId = prepared.Work.ExpectedOperationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity
            });
        using var wrongWorkflowBody = JsonDocument.Parse(await wrongWorkflow.Content.ReadAsStringAsync());
        Assert.Equal("Unexpected", wrongWorkflowBody.RootElement.GetProperty("message").GetProperty("status").GetString());

        using var cancellationRequest = await stateHost.Client.PostAsJsonAsync(
            $"/api/instances/{prepared.WorkflowInstanceId}/commands",
            new { messageId = "asteria-cancel-intent", type = "RequestCancellation", expectedRevision = 1L });
        Assert.Equal(HttpStatusCode.OK, cancellationRequest.StatusCode);
        using var cancellation = await stateHost.Client.PostAsJsonAsync(
            $"/api/instances/{prepared.WorkflowInstanceId}/commands",
            new { messageId = "asteria-cancel-parent", type = "CancelRelease", expectedRevision = 1L });
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);

        using var staleObservation = await stateHost.Client.PostAsJsonAsync(
            $"/api/instances/{prepared.WorkflowInstanceId}/observations",
            new
            {
                messageId = "asteria-stale-parent-result",
                operationId = prepared.Work.ExpectedOperationId,
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision = prepared.Work.ParentRevision,
                correlationId = prepared.Work.ExpectedOperationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity
            });
        using var staleBody = JsonDocument.Parse(await staleObservation.Content.ReadAsStringAsync());
        var current = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{prepared.WorkflowInstanceId}");

        Assert.Equal("Stale", staleBody.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Cancelled", current.GetProperty("currentState").GetString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Conflicting_observation_cannot_change_accepted_assessment()
    {
        using var stateHost = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync("AST-CONFLICTING-OBSERVATION-001", Material, Now);
        var handler = new DecisionApiHandler(snapshot => CreatePersistedResponse(snapshot, "RequirementsDetermined"));
        using var decisionHttp = new HttpClient(handler) { BaseAddress = new Uri("http://decision.test/") };
        var coordinator = CreateCoordinator(stateHost, registry, decisionHttp);
        var accepted = await coordinator.ResumeAsync(release.ReleaseId);

        using var selectedVersions = JsonDocument.Parse("[]");
        using var originalResponse = JsonDocument.Parse(accepted.Work.OriginalResponseJson!);
        using var conflict = await stateHost.Client.PostAsJsonAsync(
            $"/api/instances/{accepted.WorkflowInstanceId}/observations",
            new
            {
                messageId = "different-asteria-observation-delivery",
                operationId = accepted.Work.ExpectedOperationId,
                observationId = "different-observation",
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision = accepted.Work.ParentRevision,
                correlationId = accepted.Work.ExpectedOperationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity,
                facts = new { assessmentOutcome = "Prohibited" },
                payload = new { outcome = "Prohibited" },
                evidence = new
                {
                    releaseId = accepted.ReleaseId,
                    assessmentGeneration = accepted.AssessmentGeneration,
                    materialDigest = accepted.MaterialDigest,
                    parentRevision = accepted.Work.ParentRevision,
                    expectedOperationId = accepted.Work.ExpectedOperationId,
                    decisionRequestFingerprint = accepted.Work.DecisionRequestFingerprint,
                    selectedPolicySourceVersions = selectedVersions.RootElement.Clone(),
                    originalResponse = originalResponse.RootElement.Clone(),
                    policySelectionContext = new { }
                }
            });
        using var conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        var instance = await stateHost.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{accepted.WorkflowInstanceId}");

        Assert.Equal(HttpStatusCode.OK, conflict.StatusCode);
        Assert.Equal("Conflicting", conflictBody.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("RequirementsIdentified", instance.GetProperty("currentState").GetString());
    }

    private static AsteriaAssessmentCoordinator CreateCoordinator(
        StateMachineTestHost stateHost,
        AsteriaReleaseRegistry registry,
        HttpClient decisionHttp) =>
        new(
            registry,
            new AsteriaStateMachineClient(stateHost.Client, TrustedKey),
            new AsteriaDecisionApiClient(decisionHttp),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "asteria-release-governance-v2.json")),
            TimeProvider.System);

    private static string CreateNoPolicyResponse(AsteriaReleaseSnapshot release) =>
        JsonSerializer.Serialize(new
        {
            evaluationId = (string?)null,
            binding = new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest
            },
            outcome = "Unresolved",
            riskClassification = "Unresolved",
            functionalVerification = new { status = "Unresolved", clauseReferences = Array.Empty<string>() },
            securityVerification = new { status = "Unresolved", clauseReferences = Array.Empty<string>() },
            releaseReviewApproval = new { status = "Unresolved", clauseReferences = Array.Empty<string>() },
            emergencyApproval = new { status = "Unresolved", clauseReferences = Array.Empty<string>() },
            businessEffectiveAt = release.Work.BusinessEffectiveAt,
            knownAt = release.Work.KnownAt,
            factDigest = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            unresolvedFindings = new[] { "No policy version was applicable at the supplied temporal context." },
            selectedPolicySourceVersions = Array.Empty<object>(),
            evidence = (object?)null,
            deploymentAuthorised = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    internal static string CreatePersistedResponse(AsteriaReleaseSnapshot release, string outcome)
    {
        var material = release.Material;
        var facts = new Dictionary<string, (string Type, object Value)>(StringComparer.Ordinal)
        {
            ["releaseId"] = ("String", release.ReleaseId),
            ["assessmentGeneration"] = ("Integer", release.AssessmentGeneration),
            ["materialDigest"] = ("String", release.MaterialDigest),
            ["product"] = ("String", material.Product),
            ["targetVersion"] = ("String", material.TargetVersion),
            ["environment"] = ("String", material.Environment),
            ["releaseCategory"] = ("String", material.ReleaseCategory),
            ["changeSize"] = ("String", material.ChangeSize),
            ["authenticationChange"] = ("Boolean", material.AuthenticationChange),
            ["customerDataImpact"] = ("Boolean", material.CustomerDataImpact),
            ["releaseSummary"] = ("String", material.ReleaseSummary),
            ["plannedDeploymentAt"] = ("String", material.PlannedDeploymentAt.ToUniversalTime().ToString("O"))
        };
        var entries = facts.Select(item => new
        {
            name = item.Key,
            type = item.Value.Type,
            state = "Present",
            value = new
            {
                type = item.Value.Type,
                stringValue = item.Value.Type == "String" ? (string)item.Value.Value : null,
                integerValue = item.Value.Type == "Integer" ? (int)item.Value.Value : (int?)null,
                decimalValue = (decimal?)null,
                booleanValue = item.Value.Type == "Boolean" ? (bool)item.Value.Value : (bool?)null,
                dateValue = (string?)null,
                stringListValue = (string[]?)null
            }
        }).ToArray();

        const string versionId = "14000000-0000-0000-0000-000000000001";
        const string sourceDigest = "BC7148AAD26F095E18984B311D5AE1849CEA24C0099F85388B1C3ED73C0F8041";
        const string ruleDigest = "275E8D09297420C4C6BE340F95983D6461A6D0788CE6EF92F2A6870D9139247D";
        const string factDigest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var evaluatedAt = release.Work.KnownAt.AddSeconds(1);
        var policyVersion = new
        {
            sourceId = "AST-POL-001",
            versionId,
            versionNumber = 1,
            recordedAt = release.Work.KnownAt,
            effectiveFrom = "2027-02-01",
            effectiveTo = (string?)null,
            sourceDigest,
            ruleDigest
        };
        var evidence = new
        {
            evaluationId = "24000000-0000-0000-0000-000000000001",
            evaluatedAt,
            businessEffectiveAt = release.Work.BusinessEffectiveAt,
            knownAt = release.Work.KnownAt,
            factSet = new { entries, digestAlgorithm = "SHA-256", digest = factDigest },
            unknownFacts = Array.Empty<string>(),
            unavailableFacts = Array.Empty<string>(),
            ruleSources = new[]
            {
                new
                {
                    sourceId = "AST-POL-001",
                    policySourceVersionId = versionId,
                    versionNumber = 1,
                    recordedAt = release.Work.KnownAt,
                    effectiveFrom = "2027-02-01",
                    effectiveTo = (string?)null,
                    policySourceDigest = sourceDigest,
                    executableRuleDigest = ruleDigest
                }
            }
        };
        return JsonSerializer.Serialize(new
        {
            evaluationId = evidence.evaluationId,
            binding = new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest = release.MaterialDigest
            },
            outcome,
            riskClassification = outcome == "Unresolved" ? "Unresolved" : "HighRisk",
            functionalVerification = new { status = "Required", clauseReferences = new[] { "AST-POL-02" } },
            securityVerification = new { status = "Required", clauseReferences = new[] { "AST-POL-03" } },
            releaseReviewApproval = new { status = "Required", clauseReferences = new[] { "AST-POL-05" } },
            emergencyApproval = new { status = "NotRequired", clauseReferences = Array.Empty<string>() },
            businessEffectiveAt = release.Work.BusinessEffectiveAt,
            knownAt = release.Work.KnownAt,
            factDigest,
            unresolvedFindings = Array.Empty<string>(),
            selectedPolicySourceVersions = new[] { policyVersion },
            evidence,
            deploymentAuthorised = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"asteria-m3b-{Guid.NewGuid():N}.db");

    private static AsteriaActivityReservation CreateActivityReservation(
        AsteriaReleaseSnapshot release,
        string suffix) =>
        new(
            $"work-{suffix}",
            $"activity-{suffix}",
            release.ReleaseId,
            release.AssessmentGeneration,
            release.MaterialDigest,
            release.WorkflowInstanceId,
            release.DefinitionId,
            release.DefinitionVersion,
            AsteriaActivityLedger.Fingerprint(release.DefinitionVersion),
            "FunctionalVerification",
            "Verification",
            $"attempt-{suffix}",
            $"operation-{suffix}",
            release.Work.ParentRevision ?? 1,
            1,
            $"submission-{suffix}",
            $"idempotency-{suffix}",
            """{"provider":"synthetic"}""",
            "synthetic-provider",
            "api-description-v1",
            "verify",
            null,
            null,
            null);

    private static long ConcernRevision(JsonElement projection, string concernId) =>
        projection.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("revision").GetInt64();

    private static string? ConcernState(JsonElement projection, string concernId) =>
        projection.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("currentState").GetString();

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class VerificationHarnessHandler(
        Func<JsonElement, string> providerResponseFactory,
        HttpStatusCode providerStatusCode = HttpStatusCode.OK,
        Func<JsonElement, HttpStatusCode>? providerStatusCodeFactory = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Method != HttpMethod.Post ||
                request.RequestUri?.AbsolutePath != "/executions")
            {
                throw new HttpRequestException("Unexpected controlled Generic Harness route.");
            }
            using var requestDocument = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var requestRoot = requestDocument.RootElement;
            var providerBody = providerResponseFactory(requestRoot);
            var statusCode = providerStatusCodeFactory?.Invoke(requestRoot) ?? providerStatusCode;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new
                {
                    execution = new
                    {
                        id = Guid.NewGuid().ToString(),
                        state = 2,
                        providerId = requestRoot.GetProperty("providerId").GetString(),
                        apiDescriptionVersionId = requestRoot.GetProperty("apiDescriptionVersionId").GetString(),
                        operationKey = requestRoot.GetProperty("operationKey").GetString()
                    },
                    providerResponse = new
                    {
                        statusCode = (int)statusCode,
                        contentType = "application/json",
                        body = providerBody,
                        truncated = false
                    }
                })
            };
        }
    }

    private sealed class DecisionApiHandler(Func<AsteriaReleaseSnapshot, string> responseFactory) : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Body, string Response)> _saved = new(StringComparer.Ordinal);
        private bool _lostResponse;

        public bool LoseFirstResponseAfterPersistence { get; init; }
        public List<(string IdempotencyKey, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues("Idempotency-Key").Single();
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((key, body));
            if (_saved.TryGetValue(key, out var prior))
            {
                if (!string.Equals(prior.Body, body, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent(
                            """{"type":"idempotency_key_reused","title":"Conflict","detail":"The request changed."}""",
                            Encoding.UTF8,
                            "application/json")
                    };
                }
                if (LoseFirstResponseAfterPersistence && !_lostResponse)
                {
                    _lostResponse = true;
                    throw new HttpRequestException("Controlled response loss after persistence.");
                }
                return JsonResponse(prior.Response);
            }

            using var document = JsonDocument.Parse(body);
            var releaseId = document.RootElement.GetProperty("releaseId").GetString()!;
            var generation = document.RootElement.GetProperty("assessmentGeneration").GetInt32();
            var digest = document.RootElement.GetProperty("materialDigest").GetString()!;
            var work = new AsteriaAssessmentWork(
                "fake-work",
                releaseId,
                generation,
                digest,
                "fake-workflow-instance",
                "asteria-release-governance",
                "2.0",
                1,
                "fake-operation",
                key,
                document.RootElement.GetProperty("businessEffectiveAt").GetString()!,
                document.RootElement.GetProperty("knownAt").GetDateTimeOffset(),
                null,
                null,
                null,
                "[]",
                null,
                null,
                "DecisionRequested",
                "{}");
            var facts = document.RootElement.GetProperty("facts");
            var material = new AsteriaReleaseMaterial(
                facts.GetProperty("product").GetProperty("value").GetString()!,
                facts.GetProperty("targetVersion").GetProperty("value").GetString()!,
                facts.GetProperty("environment").GetProperty("value").GetString()!,
                facts.GetProperty("releaseCategory").GetProperty("value").GetString()!,
                facts.GetProperty("changeSize").GetProperty("value").GetString()!,
                facts.GetProperty("authenticationChange").GetProperty("value").GetBoolean(),
                facts.GetProperty("customerDataImpact").GetProperty("value").GetBoolean(),
                facts.GetProperty("releaseSummary").GetProperty("value").GetString()!,
                "test-owner",
                DateTimeOffset.Parse(facts.GetProperty("plannedDeploymentAt").GetProperty("value").GetString()!));
            var snapshot = new AsteriaReleaseSnapshot(
                releaseId, "fake-workflow-instance", "asteria-release-governance", "2.0",
                material, generation, digest, "fake-work", "Registered", work);
            var response = responseFactory(snapshot);
            _saved.Add(key, (body, response));
            if (LoseFirstResponseAfterPersistence && !_lostResponse)
            {
                _lostResponse = true;
                throw new HttpRequestException("Controlled response loss after persistence.");
            }
            return JsonResponse(response);
        }

        private static HttpResponseMessage JsonResponse(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
    }

    private sealed class StateMachineTestHost : IDisposable
    {
        private readonly string _databasePath = NewDatabasePath();
        private readonly string? _previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        private readonly string? _previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        private readonly WebApplicationFactory<Program> _factory;

        public StateMachineTestHost()
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", _databasePath);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
            _factory = new WebApplicationFactory<Program>();
            Client = _factory.CreateClient();
            Client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        }

        public HttpClient Client { get; }
        public HttpClient CreateClient(params DelegatingHandler[] handlers) =>
            _factory.CreateDefaultClient(handlers);

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", _previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", _previousKey);
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                var filePath = _databasePath + suffix;
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }
    }

    private sealed class DropObservationAcknowledgementHandler : DelegatingHandler
    {
        private bool _dropped;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!_dropped &&
                request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/observations", StringComparison.Ordinal) == true &&
                response.IsSuccessStatusCode)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var status = body.RootElement.GetProperty("message").GetProperty("status").GetString();
                if (status == "Accepted")
                {
                    _dropped = true;
                    response.Dispose();
                    throw new HttpRequestException("Controlled lost State Machine acknowledgement.");
                }
            }
            return response;
        }
    }
}
