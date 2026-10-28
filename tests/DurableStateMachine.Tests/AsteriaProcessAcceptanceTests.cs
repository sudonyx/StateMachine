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
            ["FunctionalVerification"] = CreateBinding(VerificationOperation),
            ["SecurityVerification"] = CreateBinding("POST:/security-verification")
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
            string concernId = "FunctionalVerification")
        {
            var startInfo = NewProcessStartInfo(_clientAssembly);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add(releaseId);
            if (command == "verify")
            {
                startInfo.ArgumentList.Add(concernId);
            }
            SetCoordinatorEnvironment(startInfo);
            if (boundary is not null)
            {
                startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
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

        private void SetCoordinatorEnvironment(ProcessStartInfo startInfo)
        {
            startInfo.Environment["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = TrustedKey;
            startInfo.Environment["DURABLE_STATE_MACHINE_API_URL"] = _stateMachineHttp.BaseAddress!.ToString();
            startInfo.Environment["DURABLE_STATE_MACHINE_DB"] = StateDatabasePath;
            startInfo.Environment["ASTERIA_RELEASE_REGISTRY_PATH"] = RegistryPath;
            startInfo.Environment["GENERIC_HARNESS_API_URL"] = _harnessUrl;
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
            SetHarnessBinding(startInfo, "DEPLOYMENT", "synthetic-deployment-process-provider", DeploymentOperation);
            SetHarnessBinding(startInfo, "DEPLOYMENT_STATUS", "synthetic-deployment-status-provider", DeploymentStatusOperation);
            SetHarnessBinding(startInfo, "DEPLOYMENT_IDEMPOTENCY_LOOKUP", "synthetic-deployment-key-provider", DeploymentKeyLookupOperation);
            SetHarnessBinding(startInfo, "HEALTH", "synthetic-health-process-provider", HealthOperation);
        }

        private static void SetHarnessBinding(
            ProcessStartInfo startInfo,
            string prefix,
            string providerIdentity,
            string operationKey)
        {
            var environmentPrefix = "ASTERIA_" + prefix;
            startInfo.Environment[environmentPrefix + "_PROVIDER_IDENTITY"] = providerIdentity;
            startInfo.Environment[environmentPrefix + "_PROVIDER_ID"] = ProviderId;
            startInfo.Environment[environmentPrefix + "_API_DESCRIPTION_VERSION_ID"] = DescriptionId;
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
