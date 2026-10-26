using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DurableStateMachine.Api;
using DurableStateMachine.AsteriaClient;
using DurableStateMachine.Core;
using Microsoft.AspNetCore.Mvc.Testing;
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

    private static string CreatePersistedResponse(AsteriaReleaseSnapshot release, string outcome)
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
