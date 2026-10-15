using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DurableStateMachine.MeridianClient;

namespace DurableStateMachine.Tests;

public sealed class MeridianAdapterTests
{
    [Fact]
    public async Task Sends_valid_idempotency_header_and_explicit_temporal_context()
    {
        string? capturedRequestBody = null;
        string? capturedIdempotencyKey = null;
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            capturedRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            capturedIdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return Ok(ValidResponse);
        }));
        var adapter = new MeridianDecisionAdapter(client);
        var assessment = Assessment("operation-01", "assessment.operation-01");

        var response = await adapter.EvaluateAsync(new Uri("http://localhost:5267/"), assessment);

        Assert.Equal(MeridianEvaluationCallStatus.Completed, response.Status);
        Assert.Equal("assessment.operation-01", capturedIdempotencyKey);
        using var requestDocument = JsonDocument.Parse(capturedRequestBody!);
        Assert.Equal("2026-10-14", requestDocument.RootElement.GetProperty("businessEffectiveAt").GetString());
        Assert.Equal("2026-10-14T15:37:00-04:00", requestDocument.RootElement.GetProperty("knownAt").GetString());
        Assert.Equal("Proceed", response.Decision!.Outcome?.GetValue<string>());
        Assert.Equal("SHA256-FACT-DIGEST", response.Decision.InputFactSetDigest);
        Assert.Equal(["00000000-0000-0000-0000-000000000002"], response.Decision.SelectedPolicyVersionIds);
        Assert.Contains("supplierCategory", response.Decision.MissingFacts);
        Assert.Contains("sanctionsScreeningStatus", response.Decision.UnknownFacts);
        Assert.Contains("insuranceStatus", response.Decision.UnavailableFacts);
        Assert.Equal(assessment.RequestJson, capturedRequestBody);

        var submission = response.Decision.CreateEventSubmission("DecisionAvailable");
        Assert.Equal("Proceed", submission["facts"]!["decisionOutcome"]!.GetValue<string>());
        Assert.Equal("Present", submission["decisionEvidence"]!["outcomeState"]!.GetValue<string>());
        Assert.Equal(response.Decision.OriginalResponseJson,
            submission["decisionEvidence"]!["originalResponseJson"]!.GetValue<string>());
        Assert.Equal(response.Decision.SelectedPolicyVersionIds,
            submission["decisionEvidence"]!["policyVersionIdentities"]!.AsArray()
                .Select(value => value!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task Explicit_retry_reuses_exact_key_request_payload_and_original_decision_evidence()
    {
        var calls = new List<(string Key, string Body)>();
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            calls.Add((
                request.Headers.GetValues("Idempotency-Key").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Ok(ValidResponse);
        }));
        var adapter = new MeridianDecisionAdapter(client);
        var assessment = Assessment("operation-repeat", "assessment-repeat");

        var first = (await adapter.EvaluateAsync(new Uri("http://localhost:5267/"), assessment)).Decision!;
        var repeated = (await adapter.EvaluateAsync(new Uri("http://localhost:5267/"), assessment)).Decision!;

        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Equal("assessment-repeat", call.Key));
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal(assessment.RequestJson, calls[0].Body);
        Assert.Equal(first.EvaluationId, repeated.EvaluationId);
        Assert.Equal(first.OriginalResponseJson, repeated.OriginalResponseJson);
        Assert.Equal(first.InputFactSetDigest, repeated.InputFactSetDigest);
        Assert.Equal(first.SelectedPolicyVersionIds, repeated.SelectedPolicyVersionIds);
    }

    [Fact]
    public void New_assessments_receive_distinct_operation_identities_and_keys()
    {
        var first = Assessment(Guid.NewGuid().ToString("N"), $"assessment-{Guid.NewGuid():N}");
        var second = Assessment(Guid.NewGuid().ToString("N"), $"assessment-{Guid.NewGuid():N}");

        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public void Validates_contract_key_grammar_and_requires_explicit_temporal_context()
    {
        Assert.True(MeridianAssessmentRequest.Create("operation", new string('a', 128),
            ValidRequestJson("2026-10-14", "2026-10-14T15:37:00Z")) is not null);
        Assert.Throws<ArgumentException>(() => Assessment("operation", ""));
        Assert.Throws<ArgumentException>(() => Assessment("operation", new string('a', 129)));
        Assert.Throws<ArgumentException>(() => Assessment("operation", "key with spaces"));
        Assert.Throws<ArgumentException>(() => MeridianAssessmentRequest.Create(
            "operation", "valid-key", """{"businessEffectiveAt":"2026-10-14","knownAt":"2026-10-14T15:37:00"}"""));
        Assert.Throws<ArgumentException>(() => MeridianAssessmentRequest.Create(
            "operation", "valid-key", """{"knownAt":"2026-10-14T15:37:00Z"}"""));
    }

    [Fact]
    public async Task Same_key_with_changed_request_surfaces_conflict_without_returning_a_decision()
    {
        var assessment = Assessment("operation-conflict", "assessment-conflict");
        var changedRequest = JsonNode.Parse(assessment.RequestJson)!.AsObject();
        changedRequest["supplierCountry"] = "DifferentExampleland";
        var changedAssessment = MeridianAssessmentRequest.Create(
            assessment.OperationId,
            assessment.IdempotencyKey,
            changedRequest.ToJsonString());
        string? sentBody = null;
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(assessment.IdempotencyKey, request.Headers.GetValues("Idempotency-Key").Single());
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("key is already bound to a different request")
            };
        }));
        var adapter = new MeridianDecisionAdapter(client);
        var exception = await Assert.ThrowsAsync<MeridianIdempotencyConflictException>(() =>
            adapter.EvaluateAsync(new Uri("http://localhost:5267/"),
                changedAssessment));

        Assert.Equal("assessment-conflict", exception.IdempotencyKey);
        Assert.Contains("different request", exception.ResponseBody);
        Assert.Equal(changedAssessment.RequestJson, sentBody);
    }

    [Fact]
    public async Task Retryable_conflict_with_retry_after_is_exposed_as_in_progress()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Conflict);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        }));
        var adapter = new MeridianDecisionAdapter(client);

        var result = await adapter.EvaluateAsync(new Uri("http://localhost:5267/"),
            Assessment("operation-pending", "assessment-pending"));

        Assert.True(result.IsInProgress);
        Assert.Equal(TimeSpan.FromSeconds(1), result.RetryAfter);
        Assert.Null(result.Decision);
    }

    [Fact]
    public async Task Transport_timeout_remains_uncertain_and_does_not_create_a_new_key_or_retry()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            throw new TaskCanceledException("simulated response timeout");
        }));
        var adapter = new MeridianDecisionAdapter(client);
        var assessment = Assessment("operation-timeout", "assessment-timeout");

        var exception = await Assert.ThrowsAsync<MeridianEvaluationUncertainException>(() =>
            adapter.EvaluateAsync(new Uri("http://localhost:5267/"), assessment));

        Assert.Equal("assessment-timeout", exception.IdempotencyKey);
        Assert.Contains("may have committed", exception.Message);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task Server_error_is_uncertain_and_never_converted_to_a_decision()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("evaluation store unavailable")
            })));
        var adapter = new MeridianDecisionAdapter(client);

        var exception = await Assert.ThrowsAsync<MeridianEvaluationUncertainException>(() =>
            adapter.EvaluateAsync(new Uri("http://localhost:5267/"),
                Assessment("operation-server-error", "assessment-server-error")));

        Assert.Contains("HTTP 500", exception.Message);
        Assert.Equal("assessment-server-error", exception.IdempotencyKey);
    }

    [Fact]
    public async Task Client_error_is_reported_as_an_api_error_not_as_a_decision_or_uncertain_success()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("invalid temporal context")
            })));
        var adapter = new MeridianDecisionAdapter(client);

        var exception = await Assert.ThrowsAsync<MeridianApiException>(() =>
            adapter.EvaluateAsync(new Uri("http://localhost:5267/"),
                Assessment("operation-bad-request", "assessment-bad-request")));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid temporal context", exception.ResponseBody);
    }

    [Fact]
    public async Task Malformed_success_response_is_uncertain_and_keeps_the_same_key_for_retry()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(Ok("""{"evidence":{"evaluationId":"not-an-id"}}"""))));
        var adapter = new MeridianDecisionAdapter(client);

        var exception = await Assert.ThrowsAsync<MeridianEvaluationUncertainException>(() =>
            adapter.EvaluateAsync(new Uri("http://localhost:5267/"),
                Assessment("operation-malformed", "assessment-malformed")));

        Assert.Equal("assessment-malformed", exception.IdempotencyKey);
        Assert.Contains("may be committed", exception.Message);
    }

    [Fact]
    public async Task Invalid_key_and_missing_temporal_context_are_rejected_before_http()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Ok(ValidResponse));
        }));
        var adapter = new MeridianDecisionAdapter(client);
        var missingTemporalContext = new MeridianAssessmentRequest
        {
            OperationId = "invalid-operation",
            IdempotencyKey = "invalid-key",
            RequestJson = """{"supplierId":"SUP-0001"}"""
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            adapter.EvaluateAsync(new Uri("http://localhost:5267/"), missingTemporalContext));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Preserves_null_business_decision_as_explicit_null_not_http_failure()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(Ok(ValidResponse.Replace("\"finalDecision\": \"Proceed\"", "\"finalDecision\": null", StringComparison.Ordinal)))));
        var adapter = new MeridianDecisionAdapter(client);

        var result = await adapter.EvaluateAsync(new Uri("http://localhost:5267/"),
            Assessment("operation-null", "assessment-null"));

        Assert.Equal(MeridianEvaluationCallStatus.Completed, result.Status);
        Assert.Equal("ExplicitNull", result.Decision!.OutcomeState);
        Assert.Null(result.Decision.Outcome);
    }

    [Fact]
    public async Task Saved_envelope_reuses_key_and_exact_request_after_reload_and_blocks_overwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-assessment-{Guid.NewGuid():N}.json");
        try
        {
            var request = Assessment("operation-envelope", "assessment-envelope");
            var envelope = MeridianAssessmentEnvelope.FromRequest(request);
            await envelope.SaveNewAsync(path);

            var loaded = await MeridianAssessmentEnvelope.LoadAsync(path);
            var loadedRequest = loaded.ToRequest();

            Assert.Equal(request.OperationId, loadedRequest.OperationId);
            Assert.Equal(request.IdempotencyKey, loadedRequest.IdempotencyKey);
            Assert.Equal(request.RequestJson, loadedRequest.RequestJson);
            await Assert.ThrowsAsync<IOException>(() => envelope.SaveNewAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static MeridianAssessmentRequest Assessment(string operationId, string key) =>
        MeridianAssessmentRequest.CreateSynthetic(
            operationId,
            key,
            "2026-10-14",
            "2026-10-14T15:37:00-04:00");

    private static string ValidRequestJson(string businessEffectiveAt, string knownAt) =>
        $$"""{"businessEffectiveAt":"{{businessEffectiveAt}}","knownAt":"{{knownAt}}"}""";

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private const string ValidResponse = """
        {
          "evidence": {
            "evaluationId": "00000000-0000-0000-0000-000000000001",
            "evaluatedAt": "2026-10-14T15:37:02Z",
            "factSet": {
              "entries": [
                { "name": "supplierCategory", "type": "String", "state": "Missing", "value": null },
                { "name": "sanctionsScreeningStatus", "type": "String", "state": "Unknown", "value": null },
                { "name": "insuranceStatus", "type": "String", "state": "Unavailable", "value": null }
              ],
              "digestAlgorithm": "SHA-256",
              "digest": "SHA256-FACT-DIGEST"
            },
            "ruleSources": [],
            "composition": {
              "effectiveRequirements": [],
              "replacedOrSuppressedRequirements": [],
              "conflicts": [],
              "businessDecision": "Proceed",
              "compositionState": "Resolved"
            },
            "finalDecision": "Proceed",
            "justification": {
              "compositionState": "Resolved",
              "businessDecision": "Proceed",
              "contributingRules": [],
              "conflicts": [],
              "unresolvedRules": []
            },
            "contributingRules": [],
            "conflicts": [],
            "unknownFacts": ["sanctionsScreeningStatus"],
            "unavailableFacts": ["insuranceStatus"],
            "validationSummary": {
              "validatedSourceCount": 1,
              "validatedRuleCount": 1,
              "invalidSourceCount": 0,
              "invalidRuleCount": 0,
              "unsupportedSourceCount": 0,
              "unsupportedRuleCount": 0
            },
            "engineMetadata": {
              "engineName": "Meridian",
              "engineVersion": "1",
              "ruleLanguageVersions": ["1"]
            },
            "businessEffectiveAt": "2026-10-14",
            "knownAt": "2026-10-14T15:37:00-04:00",
            "factAcquisition": null
          },
          "selectedPolicySourceVersionIds": [
            "00000000-0000-0000-0000-000000000002"
          ]
        }
        """;

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
