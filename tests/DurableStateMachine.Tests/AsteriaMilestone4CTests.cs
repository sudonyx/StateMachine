using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DurableStateMachine.AsteriaClient;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class AsteriaMilestone4CTests
{
    private const string TrustedKey = "asteria-milestone4c-test-ingestion-key";
    private const string ReleaseId = "AST-GATE14-4C-001";
    private const string AmendmentId = "AST-GATE14-4C-AMEND-001";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly AsteriaReleaseMaterial InitialMaterial = new(
        "AsteriaPortal",
        "4.2.0",
        "production",
        "Standard",
        "Small",
        false,
        false,
        "Synthetic low-risk Standard release",
        "release-owner@example.invalid",
        new DateTimeOffset(2027, 3, 15, 10, 0, 0, TimeSpan.Zero));
    private static readonly AsteriaReleaseMaterial AmendedMaterial = InitialMaterial with
    {
        TargetVersion = "4.3.0",
        ReleaseCategory = "Emergency",
        ChangeSize = "Large",
        AuthenticationChange = true,
        CustomerDataImpact = true,
        ReleaseSummary = "Synthetic emergency release with authentication and customer-data impact"
    };

    [Fact]
    public async Task Two_generations_use_separate_material_evidence_and_terminal_history()
    {
        using var host = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var policyHandler = new ContractDecisionApiHandler();
        using var decisionHttp = CreateDecisionHttp(policyHandler);
        var decisionApi = new AsteriaDecisionApiClient(decisionHttp);
        var definition = ReadV3();
        var coordinator = new AsteriaAssessmentCoordinator(registry, stateMachine, decisionApi, definition);

        var generationOne = await coordinator.CreateAndAssessAsync(ReleaseId, InitialMaterial);
        var generationOneInstance = await stateMachine.InspectGenerationAsync(generationOne);
        using var generationOneResponse = JsonDocument.Parse(generationOne.Work.OriginalResponseJson!);
        Assert.Equal(1, generationOne.AssessmentGeneration);
        Assert.Equal("3.0", generationOne.DefinitionVersion);
        Assert.Equal("RequirementsIdentified", generationOneInstance.CurrentState);
        Assert.Equal("RequirementsDetermined",
            generationOneResponse.RootElement.GetProperty("outcome").GetString());
        Assert.NotNull(generationOne.Work.EvaluationId);

        var amendmentCoordinator = new AsteriaAmendmentCoordinator(registry, stateMachine, definition);
        var amendment = await amendmentCoordinator.AmendAsync(
            ReleaseId, AmendmentId, 1, AmendedMaterial);
        var currentAfterAmendment = await registry.GetReleaseAsync(ReleaseId);
        var generationOneSnapshot = await registry.GetGenerationAsync(ReleaseId, 1);
        var generationTwoReserved = await registry.GetGenerationAsync(ReleaseId, 2);

        Assert.Equal("NewGenerationActivated", amendment.Phase);
        Assert.NotNull(currentAfterAmendment);
        Assert.NotNull(generationOneSnapshot);
        Assert.NotNull(generationTwoReserved);
        Assert.Equal(2, currentAfterAmendment.AssessmentGeneration);
        Assert.Equal("Operational", currentAfterAmendment.RegistryState);
        Assert.NotEqual(generationOneSnapshot.MaterialDigest, generationTwoReserved.MaterialDigest);
        Assert.NotEqual(generationOneSnapshot.WorkflowInstanceId, generationTwoReserved.WorkflowInstanceId);
        Assert.Equal("Superseded", (await stateMachine.InspectGenerationAsync(generationOneSnapshot)).CurrentState);
        Assert.Equal("Draft", (await stateMachine.InspectGenerationAsync(generationTwoReserved)).CurrentState);
        Assert.Null(generationTwoReserved.Work.EvaluationId);
        Assert.Null(generationTwoReserved.Work.ParentRevision);

        using (var untrustedClient = host.CreateClient())
        using (var untrustedStart = await untrustedClient.PostAsJsonAsync(
                   $"/api/instances/{Uri.EscapeDataString(generationTwoReserved.WorkflowInstanceId)}/observations",
                   new
                   {
                       messageId = "untrusted-generation-two-start",
                       deliveryId = "untrusted-generation-two-start",
                       operationId = "untrusted-generation-two-start-operation",
                       observationId = "untrusted-generation-two-start-observation",
                       type = AsteriaStateMachineClient.AssessmentStartObservation,
                       expectedRevision = 0,
                       correlationId = "untrusted-generation-two-start-operation",
                       source = AsteriaStateMachineClient.AssessmentStartSourceIdentity,
                       payload = new { releaseId = ReleaseId },
                       facts = new { },
                       evidence = new { releaseId = ReleaseId, assessmentGeneration = 2, generationTwoReserved.MaterialDigest }
                   }))
        {
            Assert.True(untrustedStart.IsSuccessStatusCode);
            using var refusedStart = JsonDocument.Parse(await untrustedStart.Content.ReadAsStringAsync());
            Assert.NotEqual(
                "Accepted",
                refusedStart.RootElement.GetProperty("message").GetProperty("status").GetString());
        }
        Assert.Equal("Draft", (await stateMachine.InspectGenerationAsync(generationTwoReserved)).CurrentState);

        var generationTwo = await coordinator.ResumeAsync(ReleaseId);
        var generationTwoInstance = await stateMachine.InspectGenerationAsync(generationTwo);
        using var originalResponse = JsonDocument.Parse(generationTwo.Work.OriginalResponseJson!);
        var responseRoot = originalResponse.RootElement;
        Assert.Equal(2, generationTwo.AssessmentGeneration);
        Assert.Equal("AdditionalReviewRequired", responseRoot.GetProperty("outcome").GetString());
        Assert.Equal("HighRisk", responseRoot.GetProperty("riskClassification").GetString());
        Assert.False(responseRoot.GetProperty("deploymentAuthorised").GetBoolean());
        Assert.Equal("Required", responseRoot.GetProperty("functionalVerification").GetProperty("status").GetString());
        Assert.Equal("Required", responseRoot.GetProperty("securityVerification").GetProperty("status").GetString());
        Assert.Equal("Required", responseRoot.GetProperty("releaseReviewApproval").GetProperty("status").GetString());
        Assert.Equal("Required", responseRoot.GetProperty("emergencyApproval").GetProperty("status").GetString());
        Assert.NotEqual(generationOne.Work.EvaluationId, generationTwo.Work.EvaluationId);
        Assert.NotEqual(
            generationOne.Work.SelectedPolicyVersionsJson,
            generationTwo.Work.SelectedPolicyVersionsJson);
        Assert.Equal("ReviewObligationsIdentified", generationTwoInstance.CurrentState);

        var generationOneOperations = await stateMachine.ListExpectedOperationsAsync(generationOne.WorkflowInstanceId);
        var generationTwoOperations = await stateMachine.ListExpectedOperationsAsync(generationTwo.WorkflowInstanceId);
        Assert.NotEqual(generationOne.Work.ExpectedOperationId, generationTwo.Work.ExpectedOperationId);
        Assert.Contains(generationOneOperations, item =>
            item.OperationId == generationOne.Work.ExpectedOperationId && item.Status == "Resolved");
        Assert.Contains(generationTwoOperations, item =>
            item.OperationId == generationTwo.Work.ExpectedOperationId && item.Status == "Resolved");

        var generationOneHistory = await stateMachine.ListTransitionHistoryAsync(generationOne.WorkflowInstanceId);
        var generationTwoHistory = await stateMachine.ListTransitionHistoryAsync(generationTwo.WorkflowInstanceId);
        Assert.Contains(generationOneHistory, entry =>
            entry.GetProperty("event").GetString() == AsteriaStateMachineClient.SupersessionObservation &&
            entry.GetProperty("newState").GetString() == "Superseded");
        Assert.Contains(generationTwoHistory, entry =>
            entry.GetProperty("event").GetString() == AsteriaStateMachineClient.AssessmentObservation &&
            entry.GetProperty("newState").GetString() == "ReviewObligationsIdentified");

        foreach (var generation in new[] { generationOneSnapshot, generationTwo })
        {
            var replay = await host.Client.GetFromJsonAsync<JsonElement>(
                $"/api/instances/{Uri.EscapeDataString(generation.WorkflowInstanceId)}/replay/decision-verification");
            Assert.Equal("Verified", replay.GetProperty("decisionVerificationOutcome").GetString());
            var persisted = await registry.GetGenerationAsync(generation.ReleaseId, generation.AssessmentGeneration);
            Assert.Equal(generation.Work.OriginalResponseJson, persisted!.Work.OriginalResponseJson);
            Assert.Equal(generation.Work.SelectedPolicyVersionsJson, persisted.Work.SelectedPolicyVersionsJson);
        }

        var repeatedAssessment = await coordinator.ResumeAsync(ReleaseId);
        Assert.Equal(generationTwo.Work.EvaluationId, repeatedAssessment.Work.EvaluationId);
        Assert.Equal(2, repeatedAssessment.AssessmentGeneration);
        Assert.Null(await registry.GetGenerationAsync(ReleaseId, 3));
        Assert.Equal(2, policyHandler.Requests.Count);
        var supportedStates = TestSupport.LoadWorkflow("asteria-release-governance-v3.json").States.Keys;
        Assert.DoesNotContain(supportedStates, state =>
            state is "ReadyForDeployment" or "DeploymentInProgress" or "PostDeploymentVerification" or "Completed");
    }

    [Fact]
    public async Task A_delayed_generation_one_decision_cannot_write_into_or_advance_generation_two()
    {
        using var host = new StateMachineTestHost();
        var registryPath = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(registryPath);
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var definition = ReadV3();
        var initial = await new AsteriaAssessmentCoordinator(
            registry, stateMachine, new AsteriaDecisionApiClient(CreateDecisionHttp(new UnavailableHandler())), definition)
            .PrepareAsync("AST-GATE14-4C-DELAYED-001", InitialMaterial);
        var delayedHandler = new DelayedDecisionApiHandler(initial);
        using var delayedHttp = CreateDecisionHttp(delayedHandler);
        var pendingAssessment = new AsteriaAssessmentCoordinator(
            registry, stateMachine, new AsteriaDecisionApiClient(delayedHttp), definition)
            .ResumeAsync(initial.ReleaseId);
        await delayedHandler.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await ExpireOrdinaryCoordinatorLeaseAsync(registryPath, initial.ReleaseId);
        var amendment = await new AsteriaAmendmentCoordinator(registry, stateMachine, definition).AmendAsync(
            initial.ReleaseId,
            "AST-GATE14-4C-DELAYED-AMENDMENT",
            1,
            AmendedMaterial);
        Assert.Equal("NewGenerationActivated", amendment.Phase);
        delayedHandler.Complete();

        var delayedDecisionFailure = await Record.ExceptionAsync(() => pendingAssessment);
        Assert.IsType<InvalidOperationException>(delayedDecisionFailure);
        var oldGeneration = await registry.GetGenerationAsync(initial.ReleaseId, 1);
        var current = await registry.GetReleaseAsync(initial.ReleaseId);
        var newGeneration = await registry.GetGenerationAsync(initial.ReleaseId, 2);
        Assert.NotNull(oldGeneration);
        Assert.NotNull(current);
        Assert.NotNull(newGeneration);
        Assert.Null(oldGeneration.Work.OriginalResponseJson);
        Assert.Equal(2, current.AssessmentGeneration);
        Assert.Equal("Superseded", (await stateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);
        Assert.Equal("Draft", (await stateMachine.InspectGenerationAsync(newGeneration)).CurrentState);
        Assert.Null(newGeneration.Work.OriginalResponseJson);
    }

    [Fact]
    public async Task Stale_generation_one_operation_and_material_digest_are_refused_for_generation_two()
    {
        using var host = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var decisionHandler = new ContractDecisionApiHandler();
        using var decisionHttp = CreateDecisionHttp(decisionHandler);
        var definition = ReadV3();
        var coordinator = new AsteriaAssessmentCoordinator(
            registry, stateMachine, new AsteriaDecisionApiClient(decisionHttp), definition);
        var generationOne = await coordinator.CreateAndAssessAsync(
            "AST-GATE14-4C-STALE-001", InitialMaterial);
        _ = await new AsteriaAmendmentCoordinator(registry, stateMachine, definition).AmendAsync(
            generationOne.ReleaseId, "AST-GATE14-4C-STALE-AMENDMENT", 1, AmendedMaterial);
        var generationTwo = await registry.GetGenerationAsync(generationOne.ReleaseId, 2);
        Assert.NotNull(generationTwo);

        using var staleResponse = JsonDocument.Parse(CreatePersistedResponse(
            generationTwo,
            highRisk: true,
            usePreviousDigest: true,
            previousDigest: generationOne.MaterialDigest));
        Assert.Throws<InvalidDataException>(() =>
            AsteriaDecisionApiClient.ValidateResponse(staleResponse.RootElement.GetRawText(), generationTwo));

        using var reusedOperation = await host.Client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(generationTwo.WorkflowInstanceId)}/observations",
            new
            {
                messageId = "old-generation-operation-reuse",
                deliveryId = "old-generation-operation-reuse",
                operationId = generationOne.Work.ExpectedOperationId,
                observationId = "old-generation-operation-reuse-observation",
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision = 1,
                correlationId = generationOne.Work.ExpectedOperationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity,
                payload = new { releaseId = generationOne.ReleaseId },
                facts = new { assessmentOutcome = "AdditionalReviewRequired" },
                evidence = new
                {
                    releaseId = generationOne.ReleaseId,
                    assessmentGeneration = 1,
                    materialDigest = generationOne.MaterialDigest
                }
            });
        Assert.True(reusedOperation.IsSuccessStatusCode);
        using var refused = JsonDocument.Parse(await reusedOperation.Content.ReadAsStringAsync());
        Assert.NotEqual("Accepted", refused.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Draft", (await stateMachine.InspectGenerationAsync(generationTwo)).CurrentState);
    }

    [Fact]
    public async Task Missing_policy_remains_unresolved_and_historical_replay_keeps_versioned_evidence()
    {
        using var host = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var definition = ReadV3();
        var noPolicyHandler = new ContractDecisionApiHandler(returnNoPolicy: true);
        using var decisionHttp = CreateDecisionHttp(noPolicyHandler);
        var coordinator = new AsteriaAssessmentCoordinator(
            registry, stateMachine, new AsteriaDecisionApiClient(decisionHttp), definition);
        var release = await coordinator.CreateAndAssessAsync(
            "AST-GATE14-4C-NO-POLICY-001", InitialMaterial);
        Assert.Null(release.Work.EvaluationId);
        using var persistedResponse = JsonDocument.Parse(release.Work.OriginalResponseJson!);
        Assert.Equal("Unresolved", persistedResponse.RootElement.GetProperty("outcome").GetString());
        Assert.False(persistedResponse.RootElement.GetProperty("deploymentAuthorised").GetBoolean());
        Assert.All(
            new[] { "functionalVerification", "securityVerification", "releaseReviewApproval", "emergencyApproval" },
            name => Assert.Equal(
                "Unresolved",
                persistedResponse.RootElement.GetProperty(name).GetProperty("status").GetString()));
        Assert.Equal("AssessmentUnresolved", (await stateMachine.InspectGenerationAsync(release)).CurrentState);

        var historical = await host.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/replay/decision-verification");
        Assert.Equal("Verified", historical.GetProperty("decisionVerificationOutcome").GetString());
        var afterReplay = await registry.GetGenerationAsync(release.ReleaseId, 1);
        Assert.Equal(release.Work.OriginalResponseJson, afterReplay!.Work.OriginalResponseJson);
        Assert.Equal(release.Work.SelectedPolicyVersionsJson, afterReplay.Work.SelectedPolicyVersionsJson);
    }

    [Fact]
    public async Task Accepted_work_cannot_bypass_amendment_suspension()
    {
        using var host = new StateMachineTestHost();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var definition = ReadV3();
        var coordinator = new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(CreateDecisionHttp(new ContractDecisionApiHandler())),
            definition);
        var release = await coordinator.CreateAndAssessAsync(
            "AST-GATE14-4C-ADMISSION-001", InitialMaterial);
        Assert.Equal("Accepted", release.Work.Status);

        var instance = await stateMachine.InspectGenerationAsync(release);
        var now = DateTimeOffset.UtcNow;
        _ = await registry.ReserveAmendmentAsync(
            release.ReleaseId,
            "AST-GATE14-4C-ADMISSION-AMENDMENT",
            1,
            AmendedMaterial,
            instance.CurrentState,
            "admission-test-amendment-lease",
            now,
            TimeSpan.FromMinutes(2),
            "asteria-release-governance",
            "3.0");

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ResumeAsync(release.ReleaseId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.PrepareAsync(release.ReleaseId, InitialMaterial));
        var suspended = await registry.GetReleaseAsync(release.ReleaseId);
        Assert.Equal(1, suspended!.AssessmentGeneration);
        Assert.Equal("AmendmentPending", suspended.RegistryState);
        Assert.Equal("RequirementsIdentified", (await stateMachine.InspectGenerationAsync(release)).CurrentState);
    }

    [Fact]
    public async Task Activation_failure_after_supersession_recovers_without_reopening_generation_one()
    {
        using var host = new StateMachineTestHost();
        var registryPath = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(registryPath);
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var definition = ReadV3();
        var release = await new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(CreateDecisionHttp(new UnavailableHandler())),
            definition).PrepareAsync("AST-GATE14-4C-ACTIVATION-FAULT-001", InitialMaterial);
        await InstallActivationFailureTriggerAsync(registryPath);

        await Assert.ThrowsAsync<SqliteException>(() =>
            new AsteriaAmendmentCoordinator(registry, stateMachine, definition).AmendAsync(
                release.ReleaseId, "AST-GATE14-4C-ACTIVATION-FAULT-AMENDMENT", 1, AmendedMaterial));

        var interrupted = await registry.GetReleaseAsync(release.ReleaseId);
        var amendment = await registry.GetAmendmentAsync(
            release.ReleaseId, "AST-GATE14-4C-ACTIVATION-FAULT-AMENDMENT");
        var oldGeneration = await registry.GetGenerationAsync(release.ReleaseId, 1);
        var newGeneration = await registry.GetGenerationAsync(release.ReleaseId, 2);
        Assert.NotNull(interrupted);
        Assert.NotNull(amendment);
        Assert.NotNull(oldGeneration);
        Assert.NotNull(newGeneration);
        Assert.Equal("OldInstanceSuperseded", amendment.Phase);
        Assert.Equal(1, interrupted.AssessmentGeneration);
        Assert.Equal("ActivationIncomplete", interrupted.RegistryState);
        Assert.Equal("Superseded", (await stateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);
        Assert.Equal("Draft", (await stateMachine.InspectGenerationAsync(newGeneration)).CurrentState);

        await RemoveActivationFailureTriggerAsync(registryPath);
        var recovered = await new AsteriaAmendmentCoordinator(
            registry, stateMachine, definition).AmendAsync(
                release.ReleaseId, "AST-GATE14-4C-ACTIVATION-FAULT-AMENDMENT", 1, AmendedMaterial);
        var current = await registry.GetReleaseAsync(release.ReleaseId);
        Assert.Equal("NewGenerationActivated", recovered.Phase);
        Assert.Equal(2, current!.AssessmentGeneration);
        Assert.Equal("Superseded", (await stateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);
    }

    [Fact]
    public async Task Amendment_activation_recovers_after_a_real_state_machine_process_restart()
    {
        var stateDatabase = NewDatabasePath();
        var registryDatabase = NewDatabasePath();
        var port = GetUnusedPort();
        var apiAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.Api.dll");
        var definition = ReadV3();
        var process = StartApiProcess(apiAssembly, stateDatabase, port);
        using var stateMachineHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        try
        {
            await WaitForApiAsync(stateMachineHttp, process);
            await using var registry = new AsteriaReleaseRegistry(registryDatabase);
            var stateMachine = new AsteriaStateMachineClient(stateMachineHttp, TrustedKey);
            var decisions = new ContractDecisionApiHandler();
            using var decisionHttp = CreateDecisionHttp(decisions);
            var coordinator = new AsteriaAssessmentCoordinator(
                registry, stateMachine, new AsteriaDecisionApiClient(decisionHttp), definition);
            var generationOne = await coordinator.CreateAndAssessAsync(
                "AST-GATE14-4C-PROCESS-RESTART-001", InitialMaterial);

            var oldGeneration = await registry.GetGenerationAsync(generationOne.ReleaseId, 1)
                ?? throw new InvalidDataException("Generation 1 was not recorded.");
            var oldInstance = await stateMachine.InspectGenerationAsync(oldGeneration);
            const string pendingOldOperationId = "AST-GATE14-4C-PENDING-OLD-OP";
            using (var registerPending = await stateMachineHttp.PostAsJsonAsync(
                       $"/api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/expected-operations",
                       new
                       {
                           operationId = pendingOldOperationId,
                           observationType = AsteriaStateMachineClient.AssessmentObservation,
                           sourceIdentity = AsteriaStateMachineClient.DecisionSourceIdentity,
                           expectedRevision = oldInstance.Revision,
                           requiredEvidence = new[]
                           {
                               "releaseId",
                               "assessmentGeneration",
                               "materialDigest",
                               "parentRevision",
                               "expectedOperationId",
                               "decisionRequestFingerprint",
                               "selectedPolicySourceVersions",
                               "originalResponse",
                               "policySelectionContext"
                           }
                       }))
            {
                Assert.Equal(HttpStatusCode.Created, registerPending.StatusCode);
            }
            using (var armPause = await stateMachineHttp.PostAsync(
                       $"/api/test-controls/inputs/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/AfterIntakeCommitBeforeEvaluation/pause",
                       content: null))
            {
                Assert.Equal(HttpStatusCode.OK, armPause.StatusCode);
            }
            var pendingReceiptTask = stateMachineHttp.PostAsJsonAsync(
                $"/api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/observations",
                new
                {
                    messageId = "AST-GATE14-4C-PENDING-OLD-MESSAGE",
                    deliveryId = "AST-GATE14-4C-PENDING-OLD-MESSAGE",
                    operationId = pendingOldOperationId,
                    observationId = "AST-GATE14-4C-PENDING-OLD-OBS",
                    type = AsteriaStateMachineClient.AssessmentObservation,
                    expectedRevision = oldInstance.Revision,
                    correlationId = pendingOldOperationId,
                    source = AsteriaStateMachineClient.DecisionSourceIdentity,
                    payload = new { releaseId = generationOne.ReleaseId },
                    facts = new { assessmentOutcome = "RequirementsDetermined" },
                    evidence = new
                    {
                        releaseId = generationOne.ReleaseId,
                        assessmentGeneration = 1,
                        materialDigest = generationOne.MaterialDigest,
                        parentRevision = oldInstance.Revision,
                        expectedOperationId = pendingOldOperationId,
                        decisionRequestFingerprint = "pending-old-fingerprint",
                        selectedPolicySourceVersions = Array.Empty<string>(),
                        originalResponse = new { outcome = "RequirementsDetermined" },
                        policySelectionContext = new { }
                    }
                });
            await WaitForPauseAsync(
                stateMachineHttp,
                process,
                $"/api/test-controls/inputs/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/AfterIntakeCommitBeforeEvaluation/pause-status");
            var receiptBeforeRestart = Assert.Single(await stateMachine.ListInboundMessagesAsync(
                oldGeneration.WorkflowInstanceId),
                item => item.GetProperty("messageId").GetString() == "AST-GATE14-4C-PENDING-OLD-MESSAGE");
            Assert.Equal("Received", receiptBeforeRestart.GetProperty("status").GetString());

            StopProcess(process);
            var pendingReceiptFailure = await Record.ExceptionAsync(async () => await pendingReceiptTask);
            Assert.NotNull(pendingReceiptFailure);
            Assert.True(pendingReceiptFailure is HttpRequestException or TaskCanceledException);
            process = StartApiProcess(apiAssembly, stateDatabase, port);
            await WaitForApiAsync(stateMachineHttp, process);

            var afterIntakeRestartStateMachine = new AsteriaStateMachineClient(stateMachineHttp, TrustedKey);
            var receiptAfterIntakeRestart = Assert.Single(await afterIntakeRestartStateMachine.ListInboundMessagesAsync(
                oldGeneration.WorkflowInstanceId),
                item => item.GetProperty("messageId").GetString() == "AST-GATE14-4C-PENDING-OLD-MESSAGE");
            Assert.Equal("Received", receiptAfterIntakeRestart.GetProperty("status").GetString());
            Assert.Equal("RequirementsIdentified",
                (await afterIntakeRestartStateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);

            const string amendmentId = "AST-GATE14-4C-PROCESS-RESTART-AMENDMENT";
            const string leaseToken = "gate14-m4c-process-restart-stage";
            var amendment = await registry.ReserveAmendmentAsync(
                generationOne.ReleaseId,
                amendmentId,
                1,
                AmendedMaterial,
                oldInstance.CurrentState,
                leaseToken,
                Now,
                TimeSpan.FromMinutes(2),
                "asteria-release-governance",
                "3.0");
            var generationTwo = await registry.GetGenerationAsync(generationOne.ReleaseId, 2)
                ?? throw new InvalidDataException("Generation 2 reservation was not recorded.");

            await afterIntakeRestartStateMachine.EnsureDefinitionAsync(definition);
            _ = await afterIntakeRestartStateMachine.EnsureInstanceAsync(generationTwo);
            await registry.AdvanceAmendmentPhaseAsync(
                generationOne.ReleaseId, amendmentId, leaseToken, "NewInstanceProvisioned", Now);
            var attempt = await registry.GetOrCreateSupersessionAttemptAsync(
                generationOne.ReleaseId, amendmentId, oldInstance.Revision, leaseToken, Now);
            var attempts = await registry.ListSupersessionAttemptsAsync(generationOne.ReleaseId, amendmentId);
            Assert.Contains(attempt, attempts);
            await afterIntakeRestartStateMachine.EnsureSupersededAsync(oldGeneration, generationTwo, amendment, attempts);
            Assert.Equal("Superseded",
                (await afterIntakeRestartStateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);
            await registry.AdvanceAmendmentPhaseAsync(
                generationOne.ReleaseId, amendmentId, leaseToken, "OldInstanceSuperseded", Now);

            var beforeRestart = await registry.GetReleaseAsync(generationOne.ReleaseId);
            var oldOperations = await afterIntakeRestartStateMachine.ListExpectedOperationsAsync(oldGeneration.WorkflowInstanceId);
            var oldReceipts = await afterIntakeRestartStateMachine.ListInboundMessagesAsync(oldGeneration.WorkflowInstanceId);
            Assert.Equal("OldInstanceSuperseded",
                (await registry.GetAmendmentAsync(generationOne.ReleaseId, amendmentId))!.Phase);
            Assert.Equal(1, beforeRestart!.AssessmentGeneration);
            Assert.Equal("ActivationIncomplete", beforeRestart.RegistryState);
            Assert.Equal("Superseded",
                (await afterIntakeRestartStateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);
            Assert.Contains(oldOperations, operation =>
                operation.OperationId == attempt.OperationId && operation.Status == "Resolved");
            Assert.Contains(oldReceipts, receipt =>
                receipt.TryGetProperty("messageId", out var messageId) && messageId.GetString() == attempt.MessageId);
            Assert.Contains(oldReceipts, receipt =>
                receipt.TryGetProperty("messageId", out var messageId) &&
                messageId.GetString() == "AST-GATE14-4C-PENDING-OLD-MESSAGE" &&
                receipt.GetProperty("status").GetString() == "Received");
            await registry.ReleaseAmendmentLeaseAsync(generationOne.ReleaseId, leaseToken);

            StopProcess(process);
            process = StartApiProcess(apiAssembly, stateDatabase, port);
            await WaitForApiAsync(stateMachineHttp, process);

            var afterSupersessionRestartStateMachine = new AsteriaStateMachineClient(stateMachineHttp, TrustedKey);
            var restartedOldInstance = await afterSupersessionRestartStateMachine.InspectGenerationAsync(oldGeneration);
            var restartedOldReceipts = await afterSupersessionRestartStateMachine.ListInboundMessagesAsync(oldGeneration.WorkflowInstanceId);
            Assert.Equal("Superseded", restartedOldInstance.CurrentState);
            Assert.Contains(restartedOldReceipts, receipt =>
                receipt.TryGetProperty("messageId", out var messageId) && messageId.GetString() == attempt.MessageId);
            Assert.Contains(restartedOldReceipts, receipt =>
                receipt.TryGetProperty("messageId", out var messageId) &&
                messageId.GetString() == "AST-GATE14-4C-PENDING-OLD-MESSAGE" &&
                receipt.GetProperty("status").GetString() == "Received");

            var recovered = await new AsteriaAmendmentCoordinator(
                registry, afterSupersessionRestartStateMachine, definition).AmendAsync(
                    generationOne.ReleaseId, amendmentId, 1, AmendedMaterial);
            var activated = await registry.GetReleaseAsync(generationOne.ReleaseId);
            Assert.Equal("NewGenerationActivated", recovered.Phase);
            Assert.Equal(2, activated!.AssessmentGeneration);
            Assert.Equal("Operational", activated.RegistryState);
            Assert.Equal("Superseded",
                (await afterSupersessionRestartStateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);

            using (var resumedOldReceipt = await stateMachineHttp.PostAsync(
                       $"/api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/inbound-messages/{Uri.EscapeDataString(receiptBeforeRestart.GetProperty("id").GetString()!)}/resume",
                       content: null))
            {
                Assert.Equal(HttpStatusCode.OK, resumedOldReceipt.StatusCode);
                using var resumedResult = JsonDocument.Parse(await resumedOldReceipt.Content.ReadAsStringAsync());
                Assert.NotEqual(
                    "Accepted",
                    resumedResult.RootElement.GetProperty("message").GetProperty("status").GetString());
            }
            Assert.Equal(
                "Superseded",
                (await afterSupersessionRestartStateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);

            var assessedGenerationTwo = await new AsteriaAssessmentCoordinator(
                registry,
                afterSupersessionRestartStateMachine,
                new AsteriaDecisionApiClient(decisionHttp),
                definition).ResumeAsync(generationOne.ReleaseId);
            Assert.Equal(2, assessedGenerationTwo.AssessmentGeneration);
            using var generationTwoPersistedResponse =
                JsonDocument.Parse(assessedGenerationTwo.Work.OriginalResponseJson!);
            Assert.Equal(
                "AdditionalReviewRequired",
                generationTwoPersistedResponse.RootElement.GetProperty("outcome").GetString());

            var evidencePath = Path.Combine(Path.GetTempPath(), $"asteria-m4c-evidence-{Guid.NewGuid():N}.json");
            try
            {
                var clientAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.AsteriaClient.dll");
                await RunAsteriaClientAsync(
                    clientAssembly,
                    ["export-evidence", generationOne.ReleaseId, evidencePath],
                    stateDatabase,
                    registryDatabase,
                    port);
                using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath));
                var evidenceRoot = evidence.RootElement;
                var exportedGenerations = evidenceRoot.GetProperty("generations");
                Assert.Equal(2, exportedGenerations.GetArrayLength());
                Assert.Equal(2, evidenceRoot.GetProperty("currentGeneration").GetInt32());
                Assert.Equal(
                    "NewGenerationActivated",
                    evidenceRoot.GetProperty("amendments")[0]
                        .GetProperty("amendment").GetProperty("phase").GetString());
                Assert.Single(evidenceRoot.GetProperty("amendments")[0].GetProperty("supersessionAttempts")
                    .EnumerateArray());

                var exportedOne = exportedGenerations[0];
                var exportedTwo = exportedGenerations[1];
                Assert.Equal(1, exportedOne.GetProperty("generation").GetInt32());
                Assert.Equal(2, exportedTwo.GetProperty("generation").GetInt32());
                Assert.False(exportedOne.TryGetProperty("parentState", out _));
                Assert.False(exportedOne.TryGetProperty("parentRevision", out _));
                Assert.False(exportedOne.TryGetProperty("instance", out _));

                var projectionOne = exportedOne.GetProperty("currentStateMachineProjection");
                var projectionTwo = exportedTwo.GetProperty("currentStateMachineProjection");
                Assert.Equal("Superseded", projectionOne.GetProperty("currentState").GetString());
                Assert.Equal("ReviewObligationsIdentified", projectionTwo.GetProperty("currentState").GetString());

                foreach (var exported in new[] { exportedOne, exportedTwo })
                {
                    var binding = exported.GetProperty("assessmentBinding");
                    var boundRevision = binding.GetProperty("boundParentRevision").GetInt64();
                    Assert.Equal(1, boundRevision);
                    Assert.Equal("UnderAssessment", binding.GetProperty("boundParentState").GetString());
                    Assert.Equal(boundRevision, exported.GetProperty("assessmentWork")
                        .GetProperty("parentRevision").GetInt64());
                    Assert.Equal(boundRevision, exported.GetProperty("expectedOperation")
                        .GetProperty("expectedRevision").GetInt64());
                    Assert.True(projectionOne.GetProperty("revision").GetInt64() > boundRevision);
                    Assert.True(projectionTwo.GetProperty("revision").GetInt64() > boundRevision);
                    Assert.Contains(
                        exported.GetProperty("acceptedTransitionHistory").EnumerateArray(),
                        item => item.GetProperty("revisionAfter").GetInt64() == boundRevision &&
                            item.GetProperty("newState").GetString() == "UnderAssessment");
                    Assert.Equal(
                        exported.GetProperty("acceptedTransitionHistory").EnumerateArray()
                            .Max(item => item.GetProperty("revisionAfter").GetInt64()),
                        exported.GetProperty("currentStateMachineProjection").GetProperty("revision").GetInt64());
                }
                Assert.Equal("Resolved",
                    exportedTwo.GetProperty("expectedOperation").GetProperty("status").GetString());
                Assert.Equal(
                    "Required",
                    exportedTwo.GetProperty("independentObligations")
                        .GetProperty("emergencyApproval").GetProperty("status").GetString());
                Assert.True(exportedTwo.GetProperty("assessmentWork")
                    .GetProperty("originalResponseJson").GetString()!.Contains("AST-POL-001", StringComparison.Ordinal));
            }
            finally
            {
                if (File.Exists(evidencePath))
                {
                    File.Delete(evidencePath);
                }
            }
        }
        finally
        {
            StopProcess(process);
            stateMachineHttp.Dispose();
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(stateDatabase);
            DeleteDatabaseFiles(registryDatabase);
        }
    }

    private static async Task ExpireOrdinaryCoordinatorLeaseAsync(string registryPath, string releaseId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = registryPath,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AsteriaReleasePointers
            SET LeaseExpiresAt = 0
            WHERE ReleaseId = $releaseId;
            """;
        command.Parameters.AddWithValue("$releaseId", releaseId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task InstallActivationFailureTriggerAsync(string registryPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = registryPath,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER Gate14TestFailGenerationActivation
            BEFORE UPDATE OF CurrentGeneration ON AsteriaReleasePointers
            WHEN NEW.CurrentGeneration = 2
            BEGIN
                SELECT RAISE(ABORT, 'controlled Gate 14 activation failure');
            END;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveActivationFailureTriggerAsync(string registryPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = registryPath,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER Gate14TestFailGenerationActivation;";
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadV3() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "definitions", "asteria-release-governance-v3.json"));

    private static HttpClient CreateDecisionHttp(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://decision-api.test/") };

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"asteria-m4c-{Guid.NewGuid():N}.db");

    private static string CreatePersistedResponse(
        AsteriaReleaseSnapshot release,
        bool highRisk,
        bool usePreviousDigest = false,
        string? previousDigest = null)
    {
        var material = release.Material;
        var materialDigest = usePreviousDigest
            ? previousDigest ?? throw new ArgumentNullException(nameof(previousDigest))
            : release.MaterialDigest;
        var factValues = new Dictionary<string, (string Type, object Value)>(StringComparer.Ordinal)
        {
            ["releaseId"] = ("String", release.ReleaseId),
            ["assessmentGeneration"] = ("Integer", release.AssessmentGeneration),
            ["materialDigest"] = ("String", materialDigest),
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
        var entries = factValues.Select(item => new
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

        var versionId = release.AssessmentGeneration == 1
            ? "14000000-0000-0000-0000-000000000001"
            : "14000000-0000-0000-0000-000000000002";
        var evaluationId = release.AssessmentGeneration == 1
            ? "24000000-0000-0000-0000-000000000001"
            : "24000000-0000-0000-0000-000000000002";
        const string sourceDigest = "BC7148AAD26F095E18984B311D5AE1849CEA24C0099F85388B1C3ED73C0F8041";
        const string ruleDigest = "275E8D09297420C4C6BE340F95983D6461A6D0788CE6EF92F2A6870D9139247D";
        const string factDigest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var knownAt = release.Work.KnownAt;
        var policyVersion = new
        {
            sourceId = "AST-POL-001",
            versionId,
            versionNumber = release.AssessmentGeneration,
            recordedAt = knownAt,
            effectiveFrom = "2027-02-01",
            effectiveTo = (string?)null,
            sourceDigest,
            ruleDigest
        };
        var evidence = new
        {
            evaluationId,
            evaluatedAt = knownAt.AddSeconds(1),
            businessEffectiveAt = release.Work.BusinessEffectiveAt,
            knownAt,
            factSet = new { entries, digestAlgorithm = "SHA-256", digest = factDigest },
            unknownFacts = Array.Empty<string>(),
            unavailableFacts = Array.Empty<string>(),
            ruleSources = new[]
            {
                new
                {
                    sourceId = "AST-POL-001",
                    policySourceVersionId = versionId,
                    versionNumber = release.AssessmentGeneration,
                    recordedAt = knownAt,
                    effectiveFrom = "2027-02-01",
                    effectiveTo = (string?)null,
                    policySourceDigest = sourceDigest,
                    executableRuleDigest = ruleDigest
                }
            }
        };
        var allRequired = highRisk;
        return JsonSerializer.Serialize(new
        {
            evaluationId,
            binding = new
            {
                releaseId = release.ReleaseId,
                assessmentGeneration = release.AssessmentGeneration,
                materialDigest
            },
            outcome = highRisk ? "AdditionalReviewRequired" : "RequirementsDetermined",
            riskClassification = highRisk ? "HighRisk" : "NotHighRisk",
            functionalVerification = new
            {
                status = allRequired ? "Required" : "NotRequired",
                clauseReferences = allRequired ? new[] { "AST-POL-02" } : Array.Empty<string>()
            },
            securityVerification = new
            {
                status = allRequired ? "Required" : "NotRequired",
                clauseReferences = allRequired ? new[] { "AST-POL-03" } : Array.Empty<string>()
            },
            releaseReviewApproval = new
            {
                status = allRequired ? "Required" : "NotRequired",
                clauseReferences = allRequired ? new[] { "AST-POL-05" } : Array.Empty<string>()
            },
            emergencyApproval = new
            {
                status = allRequired ? "Required" : "NotRequired",
                clauseReferences = allRequired ? new[] { "AST-POL-04" } : Array.Empty<string>()
            },
            businessEffectiveAt = release.Work.BusinessEffectiveAt,
            knownAt,
            factDigest,
            unresolvedFindings = Array.Empty<string>(),
            selectedPolicySourceVersions = new[] { policyVersion },
            evidence,
            deploymentAuthorised = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

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
            unresolvedFindings = new[] { "No policy source was applicable for this synthetic test response." },
            selectedPolicySourceVersions = Array.Empty<object>(),
            evidence = (object?)null,
            deploymentAuthorised = false
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Process StartApiProcess(string apiAssembly, string database, int port)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(apiAssembly);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["DURABLE_STATE_MACHINE_DB"] = database;
        startInfo.Environment["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = TrustedKey;
        startInfo.Environment["DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS"] = "true";
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the State Machine API process.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task RunAsteriaClientAsync(
        string clientAssembly,
        IReadOnlyList<string> arguments,
        string stateDatabase,
        string registryDatabase,
        int port)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(clientAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = TrustedKey;
        startInfo.Environment["DURABLE_STATE_MACHINE_API_URL"] = $"http://127.0.0.1:{port}";
        startInfo.Environment["ASTERIA_DECISION_API_URL"] = "http://decision-api.test/";
        startInfo.Environment["DURABLE_STATE_MACHINE_DB"] = stateDatabase;
        startInfo.Environment["ASTERIA_RELEASE_REGISTRY_PATH"] = registryDatabase;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Asteria evidence-export process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Asteria evidence export failed with exit code {process.ExitCode}: " +
                $"{await standardOutput}{await standardError}");
        }
    }

    private static async Task WaitForApiAsync(HttpClient client, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"State Machine API process exited with code {process.ExitCode}.");
            }
            try
            {
                using var response = await client.GetAsync("/health", timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("The restarted State Machine API did not become healthy.");
    }

    private static async Task WaitForPauseAsync(
        HttpClient client,
        Process process,
        string statusPath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"State Machine API process exited with code {process.ExitCode}.");
            }
            using var response = await client.GetAsync(statusPath, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (status.RootElement.GetProperty("reached").GetBoolean())
            {
                return;
            }
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException("The State Machine API did not pause after durable inbound-message intake.");
    }

    private static void StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        process.Dispose();
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var filePath = path + suffix;
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
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
        public HttpClient CreateClient() => _factory.CreateClient();

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", _previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", _previousKey);
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(_databasePath);
        }
    }

    private sealed class ContractDecisionApiHandler(bool returnNoPolicy = false) : HttpMessageHandler
    {
        private readonly Dictionary<string, (string Request, string Response)> _persisted = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = request.Headers.GetValues("Idempotency-Key").Single();
            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add(requestJson);
            if (_persisted.TryGetValue(key, out var saved))
            {
                if (!string.Equals(saved.Request, requestJson, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }
                return JsonResponse(saved.Response);
            }

            using var document = JsonDocument.Parse(requestJson);
            var response = returnNoPolicy
                ? CreateNoPolicyResponse(ToSnapshot(document.RootElement))
                : CreatePersistedResponse(
                    ToSnapshot(document.RootElement),
                    highRisk: document.RootElement.GetProperty("facts")
                        .GetProperty("changeSize").GetProperty("value").GetString() == "Large");
            _persisted.Add(key, (requestJson, response));
            return JsonResponse(response);
        }

        private static AsteriaReleaseSnapshot ToSnapshot(JsonElement request)
        {
            var facts = request.GetProperty("facts");
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
            var generation = request.GetProperty("assessmentGeneration").GetInt32();
            var work = new AsteriaAssessmentWork(
                "test-work",
                request.GetProperty("releaseId").GetString()!,
                generation,
                request.GetProperty("materialDigest").GetString()!,
                "test-instance",
                "asteria-release-governance",
                "3.0",
                1,
                "test-operation",
                "test-idempotency-key",
                request.GetProperty("businessEffectiveAt").GetString()!,
                request.GetProperty("knownAt").GetDateTimeOffset(),
                null,
                null,
                null,
                "[]",
                null,
                null,
                "DecisionRequested",
                "{}");
            return new AsteriaReleaseSnapshot(
                work.ReleaseId,
                work.WorkflowInstanceId,
                work.DefinitionId,
                work.DefinitionVersion,
                material,
                generation,
                work.MaterialDigest,
                work.WorkId,
                "Registered",
                work);
        }

        private static HttpResponseMessage JsonResponse(string response) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
    }

    private sealed class DelayedDecisionApiHandler(AsteriaReleaseSnapshot release) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RequestReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete() => _completion.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestReceived.TrySetResult();
            await _completion.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    CreatePersistedResponse(release, highRisk: false),
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

}
