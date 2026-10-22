using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DurableStateMachine.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate7HarnessTests
{
    [Fact]
    public async Task Harness_http_client_uses_documented_registration_execution_and_inspection_routes()
    {
        var handler = new RecordingHarnessHandler();
        using var httpClient = new HttpClient(handler);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:BaseUrl"] = "http://harness.test"
            })
            .Build();
        var harness = new HarnessExecutionClient(httpClient, configuration);
        using var description = JsonDocument.Parse(
            """{"openapi":"3.0.3","info":{"title":"provider","version":"1.0.0"},"servers":[{"url":"http://provider.test"}],"paths":{}}""");

        await harness.RegisterApiDescriptionAsync(
            "11111111-1111-1111-1111-111111111111", description.RootElement, CancellationToken.None);
        await harness.GetCallableOperationsAsync(
            "22222222-2222-2222-2222-222222222222", CancellationToken.None);
        await harness.RequestExecutionAsync(new HarnessExecutionRequest(
            "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222",
            "POST:/notifications",
            null,
            "stable-key",
            1,
            [],
            null,
            null,
            null,
            JsonSerializer.SerializeToElement(new { operationId = "stable-key" })),
            CancellationToken.None);
        await harness.GetExecutionAsync("execution-id", CancellationToken.None);
        await harness.GetInspectionAsync("execution-id", CancellationToken.None);
        await harness.GetAttemptsAsync("execution-id", CancellationToken.None);
        await harness.GetEvidenceAsync("execution-id", CancellationToken.None);
        await harness.EvaluateReplaySafetyAsync("execution-id", CancellationToken.None);

        Assert.Equal(
        [
            "POST providers/11111111-1111-1111-1111-111111111111/api-descriptions",
            "GET api-descriptions/22222222-2222-2222-2222-222222222222/callable-operations",
            "POST executions",
            "GET executions/execution-id",
            "GET executions/execution-id/inspection",
            "GET executions/execution-id/attempts",
            "GET executions/execution-id/evidence",
            "POST executions/execution-id/replay"
        ], handler.Requests);
        Assert.Equal("application/json", handler.RegistrationContentType);
    }

    [Fact]
    public async Task Harness_execution_uses_exact_capability_binding_and_accepts_verified_provider_result()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Success);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        using var response = await runtime.StartOfferAsync(instanceId, "offer-through-harness");
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ObservationAccepted", result.GetProperty("status").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", result.GetProperty("apiDescriptionVersionId").GetString());
        Assert.Equal("PUT:/offers/{operationId}", result.GetProperty("harnessOperationKey").GetString());
        Assert.Equal("harness-execution-success", result.GetProperty("harnessExecutionId").GetString());
        Assert.Equal(2, result.GetProperty("harnessExecutionState").GetInt32());
        Assert.Equal(0, result.GetProperty("operationReplaySafety").GetInt32());
        Assert.Equal(0, result.GetProperty("idempotencyDecision").GetInt32());
        Assert.Equal(0, result.GetProperty("replayDecision").GetInt32());
        Assert.Equal("ObservedAndValidated", result.GetProperty("outcomeConfidence").GetString());

        var attempt = result.GetProperty("attempts")[0];
        Assert.Equal("Succeeded", attempt.GetProperty("providerOutcome").GetString());
        Assert.Equal("harness-execution-success", attempt.GetProperty("harnessExecutionId").GetString());
        Assert.Equal("offer-document", attempt.GetProperty("providerEvidence").GetProperty("documentId").GetString());
        Assert.Equal("issue-offer-after-eligibility", attempt.GetProperty("workflowTransitionId").GetString());
        Assert.Equal(2, attempt.GetProperty("workflowRevisionAfter").GetInt64());
        Assert.Contains("harness-attempt-1", attempt.GetProperty("harnessAttemptIds")
            .EnumerateArray().Select(id => id.GetString()));
        Assert.Contains("harness-evidence-1", attempt.GetProperty("harnessEvidenceIds")
            .EnumerateArray().Select(id => id.GetString()));
        Assert.Equal("AwaitingOfferAcceptance",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        var request = runtime.Handler.ExecutionRequest
            ?? throw new InvalidOperationException("The harness request was not captured.");
        Assert.Equal("11111111-1111-1111-1111-111111111111", request.GetProperty("providerId").GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", request.GetProperty("apiDescriptionVersionId").GetString());
        Assert.Equal("PUT:/offers/{operationId}", request.GetProperty("operationKey").GetString());
        Assert.Equal(1, request.GetProperty("maxAttempts").GetInt32());
        Assert.Empty(request.GetProperty("retryableFailureCodes").EnumerateArray());
        Assert.Equal("synthetic-customer-1",
            request.GetProperty("body").GetProperty("customerReference").GetString());
        Assert.Equal("synthetic-offer-1",
            request.GetProperty("body").GetProperty("offerReference").GetString());
        var key = result.GetProperty("harnessIdempotencyKey").GetString();
        Assert.Equal(key, request.GetProperty("idempotencyKey").GetString());
        Assert.Equal(key,
            request.GetProperty("pathParameters").GetProperty("operationId").GetString());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);

        var inspectionResponse = await runtime.Client.GetAsync(
            $"/api/instances/{instanceId}/effects/offer-through-harness/inspection");
        Assert.Equal(HttpStatusCode.OK, inspectionResponse.StatusCode);
        var inspection = await inspectionResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, inspection.GetProperty("replaySafetyEvaluation")
            .GetProperty("replayDecision").GetInt32());
        Assert.False(inspection.GetProperty("replaySafetyEvaluation")
            .GetProperty("providerCalled").GetBoolean());
        using var duplicate = await runtime.StartOfferAsync(instanceId, "offer-through-harness");
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);

        await runtime.RestartApiAsync();
        var afterRestart = await runtime.GetEffectAsync(instanceId, "offer-through-harness");
        Assert.Equal("harness-execution-success", afterRestart.GetProperty("harnessExecutionId").GetString());
        Assert.Equal("ObservationAccepted", afterRestart.GetProperty("status").GetString());
        Assert.Equal(0, runtime.Handler.ExecutionPostCount);
        using var resumed = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/effects/offer-through-harness/resume",
            content: null);
        Assert.True(resumed.StatusCode == HttpStatusCode.OK,
            await resumed.Content.ReadAsStringAsync());
        Assert.Equal("ObservationAccepted",
            (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(0, runtime.Handler.ExecutionPostCount);
    }

    [Fact]
    public async Task Missing_harness_remote_evidence_is_reported_without_losing_local_effect_record()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.RemoteEvidenceUnavailable);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        using var effectResponse = await runtime.StartOfferAsync(instanceId, "remote-evidence-unavailable");
        Assert.Equal(HttpStatusCode.OK, effectResponse.StatusCode);
        var effect = await effectResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OutcomeUnknown", effect.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, effect.GetProperty("harnessExecutionId").ValueKind);

        var inspection = await runtime.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{instanceId}/effects/remote-evidence-unavailable/inspection");
        Assert.Equal("Unavailable", inspection.GetProperty("remoteEvidenceStatus").GetString());
        Assert.True(inspection.GetProperty("correlationAvailable").GetBoolean());
        Assert.Equal(effect.GetProperty("harnessExecutionId").GetString(),
            inspection.GetProperty("remoteExecutionReference").GetString());
        Assert.Equal(4, inspection.GetProperty("unavailableReferences").GetArrayLength());
        Assert.Equal("OutcomeUnknown",
            inspection.GetProperty("operation").GetProperty("status").GetString());
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
    }

    [Fact]
    public async Task Verified_harness_result_is_durable_before_gate5_acceptance_and_resumes_once()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Success);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        await runtime.ArmInputFaultAsync(instanceId, "AfterExternalResultStagingCommit");

        using (var interrupted = await runtime.StartOfferAsync(instanceId, "staged-offer"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        }

        var beforeResume = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/staged-observations");
        var staged = Assert.Single(beforeResume!);
        var stagedId = staged.GetProperty("id").GetString()!;
        Assert.Equal("Received", staged.GetProperty("status").GetString());
        Assert.Equal("verified-harness-adapter", staged.GetProperty("sourceAuthority").GetString());
        Assert.Equal("harness-execution-success", staged.GetProperty("harnessExecutionId").GetString());
        Assert.Equal("offer-document", staged.GetProperty("capability").GetString());
        Assert.Contains("\"documentId\":\"offer-document\"", staged.GetProperty("evidenceJson").GetString());
        Assert.Contains("harness-evidence-1", staged.GetProperty("evidenceReferencesJson").GetString());
        Assert.DoesNotContain("test-key-", staged.GetRawText());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);

        var assessment = await runtime.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{instanceId}/recovery");
        Assert.Contains(assessment.GetProperty("unresolvedStagedObservations").EnumerateArray(), item =>
            item.GetProperty("stagedResultId").GetString() == stagedId);
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        await runtime.RestartApiAsync();
        using var resumed = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/staged-observations/{stagedId}/resume", content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal("Accepted",
            (await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message")
                .GetProperty("status").GetString());
        using var repeated = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/staged-observations/{stagedId}/resume", content: null);
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(0, runtime.Handler.ExecutionPostCount);
        Assert.Equal("AwaitingOfferAcceptance",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
        var history = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/history");
        Assert.Equal(2, history!.Length);
        var afterResume = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/staged-observations");
        Assert.Equal("Accepted", Assert.Single(afterResume!).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Staged_success_after_cancellation_is_preserved_but_refused_by_gate5()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Success);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        await runtime.ArmInputFaultAsync(instanceId, "AfterExternalResultStagingCommit");
        using (var interrupted = await runtime.StartOfferAsync(instanceId, "result-before-cancel"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        }

        using (var cancelled = await runtime.Client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/commands", new
            {
                messageId = "cancel-after-result-staged",
                type = "CancelApplication",
                expectedRevision = 1
            }))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }
        var staged = Assert.Single(await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/staged-observations") ?? []);
        using var resumed = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/staged-observations/{staged.GetProperty("id").GetString()}/resume",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        using var result = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
        Assert.Equal("Stale", result.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Cancelled",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
        var finalStaged = Assert.Single(await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/staged-observations") ?? []);
        Assert.Equal("Refused", finalStaged.GetProperty("status").GetString());
        Assert.Contains("\"documentId\":\"offer-document\"",
            finalStaged.GetProperty("evidenceJson").GetString());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
    }

    [Fact]
    public async Task Cancellation_during_harness_execution_preserves_and_refuses_the_late_result()
    {
        using var runtime = await TestRuntime.StartAsync(
            HarnessResponse.Success,
            holdExecutionResponse: true);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        const string operationId = "cancel-during-harness-execution";
        var effectRequest = runtime.StartOfferAsync(instanceId, operationId);

        await runtime.Handler.WaitForExecutionRequestAsync(TimeSpan.FromSeconds(10));
        var pending = await runtime.GetEffectAsync(instanceId, operationId);
        Assert.Equal("OutcomeUnknown", pending.GetProperty("status").GetString());
        Assert.Equal(operationId, pending.GetProperty("operationId").GetString());

        using (var cancelled = await runtime.Client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/commands", new
            {
                messageId = "cancel-while-harness-pending",
                type = "CancelApplication",
                expectedRevision = 1
            }))
        {
            Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        }
        Assert.Equal("Cancelled", (await runtime.GetInstanceAsync(instanceId))
            .GetProperty("currentState").GetString());

        runtime.Handler.ReleaseExecutionResponse();
        using var lateResult = await effectRequest.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, lateResult.StatusCode);
        Assert.Equal("Cancelled", (await runtime.GetInstanceAsync(instanceId))
            .GetProperty("currentState").GetString());
        var staged = Assert.Single(await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/staged-observations") ?? []);
        Assert.Equal("Refused", staged.GetProperty("status").GetString());
        Assert.Contains("\"documentId\":\"offer-document\"",
            staged.GetProperty("evidenceJson").GetString());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
    }

    [Theory]
    [InlineData("BeforeExternalResultStaging", false, false)]
    [InlineData("DuringExternalResultStagingPersistence", false, false)]
    [InlineData("BeforeStagedObservationEvaluation", true, false)]
    [InlineData("DuringStagedObservationFinalization", true, false)]
    [InlineData("AfterStagedObservationCommit", true, true)]
    public async Task Staged_result_fault_boundaries_preserve_only_committed_work(
        string point,
        bool stagingCommitted,
        bool observationCommitted)
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Success);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        await runtime.ArmInputFaultAsync(instanceId, point);
        using (var interrupted = await runtime.StartOfferAsync(instanceId, $"fault-{point}"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        }

        var staged = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/staged-observations");
        if (!stagingCommitted)
        {
            Assert.Empty(staged ?? []);
            using var resumedEffect = await runtime.Client.PostAsync(
                $"/api/instances/{instanceId}/effects/fault-{point}/resume", content: null);
            Assert.Equal(HttpStatusCode.OK, resumedEffect.StatusCode);
        }
        else
        {
            var stored = Assert.Single(staged!);
            var stagedId = stored.GetProperty("id").GetString()!;
            Assert.Equal(observationCommitted ? "Accepted" : "Received",
                stored.GetProperty("status").GetString());
            using var resumed = await runtime.Client.PostAsync(
                $"/api/instances/{instanceId}/staged-observations/{stagedId}/resume", content: null);
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            Assert.Equal("Accepted",
                (await resumed.Content.ReadFromJsonAsync<JsonElement>())
                    .GetProperty("message").GetProperty("status").GetString());
        }

        Assert.Equal("AwaitingOfferAcceptance",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
        Assert.Equal(2, (await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/history") ?? []).Length);
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
    }

    [Fact]
    public async Task Harness_rejection_is_not_converted_to_a_successful_workflow_observation()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Rejected);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        using var response = await runtime.StartOfferAsync(instanceId, "rejected-offer");
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HarnessRejected", result.GetProperty("status").GetString());
        Assert.Equal(5, result.GetProperty("harnessExecutionState").GetInt32());
        Assert.Equal("harness-execution-rejected", result.GetProperty("harnessExecutionId").GetString());
        Assert.Equal("HarnessRejected", result.GetProperty("attempts")[0]
            .GetProperty("providerOutcome").GetString());
        Assert.Equal(1, result.GetProperty("attempts")[0]
            .GetProperty("harnessRejectionReason").GetInt32());
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
        var inbound = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/inbound-messages");
        Assert.DoesNotContain(inbound ?? [], message =>
            message.TryGetProperty("correlationId", out var correlation) &&
            correlation.GetString() == "rejected-offer");
    }

    [Fact]
    public async Task Object_shaped_harness_provider_response_body_is_accepted()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.SuccessObjectBody);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        using var response = await runtime.StartOfferAsync(instanceId, "object-provider-body");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ObservationAccepted", result.GetProperty("status").GetString());
        Assert.Equal("offer-document-1", result.GetProperty("attempts")[0]
            .GetProperty("providerEffectId").GetString());
        Assert.Equal("offer-document", result.GetProperty("attempts")[0]
            .GetProperty("providerEvidence").GetProperty("documentId").GetString());
    }

    [Fact]
    public async Task Unsupported_imported_operation_is_sent_to_the_harness_and_remains_rejected()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.UnsupportedOperation);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        using var response = await runtime.StartOfferAsync(instanceId, "unsupported-offer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("HarnessRejected", result.GetProperty("status").GetString());
        Assert.Equal(0, result.GetProperty("attempts")[0]
            .GetProperty("harnessRejectionReason").GetInt32());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("operationReplaySafety").ValueKind);
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Lost_execution_response_remains_unknown_and_resume_does_not_submit_a_second_execution()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.LosePostResponse);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        using var response = await runtime.StartOfferAsync(instanceId, "lost-response-offer");
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OutcomeUnknown", result.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("harnessExecutionId").ValueKind);
        Assert.Equal("RemoteOutcomeUnknown",
            result.GetProperty("attempts")[0].GetProperty("outcomeConfidence").GetString());
        Assert.Contains("no response was received",
            result.GetProperty("attempts")[0].GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        var pending = await runtime.Client.GetFromJsonAsync<JsonElement>(
            $"/api/instances/{instanceId}/pending-work");
        Assert.Contains(pending.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("operationId").GetString() == "lost-response-offer");

        using var resumed = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/effects/lost-response-offer/resume",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Pending_intent_survives_api_host_restart_and_is_resumed_with_its_persisted_binding()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Success);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        await runtime.ArmFaultAsync("pending-after-commit", "AfterLocalIntentCommitBeforeHarness");
        using (var interrupted = await runtime.StartOfferAsync(instanceId, "pending-after-commit"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        }
        var pending = await runtime.GetEffectAsync(instanceId, "pending-after-commit");
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Empty(pending.GetProperty("attempts").EnumerateArray());
        Assert.Equal(0, runtime.Handler.ExecutionPostCount);

        await runtime.RestartApiAsync();
        var afterRestart = await runtime.GetEffectAsync(instanceId, "pending-after-commit");
        Assert.Equal("Pending", afterRestart.GetProperty("status").GetString());
        using var resumed = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/effects/pending-after-commit/resume",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        var completion = await resumed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ObservationAccepted", completion.GetProperty("status").GetString());
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
    }

    [Fact]
    public async Task Harness_accepted_response_lost_before_local_persistence_stays_unresolved_after_restart()
    {
        using var runtime = await TestRuntime.StartAsync(HarnessResponse.Success);
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        await runtime.ArmFaultAsync("lost-after-harness", "AfterHarnessResponseBeforeLocalPersistence");
        using (var interrupted = await runtime.StartOfferAsync(instanceId, "lost-after-harness"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        }
        Assert.Equal(1, runtime.Handler.ExecutionPostCount);
        var unresolved = await runtime.GetEffectAsync(instanceId, "lost-after-harness");
        Assert.Equal("OutcomeUnknown", unresolved.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("harnessExecutionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("attempts")[0]
            .GetProperty("harnessExecutionId").ValueKind);

        await runtime.RestartApiAsync();
        var restored = await runtime.GetEffectAsync(instanceId, "lost-after-harness");
        Assert.Equal("OutcomeUnknown", restored.GetProperty("status").GetString());
        using var resumed = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/effects/lost-after-harness/resume",
            content: null);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(0, runtime.Handler.ExecutionPostCount);
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
    }

    private enum HarnessResponse
    {
        Success,
        SuccessObjectBody,
        RemoteEvidenceUnavailable,
        Rejected,
        UnsupportedOperation,
        LosePostResponse
    }

    private sealed class RecordingHarnessHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? RegistrationContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path.TrimStart('/')}");
            if (path.EndsWith("/api-descriptions", StringComparison.Ordinal))
            {
                RegistrationContentType = request.Content?.Headers.ContentType?.MediaType;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        """{"apiDescription":{"id":"22222222-2222-2222-2222-222222222222"},"operations":[]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
            if (path.EndsWith("/callable-operations", StringComparison.Ordinal) ||
                path.EndsWith("/attempts", StringComparison.Ordinal) ||
                path.EndsWith("/evidence", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            }
            var isCreated = request.Method == HttpMethod.Post && path == "/executions";
            return new HttpResponseMessage(isCreated ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent(
                    isCreated
                        ? """{"execution":{},"providerResponse":null}"""
                        : """{}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class TestHarnessHandler(
        HarnessResponse responseMode,
        JsonElement? priorExecutionRequest = null,
        bool holdExecutionResponse = false) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _executionRequestReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseExecutionResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonElement? ExecutionRequest { get; private set; } = priorExecutionRequest;
        public int ExecutionPostCount { get; private set; }

        public Task WaitForExecutionRequestAsync(TimeSpan timeout) =>
            _executionRequestReceived.Task.WaitAsync(timeout);

        public void ReleaseExecutionResponse() => _releaseExecutionResponse.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath
                ?? throw new InvalidOperationException("The harness request has no URI.");
            if (path.EndsWith("/callable-operations", StringComparison.Ordinal))
            {
                if (responseMode == HarnessResponse.UnsupportedOperation)
                {
                    return Json(HttpStatusCode.OK, "[]");
                }
                return Json(HttpStatusCode.OK, """
                    [{
                      "id": "operation-1",
                      "apiDescriptionVersionId": "22222222-2222-2222-2222-222222222222",
                      "operationId": "generateOfferDocument",
                      "key": "PUT:/offers/{operationId}",
                      "method": "PUT",
                      "path": "/offers/{operationId}",
                      "supportStatus": 0,
                      "replaySafety": 0
                    }]
                    """);
            }
            if (path == "/executions" && request.Method == HttpMethod.Post)
            {
                ExecutionPostCount++;
                using var body = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                ExecutionRequest = body.RootElement.Clone();
                if (holdExecutionResponse)
                {
                    _executionRequestReceived.TrySetResult(true);
                    await _releaseExecutionResponse.Task.WaitAsync(cancellationToken);
                }
                if (responseMode == HarnessResponse.LosePostResponse)
                {
                    throw new HttpRequestException("Connection closed before the invocation response arrived.");
                }

                var idempotencyKey = ExecutionRequest!.Value.GetProperty("idempotencyKey").GetString()!;
                var isSuccess = responseMode is HarnessResponse.Success or
                    HarnessResponse.SuccessObjectBody or HarnessResponse.RemoteEvidenceUnavailable;
                var execution = isSuccess
                    ? SuccessExecution(idempotencyKey)
                    : RejectedExecution(
                        idempotencyKey,
                        responseMode == HarnessResponse.UnsupportedOperation ? 0 : 1);
                var providerResponse = responseMode switch
                {
                    HarnessResponse.Success => """
                      {"statusCode":200,"contentType":"application/json","body":"{\"outcome\":\"Succeeded\",\"effectId\":\"offer-document-1\",\"evidence\":{\"documentId\":\"offer-document\",\"offerDigest\":\"sha256:test\"},\"occurredAt\":\"2026-10-16T13:00:00Z\"}","truncated":false}
                      """,
                    HarnessResponse.SuccessObjectBody => """
                      {"statusCode":200,"contentType":"application/json","body":{"outcome":"Succeeded","effectId":"offer-document-1","evidence":{"documentId":"offer-document","offerDigest":"sha256:test"},"occurredAt":"2026-10-16T13:00:00Z"},"truncated":false}
                      """,
                    HarnessResponse.RemoteEvidenceUnavailable => """
                      {"statusCode":200,"contentType":"application/json","body":"{\"outcome\":\"Succeeded\",\"effectId\":\"offer-document-1\",\"evidence\":{\"documentId\":\"offer-document\",\"offerDigest\":\"sha256:test\"},\"occurredAt\":\"2026-10-16T13:00:00Z\"}","truncated":false}
                      """,
                    _ => "null"
                };
                return Json(HttpStatusCode.Created,
                    $$"""{"execution":{{execution}},"providerResponse":{{providerResponse}}}""");
            }
            if (path.EndsWith("/inspection", StringComparison.Ordinal))
            {
                if (responseMode == HarnessResponse.RemoteEvidenceUnavailable)
                    throw new HttpRequestException("Controlled Harness inspection outage.");
                var idempotencyKey = ExecutionRequest!.Value.GetProperty("idempotencyKey").GetString()!;
                var isSuccess = responseMode is HarnessResponse.Success or
                    HarnessResponse.SuccessObjectBody or HarnessResponse.RemoteEvidenceUnavailable;
                var execution = isSuccess
                    ? SuccessExecution(idempotencyKey)
                    : RejectedExecution(
                        idempotencyKey,
                        responseMode == HarnessResponse.UnsupportedOperation ? 0 : 1);
                var confidence = isSuccess
                    ? "ObservedAndValidated"
                    : "NotApplicable";
                var operation = responseMode == HarnessResponse.UnsupportedOperation
                    ? "null"
                    : """
                      {
                        "id": "operation-1",
                        "apiDescriptionVersionId": "22222222-2222-2222-2222-222222222222",
                        "operationId": "generateOfferDocument",
                        "key": "PUT:/offers/{operationId}",
                        "method": "PUT",
                        "path": "/offers/{operationId}",
                        "supportStatus": 0,
                        "replaySafety": 0
                      }
                      """;
                return Json(HttpStatusCode.OK,
                    $$"""
                    {
                      "execution": {{execution}},
                      "operation": {{operation}},
                      "attempts": [{"id":"harness-attempt-1"}],
                      "evidence": [{"id":"harness-evidence-1"}],
                      "lastDurableCheckpoint": 11,
                      "outcomeConfidence": "{{confidence}}",
                      "unknownFacts": [],
                      "replayDecision": 0,
                      "replayInvoked": false
                    }
                    """);
            }
            if (path.EndsWith("/attempts", StringComparison.Ordinal))
            {
                if (responseMode == HarnessResponse.RemoteEvidenceUnavailable)
                    throw new HttpRequestException("Controlled Harness attempts outage.");
                return Json(HttpStatusCode.OK, """[{"id":"harness-attempt-1"}]""");
            }
            if (path.EndsWith("/evidence", StringComparison.Ordinal))
            {
                if (responseMode == HarnessResponse.RemoteEvidenceUnavailable)
                    throw new HttpRequestException("Controlled Harness evidence outage.");
                return Json(HttpStatusCode.OK, """[{"id":"harness-evidence-1"}]""");
            }
            if (path.EndsWith("/replay", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                if (responseMode == HarnessResponse.RemoteEvidenceUnavailable)
                    throw new HttpRequestException("Controlled Harness replay outage.");
                return Json(HttpStatusCode.OK,
                    """{"executionId":"harness-execution","replayDecision":0,"providerCalled":false,"reason":"No new execution performed."}""");
            }
            throw new InvalidOperationException($"Unexpected harness request {request.Method} {path}.");
        }

        private static string SuccessExecution(string idempotencyKey) => $$"""
            {
              "id": "harness-execution-success",
              "providerId": "11111111-1111-1111-1111-111111111111",
              "apiDescriptionVersionId": "22222222-2222-2222-2222-222222222222",
              "operationKey": "PUT:/offers/{operationId}",
              "credentialBindingId": null,
              "idempotencyKey": "{{idempotencyKey}}",
              "state": 2,
              "requestedAt": "2026-10-16T13:00:00Z",
              "startedAt": "2026-10-16T13:00:00Z",
              "completedAt": "2026-10-16T13:00:01Z",
              "rejectionReason": null,
              "failureCode": null,
              "failureMessage": null,
              "responseStatusCode": 200,
              "mutationSafety": 0,
              "idempotencyDecision": 0
            }
            """;

        private static string RejectedExecution(string idempotencyKey, int rejectionReason) => $$"""
            {
              "id": "harness-execution-rejected",
              "providerId": "11111111-1111-1111-1111-111111111111",
              "apiDescriptionVersionId": "22222222-2222-2222-2222-222222222222",
              "operationKey": "PUT:/offers/{operationId}",
              "credentialBindingId": null,
              "idempotencyKey": "{{idempotencyKey}}",
              "state": 5,
              "requestedAt": "2026-10-16T13:00:00Z",
              "startedAt": null,
              "completedAt": "2026-10-16T13:00:00Z",
              "rejectionReason": {{rejectionReason}},
              "failureCode": null,
              "failureMessage": null,
              "responseStatusCode": null,
              "mutationSafety": 1,
              "idempotencyDecision": 1
            }
            """;

        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class TestRuntime : IDisposable
    {
        private readonly string _directory;
        private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;
        private readonly HarnessResponse _responseMode;
        private readonly bool _holdExecutionResponse;
        private WebApplicationFactory<Program> _factory = null!;

        private TestRuntime(HarnessResponse responseMode, bool holdExecutionResponse)
        {
            _responseMode = responseMode;
            _holdExecutionResponse = holdExecutionResponse;
            _directory = Path.Combine(Path.GetTempPath(), $"dsm-gate7-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            var providerId = "11111111-1111-1111-1111-111111111111";
            var versionId = "22222222-2222-2222-2222-222222222222";
            var settings = new Dictionary<string, string?>
            {
                ["DURABLE_STATE_MACHINE_DB"] = Path.Combine(_directory, "workflow.db"),
                ["DURABLE_STATE_MACHINE_PROVIDER_DB_DIR"] = Path.Combine(_directory, "providers"),
                ["DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"] = $"test-key-{Guid.NewGuid():N}",
                ["DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS"] = "true",
                ["Harness__BaseUrl"] = "http://harness.test",
                ["Harness__Capabilities__offer-document__ProviderIdentity"] = "synthetic-offer-provider",
                ["Harness__Capabilities__offer-document__ProviderId"] = providerId,
                ["Harness__Capabilities__offer-document__ApiDescriptionVersionId"] = versionId,
                ["Harness__Capabilities__offer-document__OperationKey"] = "PUT:/offers/{operationId}"
            };
            _previousEnvironment = settings.Keys.ToDictionary(
                key => key,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);
            foreach (var (key, value) in settings)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            CreateFactory();
        }

        public static Task<TestRuntime> StartAsync(
            HarnessResponse responseMode,
            bool holdExecutionResponse = false) =>
            Task.FromResult(new TestRuntime(responseMode, holdExecutionResponse));

        public HttpClient Client { get; private set; } = null!;
        public TestHarnessHandler Handler { get; private set; } = null!;

        public async Task<string> CreateAssessmentReadyInstanceAsync()
        {
            var definitionPath = Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v1.json");
            using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(definitionPath));
            using var registration = await Client.PostAsJsonAsync("/api/definitions", definition.RootElement);
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
            var id = $"orion-gate7-{Guid.NewGuid():N}";
            using var create = await Client.PostAsJsonAsync("/api/instances", new
            {
                id,
                definitionId = "orion-equipment-lease",
                definitionVersion = "3.0",
                facts = new
                {
                    documentsComplete = true,
                    riskScore = 75,
                    equipmentValue = 50000,
                    checksComplete = true,
                    eligibilityDecision = "Eligible",
                    cancellationPermitted = true
                }
            });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var submit = await Client.PostAsJsonAsync($"/api/instances/{id}/commands", new
            {
                messageId = $"submit-{Guid.NewGuid():N}",
                type = "SubmitApplication",
                expectedRevision = 0
            });
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
            var submission = await submit.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Accepted", submission.GetProperty("message").GetProperty("status").GetString());
            return id;
        }

        public Task<HttpResponseMessage> StartOfferAsync(string instanceId, string operationId) =>
            Client.PostAsJsonAsync($"/api/instances/{instanceId}/effects", new
            {
                operationId,
                capability = "offer-document",
                customerReference = "synthetic-customer-1",
                offerReference = "synthetic-offer-1"
            });

        public async Task ArmFaultAsync(string operationId, string point)
        {
            using var response = await Client.PostAsync(
                $"/api/test-controls/effects/{Uri.EscapeDataString(operationId)}/{point}",
                content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task ArmInputFaultAsync(string instanceId, string point)
        {
            using var response = await Client.PostAsync(
                $"/api/test-controls/inputs/{Uri.EscapeDataString(instanceId)}/{point}",
                content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public Task<JsonElement> GetInstanceAsync(string instanceId) =>
            Client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");

        public Task<JsonElement> GetEffectAsync(string instanceId, string operationId) =>
            Client.GetFromJsonAsync<JsonElement>(
                $"/api/instances/{instanceId}/effects/{Uri.EscapeDataString(operationId)}");

        public async Task RestartApiAsync()
        {
            Client.Dispose();
            _factory.Dispose();
            CreateFactory();
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            foreach (var (key, value) in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private void CreateFactory()
        {
            Handler = new TestHarnessHandler(
                _responseMode,
                Handler?.ExecutionRequest,
                _holdExecutionResponse);
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                    services.AddHttpClient<HarnessExecutionClient>()
                        .ConfigurePrimaryHttpMessageHandler(() => Handler)));
            Client = _factory.CreateClient();
            Client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-State-Machine-Trusted-Ingestion-Key",
                Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY"));
        }
    }
}
