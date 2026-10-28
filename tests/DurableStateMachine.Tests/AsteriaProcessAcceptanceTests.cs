using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using DurableStateMachine.AsteriaClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class AsteriaProcessAcceptanceTests
{
    private const string TrustedKey = "asteria-process-acceptance-ingestion-key";
    private const string ProviderId = "14000000-0000-0000-0000-000000000101";
    private const string DescriptionId = "14000000-0000-0000-0000-000000000102";
    private const string VerificationOperation = "POST:/verification";
    private const string DeploymentOperation = "POST:/deployments";
    private const string DeploymentStatusOperation = "GET:/deployments/{deploymentId}";
    private const string DeploymentKeyLookupOperation = "GET:/deployments/by-idempotency-key/{idempotencyKey}";
    private const string HealthOperation = "GET:/health";
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly string DefinitionJson = File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "definitions", "asteria-release-governance-v5.json"));

    private static AsteriaHarnessOperationBinding CreateBinding(string operationKey) =>
        new("synthetic-process-provider",
            ProviderId, DescriptionId, operationKey);

    private static Dictionary<string, AsteriaHarnessOperationBinding> VerificationBindings() =>
        new(StringComparer.Ordinal)
        {
            ["FunctionalVerification"] = new(
                "synthetic-functional-process-provider", ProviderId, DescriptionId, VerificationOperation),
            ["SecurityVerification"] = new(
                "synthetic-security-process-provider", ProviderId, DescriptionId, "POST:/security-verification")
        };

    private static AsteriaDeploymentHarnessBindings DeploymentBindings() =>
        new(
            CreateBinding(DeploymentOperation),
            CreateBinding(DeploymentStatusOperation),
            CreateBinding(DeploymentKeyLookupOperation));

    private static string CreateAssessmentResponse(
        AsteriaReleaseSnapshot release,
        bool requireEmergencyApproval)
    {
        var response = JsonNode.Parse(
            AsteriaMilestone3BTests.CreatePersistedResponse(release, "AdditionalReviewRequired"))!;
        if (requireEmergencyApproval)
        {
            response["emergencyApproval"]!["status"] = "Required";
            response["emergencyApproval"]!["clauseReferences"] = new JsonArray("AST-POL-06");
        }
        return response.ToJsonString();
    }

    [Theory]
    [InlineData("verification-after-dispatch-pending", "DispatchPending", "OutcomeUnknown", false)]
    [InlineData("verification-after-remote-response", "DispatchPending", "OutcomeUnknown", false)]
    [InlineData("verification-after-response-archived", "ResponsePersisted", "Accepted", true)]
    [InlineData("verification-after-observation-accepted", "ResponsePersisted", "Accepted", true)]
    [InlineData("verification-before-work-accepted", "ObservationSubmitted", "Accepted", true)]
    public async Task Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence(
        string boundary,
        string expectedStatusAtCrash,
        string expectedStatusAfterRestart,
        bool expectedProviderCall)
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-PROCESS-" + Guid.NewGuid().ToString("N"));
        var activityLedger = new AsteriaActivityLedger(scenario.RegistryPath);
        await using (activityLedger)
        {
            var runId = Guid.NewGuid().ToString("N");
            var syncDirectory = Path.Combine(scenario.Root, "sync-" + runId);
            Directory.CreateDirectory(syncDirectory);
            using var interrupted = scenario.StartCoordinator(
                release.ReleaseId, boundary, syncDirectory, runId);
            var reachedPath = await WaitForBoundaryAsync(
                interrupted.Process,
                syncDirectory,
                runId,
                boundary);
            var signal = (await File.ReadAllTextAsync(reachedPath)).Split(':');
            Assert.Equal(interrupted.Process.Id, int.Parse(signal[0]));
            Assert.False(interrupted.Process.HasExited);

            var activityAtCrash = Assert.Single(await activityLedger.ListGenerationAsync(
                release.ReleaseId, release.AssessmentGeneration));
            Assert.Equal(expectedStatusAtCrash, activityAtCrash.Status);
            Assert.True(File.Exists(scenario.StateDatabasePath));
            Assert.True(File.Exists(scenario.RegistryPath));

            interrupted.Process.Kill(entireProcessTree: false);
            await interrupted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, interrupted.Process.ExitCode);

            using var restarted = scenario.StartCoordinator(release.ReleaseId);
            var recoveryOutput = await CompleteProcessAsync(restarted.Process);
            if (expectedStatusAfterRestart == "OutcomeUnknown")
            {
                Assert.True(restarted.Process.ExitCode != 0, recoveryOutput);
            }
            else
            {
                Assert.True(restarted.Process.ExitCode == 0, recoveryOutput);
            }

            var recovered = await activityLedger.GetAsync(activityAtCrash.WorkId);
            Assert.NotNull(recovered);
            Assert.Equal(expectedStatusAfterRestart, recovered.Status);
            Assert.Equal(boundary == "verification-after-remote-response" || expectedProviderCall ? 1 : 0,
                scenario.Harness.VerificationRequestCount);
            Assert.Equal(expectedProviderCall, recovered.HarnessExecutionId is not null);
            Assert.Equal(expectedProviderCall, recovered.OriginalResponseJson is not null);

            var stateMachine = scenario.StateMachine;
            var concern = await stateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
            var operations = await stateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
            var receipts = await stateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
            var history = await stateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
            var replay = await stateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
            Assert.Equal(expectedStatusAfterRestart == "Accepted" ? "Passed" : "Requested",
                concern["FunctionalVerification"].CurrentState);
            Assert.Contains(operations, item =>
                item.OperationId == activityAtCrash.ExpectedOperationId &&
                item.Status == (expectedStatusAfterRestart == "Accepted" ? "Resolved" : "Expected"));
            var acceptedReceipts = receipts.Where(item =>
                item.GetProperty("inputType").GetString() == "AsteriaFunctionalVerificationResult" &&
                item.GetProperty("status").GetString() == "Accepted").ToArray();
            if (expectedStatusAfterRestart == "Accepted")
            {
                Assert.Single(acceptedReceipts);
            }
            else
            {
                Assert.Empty(acceptedReceipts);
            }
            Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
            Assert.NotEmpty(history);
            Assert.True(File.Exists(scenario.StateDatabasePath));
            Assert.True(File.Exists(scenario.RegistryPath));
        }
    }

    [Fact]
    public async Task Coordinator_process_runs_without_test_synchronization_by_default()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-PROCESS-NO-HOOK-" + Guid.NewGuid().ToString("N"));
        var markerDirectory = Path.Combine(scenario.Root, "no-hook");
        Directory.CreateDirectory(markerDirectory);

        using var coordinator = scenario.StartCoordinator(release.ReleaseId);
        _ = await CompleteProcessAsync(coordinator.Process);
        Assert.Equal(0, coordinator.Process.ExitCode);
        Assert.Empty(Directory.GetFiles(markerDirectory));

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        var accepted = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration));
        Assert.Equal("Accepted", accepted.Status);
        Assert.Equal(1, scenario.Harness.VerificationRequestCount);
    }

    [Theory]
    [InlineData("deployment-after-dispatch-claim", "Dispatched", "OutcomeUnknown", 0)]
    [InlineData("deployment-after-remote-response", "Dispatched", "Accepted", 1)]
    [InlineData("deployment-after-admission-archived", "ResponsePersisted", "Accepted", 1)]
    public async Task Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely(
        string boundary,
        string expectedStatusAtCrash,
        string expectedStatusAfterRestart,
        int expectedMutationCount)
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-DEPLOY-PROCESS-" + Guid.NewGuid().ToString("N"));
        var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await using (ledger)
        {
            var runId = Guid.NewGuid().ToString("N");
            var syncDirectory = Path.Combine(scenario.Root, "sync-" + runId);
            Directory.CreateDirectory(syncDirectory);
            using var interrupted = scenario.StartWorkCoordinator(
                "deploy", release.ReleaseId, boundary, syncDirectory, runId);
            _ = await WaitForBoundaryAsync(interrupted.Process, syncDirectory, runId, boundary);
            var signal = (await File.ReadAllTextAsync(Path.Combine(
                syncDirectory,
                $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant()}.{boundary}.reached")))
                .Split(':');
            Assert.Equal(interrupted.Process.Id, int.Parse(signal[0]));
            var activityAtCrash = Assert.Single(await ledger.ListGenerationAsync(
                release.ReleaseId, release.AssessmentGeneration),
                item => item.ActivityType == "Deployment");
            Assert.Equal(expectedStatusAtCrash, activityAtCrash.Status);
            Assert.Equal(expectedMutationCount, scenario.Harness.ExternalDeploymentMutationCount);
            Assert.True(File.Exists(scenario.StateDatabasePath));
            Assert.True(File.Exists(scenario.RegistryPath));

            interrupted.Process.Kill(entireProcessTree: false);
            await interrupted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

            using var restarted = scenario.StartWorkCoordinator("deploy", release.ReleaseId);
            var recoveryOutput = await CompleteProcessAsync(restarted.Process);
            Assert.Equal(expectedStatusAfterRestart != "OutcomeUnknown", restarted.Process.ExitCode == 0);
            if (expectedStatusAfterRestart == "OutcomeUnknown")
            {
                Assert.Contains("authoritative result", recoveryOutput, StringComparison.OrdinalIgnoreCase);
            }

            var recovered = await ledger.GetAsync(activityAtCrash.WorkId);
            Assert.NotNull(recovered);
            Assert.Equal(expectedStatusAfterRestart, recovered.Status);
            Assert.Equal(expectedMutationCount, scenario.Harness.ExternalDeploymentMutationCount);
            var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
            var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
            var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
            var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
            var parentHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
            var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(
                release.WorkflowInstanceId);
            var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
            Assert.Equal(expectedStatusAfterRestart == "Accepted"
                ? "PostDeploymentVerification"
                : "DeploymentInProgress", parent!.CurrentState);
            Assert.Equal(expectedStatusAfterRestart == "Accepted" ? "Succeeded" : "Running",
                concerns["Deployment"].CurrentState);
            Assert.Contains(operations, operation =>
                operation.OperationId == recovered.ExpectedOperationId &&
                operation.Status == "Resolved");
            Assert.NotEmpty(parentHistory);
            Assert.Contains(concernHistory, item =>
                item.GetProperty("concernId").GetString() == "Deployment");
            var acceptedDeploymentReceipts = receipts.Where(receipt =>
                receipt.GetProperty("inputType").GetString() == "AsteriaDeploymentResult" &&
                receipt.GetProperty("status").GetString() == "Accepted").ToArray();
            if (expectedStatusAfterRestart == "Accepted")
            {
                Assert.Single(acceptedDeploymentReceipts);
            }
            else
            {
                Assert.Empty(acceptedDeploymentReceipts);
            }
            Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
            Assert.True(File.Exists(scenario.StateDatabasePath));
            Assert.True(File.Exists(scenario.RegistryPath));
        }
    }

    [Fact]
    public async Task Accepted_deployment_process_restart_does_not_repeat_external_mutation()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-DEPLOY-ACCEPTED-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var syncDirectory = Path.Combine(scenario.Root, "sync-" + runId);
        Directory.CreateDirectory(syncDirectory);
        using var interrupted = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, "deployment-after-result-accepted", syncDirectory, runId);
        _ = await WaitForBoundaryAsync(
            interrupted.Process, syncDirectory, runId, "deployment-after-result-accepted");
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
        var originalDatabase = File.GetLastWriteTimeUtc(scenario.RegistryPath);
        interrupted.Process.Kill(entireProcessTree: false);
        await interrupted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var restarted = scenario.StartWorkCoordinator("deploy", release.ReleaseId);
        _ = await CompleteProcessAsync(restarted.Process);
        Assert.Equal(0, restarted.Process.ExitCode);
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
        Assert.True(File.Exists(scenario.RegistryPath));
        Assert.True(File.GetLastWriteTimeUtc(scenario.RegistryPath) >= originalDatabase);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var deployment = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration),
            item => item.ActivityType == "Deployment");
        Assert.Equal("Accepted", deployment.Status);
        Assert.Equal("Succeeded", deployment.ValidatedOutcome);
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var transitions = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        Assert.Equal("PostDeploymentVerification", parent!.CurrentState);
        Assert.Equal("Succeeded", concerns["Deployment"].CurrentState);
        Assert.Contains(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaDeploymentResult" &&
            receipt.GetProperty("status").GetString() == "Accepted");
        Assert.NotEmpty(transitions);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "Deployment");
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Synchronization_opt_in_is_ignored_outside_development_environment()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-PRODUCTION-HOOKS-DISABLED-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var coordinator = scenario.StartWorkCoordinator(
            "verify", release.ReleaseId, "verification-after-response-archived", sync, runId,
            synchronizationEnvironment: "Production");
        var output = await CompleteProcessAsync(coordinator.Process);
        Assert.True(coordinator.Process.ExitCode == 0, output);
        Assert.False(File.Exists(BoundaryPath(sync, runId, "verification-after-response-archived")));
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        Assert.Equal("Accepted", Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration)).Status);
        Assert.Equal(1, scenario.Harness.VerificationRequestCount);
    }

    [Fact]
    public async Task Competing_deployment_processes_preserve_unknown_outcome_without_duplicate_dispatch()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-DEPLOY-RACE-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var syncDirectory = Path.Combine(scenario.Root, "sync-" + runId);
        Directory.CreateDirectory(syncDirectory);
        using var first = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, "deployment-after-dispatch-claim", syncDirectory, runId);
        _ = await WaitForBoundaryAsync(
            first.Process, syncDirectory, runId, "deployment-after-dispatch-claim");

        using var second = scenario.StartWorkCoordinator("deploy", release.ReleaseId);
        var secondOutput = await CompleteProcessAsync(second.Process);
        Assert.NotEqual(0, second.Process.ExitCode);
        Assert.Contains("authoritative result", secondOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, scenario.Harness.ExternalDeploymentMutationCount);

        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))
            .ToLowerInvariant();
        await File.WriteAllTextAsync(
            Path.Combine(syncDirectory, $"{runHash}.deployment-after-dispatch-claim.release"),
            "continue");
        _ = await CompleteProcessAsync(first.Process);
        Assert.NotEqual(0, first.Process.ExitCode);
        Assert.Equal(1, scenario.Harness.DeploymentRequestCount);
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var deployment = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration),
            item => item.ActivityType == "Deployment");
        Assert.Equal("OutcomeUnknown", deployment.Status);
        Assert.Null(deployment.ValidatedOutcome);
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("DeploymentInProgress", parent!.CurrentState);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Functional_and_security_verification_processes_complete_concurrently_with_independent_evidence()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-VERIFY-RACE-" + Guid.NewGuid().ToString("N"));
        var functionalRun = Guid.NewGuid().ToString("N");
        var securityRun = Guid.NewGuid().ToString("N");
        var functionalSync = CreateSynchronizationDirectory(scenario.Root, functionalRun);
        var securitySync = CreateSynchronizationDirectory(scenario.Root, securityRun);
        using var functional = scenario.StartWorkCoordinator(
            "verify", release.ReleaseId, "verification-after-response-archived",
            functionalSync, functionalRun, "FunctionalVerification");
        using var security = scenario.StartWorkCoordinator(
            "verify", release.ReleaseId, "verification-after-response-archived",
            securitySync, securityRun, "SecurityVerification");
        _ = await WaitForBoundaryAsync(
            functional.Process, functionalSync, functionalRun, "verification-after-response-archived");
        _ = await WaitForBoundaryAsync(
            security.Process, securitySync, securityRun, "verification-after-response-archived");

        var parentAtBarrier = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var activitiesAtBarrier = await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration);
        var functionalWork = Assert.Single(activitiesAtBarrier,
            item => item.ConcernId == "FunctionalVerification");
        var securityWork = Assert.Single(activitiesAtBarrier,
            item => item.ConcernId == "SecurityVerification");
        Assert.Equal(parentAtBarrier!.Revision, functionalWork.ParentRevision);
        Assert.Equal(parentAtBarrier.Revision, securityWork.ParentRevision);
        Assert.NotEqual(functionalWork.WorkId, securityWork.WorkId);
        Assert.NotEqual(functionalWork.ExpectedOperationId, securityWork.ExpectedOperationId);

        await ReleaseBoundaryAsync(functionalSync, functionalRun, "verification-after-response-archived");
        await ReleaseBoundaryAsync(securitySync, securityRun, "verification-after-response-archived");
        var outputs = await Task.WhenAll(
            CompleteProcessAsync(functional.Process),
            CompleteProcessAsync(security.Process));
        Assert.Equal(0, functional.Process.ExitCode);
        Assert.Equal(0, security.Process.ExitCode);

        var current = await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration);
        Assert.Equal("Accepted", Assert.Single(current, item => item.WorkId == functionalWork.WorkId).Status);
        Assert.Equal("Accepted", Assert.Single(current, item => item.WorkId == securityWork.WorkId).Status);
        Assert.Equal(2, scenario.Harness.VerificationRequestCount);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var histories = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var transitions = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("Passed", concerns["FunctionalVerification"].CurrentState);
        Assert.Equal("Passed", concerns["SecurityVerification"].CurrentState);
        Assert.Contains(operations, item => item.OperationId == functionalWork.ExpectedOperationId && item.Status == "Resolved");
        Assert.Contains(operations, item => item.OperationId == securityWork.ExpectedOperationId && item.Status == "Resolved");
        Assert.Equal(1, receipts.Count(item =>
            item.GetProperty("inputType").GetString() == "AsteriaFunctionalVerificationResult" &&
            item.GetProperty("status").GetString() == "Accepted"));
        Assert.Equal(1, receipts.Count(item =>
            item.GetProperty("inputType").GetString() == "AsteriaSecurityVerificationResult" &&
            item.GetProperty("status").GetString() == "Accepted"));
        Assert.Contains(histories, item => item.GetProperty("concernId").GetString() == "FunctionalVerification");
        Assert.Contains(histories, item => item.GetProperty("concernId").GetString() == "SecurityVerification");
        Assert.NotEmpty(transitions);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Independent_api_clients_competing_verification_results_do_not_overwrite_accepted_evidence()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-VERIFY-CONFLICT-" + Guid.NewGuid().ToString("N"));
        using (var originalCoordinator = scenario.StartCoordinator(release.ReleaseId))
        {
            _ = await CompleteProcessAsync(originalCoordinator.Process);
            Assert.Equal(0, originalCoordinator.Process.ExitCode);
        }
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var acceptedActivity = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration));
        Assert.Equal("Accepted", acceptedActivity.Status);
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var operationId = acceptedActivity.ExpectedOperationId;
        var activityId = acceptedActivity.ActivityId;
        const string source = "AsteriaVerificationCoordinator";
        const string observationType = "AsteriaFunctionalVerificationResult";
        var evidenceFields = new[]
        {
            "releaseId", "assessmentGeneration", "materialDigest", "activityId",
            "expectedOperationId", "result", "evidenceDigest", "providerExecutionId"
        };
        var operation = Assert.Single(await scenario.StateMachine.ListExpectedOperationsAsync(
            release.WorkflowInstanceId), item => item.OperationId == operationId);
        Assert.Equal("Resolved", operation.Status);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new CountdownEvent(2);
        using var clientA = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        using var clientB = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        clientA.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        clientB.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        var pass = SubmitCompetingResultAsync(
            clientA, release, operation.ExpectedRevision, operationId, activityId, observationType,
            source, evidenceFields, "Passed", gate.Task, ready, acceptedActivity);
        var fail = SubmitCompetingResultAsync(
            clientB, release, operation.ExpectedRevision, operationId, activityId, observationType,
            source, evidenceFields, "Failed", gate.Task, ready);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        gate.SetResult();
        var responses = await Task.WhenAll(pass, fail);

        Assert.Contains(responses, response => response.Status == "Duplicate");
        Assert.Contains(responses, response => response.Status is "Conflicting" or "Refused" or "Rejected");
        var acceptedResult = acceptedActivity.ValidatedOutcome;
        var concern = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        using var attemptsClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        var attempts = await attemptsClient.GetFromJsonAsync<JsonElement[]>(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/attempts");
        Assert.NotNull(attempts);
        var parentHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal(acceptedResult, concern["FunctionalVerification"].CurrentState);
        Assert.Equal("Resolved", Assert.Single(operations, item => item.OperationId == operationId).Status);
        Assert.Equal(1, receipts.Count(item =>
            item.GetProperty("inputType").GetString() == observationType &&
            item.GetProperty("status").GetString() == "Accepted"));
        Assert.Contains(attempts, attempt => attempt.GetProperty("status").GetString() == "Accepted");
        Assert.Contains(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == observationType &&
            receipt.GetProperty("status").GetString() is "Rejected" or "Conflicting" or "Refused");
        Assert.NotEmpty(parentHistory);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "FunctionalVerification");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("Passed", "Failed")]
    [InlineData("Failed", "Passed")]
    public async Task Concurrent_verification_results_preserve_the_first_accepted_evidence(
        string winningResult,
        string losingResult)
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync(
            $"AST-VERIFY-ORDER-{winningResult}-{losingResult}-{Guid.NewGuid():N}");
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var coordinator = scenario.StartWorkCoordinator(
            "verify", release.ReleaseId, "verification-after-response-archived", sync, runId);
        _ = await WaitForBoundaryAsync(
            coordinator.Process, sync, runId, "verification-after-response-archived");

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var activity = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration));
        Assert.Equal("ResponsePersisted", activity.Status);
        Assert.Null(activity.ValidatedOutcome);
        var operation = Assert.Single(await scenario.StateMachine.ListExpectedOperationsAsync(
            release.WorkflowInstanceId), item => item.OperationId == activity.ExpectedOperationId);
        Assert.Equal("Expected", operation.Status);
        using var firstClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        using var secondClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        firstClient.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        secondClient.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
        const string observationType = "AsteriaFunctionalVerificationResult";
        const string source = "AsteriaVerificationCoordinator";
        var evidenceFields = new[]
        {
            "releaseId", "assessmentGeneration", "materialDigest", "activityId",
            "expectedOperationId", "result", "evidenceDigest", "providerExecutionId"
        };
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReady = new CountdownEvent(1);
        var firstTask = SubmitCompetingResultAsync(
            firstClient, release, operation.ExpectedRevision, operation.OperationId,
            activity.ActivityId, observationType, source, evidenceFields, winningResult,
            firstGate.Task, firstReady);
        Assert.True(firstReady.Wait(TimeSpan.FromSeconds(10)));
        firstGate.SetResult();
        var first = await firstTask;
        Assert.Equal("Accepted", first.Status);

        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new CountdownEvent(1);
        var secondTask = SubmitCompetingResultAsync(
            secondClient, release, operation.ExpectedRevision, operation.OperationId,
            activity.ActivityId, observationType, source, evidenceFields, losingResult,
            secondGate.Task, secondReady);
        Assert.True(secondReady.Wait(TimeSpan.FromSeconds(10)));
        secondGate.SetResult();
        var second = await secondTask;
        Assert.True(second.Status is "Conflicting" or "Refused" or "Rejected", second.Status);

        coordinator.Process.Kill(entireProcessTree: true);
        await coordinator.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var acceptedActivity = await ledger.GetAsync(activity.WorkId);
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var parentHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("ResponsePersisted", acceptedActivity!.Status);
        Assert.Null(acceptedActivity.ValidatedOutcome);
        Assert.Equal("ReviewObligationsIdentified", parent!.CurrentState);
        Assert.Equal(winningResult, concerns["FunctionalVerification"].CurrentState);
        Assert.Equal("Resolved", Assert.Single(
            operations, item => item.OperationId == activity.ExpectedOperationId).Status);
        var resultReceipts = receipts.Where(receipt =>
            receipt.GetProperty("inputType").GetString() == observationType).ToArray();
        Assert.Equal(1, resultReceipts.Count(receipt =>
            receipt.GetProperty("status").GetString() == "Accepted"));
        Assert.Contains(resultReceipts, receipt =>
            receipt.GetProperty("status").GetString() is "Rejected" or "Conflicting" or "Refused");
        Assert.NotEmpty(parentHistory);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "FunctionalVerification");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        Assert.Equal(1, scenario.Harness.VerificationRequestCount);
    }

    [Fact]
    public async Task Concurrent_readiness_processes_accept_one_stable_readiness_transition()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-READINESS-RACE-" + Guid.NewGuid().ToString("N"));
        var runA = Guid.NewGuid().ToString("N");
        var runB = Guid.NewGuid().ToString("N");
        var syncA = CreateSynchronizationDirectory(scenario.Root, runA);
        var syncB = CreateSynchronizationDirectory(scenario.Root, runB);
        using var first = scenario.StartWorkCoordinator(
            "readiness", release.ReleaseId, "readiness-before-observation", syncA, runA);
        using var second = scenario.StartWorkCoordinator(
            "readiness", release.ReleaseId, "readiness-before-observation", syncB, runB);
        _ = await WaitForBoundaryAsync(first.Process, syncA, runA, "readiness-before-observation");
        _ = await WaitForBoundaryAsync(second.Process, syncB, runB, "readiness-before-observation");

        await ReleaseBoundaryAsync(syncA, runA, "readiness-before-observation");
        _ = await CompleteProcessAsync(first.Process);
        Assert.Equal(0, first.Process.ExitCode);
        await ReleaseBoundaryAsync(syncB, runB, "readiness-before-observation");
        _ = await CompleteProcessAsync(second.Process);
        Assert.Equal(0, second.Process.ExitCode);

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var readiness = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration),
            item => item.ActivityType == "Readiness");
        Assert.Equal("Accepted", readiness.Status);
        Assert.Equal(release.AssessmentGeneration, readiness.Generation);
        Assert.Equal(release.MaterialDigest, readiness.MaterialDigest);
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var attemptsClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        using (attemptsClient)
        {
            var attempts = await attemptsClient.GetFromJsonAsync<JsonElement[]>(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/attempts");
            Assert.NotNull(attempts);
            Assert.Contains(attempts, attempt => attempt.GetProperty("status").GetString() == "Accepted");
        }
        var transitionHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("ReadyForDeployment", parent!.CurrentState);
        Assert.Equal("Resolved", Assert.Single(operations, item =>
            item.OperationId == readiness.ExpectedOperationId).Status);
        Assert.Equal(1, receipts.Count(receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaReadinessConfirmed" &&
            receipt.GetProperty("status").GetString() == "Accepted"));
        Assert.NotEmpty(transitionHistory);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "FunctionalVerification");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Concurrent_health_processes_reconcile_to_one_completion_transition()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-COMPLETION-RACE-" + Guid.NewGuid().ToString("N"));
        _ = await scenario.ExecuteDeploymentAsync(release);
        var runA = Guid.NewGuid().ToString("N");
        var runB = Guid.NewGuid().ToString("N");
        var syncA = CreateSynchronizationDirectory(scenario.Root, runA);
        var syncB = CreateSynchronizationDirectory(scenario.Root, runB);
        using var first = scenario.StartWorkCoordinator(
            "health", release.ReleaseId, "health-after-observation-accepted", syncA, runA);
        _ = await WaitForBoundaryAsync(first.Process, syncA, runA, "health-after-observation-accepted");
        using var second = scenario.StartWorkCoordinator(
            "health", release.ReleaseId, "health-after-observation-accepted", syncB, runB);
        _ = await WaitForBoundaryAsync(second.Process, syncB, runB, "health-after-observation-accepted");

        await ReleaseBoundaryAsync(syncA, runA, "health-after-observation-accepted");
        _ = await CompleteProcessAsync(first.Process);
        Assert.Equal(0, first.Process.ExitCode);
        await ReleaseBoundaryAsync(syncB, runB, "health-after-observation-accepted");
        _ = await CompleteProcessAsync(second.Process);
        Assert.Equal(0, second.Process.ExitCode);

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var activities = await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration);
        var deployment = Assert.Single(activities, item => item.ActivityType == "Deployment");
        var health = Assert.Single(activities, item => item.ActivityType == "HealthVerification");
        Assert.Equal("Accepted", deployment.Status);
        Assert.Equal("Accepted", health.Status);
        Assert.Equal(deployment.Generation, health.Generation);
        Assert.Equal(deployment.MaterialDigest, health.MaterialDigest);
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var transitions = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var histories = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var attemptsClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        using (attemptsClient)
        {
            var attempts = await attemptsClient.GetFromJsonAsync<JsonElement[]>(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/attempts");
            Assert.NotNull(attempts);
            Assert.Contains(attempts, attempt => attempt.GetProperty("status").GetString() == "Accepted");
        }
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("Completed", parent!.CurrentState);
        Assert.Equal("Passed", concerns["HealthVerification"].CurrentState);
        Assert.Equal("Resolved", Assert.Single(operations, item =>
            item.OperationId == health.ExpectedOperationId).Status);
        Assert.Equal(1, receipts.Count(receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaHealthVerificationResult" &&
            receipt.GetProperty("status").GetString() == "Accepted"));
        Assert.NotEmpty(transitions);
        Assert.Contains(histories, item => item.GetProperty("concernId").GetString() == "HealthVerification");
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Independent_approval_clients_preserve_exact_duplicate_and_refuse_changed_payload()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync(
            "AST-APPROVAL-RACE-" + Guid.NewGuid().ToString("N"),
            requireEmergencyApproval: true);
        await using var registry = new AsteriaReleaseRegistry(scenario.RegistryPath);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var approvals = new AsteriaApprovalRegistry(scenario.RegistryPath);
        await approvals.InitializeAsync();
        var reviewerToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var emergencyToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var credentials = new AsteriaApprovalCredentialRegistry(
            "process-race-authority-v1",
            [
                new AsteriaApprovalCredential("race-reviewer", "ReleaseReviewer", reviewerToken),
                new AsteriaApprovalCredential("race-emergency-authoriser", "EmergencyReleaseAuthoriser", emergencyToken)
            ]);
        var gateway = new AsteriaApprovalGateway(
            registry,
            ledger,
            approvals,
            credentials,
            scenario.StateMachine,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = DefinitionJson });
        var port = GetUnusedPort();
        var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add($"http://127.0.0.1:{port}");
        gateway.MapEndpoints(app);
        await app.StartAsync();
        try
        {
            using var initialClient = CreateApprovalClient(port, reviewerToken);
            var submission = new AsteriaApprovalSubmission(
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                "ReleaseReviewApproval",
                "approval-race-" + Guid.NewGuid().ToString("N"),
                "approval-race-key-" + Guid.NewGuid().ToString("N"),
                "Approved",
                "race-review-evidence");
            using var initialResponse = await initialClient.PostAsJsonAsync("/approvals", submission);
            Assert.Equal(HttpStatusCode.OK, initialResponse.StatusCode);
            var accepted = await initialResponse.Content.ReadFromJsonAsync<AsteriaApprovalResponse>();
            Assert.NotNull(accepted);
            Assert.Equal("Accepted", accepted.Activity.Status);
            Assert.Equal("Approved", accepted.Decision.Decision);

            using var exactClient = CreateApprovalClient(port, reviewerToken);
            using var changedClient = CreateApprovalClient(port, reviewerToken);
            var ready = new CountdownEvent(2);
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var exact = SubmitApprovalAfterGateAsync(exactClient, submission, ready, releaseGate.Task);
            var changed = SubmitApprovalAfterGateAsync(
                changedClient,
                submission with { EvidenceReference = "changed-race-review-evidence" },
                ready,
                releaseGate.Task);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
            releaseGate.SetResult();
            var responses = await Task.WhenAll(exact, changed);
            Assert.Equal(HttpStatusCode.OK, responses.Single(response =>
                response.PayloadEvidenceReference == submission.EvidenceReference).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, responses.Single(response =>
                response.PayloadEvidenceReference != submission.EvidenceReference).StatusCode);

            var decisions = await approvals.ListGenerationAsync(
                release.ReleaseId, release.AssessmentGeneration);
            var decision = Assert.Single(decisions, item =>
                item.ConcernId == "ReleaseReviewApproval");
            Assert.Equal("race-reviewer", decision.PrincipalId);
            Assert.Equal("Approved", decision.Decision);
            var activities = await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration);
            var activity = Assert.Single(activities, item => item.ConcernId == "ReleaseReviewApproval");
            Assert.Equal("Accepted", activity.Status);
            Assert.Equal(release.AssessmentGeneration, activity.Generation);
            Assert.Equal(release.MaterialDigest, activity.MaterialDigest);
            var conflicts = await ledger.ListConflictsAsync(activity.WorkId);
            Assert.Single(conflicts);
            var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
            var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
            var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
            var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
            var attemptsClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
            using (attemptsClient)
            {
                var attempts = await attemptsClient.GetFromJsonAsync<JsonElement[]>(
                    $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/attempts");
                Assert.NotNull(attempts);
                Assert.Contains(attempts, attempt => attempt.GetProperty("status").GetString() == "Accepted");
            }
            var parentHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
            var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
            var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
            Assert.Equal("ReviewObligationsIdentified", parent!.CurrentState);
            Assert.Equal("Approved", concerns["ReleaseReviewApproval"].CurrentState);
            Assert.Contains(operations, item =>
                item.OperationId == activity.ExpectedOperationId && item.Status == "Resolved");
            Assert.Equal(1, receipts.Count(receipt =>
                receipt.GetProperty("inputType").GetString() == "AsteriaReleaseReviewApprovalResult" &&
                receipt.GetProperty("status").GetString() == "Accepted"));
            Assert.NotEmpty(parentHistory);
            Assert.Contains(concernHistory, item =>
                item.GetProperty("concernId").GetString() == "ReleaseReviewApproval");
            Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("original", "changed")]
    [InlineData("changed", "original")]
    public async Task Approval_payload_conflict_preserves_whichever_valid_submission_wins(
        string winningPayload,
        string losingPayload)
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync(
            $"AST-APPROVAL-ORDER-{winningPayload}-{losingPayload}-{Guid.NewGuid():N}",
            requireEmergencyApproval: true);
        await using var registry = new AsteriaReleaseRegistry(scenario.RegistryPath);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var approvals = new AsteriaApprovalRegistry(scenario.RegistryPath);
        await approvals.InitializeAsync();
        var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var emergencyToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var credentials = new AsteriaApprovalCredentialRegistry(
            "process-acceptance-approval-order-v1",
            [
                new AsteriaApprovalCredential("ordered-reviewer", "ReleaseReviewer", token),
                new AsteriaApprovalCredential("ordered-emergency-authoriser", "EmergencyReleaseAuthoriser", emergencyToken)
            ]);
        Assert.True(credentials.TryAuthenticate(token, out var principal));
        Assert.NotNull(principal);
        var gateway = new AsteriaApprovalGateway(
            registry,
            ledger,
            approvals,
            credentials,
            scenario.StateMachine,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = DefinitionJson });
        var port = GetUnusedPort();
        var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add($"http://127.0.0.1:{port}");
        gateway.MapEndpoints(app);
        await app.StartAsync();
        try
        {
            var original = new AsteriaApprovalSubmission(
                release.ReleaseId,
                release.AssessmentGeneration,
                release.MaterialDigest,
                "ReleaseReviewApproval",
                "ordered-approval-" + Guid.NewGuid().ToString("N"),
                "ordered-approval-key-" + Guid.NewGuid().ToString("N"),
                "Approved",
                "original-evidence");
            var changed = original with { EvidenceReference = "changed-evidence" };
            using var originalClient = CreateApprovalClient(port, token);
            using var changedClient = CreateApprovalClient(port, token);
            var ready = new CountdownEvent(2);
            var originalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var originalTask = SubmitApprovalAfterGateAsync(
                originalClient, original, ready, originalGate.Task);
            var changedTask = SubmitApprovalAfterGateAsync(
                changedClient, changed, ready, changedGate.Task);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));

            var winnerGate = winningPayload == "original" ? originalGate : changedGate;
            var winnerTask = winningPayload == "original" ? originalTask : changedTask;
            var loserGate = winningPayload == "original" ? changedGate : originalGate;
            var loserTask = winningPayload == "original" ? changedTask : originalTask;
            winnerGate.SetResult();
            var winnerResponse = await winnerTask;
            loserGate.SetResult();
            var loserResponse = await loserTask;
            Assert.Equal(HttpStatusCode.OK, winnerResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, loserResponse.StatusCode);
            var expectedEvidence = winningPayload == "original"
                ? original.EvidenceReference
                : changed.EvidenceReference;

            var decision = Assert.Single(await approvals.ListGenerationAsync(
                release.ReleaseId, release.AssessmentGeneration),
                item => item.ConcernId == "ReleaseReviewApproval");
            Assert.Equal("ordered-reviewer", decision.PrincipalId);
            Assert.Equal("Approved", decision.Decision);
            Assert.Equal(expectedEvidence, decision.EvidenceReference);
            var activity = Assert.Single(
                await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration),
                item => item.ConcernId == "ReleaseReviewApproval");
            Assert.Equal("Accepted", activity.Status);
            Assert.Single(await ledger.ListConflictsAsync(activity.WorkId));
            var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
            var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
            var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
            var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
            var parentHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
            var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
            var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
            Assert.Equal("ReviewObligationsIdentified", parent!.CurrentState);
            Assert.Equal("Approved", concerns["ReleaseReviewApproval"].CurrentState);
            Assert.Contains(operations, item =>
                item.OperationId == activity.ExpectedOperationId && item.Status == "Resolved");
            Assert.Single(receipts, receipt =>
                receipt.GetProperty("inputType").GetString() == "AsteriaReleaseReviewApprovalResult" &&
                receipt.GetProperty("status").GetString() == "Accepted");
            Assert.NotEmpty(parentHistory);
            Assert.Contains(concernHistory, item =>
                item.GetProperty("concernId").GetString() == "ReleaseReviewApproval");
            Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Supersession_fences_late_old_generation_verification_result()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-SUPERSESSION-RACE-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var verifier = scenario.StartWorkCoordinator(
            "verify", release.ReleaseId, "verification-after-response-archived",
            sync, runId, "FunctionalVerification");
        _ = await WaitForBoundaryAsync(
            verifier.Process, sync, runId, "verification-after-response-archived");
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var oldActivity = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration));
        Assert.Equal("ResponsePersisted", oldActivity.Status);
        Assert.Equal(1, oldActivity.Generation);
        Assert.Equal(release.MaterialDigest, oldActivity.MaterialDigest);
        var amendmentMaterialPath = Path.Combine(scenario.Root, "amendment-material.json");
        await File.WriteAllTextAsync(
            amendmentMaterialPath,
            JsonSerializer.Serialize(release.Material with { TargetVersion = "4.2.1" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var amendment = scenario.StartAmendmentCoordinator(
            release.ReleaseId, "AST-SUPERSESSION-RACE-AMENDMENT", release.AssessmentGeneration,
            amendmentMaterialPath);
        var amendmentOutput = await CompleteProcessAsync(amendment.Process);
        Assert.True(amendment.Process.ExitCode == 0, amendmentOutput);

        await using var releaseRegistry = new AsteriaReleaseRegistry(scenario.RegistryPath);
        var current = await releaseRegistry.GetReleaseAsync(release.ReleaseId);
        Assert.NotNull(current);
        Assert.Equal(2, current.AssessmentGeneration);
        Assert.NotEqual(release.MaterialDigest, current.MaterialDigest);
        await ReleaseBoundaryAsync(sync, runId, "verification-after-response-archived");
        var verifierOutput = await CompleteProcessAsync(verifier.Process);
        Assert.NotEqual(0, verifier.Process.ExitCode);

        var oldInstance = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var newInstance = await scenario.StateMachine.GetInstanceAsync(current.WorkflowInstanceId);
        var oldConcern = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var newConcern = await scenario.StateMachine.GetConcernStatesAsync(current.WorkflowInstanceId);
        var oldOperations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var oldReceipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var oldAttemptsClient = new HttpClient { BaseAddress = scenario.StateMachineBaseAddress };
        using (oldAttemptsClient)
        {
            var oldAttempts = await oldAttemptsClient.GetFromJsonAsync<JsonElement[]>(
                $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/attempts");
            Assert.NotNull(oldAttempts);
            Assert.Contains(oldAttempts, attempt => attempt.GetProperty("status").GetString() == "Accepted");
        }
        var oldHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var oldConcernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var oldReplay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        var newReplay = await scenario.StateMachine.GetHistoricalReplayAsync(current.WorkflowInstanceId);
        Assert.Equal("Superseded", oldInstance!.CurrentState);
        Assert.Equal("Draft", newInstance!.CurrentState);
        Assert.Equal("Requested", oldConcern["FunctionalVerification"].CurrentState);
        Assert.Equal("ApplicabilityPending", newConcern["FunctionalVerification"].CurrentState);
        Assert.Contains(oldOperations, item =>
            item.OperationId == oldActivity.ExpectedOperationId && item.Status == "Expected");
        Assert.DoesNotContain(oldReceipts, item =>
            item.GetProperty("inputType").GetString() == "AsteriaFunctionalVerificationResult" &&
            item.GetProperty("status").GetString() == "Accepted");
        var archivedLateResult = await ledger.GetAsync(oldActivity.WorkId);
        Assert.NotNull(archivedLateResult);
        Assert.Equal("ResponsePersisted", archivedLateResult.Status);
        Assert.Null(archivedLateResult.ValidatedOutcome);
        Assert.False(string.IsNullOrWhiteSpace(verifierOutput));
        Assert.NotEmpty(oldHistory);
        Assert.NotEmpty(oldConcernHistory);
        Assert.Equal("Consistent", oldReplay.GetProperty("outcome").GetString());
        Assert.Equal("Consistent", newReplay.GetProperty("outcome").GetString());
        var generationTwoActivities = await ledger.ListGenerationAsync(release.ReleaseId, 2);
        Assert.Empty(generationTwoActivities);
        Assert.Equal(1, scenario.Harness.VerificationRequestCount);
    }

    [Fact]
    public async Task Accepted_verification_result_remains_in_old_generation_when_supersession_follows()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.AssessAsync("AST-VERIFICATION-WINS-SUPERSESSION-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var verifier = scenario.StartWorkCoordinator(
            "verify", release.ReleaseId, "verification-after-response-archived",
            sync, runId, "FunctionalVerification");
        _ = await WaitForBoundaryAsync(
            verifier.Process, sync, runId, "verification-after-response-archived");
        await ReleaseBoundaryAsync(sync, runId, "verification-after-response-archived");
        var verifierOutput = await CompleteProcessAsync(verifier.Process);
        Assert.True(verifier.Process.ExitCode == 0, verifierOutput);

        var amendmentMaterialPath = Path.Combine(scenario.Root, "accepted-result-amendment.json");
        await File.WriteAllTextAsync(
            amendmentMaterialPath,
            JsonSerializer.Serialize(release.Material with { TargetVersion = "4.2.1" },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var amendment = scenario.StartAmendmentCoordinator(
            release.ReleaseId, "AST-VERIFICATION-WINS-AMENDMENT", release.AssessmentGeneration,
            amendmentMaterialPath);
        var amendmentOutput = await CompleteProcessAsync(amendment.Process);
        Assert.True(amendment.Process.ExitCode == 0, amendmentOutput);

        await using var registry = new AsteriaReleaseRegistry(scenario.RegistryPath);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var current = await registry.GetReleaseAsync(release.ReleaseId);
        Assert.NotNull(current);
        var oldInstance = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var newInstance = await scenario.StateMachine.GetInstanceAsync(current.WorkflowInstanceId);
        var oldConcern = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var oldOperations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var oldReceipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var oldHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var oldConcernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var oldReplay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        var newReplay = await scenario.StateMachine.GetHistoricalReplayAsync(current.WorkflowInstanceId);
        var activity = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration));
        Assert.Equal(2, current.AssessmentGeneration);
        Assert.NotEqual(release.MaterialDigest, current.MaterialDigest);
        Assert.Equal("Superseded", oldInstance!.CurrentState);
        Assert.Equal("Draft", newInstance!.CurrentState);
        Assert.Equal("Passed", oldConcern["FunctionalVerification"].CurrentState);
        Assert.Equal("Accepted", activity.Status);
        Assert.Contains(oldOperations, item =>
            item.OperationId == activity.ExpectedOperationId && item.Status == "Resolved");
        Assert.Single(oldReceipts, item =>
            item.GetProperty("inputType").GetString() == "AsteriaFunctionalVerificationResult" &&
            item.GetProperty("status").GetString() == "Accepted");
        Assert.NotEmpty(oldHistory);
        Assert.Contains(oldConcernHistory, item =>
            item.GetProperty("concernId").GetString() == "FunctionalVerification");
        Assert.Equal("Consistent", oldReplay.GetProperty("outcome").GetString());
        Assert.Equal("Consistent", newReplay.GetProperty("outcome").GetString());
        Assert.Empty(await ledger.ListGenerationAsync(release.ReleaseId, 2));
        Assert.Equal(1, scenario.Harness.VerificationRequestCount);
    }

    [Fact]
    public async Task Pre_dispatch_cancellation_wins_against_deployment_reservation()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-CANCEL-BEFORE-DISPATCH-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var deployment = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, "deployment-before-reservation", sync, runId);
        _ = await WaitForBoundaryAsync(
            deployment.Process, sync, runId, "deployment-before-reservation");

        using var cancellation = scenario.StartWorkCoordinator("cancel", release.ReleaseId);
        var cancellationOutput = await CompleteProcessAsync(cancellation.Process);
        Assert.True(cancellation.Process.ExitCode == 0, cancellationOutput);
        await ReleaseBoundaryAsync(sync, runId, "deployment-before-reservation");
        var deploymentOutput = await CompleteProcessAsync(deployment.Process);
        Assert.NotEqual(0, deployment.Process.ExitCode);

        await using var registry = new AsteriaReleaseRegistry(scenario.RegistryPath);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var current = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var history = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        var activities = await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration);
        Assert.Equal("Cancelled", current!.CurrentState);
        Assert.Equal("Requested", concerns["CancellationIntent"].CurrentState);
        Assert.Contains(activities, activity =>
            activity.ConcernId == "Deployment" &&
            activity.Status is "Reserved" or "OperationRegistered");
        Assert.DoesNotContain(activities, activity =>
            activity.ConcernId == "Deployment" &&
            activity.Status is "DispatchPending" or "Dispatched" or "ResponsePersisted" or "Accepted");
        Assert.Equal(0, scenario.Harness.ExternalDeploymentMutationCount);
        Assert.NotEmpty(history);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(deploymentOutput));
        Assert.Equal(1, (await registry.GetReleaseAsync(release.ReleaseId))!.AssessmentGeneration);
    }

    [Fact]
    public async Task Post_dispatch_cancellation_preserves_late_deployment_result_without_rollback()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-CANCEL-AFTER-DISPATCH-" + Guid.NewGuid().ToString("N"));
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var deployment = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, "deployment-after-remote-response", sync, runId);
        _ = await WaitForBoundaryAsync(
            deployment.Process, sync, runId, "deployment-after-remote-response");

        using var cancellation = scenario.StartWorkCoordinator("cancel", release.ReleaseId);
        var cancellationOutput = await CompleteProcessAsync(cancellation.Process);
        Assert.True(cancellation.Process.ExitCode == 0, cancellationOutput);
        await ReleaseBoundaryAsync(sync, runId, "deployment-after-remote-response");
        var deploymentOutput = await CompleteProcessAsync(deployment.Process);
        Assert.True(deployment.Process.ExitCode == 0, deploymentOutput);

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var deploymentActivity = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration),
            activity => activity.ConcernId == "Deployment");
        var current = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var history = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("Accepted", deploymentActivity.Status);
        Assert.Contains(operations, item =>
            item.OperationId == deploymentActivity.ExpectedOperationId && item.Status == "Resolved");
        Assert.Equal("PostDeploymentVerification", current!.CurrentState);
        Assert.Equal("Requested", concerns["CancellationIntent"].CurrentState);
        Assert.Contains(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaDeploymentResultConfirmed" &&
            receipt.GetProperty("status").GetString() == "Accepted");
        Assert.NotEmpty(history);
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Late_health_result_after_cancellation_intent_does_not_complete_release()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-LATE-HEALTH-CANCEL-" + Guid.NewGuid().ToString("N"));
        _ = await scenario.ExecuteDeploymentAsync(release);
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var health = scenario.StartWorkCoordinator(
            "health", release.ReleaseId, "health-after-response-archived", sync, runId);
        _ = await WaitForBoundaryAsync(health.Process, sync, runId, "health-after-response-archived");

        using var cancellation = scenario.StartWorkCoordinator("cancel", release.ReleaseId);
        var cancellationOutput = await CompleteProcessAsync(cancellation.Process);
        Assert.True(cancellation.Process.ExitCode == 0, cancellationOutput);
        await ReleaseBoundaryAsync(sync, runId, "health-after-response-archived");
        var healthOutput = await CompleteProcessAsync(health.Process);

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var healthActivity = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration),
            activity => activity.ConcernId == "HealthVerification");
        var current = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var history = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.True(health.Process.ExitCode == 0, healthOutput);
        Assert.Equal("Accepted", healthActivity.Status);
        Assert.Equal("Passed", healthActivity.ValidatedOutcome);
        Assert.Equal("PostDeploymentVerification", current!.CurrentState);
        Assert.Equal("Passed", concerns["HealthVerification"].CurrentState);
        Assert.Equal("Requested", concerns["CancellationIntent"].CurrentState);
        Assert.Contains(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaHealthVerificationResult" &&
            receipt.GetProperty("status").GetString() == "Accepted");
        Assert.NotEmpty(history);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "HealthVerification");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
    }

    [Fact]
    public async Task Late_passed_health_result_cannot_replace_accepted_health_failure()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-LATE-HEALTH-FAILURE-" + Guid.NewGuid().ToString("N"));
        _ = await scenario.ExecuteDeploymentAsync(release);
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var health = scenario.StartWorkCoordinator(
            "health", release.ReleaseId, "health-after-response-archived", sync, runId);
        _ = await WaitForBoundaryAsync(health.Process, sync, runId, "health-after-response-archived");
        var healthActivity = await ReadConcernActivityAsync(scenario, release, "HealthVerification");
        using var request = JsonDocument.Parse(healthActivity.OriginalRequestJson);
        var deploymentId = request.RootElement.GetProperty("deploymentId").GetString()!;
        var concernOperationId = StableMessageId(healthActivity.WorkId, "result-concern-operation");
        var parentOperationId = StableMessageId(healthActivity.WorkId, "result-parent-operation");
        var resultEvidenceFields = new[]
        {
            "releaseId", "assessmentGeneration", "materialDigest", "deploymentId",
            "healthWorkId", "expectedOperationId", "result", "evidenceDigest"
        };
        var concernEvidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["deploymentId"] = deploymentId,
            ["healthWorkId"] = healthActivity.WorkId,
            ["expectedOperationId"] = concernOperationId,
            ["result"] = "Failed",
            ["evidenceDigest"] = AsteriaActivityLedger.Fingerprint("ordered-health-failure-evidence")
        };
        _ = await scenario.StateMachine.SubmitConcernObservationAsync(
            release,
            "HealthVerification",
            concernOperationId,
            StableMessageId(healthActivity.WorkId, "race-failed-result-message"),
            StableMessageId(healthActivity.WorkId, "race-failed-result-observation"),
            "AsteriaHealthVerificationResult",
            "AsteriaHealthVerificationCoordinator",
            resultEvidenceFields,
            concernEvidence,
            "healthResult",
            "Failed",
            "Failed");
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        Assert.NotNull(parent);
        var parentEvidence = new Dictionary<string, object?>(concernEvidence, StringComparer.Ordinal)
        {
            ["expectedOperationId"] = parentOperationId
        };
        _ = await scenario.StateMachine.SubmitParentLifecycleObservationAsync(
            release,
            parentOperationId,
            StableMessageId(healthActivity.WorkId, "race-failed-parent-message"),
            StableMessageId(healthActivity.WorkId, "race-failed-parent-observation"),
            "AsteriaHealthResultConfirmed",
            "AsteriaHealthVerificationCoordinator",
            parent.Revision,
            resultEvidenceFields,
            parentEvidence,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["releaseId"] = release.ReleaseId,
                ["assessmentGeneration"] = release.AssessmentGeneration,
                ["materialDigest"] = release.MaterialDigest,
                ["deploymentId"] = deploymentId,
                ["healthWorkId"] = healthActivity.WorkId
            },
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["healthResult"] = "Failed" },
            "AsteriaHealthVerificationResultObserved",
            "PostDeploymentVerification",
            "HealthFailed");

        await ReleaseBoundaryAsync(sync, runId, "health-after-response-archived");
        var healthOutput = await CompleteProcessAsync(health.Process);
        Assert.NotEqual(0, health.Process.ExitCode);

        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var finalActivity = await ledger.GetAsync(healthActivity.WorkId);
        var finalParent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var parentHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("HealthFailed", finalParent!.CurrentState);
        Assert.Equal("Failed", concerns["HealthVerification"].CurrentState);
        Assert.NotEqual("Completed", finalParent.CurrentState);
        Assert.Equal("ResponsePersisted", finalActivity!.Status);
        Assert.Equal("Passed", finalActivity.ValidatedOutcome);
        Assert.Contains(operations, operation =>
            operation.OperationId == concernOperationId && operation.Status == "Resolved");
        Assert.Single(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaHealthVerificationResult" &&
            receipt.GetProperty("status").GetString() == "Accepted" &&
            receipt.GetProperty("requestEnvelope").GetProperty("evidence").GetProperty("result").GetString() == "Failed");
        Assert.DoesNotContain(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaHealthVerificationResult" &&
            receipt.GetProperty("status").GetString() == "Accepted" &&
            receipt.GetProperty("requestEnvelope").GetProperty("evidence").GetProperty("result").GetString() == "Passed");
        Assert.NotEmpty(parentHistory);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "HealthVerification");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(healthOutput));
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
    }

    [Fact]
    public async Task Generic_harness_and_provider_process_recover_admitted_deployment_by_original_key()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-REAL-PROVIDER-RECOVERY-" + Guid.NewGuid().ToString("N"));
        await using var services = await ProviderProcessServices.StartAsync(scenario.Root);
        Assert.NotEqual(services.ProviderProcessId, services.HarnessProcessId);
        var descriptionId = await services.RegisterDeploymentApiAsync();
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var interrupted = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, "deployment-after-remote-response", sync, runId,
            harnessUrl: services.HarnessUrl,
            descriptionId: descriptionId);
        _ = await WaitForBoundaryAsync(
            interrupted.Process, sync, runId, "deployment-after-remote-response");
        var originalWork = await ReadDeploymentActivityAsync(scenario, release);
        Assert.Equal("Dispatched", originalWork.Status);
        Assert.Null(originalWork.HarnessExecutionId);
        var providerBeforeRestart = await services.ReadProviderRecordAsync(originalWork.IdempotencyKey);
        Assert.Equal("Succeeded", providerBeforeRestart.Status);
        Assert.True(File.Exists(services.ProviderDatabasePath));
        Assert.True(File.Exists(services.HarnessDatabasePath));
        var harnessExecutionsBeforeRestart = await services.ListProviderExecutionsAsync();
        var deploymentExecution = Assert.Single(harnessExecutionsBeforeRestart, execution =>
            execution.GetProperty("operationKey").GetString() == DeploymentOperation);
        var originalExecutionId = deploymentExecution.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(originalExecutionId));

        interrupted.Process.Kill(entireProcessTree: true);
        await interrupted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var restarted = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, harnessUrl: services.HarnessUrl, descriptionId: descriptionId);
        var recoveryOutput = await CompleteProcessAsync(restarted.Process);
        var recoveryExecutions = await services.ListProviderExecutionsAsync();
        Assert.True(restarted.Process.ExitCode == 0,
            recoveryOutput + Environment.NewLine + JsonSerializer.Serialize(recoveryExecutions));
        var recovered = await ReadDeploymentActivityAsync(scenario, release);
        var providerAfterRestart = await services.ReadProviderRecordAsync(originalWork.IdempotencyKey);
        var harnessExecutionsAfterRestart = await services.ListProviderExecutionsAsync();
        var sameDeploymentExecution = Assert.Single(harnessExecutionsAfterRestart, execution =>
            execution.GetProperty("id").GetString() == originalExecutionId);
        Assert.Equal("Accepted", recovered.Status);
        Assert.Equal("Succeeded", providerAfterRestart.Status);
        Assert.Equal(originalWork.IdempotencyKey, providerAfterRestart.IdempotencyKey);
        Assert.Equal(release.AssessmentGeneration, providerAfterRestart.AssessmentGeneration);
        Assert.Equal(release.MaterialDigest, providerAfterRestart.MaterialDigest);
        Assert.True(File.Exists(services.ProviderDatabasePath));
        Assert.True(File.Exists(services.HarnessDatabasePath));
        Assert.Equal(1, await services.CountProviderDeploymentsAsync(originalWork.IdempotencyKey));
        using var inspection = await services.GetExecutionResourceAsync(originalExecutionId!, "inspection");
        using var evidence = await services.GetExecutionResourceAsync(originalExecutionId!, "evidence");
        using var attempts = await services.GetExecutionResourceAsync(originalExecutionId!, "attempts");
        Assert.Equal(HttpStatusCode.OK, inspection.StatusCode);
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        Assert.Equal(HttpStatusCode.OK, attempts.StatusCode);
        using var inspectionJson = JsonDocument.Parse(await inspection.Content.ReadAsStringAsync());
        using var evidenceJson = JsonDocument.Parse(await evidence.Content.ReadAsStringAsync());
        using var attemptsJson = JsonDocument.Parse(await attempts.Content.ReadAsStringAsync());
        Assert.Equal("ObservedAndValidated",
            inspectionJson.RootElement.GetProperty("outcomeConfidence").GetString());
        Assert.NotEmpty(evidenceJson.RootElement.EnumerateArray());
        Assert.NotEmpty(attemptsJson.RootElement.EnumerateArray());
        Assert.Equal(DeploymentOperation, sameDeploymentExecution.GetProperty("operationKey").GetString());

        using var repeat = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, harnessUrl: services.HarnessUrl, descriptionId: descriptionId);
        var repeatOutput = await CompleteProcessAsync(repeat.Process);
        Assert.True(repeat.Process.ExitCode == 0, repeatOutput);
        Assert.Equal(1, await services.CountProviderDeploymentsAsync(originalWork.IdempotencyKey));
        var parent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var operations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var history = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("PostDeploymentVerification", parent!.CurrentState);
        Assert.Equal("Succeeded", concerns["Deployment"].CurrentState);
        Assert.Contains(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaDeploymentResult" &&
            receipt.GetProperty("status").GetString() == "Accepted");
        Assert.Contains(operations, operation =>
            operation.OperationId == recovered.ExpectedOperationId && operation.Status == "Resolved");
        Assert.NotEmpty(history);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "Deployment");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Competing_deployment_coordinators_share_provider_owned_idempotency_record()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-REAL-PROVIDER-COMPETING-" + Guid.NewGuid().ToString("N"));
        await using var services = await ProviderProcessServices.StartAsync(scenario.Root);
        var descriptionId = await services.RegisterDeploymentApiAsync();
        var runId = Guid.NewGuid().ToString("N");
        var sync = CreateSynchronizationDirectory(scenario.Root, runId);
        using var first = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, "deployment-after-remote-response", sync, runId,
            harnessUrl: services.HarnessUrl,
            descriptionId: descriptionId);
        _ = await WaitForBoundaryAsync(first.Process, sync, runId, "deployment-after-remote-response");
        using var second = scenario.StartWorkCoordinator(
            "deploy", release.ReleaseId, harnessUrl: services.HarnessUrl, descriptionId: descriptionId);
        var secondOutput = await CompleteProcessAsync(second.Process);
        Assert.True(second.Process.ExitCode == 0, secondOutput);

        var work = await ReadDeploymentActivityAsync(scenario, release);
        var providerRecord = await services.ReadProviderRecordAsync(work.IdempotencyKey);
        Assert.Equal("Succeeded", providerRecord.Status);
        Assert.Equal(1, await services.CountProviderDeploymentsAsync(work.IdempotencyKey));
        var executions = await services.ListProviderExecutionsAsync();
        Assert.Single(executions, execution =>
            execution.GetProperty("operationKey").GetString() == DeploymentOperation);
        await ReleaseBoundaryAsync(sync, runId, "deployment-after-remote-response");
        var firstOutput = await CompleteProcessAsync(first.Process);
        Assert.True(first.Process.ExitCode == 0, firstOutput);

        var finalWork = await ReadDeploymentActivityAsync(scenario, release);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var providerObservations = await ledger.ListProviderObservationsAsync(finalWork.WorkId);
        var finalParent = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var finalOperations = await scenario.StateMachine.ListExpectedOperationsAsync(release.WorkflowInstanceId);
        var finalReceipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var finalHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("Accepted", finalWork.Status);
        Assert.Equal("PostDeploymentVerification", finalParent!.CurrentState);
        Assert.Contains(finalOperations, operation =>
            operation.OperationId == finalWork.ExpectedOperationId && operation.Status == "Resolved");
        Assert.Contains(finalReceipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaDeploymentResult" &&
            receipt.GetProperty("status").GetString() == "Accepted");
        Assert.Contains(providerObservations, observation =>
            observation.ObservationKind == "DeploymentStatusLookup");
        Assert.Contains(providerObservations, observation =>
            observation.ObservationKind == "DeploymentAdmissionResponse");
        Assert.NotEmpty(finalHistory);
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        Assert.Equal(1, await services.CountProviderDeploymentsAsync(work.IdempotencyKey));
    }

    [Fact]
    public async Task Accepted_health_observation_recovers_before_final_completion_reconciliation()
    {
        await using var scenario = await ProcessScenario.StartAsync();
        var release = await scenario.PrepareReadyReleaseAsync("AST-HEALTH-PROCESS-" + Guid.NewGuid().ToString("N"));
        _ = await scenario.ExecuteDeploymentAsync(release);
        var runId = Guid.NewGuid().ToString("N");
        var syncDirectory = Path.Combine(scenario.Root, "sync-" + runId);
        Directory.CreateDirectory(syncDirectory);
        using var interrupted = scenario.StartWorkCoordinator(
            "health", release.ReleaseId, "health-after-observation-accepted", syncDirectory, runId);
        _ = await WaitForBoundaryAsync(
            interrupted.Process, syncDirectory, runId, "health-after-observation-accepted");
        var parentBeforeRecovery = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var concernsBeforeRecovery = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        Assert.Equal("PostDeploymentVerification", parentBeforeRecovery!.CurrentState);
        Assert.Equal("Passed", concernsBeforeRecovery["HealthVerification"].CurrentState);
        interrupted.Process.Kill(entireProcessTree: false);
        await interrupted.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        using var restarted = scenario.StartWorkCoordinator("health", release.ReleaseId);
        _ = await CompleteProcessAsync(restarted.Process);
        Assert.Equal(0, restarted.Process.ExitCode);
        var completed = await scenario.StateMachine.GetInstanceAsync(release.WorkflowInstanceId);
        var finalConcerns = await scenario.StateMachine.GetConcernStatesAsync(release.WorkflowInstanceId);
        var receipts = await scenario.StateMachine.ListInboundMessagesAsync(release.WorkflowInstanceId);
        var transitionHistory = await scenario.StateMachine.ListTransitionHistoryAsync(release.WorkflowInstanceId);
        var concernHistory = await scenario.StateMachine.ListConcernHistoryAsync(release.WorkflowInstanceId);
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        var health = Assert.Single(await ledger.ListGenerationAsync(
            release.ReleaseId, release.AssessmentGeneration),
            item => item.ActivityType == "HealthVerification");
        var replay = await scenario.StateMachine.GetHistoricalReplayAsync(release.WorkflowInstanceId);
        Assert.Equal("Completed", completed!.CurrentState);
        Assert.Equal("Accepted", health.Status);
        Assert.Equal("Passed", health.ValidatedOutcome);
        Assert.Equal("Passed", finalConcerns["HealthVerification"].CurrentState);
        Assert.Contains(receipts, receipt =>
            receipt.GetProperty("inputType").GetString() == "AsteriaHealthVerificationResult" &&
            receipt.GetProperty("status").GetString() == "Accepted");
        Assert.NotEmpty(transitionHistory);
        Assert.Contains(concernHistory, item =>
            item.GetProperty("concernId").GetString() == "HealthVerification");
        Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
        Assert.Equal(1, scenario.Harness.ExternalDeploymentMutationCount);
    }

    private static async Task<string> WaitForBoundaryAsync(
        Process process,
        string directory,
        string runId,
        string boundary)
    {
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))
            .ToLowerInvariant();
        var path = Path.Combine(directory, $"{runHash}.{boundary}.reached");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"Coordinator exited before boundary '{boundary}' (exit {process.ExitCode}).{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }
            if (File.Exists(path))
            {
                return path;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        throw new TimeoutException(
            $"Coordinator PID {process.Id} did not reach explicit boundary '{boundary}'. " +
            $"Exit={process.ExitCode}.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
    }

    private static string CreateSynchronizationDirectory(string root, string runId)
    {
        var directory = Path.Combine(root, "sync-" + runId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BoundaryPath(string directory, string runId, string boundary)
    {
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))
            .ToLowerInvariant();
        return Path.Combine(directory, $"{runHash}.{boundary}.reached");
    }

    private static async Task<AsteriaActivitySnapshot> ReadDeploymentActivityAsync(
        ProcessScenario scenario,
        AsteriaReleaseSnapshot release)
    {
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        return Assert.Single(
            await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration),
            activity => activity.ConcernId == "Deployment");
    }

    private static async Task<AsteriaActivitySnapshot> ReadConcernActivityAsync(
        ProcessScenario scenario,
        AsteriaReleaseSnapshot release,
        string concernId)
    {
        await using var ledger = new AsteriaActivityLedger(scenario.RegistryPath);
        await ledger.InitializeAsync();
        return Assert.Single(
            await ledger.ListGenerationAsync(release.ReleaseId, release.AssessmentGeneration),
            activity => activity.ConcernId == concernId);
    }

    private static HttpClient CreateApprovalClient(int port, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<ApprovalClientResult> SubmitApprovalAfterGateAsync(
        HttpClient client,
        AsteriaApprovalSubmission submission,
        CountdownEvent ready,
        Task releaseGate)
    {
        ready.Signal();
        await releaseGate;
        using var response = await client.PostAsJsonAsync("/approvals", submission);
        _ = await response.Content.ReadAsStringAsync();
        return new ApprovalClientResult(response.StatusCode, submission.EvidenceReference);
    }

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Task ReleaseBoundaryAsync(string directory, string runId, string boundary)
    {
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId)))
            .ToLowerInvariant();
        return File.WriteAllTextAsync(
            Path.Combine(directory, $"{runHash}.{boundary}.release"),
            "continue");
    }

    private static async Task<ObservationOutcome> SubmitCompetingResultAsync(
        HttpClient client,
        AsteriaReleaseSnapshot release,
        long expectedRevision,
        string operationId,
        string activityId,
        string observationType,
        string sourceIdentity,
        IReadOnlyList<string> requiredEvidence,
        string businessResult,
        Task releaseGate,
        CountdownEvent ready,
        AsteriaActivitySnapshot? acceptedActivity = null)
    {
        var exactDuplicate = acceptedActivity is not null &&
            acceptedActivity.ValidatedOutcome == businessResult;
        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["releaseId"] = release.ReleaseId,
            ["assessmentGeneration"] = release.AssessmentGeneration,
            ["materialDigest"] = release.MaterialDigest,
            ["activityId"] = activityId,
            ["expectedOperationId"] = operationId,
            ["result"] = businessResult,
            ["evidenceDigest"] = exactDuplicate
                ? acceptedActivity!.ValidatedEvidenceDigest
                : AsteriaActivityLedger.Fingerprint(
                    $"competing-evidence:{release.ReleaseId}:{businessResult}"),
            ["providerExecutionId"] = exactDuplicate
                ? acceptedActivity!.HarnessExecutionId
                : "synthetic-provider-execution-" + businessResult
        };
        var messageId = exactDuplicate
            ? StableMessageId(acceptedActivity!.WorkId, "result-message")
            : "race-" + businessResult + "-" + Guid.NewGuid().ToString("N");
        var observationId = exactDuplicate
            ? StableMessageId(acceptedActivity!.WorkId, "result-observation")
            : "observation-" + businessResult + "-" + Guid.NewGuid().ToString("N");
        ready.Signal();
        await releaseGate;
        using var response = await client.PostAsJsonAsync(
            $"api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            new
            {
                messageId,
                deliveryId = messageId,
                operationId,
                observationId,
                type = observationType,
                expectedRevision,
                correlationId = operationId,
                source = sourceIdentity,
                payload = new
                {
                    releaseId = release.ReleaseId,
                    assessmentGeneration = release.AssessmentGeneration,
                    materialDigest = release.MaterialDigest,
                    activityId,
                    expectedOperationId = operationId,
                    result = businessResult
                },
                facts = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["verificationResult"] = businessResult
                },
                evidence
            });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = json.RootElement.GetProperty("message");
        var outcome = new ObservationOutcome(
            message.GetProperty("status").GetString()!,
            message.TryGetProperty("businessResult", out var returnedResult)
                ? returnedResult.GetString() ?? businessResult
                : businessResult,
            response.StatusCode);
        return outcome;
    }

    private static string StableMessageId(string value, string action) =>
        "ast-g14-" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{value}\0{action}")));

    private static async Task<string> CompleteProcessAsync(Process process)
    {
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        return (await outputTask) + Environment.NewLine + (await errorTask);
    }

    private sealed class ProcessScenario : IAsyncDisposable
    {
        private readonly Process _stateMachineProcess;
        private readonly HttpClient _stateMachineHttp;
        private readonly WebApplication _harnessApplication;
        private readonly string _harnessUrl;
        private readonly string _clientAssembly;
        private readonly string _apiAssembly;

        private ProcessScenario(
            string root,
            string stateDatabasePath,
            string registryPath,
            Process stateMachineProcess,
            HttpClient stateMachineHttp,
            WebApplication harnessApplication,
            string harnessUrl,
            string apiAssembly,
            string clientAssembly,
            FakeHarness harness)
        {
            Root = root;
            StateDatabasePath = stateDatabasePath;
            RegistryPath = registryPath;
            _stateMachineProcess = stateMachineProcess;
            _stateMachineHttp = stateMachineHttp;
            _harnessApplication = harnessApplication;
            _harnessUrl = harnessUrl;
            _apiAssembly = apiAssembly;
            _clientAssembly = clientAssembly;
            Harness = harness;
            StateMachine = new AsteriaStateMachineClient(_stateMachineHttp, TrustedKey);
        }

        public string Root { get; }
        public string StateDatabasePath { get; }
        public string RegistryPath { get; }
        public Uri StateMachineBaseAddress => _stateMachineHttp.BaseAddress!;
        public AsteriaStateMachineClient StateMachine { get; }
        public FakeHarness Harness { get; }

        public static async Task<ProcessScenario> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "asteria-process-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var stateDatabasePath = Path.Combine(root, "state-machine.db");
            var registryPath = Path.Combine(root, "asteria-registry.db");
            var apiAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.Api.dll");
            var clientAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.AsteriaClient.dll");
            var statePort = GetUnusedPort();
            var harnessPort = GetUnusedPort();
            var stateMachineProcess = StartStateMachine(apiAssembly, stateDatabasePath, statePort);
            var stateMachineHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{statePort}/") };
            await WaitForStateMachineAsync(stateMachineHttp, stateMachineProcess);

            var harness = new FakeHarness();
            var harnessApplication = WebApplication.CreateBuilder().Build();
            harnessApplication.Urls.Add($"http://127.0.0.1:{harnessPort}");
            harnessApplication.MapPost("/executions", harness.HandleExecutionAsync);
            await harnessApplication.StartAsync();
            var scenario = new ProcessScenario(
                root,
                stateDatabasePath,
                registryPath,
                stateMachineProcess,
                stateMachineHttp,
                harnessApplication,
                $"http://127.0.0.1:{harnessPort}/",
                apiAssembly,
                clientAssembly,
                harness);
            await scenario.StateMachine.EnsureDefinitionAsync(DefinitionJson);
            return scenario;
        }

        public async Task<AsteriaReleaseSnapshot> AssessAsync(
            string releaseId,
            bool requireEmergencyApproval = false)
        {
            await using var registry = new AsteriaReleaseRegistry(RegistryPath);
            var material = new AsteriaReleaseMaterial(
                "AsteriaPortal",
                "4.2.0",
                "staging",
                "Emergency",
                "Large",
                true,
                true,
                "Synthetic process-recovery acceptance release",
                "gate14-test@example.invalid",
                new DateTimeOffset(2027, 3, 15, 10, 0, 0, TimeSpan.Zero));
            _ = await registry.CreateReleaseAsync(
                releaseId, material, Now, "asteria-release-governance", "5.0");
            using var decisionHttp = new HttpClient(new DecisionHandler(registry, snapshot =>
                CreateAssessmentResponse(snapshot, requireEmergencyApproval)))
            {
                BaseAddress = new Uri("http://decision-test/")
            };
            return await new AsteriaAssessmentCoordinator(
                registry,
                StateMachine,
                new AsteriaDecisionApiClient(decisionHttp),
                DefinitionJson).ResumeAsync(releaseId);
        }

        public async Task<AsteriaReleaseSnapshot> PrepareReadyReleaseAsync(string releaseId)
        {
            var release = await AssessAsync(releaseId, requireEmergencyApproval: true);
            await using var registry = new AsteriaReleaseRegistry(RegistryPath);
            await using var ledger = new AsteriaActivityLedger(RegistryPath);
            await ledger.InitializeAsync();
            var bindings = VerificationBindings();
            using var harnessHttp = new HttpClient
            {
                BaseAddress = new Uri(_harnessUrl)
            };
            foreach (var concernId in new[] { "FunctionalVerification", "SecurityVerification" })
            {
                _ = await new AsteriaVerificationCoordinator(
                    registry, ledger, StateMachine, harnessHttp, DefinitionJson, bindings)
                    .ExecuteAsync(releaseId, concernId);
            }

            var approvals = new AsteriaApprovalRegistry(RegistryPath);
            await approvals.InitializeAsync();
            var reviewerToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var emergencyToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            var credentials = new AsteriaApprovalCredentialRegistry(
                "process-acceptance-authority-v1",
                [
                    new AsteriaApprovalCredential("process-reviewer", "ReleaseReviewer", reviewerToken),
                    new AsteriaApprovalCredential("process-emergency-authoriser", "EmergencyReleaseAuthoriser", emergencyToken)
                ]);
            var approvalGateway = new AsteriaApprovalGateway(
                registry,
                ledger,
                approvals,
                credentials,
                StateMachine,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = DefinitionJson });
            Assert.True(credentials.TryAuthenticate(reviewerToken, out var reviewer));
            Assert.True(credentials.TryAuthenticate(emergencyToken, out var emergencyAuthoriser));
            Assert.NotNull(reviewer);
            Assert.NotNull(emergencyAuthoriser);
            foreach (var approval in new[]
                     {
                         (Principal: reviewer!, Concern: "ReleaseReviewApproval", Suffix: "review"),
                         (Principal: emergencyAuthoriser!, Concern: "EmergencyApproval", Suffix: "emergency")
                     })
            {
                var response = await approvalGateway.SubmitAsync(
                    approval.Principal,
                    new AsteriaApprovalSubmission(
                        release.ReleaseId,
                        release.AssessmentGeneration,
                        release.MaterialDigest,
                        approval.Concern,
                        "process-" + approval.Suffix + "-" + Guid.NewGuid().ToString("N"),
                        "process-" + approval.Suffix + "-key-" + Guid.NewGuid().ToString("N"),
                        "Approved",
                        "process-acceptance-" + approval.Suffix));
                Assert.Equal("Accepted", response.Activity.Status);
                Assert.Equal("Approved", response.Decision.Decision);
            }
            _ = await new AsteriaReadinessCoordinator(
                registry,
                ledger,
                approvals,
                StateMachine,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["5.0"] = DefinitionJson },
                bindings).ConfirmAsync(releaseId);
            return (await registry.GetReleaseAsync(releaseId))!;
        }

        public async Task<AsteriaDeploymentResult> ExecuteDeploymentAsync(AsteriaReleaseSnapshot release)
        {
            await using var registry = new AsteriaReleaseRegistry(RegistryPath);
            await using var ledger = new AsteriaActivityLedger(RegistryPath);
            await ledger.InitializeAsync();
            using var harnessHttp = new HttpClient { BaseAddress = new Uri(_harnessUrl) };
            var bindings = DeploymentBindings();
            return await new AsteriaDeploymentCoordinator(
                registry, ledger, StateMachine, harnessHttp, DefinitionJson, bindings)
                .ExecuteAsync(release.ReleaseId);
        }

        public OwnedProcess StartCoordinator(
            string releaseId,
            string? boundary = null,
            string? syncDirectory = null,
            string? runId = null)
            => StartWorkCoordinator("verify", releaseId, boundary, syncDirectory, runId);

        public OwnedProcess StartWorkCoordinator(
            string command,
            string releaseId,
            string? boundary = null,
            string? syncDirectory = null,
            string? runId = null,
            string concernId = "FunctionalVerification",
            string? harnessUrl = null,
            string? descriptionId = null,
            string synchronizationEnvironment = "Development")
        {
            var startInfo = NewProcessStartInfo(_clientAssembly);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add(releaseId);
            if (command == "verify")
            {
                startInfo.ArgumentList.Add(concernId);
            }
            SetCoordinatorEnvironment(startInfo, harnessUrl, descriptionId);
            if (boundary is not null)
            {
                startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = synchronizationEnvironment;
                startInfo.Environment["ASTERIA_TEST_SYNCHRONIZATION"] = "true";
                startInfo.Environment["ASTERIA_TEST_SYNCHRONIZATION_BOUNDARY"] = boundary;
                startInfo.Environment["ASTERIA_TEST_SYNCHRONIZATION_DIRECTORY"] =
                    syncDirectory ?? throw new ArgumentNullException(nameof(syncDirectory));
                startInfo.Environment["ASTERIA_TEST_SYNCHRONIZATION_RUN_ID"] =
                    runId ?? throw new ArgumentNullException(nameof(runId));
            }
            else
            {
                startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION");
                startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION_BOUNDARY");
                startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION_DIRECTORY");
                startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION_RUN_ID");
                startInfo.Environment.Remove("ASPNETCORE_ENVIRONMENT");
            }
            return new OwnedProcess(Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start Asteria coordinator child process."));
        }

        public OwnedProcess StartAmendmentCoordinator(
            string releaseId,
            string amendmentId,
            int expectedBaseGeneration,
            string materialPath)
        {
            var startInfo = NewProcessStartInfo(_clientAssembly);
            startInfo.ArgumentList.Add("amend");
            startInfo.ArgumentList.Add(releaseId);
            startInfo.ArgumentList.Add(amendmentId);
            startInfo.ArgumentList.Add(expectedBaseGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(materialPath);
            SetCoordinatorEnvironment(startInfo);
            startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION");
            startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION_BOUNDARY");
            startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION_DIRECTORY");
            startInfo.Environment.Remove("ASTERIA_TEST_SYNCHRONIZATION_RUN_ID");
            startInfo.Environment.Remove("ASPNETCORE_ENVIRONMENT");
            return new OwnedProcess(Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start Asteria amendment child process."));
        }

        private void SetCoordinatorEnvironment(
            ProcessStartInfo startInfo,
            string? harnessUrl = null,
            string? descriptionId = null)
        {
            startInfo.Environment["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = TrustedKey;
            startInfo.Environment["DURABLE_STATE_MACHINE_API_URL"] = _stateMachineHttp.BaseAddress!.ToString();
            startInfo.Environment["DURABLE_STATE_MACHINE_DB"] = StateDatabasePath;
            startInfo.Environment["ASTERIA_RELEASE_REGISTRY_PATH"] = RegistryPath;
            startInfo.Environment["GENERIC_HARNESS_API_URL"] = harnessUrl ?? _harnessUrl;
            startInfo.Environment["ASTERIA_FUNCTIONAL_VERIFICATION_PROVIDER_IDENTITY"] =
                "synthetic-functional-process-provider";
            startInfo.Environment["ASTERIA_FUNCTIONAL_VERIFICATION_PROVIDER_ID"] = ProviderId;
            startInfo.Environment["ASTERIA_FUNCTIONAL_VERIFICATION_API_DESCRIPTION_VERSION_ID"] =
                DescriptionId;
            startInfo.Environment["ASTERIA_FUNCTIONAL_VERIFICATION_OPERATION_KEY"] =
                VerificationOperation;
            startInfo.Environment["ASTERIA_SECURITY_VERIFICATION_PROVIDER_IDENTITY"] =
                "synthetic-security-process-provider";
            startInfo.Environment["ASTERIA_SECURITY_VERIFICATION_PROVIDER_ID"] = ProviderId;
            startInfo.Environment["ASTERIA_SECURITY_VERIFICATION_API_DESCRIPTION_VERSION_ID"] = DescriptionId;
            startInfo.Environment["ASTERIA_SECURITY_VERIFICATION_OPERATION_KEY"] = "POST:/security-verification";
            SetHarnessBinding(startInfo, "DEPLOYMENT", "synthetic-deployment-process-provider", DeploymentOperation, descriptionId);
            SetHarnessBinding(startInfo, "DEPLOYMENT_STATUS", "synthetic-deployment-status-provider", DeploymentStatusOperation, descriptionId);
            SetHarnessBinding(startInfo, "DEPLOYMENT_IDEMPOTENCY_LOOKUP", "synthetic-deployment-key-provider", DeploymentKeyLookupOperation, descriptionId);
            SetHarnessBinding(startInfo, "HEALTH", "synthetic-health-process-provider", HealthOperation, descriptionId);
        }

        private static void SetHarnessBinding(
            ProcessStartInfo startInfo,
            string prefix,
            string providerIdentity,
            string operationKey,
            string? descriptionId)
        {
            var environmentPrefix = "ASTERIA_" + prefix;
            startInfo.Environment[environmentPrefix + "_PROVIDER_IDENTITY"] = providerIdentity;
            startInfo.Environment[environmentPrefix + "_PROVIDER_ID"] = ProviderId;
            startInfo.Environment[environmentPrefix + "_API_DESCRIPTION_VERSION_ID"] =
                descriptionId ?? DescriptionId;
            startInfo.Environment[environmentPrefix + "_OPERATION_KEY"] = operationKey;
        }

        public async ValueTask DisposeAsync()
        {
            await _harnessApplication.StopAsync();
            await _harnessApplication.DisposeAsync();
            _stateMachineHttp.Dispose();
            if (!_stateMachineProcess.HasExited)
            {
                _stateMachineProcess.Kill(entireProcessTree: true);
                await _stateMachineProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            _stateMachineProcess.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(Root))
            {
                for (var attempt = 0; File.Exists(file) && attempt < 30; attempt++)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException) when (attempt < 29)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100));
                    }
                }
                if (File.Exists(file))
                {
                    throw new IOException($"Could not clean isolated process-acceptance file '{file}'.");
                }
            }
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                Directory.Delete(directory, recursive: true);
            }
            Directory.Delete(Root);
        }

        private static Process StartStateMachine(string apiAssembly, string databasePath, int port)
        {
            var startInfo = NewProcessStartInfo(apiAssembly);
            startInfo.Environment["DURABLE_STATE_MACHINE_DB"] = databasePath;
            startInfo.Environment["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = TrustedKey;
            startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start State Machine API child process.");
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process;
        }

        private static async Task WaitForStateMachineAsync(HttpClient client, Process process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!timeout.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"State Machine API child exited with code {process.ExitCode}.");
                }
                try
                {
                    using var response = await client.GetAsync("health", timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
            throw new TimeoutException("State Machine API child did not become healthy.");
        }

        private static int GetUnusedPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static ProcessStartInfo NewProcessStartInfo(string assembly)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(assembly);
            return startInfo;
        }
    }

    private sealed class ProviderProcessServices : IAsyncDisposable
    {
        private readonly Process _providerProcess;
        private readonly Process _harnessProcess;
        private readonly HttpClient _providerClient;
        private readonly HttpClient _harnessClient;

        private ProviderProcessServices(
            string providerDatabasePath,
            string harnessDatabasePath,
            Process providerProcess,
            Process harnessProcess,
            HttpClient providerClient,
            HttpClient harnessClient)
        {
            ProviderDatabasePath = providerDatabasePath;
            HarnessDatabasePath = harnessDatabasePath;
            _providerProcess = providerProcess;
            _harnessProcess = harnessProcess;
            _providerClient = providerClient;
            _harnessClient = harnessClient;
            ProviderUrl = providerClient.BaseAddress!.ToString();
            HarnessUrl = harnessClient.BaseAddress!.ToString();
        }

        public string ProviderDatabasePath { get; }
        public string HarnessDatabasePath { get; }
        public string ProviderUrl { get; }
        public string HarnessUrl { get; }
        public int ProviderProcessId => _providerProcess.Id;
        public int HarnessProcessId => _harnessProcess.Id;

        public static async Task<ProviderProcessServices> StartAsync(string root)
        {
            var providerDatabasePath = Path.Combine(root, "synthetic-provider.db");
            var harnessDatabasePath = Path.Combine(root, "generic-harness.db");
            var providerPort = GetUnusedPort();
            var harnessPort = GetUnusedPort();
            var providerUrl = $"http://127.0.0.1:{providerPort}/";
            var harnessUrl = $"http://127.0.0.1:{harnessPort}/";
            var providerAssembly = Path.Combine(AppContext.BaseDirectory, "DurableStateMachine.AsteriaSyntheticProvider.dll");
            var harnessAssembly = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "GenericHarness", "Harness.Api",
                "bin", "Debug", "net10.0", "Harness.Api.dll"));
            if (!File.Exists(providerAssembly))
            {
                throw new FileNotFoundException("The synthetic provider child assembly was not built.", providerAssembly);
            }
            if (!File.Exists(harnessAssembly))
            {
                throw new FileNotFoundException("The Generic Harness child assembly was not built.", harnessAssembly);
            }

            var providerProcess = StartChild(
                providerAssembly,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = providerUrl.TrimEnd('/'),
                    ["ASTERIA_SYNTHETIC_PROVIDER_MODE"] = "deployment",
                    ["ASTERIA_SYNTHETIC_DEPLOYMENT_DB"] = providerDatabasePath
                });
            var harnessProcess = StartChild(
                harnessAssembly,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = harnessUrl.TrimEnd('/'),
                    ["ConnectionStrings__Harness"] =
                        new SqliteConnectionStringBuilder { DataSource = harnessDatabasePath }.ToString()
                });
            var providerClient = new HttpClient { BaseAddress = new Uri(providerUrl) };
            var harnessClient = new HttpClient { BaseAddress = new Uri(harnessUrl) };
            var services = new ProviderProcessServices(
                providerDatabasePath,
                harnessDatabasePath,
                providerProcess,
                harnessProcess,
                providerClient,
                harnessClient);
            try
            {
                await WaitForEndpointAsync(providerClient, "health");
                await WaitForEndpointAsync(harnessClient, "openapi/v1.json");
                return services;
            }
            catch
            {
                await services.DisposeAsync();
                throw;
            }
        }

        public async Task<string> RegisterDeploymentApiAsync()
        {
            var document = JsonSerializer.Serialize(new
            {
                openapi = "3.0.1",
                info = new { title = "Asteria synthetic deployment", version = "1.0.0" },
                servers = new[] { new { url = ProviderUrl.TrimEnd('/') } },
                paths = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["/deployments"] = new
                    {
                        post = new
                        {
                            operationId = "requestDeployment",
                            requestBody = new
                            {
                                required = true,
                                content = new Dictionary<string, object>(StringComparer.Ordinal)
                                {
                                    ["application/json"] = new
                                    {
                                        schema = new
                                        {
                                            type = "object",
                                            additionalProperties = true
                                        }
                                    }
                                }
                            },
                            responses = new Dictionary<string, object>
                            {
                                ["202"] = JsonResponse("Accepted"),
                                ["200"] = JsonResponse("Already admitted")
                            }
                        }
                    },
                    ["/deployments/{deploymentId}"] = new
                    {
                        get = new
                        {
                            operationId = "getDeploymentStatus",
                            parameters = new[]
                            {
                                new
                                {
                                    name = "deploymentId",
                                    @in = "path",
                                    required = true,
                                    schema = new { type = "string" }
                                }
                            },
                            responses = new Dictionary<string, object>
                            {
                                ["200"] = JsonResponse("Deployment status")
                            }
                        }
                    },
                    ["/deployments/by-idempotency-key/{idempotencyKey}"] = new
                    {
                        get = new
                        {
                            operationId = "lookupDeploymentByIdempotencyKey",
                            parameters = new[]
                            {
                                new
                                {
                                    name = "idempotencyKey",
                                    @in = "path",
                                    required = true,
                                    schema = new { type = "string" }
                                }
                            },
                            responses = new Dictionary<string, object>
                            {
                                ["200"] = JsonResponse("Deployment status")
                            }
                        }
                    }
                }
            });
            using var response = await _harnessClient.PostAsync(
                $"providers/{ProviderId}/api-descriptions",
                new StringContent(document, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(body);
            var description = json.RootElement.GetProperty("apiDescription").GetProperty("id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(description));
            var operations = json.RootElement.GetProperty("operations");
            foreach (var key in new[]
                     {
                         DeploymentOperation,
                         DeploymentStatusOperation,
                         DeploymentKeyLookupOperation
                     })
            {
                Assert.Contains(operations.EnumerateArray(), operation =>
                    operation.GetProperty("key").GetString() == key &&
                    operation.GetProperty("supportStatus").GetInt32() == 0);
            }
            return description!;
        }

        private static object JsonResponse(string description) => new
        {
            description,
            content = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["application/json"] = new
                {
                    schema = new
                    {
                        type = "object",
                        additionalProperties = true
                    }
                }
            }
        };

        public async Task<JsonElement[]> ListProviderExecutionsAsync()
        {
            return await _harnessClient.GetFromJsonAsync<JsonElement[]>(
                       $"providers/{ProviderId}/executions")
                   ?? throw new InvalidDataException("Generic Harness returned no provider execution list.");
        }

        public async Task<HttpResponseMessage> GetExecutionResourceAsync(string executionId, string resource)
        {
            return await _harnessClient.GetAsync(
                $"executions/{Uri.EscapeDataString(executionId)}/{Uri.EscapeDataString(resource)}");
        }

        public async Task<ProviderDeploymentRecord> ReadProviderRecordAsync(string idempotencyKey)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!timeout.IsCancellationRequested)
            {
                using var response = await _providerClient.GetAsync(
                    $"deployments/by-idempotency-key/{Uri.EscapeDataString(idempotencyKey)}",
                    timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var record = await response.Content.ReadFromJsonAsync<ProviderDeploymentRecord>(
                        cancellationToken: timeout.Token)
                        ?? throw new InvalidDataException("Synthetic provider returned an empty deployment record.");
                    if (record.Status == "Succeeded")
                    {
                        return record;
                    }
                }
                else if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    response.EnsureSuccessStatusCode();
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
            throw new TimeoutException(
                $"Synthetic deployment '{idempotencyKey}' did not reach authoritative Succeeded status.");
        }

        public async Task<int> CountProviderDeploymentsAsync(string idempotencyKey)
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = ProviderDatabasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM SyntheticDeployments WHERE IdempotencyKey = $key;
                """;
            command.Parameters.AddWithValue("$key", idempotencyKey);
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        private static Process StartChild(string assembly, IReadOnlyDictionary<string, string> environment)
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(assembly);
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start child service '{assembly}'.");
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process;
        }

        private static async Task WaitForEndpointAsync(HttpClient client, string path)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using var response = await client.GetAsync(path, timeout.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
            throw new TimeoutException($"Child service did not respond successfully at '{path}'.");
        }

        public async ValueTask DisposeAsync()
        {
            _providerClient.Dispose();
            _harnessClient.Dispose();
            if (!_providerProcess.HasExited)
            {
                _providerProcess.Kill(entireProcessTree: true);
                await _providerProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (!_harnessProcess.HasExited)
            {
                _harnessProcess.Kill(entireProcessTree: true);
                await _harnessProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            _providerProcess.Dispose();
            _harnessProcess.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed record ProviderDeploymentRecord(
        string DeploymentId,
        string IdempotencyKey,
        string ReleaseId,
        int AssessmentGeneration,
        string MaterialDigest,
        string Product,
        string TargetVersion,
        string Environment,
        string Status);

    private sealed record ObservationOutcome(string Status, string BusinessResult, HttpStatusCode HttpStatus);
    private sealed record ApprovalClientResult(
        HttpStatusCode StatusCode,
        string PayloadEvidenceReference);

    private sealed class OwnedProcess(Process process) : IDisposable
    {
        public Process Process { get; } = process;

        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(10_000);
            }

            Process.Dispose();
        }
    }

    private sealed class FakeHarness
    {
        private int _verificationRequests;
        private int _deploymentRequests;
        private int _externalDeploymentMutations;
        private readonly ConcurrentDictionary<string, DeploymentRequest> _deployments =
            new(StringComparer.Ordinal);

        public int VerificationRequestCount => Volatile.Read(ref _verificationRequests);
        public int DeploymentRequestCount => Volatile.Read(ref _deploymentRequests);
        public int ExternalDeploymentMutationCount => Volatile.Read(ref _externalDeploymentMutations);

        public async Task<IResult> HandleExecutionAsync(HttpRequest request)
        {
            using var body = await JsonDocument.ParseAsync(request.Body);
            var root = body.RootElement;
            var providerId = root.GetProperty("providerId").GetString()!;
            var descriptionId = root.GetProperty("apiDescriptionVersionId").GetString()!;
            var operationKey = root.GetProperty("operationKey").GetString()!;
            var executionId = Guid.NewGuid().ToString();
            var statusCode = 200;
            string result;
            if (operationKey is VerificationOperation or "POST:/security-verification")
            {
                var work = root.GetProperty("body");
                Interlocked.Increment(ref _verificationRequests);
                result = JsonSerializer.Serialize(new
                {
                    releaseId = work.GetProperty("releaseId").GetString(),
                    assessmentGeneration = work.GetProperty("assessmentGeneration").GetInt32(),
                    materialDigest = work.GetProperty("materialDigest").GetString(),
                    workflowInstanceId = work.GetProperty("workflowInstanceId").GetString(),
                    concernId = work.GetProperty("concernId").GetString(),
                    activityId = work.GetProperty("activityId").GetString(),
                    expectedOperationId = work.GetProperty("expectedOperationId").GetString(),
                    result = "Passed",
                    evidenceId = "process-test-evidence-" + Guid.NewGuid().ToString("N")
                });
            }
            else if (operationKey == DeploymentOperation)
            {
                var work = root.GetProperty("body");
                var idempotencyKey = work.GetProperty("idempotencyKey").GetString()!;
                Interlocked.Increment(ref _deploymentRequests);
                var deployment = new DeploymentRequest(
                    idempotencyKey,
                    work.GetProperty("releaseId").GetString()!,
                    work.GetProperty("assessmentGeneration").GetInt32(),
                    work.GetProperty("materialDigest").GetString()!,
                    work.GetProperty("product").GetString()!,
                    work.GetProperty("targetVersion").GetString()!,
                    work.GetProperty("environment").GetString()!,
                    "synthetic-deployment-" + idempotencyKey[..12]);
                if (_deployments.TryAdd(idempotencyKey, deployment))
                {
                    Interlocked.Increment(ref _externalDeploymentMutations);
                }
                statusCode = StatusCodes.Status202Accepted;
                result = JsonSerializer.Serialize(new
                {
                    deploymentId = deployment.DeploymentId,
                    idempotencyKey,
                    status = "Pending"
                });
            }
            else if (operationKey is DeploymentStatusOperation or DeploymentKeyLookupOperation)
            {
                DeploymentRequest? deployment = null;
                if (operationKey == DeploymentStatusOperation)
                {
                    var id = root.GetProperty("pathParameters").GetProperty("deploymentId").GetString();
                    deployment = _deployments.Values.SingleOrDefault(item => item.DeploymentId == id);
                }
                else
                {
                    var key = root.GetProperty("pathParameters").GetProperty("idempotencyKey").GetString();
                    if (key is not null)
                    {
                        _deployments.TryGetValue(key, out deployment);
                    }
                }
                if (deployment is null)
                {
                    statusCode = StatusCodes.Status404NotFound;
                    result = JsonSerializer.Serialize(new { error = "No authoritative deployment record exists." });
                }
                else
                {
                    result = JsonSerializer.Serialize(new
                    {
                        deploymentId = deployment.DeploymentId,
                        idempotencyKey = deployment.IdempotencyKey,
                        releaseId = deployment.ReleaseId,
                        assessmentGeneration = deployment.AssessmentGeneration,
                        materialDigest = deployment.MaterialDigest,
                        product = deployment.Product,
                        targetVersion = deployment.TargetVersion,
                        environment = deployment.Environment,
                        status = "Succeeded"
                    });
                }
            }
            else if (operationKey == HealthOperation)
            {
                var query = root.GetProperty("queryParameters");
                result = JsonSerializer.Serialize(new
                {
                    deploymentId = query.GetProperty("deploymentId").GetString(),
                    observedVersion = query.GetProperty("targetVersion").GetString(),
                    environment = query.GetProperty("environment").GetString(),
                    result = "Passed",
                    checkedAt = DateTimeOffset.UtcNow,
                    evidenceId = "process-health-evidence-" + Guid.NewGuid().ToString("N")
                });
            }
            else
            {
                throw new InvalidOperationException($"Unexpected isolated Harness operation '{operationKey}'.");
            }
            return Results.Json(new
            {
                execution = new
                {
                    id = executionId,
                    providerId,
                    apiDescriptionVersionId = descriptionId,
                    operationKey,
                    state = 2
                },
                providerResponse = new
                {
                    statusCode,
                    contentType = "application/json",
                    body = result,
                    truncated = false
                }
            }, statusCode: StatusCodes.Status201Created);
        }

        private sealed record DeploymentRequest(
            string IdempotencyKey,
            string ReleaseId,
            int AssessmentGeneration,
            string MaterialDigest,
            string Product,
            string TargetVersion,
            string Environment,
            string DeploymentId);
    }

    private sealed class DecisionHandler(
        AsteriaReleaseRegistry registry,
        Func<AsteriaReleaseSnapshot, string> responseFactory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var content = await JsonDocument.ParseAsync(
                await request.Content!.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var releaseId = content.RootElement.GetProperty("releaseId").GetString()
                ?? throw new InvalidDataException("Decision request has no release identity.");
            var release = await registry.GetReleaseAsync(releaseId, cancellationToken)
                ?? throw new KeyNotFoundException($"Release '{releaseId}' was not found.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseFactory(release), Encoding.UTF8, "application/json")
            };
        }
    }
}
