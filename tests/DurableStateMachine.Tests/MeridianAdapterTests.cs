using System.Net;
using System.Text;
using System.Text.Json;
using DurableStateMachine.MeridianClient;

namespace DurableStateMachine.Tests;

public sealed class MeridianAdapterTests
{
    [Fact]
    public async Task Parses_the_layered_contract_and_builds_a_generic_evidence_submission()
    {
        string? capturedRequestBody = null;
        using var client = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            capturedRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidResponse, Encoding.UTF8, "application/json")
            };
        }));
        var adapter = new MeridianDecisionAdapter(client);
        var knownAt = DateTimeOffset.Parse("2026-10-14T15:37:00Z");

        var decision = await adapter.EvaluateAsync(
            new Uri("http://localhost:5267/"),
            "2026-10-14",
            knownAt);

        Assert.Equal("00000000-0000-0000-0000-000000000001", decision.EvaluationId);
        Assert.Equal("Proceed", decision.Outcome?.GetValue<string>());
        Assert.Equal("SHA256-FACT-DIGEST", decision.InputFactSetDigest);
        Assert.Equal(["00000000-0000-0000-0000-000000000002"], decision.SelectedPolicyVersionIds);
        Assert.Contains("supplierCategory", decision.MissingFacts);
        Assert.Contains("sanctionsScreeningStatus", decision.UnknownFacts);
        Assert.Contains("insuranceStatus", decision.UnavailableFacts);

        using var requestDocument = JsonDocument.Parse(capturedRequestBody!);
        Assert.Equal("2026-10-14", requestDocument.RootElement.GetProperty("businessEffectiveAt").GetString());
        Assert.Equal(knownAt, requestDocument.RootElement.GetProperty("knownAt").GetDateTimeOffset());

        var submission = decision.CreateEventSubmission("DecisionAvailable");
        Assert.Equal("Proceed", submission["facts"]!["decisionOutcome"]!.GetValue<string>());
        Assert.Equal("Present", submission["decisionEvidence"]!["outcomeState"]!.GetValue<string>());
        Assert.Equal(decision.OriginalResponseJson,
            submission["decisionEvidence"]!["originalResponseJson"]!.GetValue<string>());
        Assert.Equal(decision.SelectedPolicyVersionIds,
            submission["decisionEvidence"]!["policyVersionIdentities"]!.AsArray()
                .Select(value => value!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task Preserves_a_null_decision_as_null_and_does_not_retry_the_non_idempotent_operation()
    {
        var requestCount = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidResponse.Replace("\"finalDecision\": \"Proceed\"", "\"finalDecision\": null", StringComparison.Ordinal),
                    Encoding.UTF8, "application/json")
            });
        }));
        var adapter = new MeridianDecisionAdapter(client);

        var decision = await adapter.EvaluateAsync(
            new Uri("http://localhost:5267/"),
            "2026-10-14",
            DateTimeOffset.Parse("2026-10-14T15:37:00Z"));

        var submission = decision.CreateEventSubmission("DecisionAvailable");
        Assert.Equal("ExplicitNull", decision.OutcomeState);
        Assert.Null(submission["facts"]!["decisionOutcome"]);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task Surfaces_failed_layered_calls_without_success_shaped_fallbacks()
    {
        using var client = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("evaluation store unavailable")
            })));
        var adapter = new MeridianDecisionAdapter(client);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => adapter.EvaluateAsync(
            new Uri("http://localhost:5267/"),
            "2026-10-14",
            DateTimeOffset.Parse("2026-10-14T15:37:00Z")));

        Assert.Contains("evaluation store unavailable", exception.Message);
    }

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
            "knownAt": "2026-10-14T15:37:00Z",
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
