using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DurableStateMachine.AsteriaClient;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class AsteriaMilestone4BTests
{
    private const string TrustedKey = "asteria-milestone4b-test-ingestion-key";
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
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
    private static readonly string[] AssessmentEvidence =
    [
        "releaseId",
        "assessmentGeneration",
        "materialDigest",
        "parentRevision",
        "expectedOperationId",
        "decisionRequestFingerprint",
        "selectedPolicySourceVersions",
        "originalResponse",
        "policySelectionContext"
    ];

    [Fact]
    public async Task Initialize_migrates_existing_milestone3b_generation_one_records()
    {
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var original = await registry.CreateReleaseAsync("AST-MIGRATION-001", Material, Now);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE AsteriaSupersessionAttempts;
                DROP TABLE AsteriaAmendments;
                DROP TABLE AsteriaReleasePointers;
                DROP TABLE AsteriaGenerations;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await registry.InitializeAsync();
        var migrated = await registry.GetReleaseAsync(original.ReleaseId);
        var generation = await registry.GetGenerationAsync(original.ReleaseId, 1);
        Assert.NotNull(migrated);
        Assert.NotNull(generation);
        Assert.Equal(1, migrated.AssessmentGeneration);
        Assert.Equal(original.MaterialDigest, generation.MaterialDigest);
        Assert.Equal(original.WorkflowInstanceId, generation.WorkflowInstanceId);
        Assert.Equal(original.Work.WorkId, generation.Work.WorkId);
        Assert.Equal("Operational", migrated.RegistryState);
    }

    [Fact]
    public async Task Reservation_deduplicates_identity_conflicts_payload_and_suspends_admission()
    {
        var path = NewDatabasePath();
        await using var registry = new AsteriaReleaseRegistry(path);
        var initial = await registry.CreateReleaseAsync(
            "AST-RESERVATION-001", Material, Now,
            definitionVersion: "3.0");
        var changed = Material with { TargetVersion = "4.2.1" };
        var first = await registry.ReserveAmendmentAsync(
            initial.ReleaseId, "amend-1", 1, changed, "Draft", "lease-1", Now, TimeSpan.FromSeconds(30));
        var repeated = await registry.ReserveAmendmentAsync(
            initial.ReleaseId, "amend-1", 1, changed, "Superseded", "lease-2",
            Now.AddSeconds(1), TimeSpan.FromSeconds(30));

        Assert.Equal(first.NewGeneration, repeated.NewGeneration);
        Assert.Equal(first.NewWorkflowInstanceId, repeated.NewWorkflowInstanceId);
        Assert.Equal("AmendmentReserved", repeated.Phase);
        var current = await registry.GetReleaseAsync(initial.ReleaseId);
        var reserved = await registry.GetGenerationAsync(initial.ReleaseId, 2);
        Assert.NotNull(current);
        Assert.NotNull(reserved);
        Assert.Equal(1, current.AssessmentGeneration);
        Assert.Equal("AmendmentPending", current.RegistryState);
        Assert.Equal("Reserved", reserved.Work.Status);
        Assert.NotEqual(initial.WorkflowInstanceId, reserved.WorkflowInstanceId);
        Assert.False(await registry.AcquireLeaseAsync(
            initial.ReleaseId, "ordinary-coordinator", Now.AddSeconds(3), TimeSpan.FromSeconds(30)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReserveAmendmentAsync(
            initial.ReleaseId, "amend-1", 1, Material, "Draft", "lease-3",
            Now.AddSeconds(2), TimeSpan.FromSeconds(30)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReserveAmendmentAsync(
            initial.ReleaseId, "amend-2", 1, Material with { TargetVersion = "4.2.2" }, "Draft", "lease-4",
            Now.AddSeconds(2), TimeSpan.FromSeconds(30)));

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE AsteriaGenerations SET MaterialDigest = 'changed'
            WHERE ReleaseId = $releaseId AND Generation = 1;
            """;
        update.Parameters.AddWithValue("$releaseId", initial.ReleaseId);
        await Assert.ThrowsAsync<SqliteException>(() => update.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Reservation_rejects_a_stale_expected_base_generation()
    {
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync(
            "AST-STALE-BASE-001", Material, Now, definitionVersion: "3.0");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ReserveAmendmentAsync(
            release.ReleaseId, "stale-amendment", 2, Material with { TargetVersion = "4.2.1" },
            "Draft", "stale-lease", Now, TimeSpan.FromSeconds(30)));
        Assert.Contains("Expected base generation 2", error.Message);
    }

    [Fact]
    public async Task Competing_amendments_cannot_both_reserve_the_same_base_generation()
    {
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var release = await registry.CreateReleaseAsync(
            "AST-COMPETING-AMENDMENTS-001", Material, Now, definitionVersion: "3.0");
        var requests = new[]
        {
            ("competing-amendment-a", Material with { TargetVersion = "4.2.1" }, "competing-lease-a"),
            ("competing-amendment-b", Material with { TargetVersion = "4.2.2" }, "competing-lease-b")
        };
        var results = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                _ = await registry.ReserveAmendmentAsync(
                    release.ReleaseId, request.Item1, 1, request.Item2, "Draft",
                    request.Item3, Now, TimeSpan.FromSeconds(30));
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }));

        Assert.Single(results, succeeded => succeeded);
        Assert.Equal(1, (await registry.GetReleaseAsync(release.ReleaseId))!.AssessmentGeneration);
        Assert.Equal("AmendmentPending", (await registry.GetReleaseAsync(release.ReleaseId))!.RegistryState);
        Assert.NotNull(await registry.GetGenerationAsync(release.ReleaseId, 2));
        Assert.Null(await registry.GetGenerationAsync(release.ReleaseId, 3));
    }

    [Fact]
    public async Task Real_api_supersession_is_terminal_and_each_generation_has_independent_history()
    {
        using var host = new StateMachineTestHost();
        var definition = ReadV3();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var coordinator = new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(new HttpClient(new RejectDecisionApiHandler())),
            definition,
            TimeProvider.System);

        var first = await coordinator.PrepareAsync("AST-4B-001", Material);
        var initialInstance = await stateMachine.InspectGenerationAsync(first);
        Assert.Equal("UnderAssessment", initialInstance.CurrentState);
        using (var untrustedClient = host.CreateClient())
        using (var unauthorized = await untrustedClient.PostAsJsonAsync(
                   $"/api/instances/{Uri.EscapeDataString(first.WorkflowInstanceId)}/expected-operations",
                   new
                   {
                       operationId = "unauthorized-supersession-operation",
                       observationType = AsteriaStateMachineClient.SupersessionObservation,
                       sourceIdentity = AsteriaStateMachineClient.AmendmentSourceIdentity,
                       expectedRevision = initialInstance.Revision,
                       requiredEvidence = new[]
                       {
                           "amendmentId", "requestFingerprint", "expectedBaseGeneration",
                           "replacementGeneration", "replacementMaterialDigest"
                       }
                   }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unauthorized.StatusCode);
        }
        await AcceptRequirementsObservationAsync(host.Client, first, initialInstance.Revision);

        var pendingOperationId = "pending-old-assessment";
        await RegisterExpectedOperationAsync(
            host.Client, first.WorkflowInstanceId, pendingOperationId,
            AsteriaStateMachineClient.AssessmentObservation,
            AsteriaStateMachineClient.DecisionSourceIdentity,
            initialInstance.Revision + 1,
            ["releaseId"]);

        using var droppingStateMachineHttp = host.CreateClient(new DropSuccessfulPostAcknowledgementHandler(
            request => request.RequestUri?.AbsolutePath.EndsWith("/observations", StringComparison.Ordinal) == true));
        var droppingStateMachine = new AsteriaStateMachineClient(droppingStateMachineHttp, TrustedKey);
        var amendmentCoordinator = new AsteriaAmendmentCoordinator(registry, stateMachine, definition);
        var amendedMaterial = Material with { TargetVersion = "4.2.1", ReleaseSummary = "Synthetic amended release" };
        var lostAcknowledgementCoordinator = new AsteriaAmendmentCoordinator(
            registry, droppingStateMachine, definition);
        await Assert.ThrowsAsync<HttpRequestException>(() => lostAcknowledgementCoordinator.AmendAsync(
            first.ReleaseId, "AST-AMEND-1", 1, amendedMaterial));
        var persistedAmendment = await registry.GetAmendmentAsync(first.ReleaseId, "AST-AMEND-1");
        Assert.NotNull(persistedAmendment);
        Assert.Equal("NewInstanceProvisioned", persistedAmendment.Phase);
        Assert.Equal("AmendmentPending", (await registry.GetReleaseAsync(first.ReleaseId))!.RegistryState);
        using (var prematureCommand = await host.Client.PostAsJsonAsync(
                   $"/api/instances/{Uri.EscapeDataString(persistedAmendment.NewWorkflowInstanceId)}/commands",
                   new
                   {
                       messageId = "premature-new-generation-submit",
                       type = "SubmitRelease",
                       expectedRevision = 0
                   }))
        {
            Assert.True(prematureCommand.IsSuccessStatusCode);
        }
        var stillDormant = await stateMachine.GetInstanceAsync(persistedAmendment.NewWorkflowInstanceId);
        Assert.Equal("Draft", stillDormant!.CurrentState);

        var amendment = await amendmentCoordinator.AmendAsync(
            first.ReleaseId, "AST-AMEND-1", 1, amendedMaterial);
        var current = await registry.GetReleaseAsync(first.ReleaseId);
        var oldGeneration = await registry.GetGenerationAsync(first.ReleaseId, 1);
        var newGeneration = await registry.GetGenerationAsync(first.ReleaseId, 2);
        Assert.NotNull(current);
        Assert.NotNull(oldGeneration);
        Assert.NotNull(newGeneration);
        Assert.Equal("NewGenerationActivated", amendment.Phase);
        Assert.Equal(2, current.AssessmentGeneration);
        Assert.Equal("Operational", current.RegistryState);
        Assert.Equal(2, newGeneration.AssessmentGeneration);
        Assert.Equal("Draft", (await stateMachine.InspectGenerationAsync(newGeneration)).CurrentState);
        Assert.NotEqual(oldGeneration.WorkflowInstanceId, newGeneration.WorkflowInstanceId);
        Assert.Equal("Superseded", (await stateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);

        var oldHistory = await host.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/history");
        var newHistory = await host.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{Uri.EscapeDataString(newGeneration.WorkflowInstanceId)}/history");
        Assert.NotNull(oldHistory);
        Assert.NotNull(newHistory);
        Assert.Contains(oldHistory, item =>
            item.GetProperty("event").GetString() == AsteriaStateMachineClient.SupersessionObservation &&
            item.GetProperty("newState").GetString() == "Superseded");
        Assert.DoesNotContain(newHistory, item =>
            item.GetProperty("event").GetString() == AsteriaStateMachineClient.AssessmentObservation);
        Assert.Null(newGeneration.Work.ParentRevision);
        Assert.Null(newGeneration.Work.DecisionRequestJson);

        var repeated = await amendmentCoordinator.AmendAsync(
            first.ReleaseId, "AST-AMEND-1", 1, amendedMaterial);
        Assert.Equal(amendment.NewWorkflowInstanceId, repeated.NewWorkflowInstanceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => amendmentCoordinator.AmendAsync(
            first.ReleaseId, "AST-AMEND-1", 1, amendedMaterial with { TargetVersion = "4.2.9" }));

        var latest = await amendmentCoordinator.AmendAsync(
            first.ReleaseId, "AST-AMEND-2", 2, amendedMaterial with { TargetVersion = "4.2.2" });
        var generationThree = await registry.GetGenerationAsync(first.ReleaseId, 3);
        Assert.Equal(3, latest.NewGeneration);
        Assert.NotNull(generationThree);
        Assert.NotEqual(newGeneration.WorkflowInstanceId, generationThree.WorkflowInstanceId);

        var sealedInstance = await stateMachine.InspectGenerationAsync(oldGeneration);
        using var commandResponse = await host.Client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/commands",
            new { messageId = "post-supersession-command", type = "CancelRelease", expectedRevision = sealedInstance.Revision });
        Assert.True(commandResponse.IsSuccessStatusCode);
        using var commandResult = JsonDocument.Parse(await commandResponse.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", commandResult.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("terminal-state", commandResult.RootElement.GetProperty("attempt").GetProperty("reasonCode").GetString());

        using var lateObservationResponse = await host.Client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(oldGeneration.WorkflowInstanceId)}/observations",
            new
            {
                messageId = "post-supersession-observation",
                deliveryId = "post-supersession-observation",
                operationId = pendingOperationId,
                observationId = "late-observation-id",
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision = sealedInstance.Revision - 1,
                correlationId = pendingOperationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity,
                payload = new { releaseId = first.ReleaseId },
                facts = new { assessmentOutcome = "RequirementsDetermined" },
                evidence = new { releaseId = first.ReleaseId }
            });
        Assert.True(lateObservationResponse.IsSuccessStatusCode);
        using var lateObservationResult = JsonDocument.Parse(await lateObservationResponse.Content.ReadAsStringAsync());
        Assert.NotEqual("Accepted", lateObservationResult.RootElement.GetProperty("message").GetProperty("status").GetString());

        var outstanding = await stateMachine.ListExpectedOperationsAsync(oldGeneration.WorkflowInstanceId);
        Assert.Equal("Expected", Assert.Single(outstanding, item => item.OperationId == pendingOperationId).Status);
        Assert.Equal("Superseded", (await stateMachine.InspectGenerationAsync(oldGeneration)).CurrentState);
    }

    [Fact]
    public async Task A_v2_instance_refuses_amendment_without_reopening_or_relabeling_it()
    {
        using var host = new StateMachineTestHost();
        var v2 = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "definitions", "asteria-release-governance-v2.json"));
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var coordinator = new AsteriaAssessmentCoordinator(
            registry, stateMachine, new AsteriaDecisionApiClient(new HttpClient(new RejectDecisionApiHandler())), v2);
        var release = await coordinator.PrepareAsync("AST-LEGACY-V2-001", Material);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new AsteriaAmendmentCoordinator(
            registry, stateMachine, ReadV3()).AmendAsync(
                release.ReleaseId, "AST-AMEND-V2-REFUSED", 1, Material with { TargetVersion = "4.2.1" }));
        var stillCurrent = await registry.GetReleaseAsync(release.ReleaseId);
        Assert.Equal(1, stillCurrent!.AssessmentGeneration);
        Assert.Equal("2.0", stillCurrent.DefinitionVersion);
        Assert.Equal("Operational", stillCurrent.RegistryState);
        Assert.Equal("UnderAssessment", (await stateMachine.InspectGenerationAsync(stillCurrent)).CurrentState);
    }

    [Fact]
    public async Task Amendment_recovers_when_new_instance_creation_acknowledgement_is_lost()
    {
        using var host = new StateMachineTestHost();
        var definition = ReadV3();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var initial = await new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(new HttpClient(new RejectDecisionApiHandler())),
            definition).PrepareAsync("AST-LOST-CREATE-001", Material);
        using var droppingHttp = host.CreateClient(new DropSuccessfulPostAcknowledgementHandler(
            request => request.RequestUri?.AbsolutePath.EndsWith("/api/instances", StringComparison.Ordinal) == true));
        var droppingCoordinator = new AsteriaAmendmentCoordinator(
            registry, new AsteriaStateMachineClient(droppingHttp, TrustedKey), definition);
        var replacementMaterial = Material with { TargetVersion = "4.2.1" };

        await Assert.ThrowsAsync<HttpRequestException>(() => droppingCoordinator.AmendAsync(
            initial.ReleaseId, "AST-LOST-CREATE-AMENDMENT", 1, replacementMaterial));
        var reservation = await registry.GetAmendmentAsync(initial.ReleaseId, "AST-LOST-CREATE-AMENDMENT");
        Assert.NotNull(reservation);
        Assert.Equal("AmendmentReserved", reservation.Phase);
        Assert.NotNull(await stateMachine.GetInstanceAsync(reservation.NewWorkflowInstanceId));
        Assert.Equal(1, (await registry.GetReleaseAsync(initial.ReleaseId))!.AssessmentGeneration);

        var recovered = await new AsteriaAmendmentCoordinator(registry, stateMachine, definition).AmendAsync(
            initial.ReleaseId, "AST-LOST-CREATE-AMENDMENT", 1, replacementMaterial);
        Assert.Equal("NewGenerationActivated", recovered.Phase);
        Assert.Equal(2, (await registry.GetReleaseAsync(initial.ReleaseId))!.AssessmentGeneration);
    }

    [Fact]
    public async Task Amendment_recovers_pointer_activation_after_old_instance_is_sealed()
    {
        using var host = new StateMachineTestHost();
        var definition = ReadV3();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var initial = await new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(new HttpClient(new RejectDecisionApiHandler())),
            definition).PrepareAsync("AST-POINTER-RECOVERY-001", Material);
        const string amendmentId = "AST-POINTER-RECOVERY-AMENDMENT";
        const string leaseToken = "pointer-recovery-lease";
        var old = await registry.GetGenerationAsync(initial.ReleaseId, 1)
            ?? throw new InvalidDataException("Generation 1 was not recorded.");
        var oldInstance = await stateMachine.InspectGenerationAsync(old);
        var replacementMaterial = Material with { TargetVersion = "4.2.1" };
        var amendment = await registry.ReserveAmendmentAsync(
            initial.ReleaseId, amendmentId, 1, replacementMaterial, oldInstance.CurrentState,
            leaseToken, Now, TimeSpan.FromMinutes(2));
        var replacement = await registry.GetGenerationAsync(initial.ReleaseId, amendment.NewGeneration)
            ?? throw new InvalidDataException("Reserved generation was not recorded.");
        await stateMachine.EnsureDefinitionAsync(definition);
        _ = await stateMachine.EnsureInstanceAsync(replacement);
        await registry.AdvanceAmendmentPhaseAsync(
            initial.ReleaseId, amendmentId, leaseToken, "NewInstanceProvisioned", Now);
        var supersessionAttempt = await registry.GetOrCreateSupersessionAttemptAsync(
            initial.ReleaseId, amendmentId, oldInstance.Revision, leaseToken, Now);
        var supersessionAttempts = await registry.ListSupersessionAttemptsAsync(initial.ReleaseId, amendmentId);
        Assert.Contains(supersessionAttempt, supersessionAttempts);
        await stateMachine.EnsureSupersededAsync(old, replacement, amendment, supersessionAttempts);
        await registry.AdvanceAmendmentPhaseAsync(
            initial.ReleaseId, amendmentId, leaseToken, "OldInstanceSuperseded", Now);
        var interrupted = await registry.GetReleaseAsync(initial.ReleaseId);
        Assert.Equal("ActivationIncomplete", interrupted!.RegistryState);
        Assert.Equal(1, interrupted.AssessmentGeneration);
        await registry.ReleaseAmendmentLeaseAsync(initial.ReleaseId, leaseToken);

        var recovered = await new AsteriaAmendmentCoordinator(registry, stateMachine, definition).AmendAsync(
            initial.ReleaseId, amendmentId, 1, replacementMaterial);
        var activated = await registry.GetReleaseAsync(initial.ReleaseId);
        Assert.Equal("NewGenerationActivated", recovered.Phase);
        Assert.Equal(2, activated!.AssessmentGeneration);
        Assert.Equal("Operational", activated.RegistryState);
    }

    [Fact]
    public async Task Concurrent_old_observation_fences_supersession_and_recovery_uses_new_stable_revision_identity()
    {
        using var host = new StateMachineTestHost();
        var definition = ReadV3();
        await using var registry = new AsteriaReleaseRegistry(NewDatabasePath());
        var stateMachine = new AsteriaStateMachineClient(host.Client, TrustedKey);
        var initial = await new AsteriaAssessmentCoordinator(
            registry,
            stateMachine,
            new AsteriaDecisionApiClient(new HttpClient(new RejectDecisionApiHandler())),
            definition).PrepareAsync("AST-CONCURRENT-OBSERVATION-001", Material);
        var instance = await stateMachine.InspectGenerationAsync(initial);
        const string winningOperation = "concurrent-old-assessment-winner";
        const string pendingOperation = "concurrent-old-assessment-still-pending";
        await RegisterExpectedOperationAsync(
            host.Client, initial.WorkflowInstanceId, winningOperation,
            AsteriaStateMachineClient.AssessmentObservation,
            AsteriaStateMachineClient.DecisionSourceIdentity, instance.Revision, AssessmentEvidence);
        await RegisterExpectedOperationAsync(
            host.Client, initial.WorkflowInstanceId, pendingOperation,
            AsteriaStateMachineClient.AssessmentObservation,
            AsteriaStateMachineClient.DecisionSourceIdentity, instance.Revision, AssessmentEvidence);

        using var racingHttp = host.CreateClient(new AcceptObservationAfterOperationRegistrationHandler(
            () => AcceptMinimalObservationAsync(
                host.Client, initial, instance.Revision, winningOperation, "concurrent-old-observation")));
        var racedCoordinator = new AsteriaAmendmentCoordinator(
            registry, new AsteriaStateMachineClient(racingHttp, TrustedKey), definition);
        var amendedMaterial = Material with { TargetVersion = "4.2.1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => racedCoordinator.AmendAsync(
            initial.ReleaseId, "AST-CONCURRENT-AMENDMENT", 1, amendedMaterial));
        var failedAttempts = await registry.ListSupersessionAttemptsAsync(
            initial.ReleaseId, "AST-CONCURRENT-AMENDMENT");
        Assert.Single(failedAttempts);
        Assert.Equal(instance.Revision, failedAttempts[0].ExpectedRevision);
        Assert.Equal("RequirementsIdentified",
            (await stateMachine.InspectGenerationAsync(initial)).CurrentState);

        var recovered = await new AsteriaAmendmentCoordinator(registry, stateMachine, definition).AmendAsync(
            initial.ReleaseId, "AST-CONCURRENT-AMENDMENT", 1, amendedMaterial);
        var attempts = await registry.ListSupersessionAttemptsAsync(
            initial.ReleaseId, "AST-CONCURRENT-AMENDMENT");
        Assert.Equal(2, attempts.Count);
        Assert.Equal(instance.Revision + 1, attempts[1].ExpectedRevision);
        Assert.NotEqual(attempts[0].OperationId, attempts[1].OperationId);
        Assert.Equal("NewGenerationActivated", recovered.Phase);

        var sealedInstance = await stateMachine.InspectGenerationAsync(initial);
        using var lateObservation = await host.Client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(initial.WorkflowInstanceId)}/observations",
            new
            {
                messageId = "concurrent-pending-late-result",
                deliveryId = "concurrent-pending-late-result",
                operationId = pendingOperation,
                observationId = "concurrent-pending-late-result-id",
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision = instance.Revision,
                correlationId = pendingOperation,
                source = AsteriaStateMachineClient.DecisionSourceIdentity,
                payload = new { releaseId = initial.ReleaseId },
                facts = new { assessmentOutcome = "RequirementsDetermined" },
                evidence = CreateAssessmentEvidence(initial, instance.Revision, pendingOperation)
            });
        Assert.True(lateObservation.IsSuccessStatusCode);
        using var lateResult = JsonDocument.Parse(await lateObservation.Content.ReadAsStringAsync());
        Assert.NotEqual("Accepted", lateResult.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Superseded", sealedInstance.CurrentState);
    }

    [Fact]
    public void V3_has_only_predeployment_supersession_targets()
    {
        var definition = TestSupport.LoadWorkflow("asteria-release-governance-v3.json");
        Assert.Equal("3.0", definition.Version);
        Assert.Contains(definition.InputPolicy!.Observations.Keys, name => name == "SupersedeAssessment");
        Assert.Contains(definition.InputPolicy.Observations.Keys, name => name == "AsteriaAssessmentStarted");
        Assert.DoesNotContain("SubmitRelease", definition.InputPolicy.Commands.Keys);
        Assert.Contains(definition.States.Keys, state => state == "Superseded");
        Assert.True(definition.States["Superseded"].IsTerminal);
        Assert.Equal(
            new[] { "Draft", "UnderAssessment", "RequirementsIdentified", "ReviewObligationsIdentified", "AssessmentUnresolved" }
                .Order(StringComparer.Ordinal),
            definition.Transitions.Where(item => item.Event == "SupersedeAssessment")
                .Select(item => item.Source).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(definition.States.Keys, state =>
            state is "ReadyForDeployment" or "DeploymentInProgress" or "PostDeploymentVerification" or "Completed");
        Assert.DoesNotContain(definition.InputPolicy.Commands, command =>
            command.Key == "PrepareDeploymentRequest");
    }

    private static async Task AcceptRequirementsObservationAsync(
        HttpClient client,
        AsteriaReleaseSnapshot release,
        long expectedRevision)
    {
        const string operationId = "assessment-before-amendment";
        await RegisterExpectedOperationAsync(
            client,
            release.WorkflowInstanceId,
            operationId,
            AsteriaStateMachineClient.AssessmentObservation,
            AsteriaStateMachineClient.DecisionSourceIdentity,
            expectedRevision,
            [
                "releaseId",
                "assessmentGeneration",
                "materialDigest",
                "parentRevision",
                "expectedOperationId",
                "decisionRequestFingerprint",
                "selectedPolicySourceVersions",
                "originalResponse",
                "policySelectionContext"
            ]);

        using var response = await client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            new
            {
                messageId = "assessment-before-amendment-message",
                deliveryId = "assessment-before-amendment-message",
                operationId,
                observationId = "assessment-before-amendment-result",
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision,
                correlationId = operationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity,
                payload = new { outcome = "RequirementsDetermined" },
                facts = new { assessmentOutcome = "RequirementsDetermined" },
                evidence = new
                {
                    releaseId = release.ReleaseId,
                    assessmentGeneration = release.AssessmentGeneration,
                    materialDigest = release.MaterialDigest,
                    parentRevision = expectedRevision,
                    expectedOperationId = operationId,
                    decisionRequestFingerprint = "synthetic-fingerprint",
                    selectedPolicySourceVersions = Array.Empty<string>(),
                    originalResponse = new { outcome = "RequirementsDetermined" },
                    policySelectionContext = new { }
                }
            });
        Assert.True(response.IsSuccessStatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Accepted", result.RootElement.GetProperty("message").GetProperty("status").GetString());
    }

    private static async Task RegisterExpectedOperationAsync(
        HttpClient client,
        string instanceId,
        string operationId,
        string observationType,
        string sourceIdentity,
        long expectedRevision,
        string[] requiredEvidence)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(instanceId)}/expected-operations",
            new { operationId, observationType, sourceIdentity, expectedRevision, requiredEvidence });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task AcceptMinimalObservationAsync(
        HttpClient client,
        AsteriaReleaseSnapshot release,
        long expectedRevision,
        string operationId,
        string messageId)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/instances/{Uri.EscapeDataString(release.WorkflowInstanceId)}/observations",
            new
            {
                messageId,
                deliveryId = messageId,
                operationId,
                observationId = messageId + "-result",
                type = AsteriaStateMachineClient.AssessmentObservation,
                expectedRevision,
                correlationId = operationId,
                source = AsteriaStateMachineClient.DecisionSourceIdentity,
                payload = new { releaseId = release.ReleaseId },
                facts = new { assessmentOutcome = "RequirementsDetermined" },
                evidence = CreateAssessmentEvidence(release, expectedRevision, operationId)
            });
        Assert.True(response.IsSuccessStatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Accepted", result.RootElement.GetProperty("message").GetProperty("status").GetString());
    }

    private static object CreateAssessmentEvidence(
        AsteriaReleaseSnapshot release,
        long expectedRevision,
        string operationId) => new
        {
            releaseId = release.ReleaseId,
            assessmentGeneration = release.AssessmentGeneration,
            materialDigest = release.MaterialDigest,
            parentRevision = expectedRevision,
            expectedOperationId = operationId,
            decisionRequestFingerprint = "synthetic-fingerprint",
            selectedPolicySourceVersions = Array.Empty<string>(),
            originalResponse = new { outcome = "RequirementsDetermined" },
            policySelectionContext = new { }
        };

    private static string ReadV3() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "definitions", "asteria-release-governance-v3.json"));

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"asteria-m4b-{Guid.NewGuid():N}.db");

    private sealed class RejectDecisionApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class DropSuccessfulPostAcknowledgementHandler(
        Func<HttpRequestMessage, bool> matches) : DelegatingHandler
    {
        private bool _dropped;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!_dropped && request.Method == HttpMethod.Post && matches(request) && response.IsSuccessStatusCode)
            {
                _dropped = true;
                response.Dispose();
                throw new HttpRequestException("Controlled lost State Machine acknowledgement.");
            }
            return response;
        }
    }

    private sealed class AcceptObservationAfterOperationRegistrationHandler(
        Func<Task> onOperationRegistered) : DelegatingHandler
    {
        private bool _executed;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!_executed &&
                request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/expected-operations", StringComparison.Ordinal) == true &&
                response.IsSuccessStatusCode)
            {
                _executed = true;
                await onOperationRegistered();
            }
            return response;
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
}
