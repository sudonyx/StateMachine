using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DurableStateMachine.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate13DecisionBindingTests
{
    private const string TrustedKey = "gate13-test-ingestion-key";
    private const string DecisionSource = "Orion Layered Decision API";

    [Fact]
    public async Task Accepted_decision_is_bound_once_to_its_expected_operation_and_original_evidence()
    {
        using var host = new TestApiHost();
        var client = host.Client;
        var instanceId = await CreateAssessmentReadyInstance(client);
        const string operationId = "decision-operation-accepted";
        await RegisterOperation(client, instanceId, operationId, "EligibilityAssessmentResult", 1, DecisionSource);
        var observation = DecisionObservation(
            instanceId,
            operationId,
            "EligibilityAssessmentResult",
            "Present",
            "Eligible",
            "orion-evaluation-accepted");

        using var accepted = await PostInput(client, instanceId, observation);
        Assert.Equal("Accepted", accepted.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Approved", accepted.RootElement.GetProperty("instance").GetProperty("currentState").GetString());

        using var duplicate = await PostInput(client, instanceId, observation);
        Assert.Equal("Duplicate", duplicate.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Approved", duplicate.RootElement.GetProperty("instance").GetProperty("currentState").GetString());

        var operations = await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/expected-operations");
        Assert.NotNull(operations);
        Assert.Equal("Resolved", operations.Single(item =>
            item.GetProperty("operationId").GetString() == operationId).GetProperty("status").GetString());

        var attempts = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/attempts");
        Assert.NotNull(attempts);
        var assessment = attempts.Single(item =>
            item.GetProperty("event").GetString() == "AssessmentResult");
        var decisionEvidence = assessment.GetProperty("evaluationEvidence").GetProperty("decisionEvidence");
        Assert.Equal("orion-evaluation-accepted",
            decisionEvidence.GetProperty("externalEvaluationId").GetString());
        Assert.Equal("Eligible", decisionEvidence.GetProperty("outcome").GetProperty("stringValue").GetString());
        Assert.Contains("ORION_POLICY_VERSION_1",
            decisionEvidence.GetProperty("policyVersionIdentities").EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Contains("originalResponse", decisionEvidence.GetProperty("originalResponseJson").GetString());
    }

    [Fact]
    public async Task Unresolved_decision_and_unavailable_decision_service_leave_assessment_unapproved()
    {
        using var host = new TestApiHost();
        var client = host.Client;

        var unresolvedInstanceId = await CreateAssessmentReadyInstance(client);
        const string unresolvedOperationId = "decision-operation-unresolved";
        await RegisterOperation(client, unresolvedInstanceId, unresolvedOperationId,
            "EligibilityAssessmentResult", 1, DecisionSource);
        var unresolved = DecisionObservation(
            unresolvedInstanceId,
            unresolvedOperationId,
            "EligibilityAssessmentResult",
            "Missing",
            null,
            "orion-evaluation-unresolved");
        using var refused = await PostInput(client, unresolvedInstanceId, unresolved);
        Assert.Equal("Accepted", refused.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("UnderAssessment", refused.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
        var unresolvedEvidence = refused.RootElement.GetProperty("attempt")
            .GetProperty("evaluationEvidence").GetProperty("decisionEvidence");
        Assert.Equal("Missing", unresolvedEvidence.GetProperty("outcomeState").GetString());
        Assert.Contains("applicantTradingMonths",
            unresolvedEvidence.GetProperty("missingFacts").EnumerateArray().Select(item => item.GetString()));

        var unavailableInstanceId = await CreateAssessmentReadyInstance(client);
        const string unavailableOperationId = "decision-operation-unavailable";
        await RegisterOperation(client, unavailableInstanceId, unavailableOperationId,
            "EligibilityAssessmentResult", 1, DecisionSource);
        using var unavailableClient = new HttpClient(new UnavailableDecisionHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            unavailableClient.PostAsJsonAsync("http://decision-api-unavailable.invalid/orion-equipment-leases/layered",
                new { workflowInstanceId = unavailableInstanceId }));

        var expected = await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{unavailableInstanceId}/expected-operations");
        Assert.NotNull(expected);
        Assert.Equal("Expected", expected.Single(item =>
            item.GetProperty("operationId").GetString() == unavailableOperationId)
            .GetProperty("status").GetString());
        using var current = await ReadJson(client, $"/api/instances/{unavailableInstanceId}");
        Assert.Equal("UnderAssessment", current.RootElement.GetProperty("currentState").GetString());

        using var delayedResult = await PostInput(client, unavailableInstanceId, DecisionObservation(
            unavailableInstanceId,
            unavailableOperationId,
            "EligibilityAssessmentResult",
            "Present",
            "Eligible",
            "orion-evaluation-after-outage"));
        Assert.Equal("Accepted",
            delayedResult.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Approved",
            delayedResult.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Duplicate_harness_reservation_result_advances_its_expected_concern_once()
    {
        using var host = new TestApiHost();
        var client = host.Client;
        var instanceId = await CreateAssessmentReadyInstance(client);
        const string assessmentOperationId = "decision-operation-for-reservation";
        await RegisterOperation(client, instanceId, assessmentOperationId,
            "EligibilityAssessmentResult", 1, DecisionSource);

        using var assessment = await PostInput(client, instanceId, DecisionObservation(
            instanceId,
            assessmentOperationId,
            "EligibilityAssessmentResult",
            "Present",
            "Eligible",
            "orion-evaluation-for-reservation"));
        Assert.Equal("Accepted",
            assessment.RootElement.GetProperty("message").GetProperty("status").GetString());

        using var request = await PostInput(client, instanceId, new
        {
            messageId = "reservation-request-for-duplicate-result",
            type = "RequestAssetReservation",
            expectedRevision = 2L
        }, "/commands");
        Assert.Equal("Accepted", request.RootElement.GetProperty("attempt").GetProperty("status").GetString());
        const string operationId = "harness-reservation-result";
        await RegisterOperation(client, instanceId, operationId, "EquipmentReserved", 2,
            "synthetic-reservation-provider");
        var reservationResult = new
        {
            messageId = "harness-reservation-result-message",
            observationId = "harness-reservation-result-observation",
            operationId,
            type = "EquipmentReserved",
            expectedRevision = 2L,
            correlationId = operationId,
            source = "synthetic-reservation-provider",
            evidence = new { reservationId = "reservation-gate13", assetId = "asset-gate13" }
        };

        using var accepted = await PostInput(client, instanceId, reservationResult);
        Assert.Equal("Accepted", accepted.RootElement.GetProperty("message").GetProperty("status").GetString());
        using var duplicate = await PostInput(client, instanceId, reservationResult);
        Assert.Equal("Duplicate", duplicate.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("Reserved",
            await GetConcernState(client, instanceId, "assetAvailability"));
        var concernHistory = await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/concerns/history");
        Assert.NotNull(concernHistory);
        Assert.Single(concernHistory, item =>
            item.GetProperty("transitionId").GetString() == "confirm-equipment-reservation");
    }

    [Fact]
    public async Task Decision_received_after_cancellation_is_preserved_as_a_refusal()
    {
        using var host = new TestApiHost();
        var client = host.Client;
        var instanceId = await CreateAssessmentReadyInstance(client);
        const string operationId = "decision-operation-stale";
        await RegisterOperation(client, instanceId, operationId, "EligibilityAssessmentResult", 1, DecisionSource);

        using var cancelled = await PostInput(client, instanceId, new
        {
            messageId = "cancel-before-decision",
            type = "CancelApplication",
            expectedRevision = 1L
        }, "/commands");
        Assert.Equal("Cancelled", cancelled.RootElement.GetProperty("instance").GetProperty("currentState").GetString());

        using var lateDecision = await PostInput(client, instanceId, DecisionObservation(
            instanceId,
            operationId,
            "EligibilityAssessmentResult",
            "Present",
            "Eligible",
            "orion-evaluation-stale"));
        Assert.NotEqual("Accepted",
            lateDecision.RootElement.GetProperty("message").GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            lateDecision.RootElement.GetProperty("message").GetProperty("explanation").GetString()));
        Assert.Equal("Cancelled", lateDecision.RootElement.GetProperty("instance").GetProperty("currentState").GetString());
    }

    private static async Task<string> CreateAssessmentReadyInstance(HttpClient client)
    {
        var instanceId = $"gate13-{Guid.NewGuid():N}";
        using var created = await client.PostAsJsonAsync("/api/instances", new
        {
            id = instanceId,
            definitionId = "orion-equipment-lease",
            definitionVersion = "6.1"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var submitted = await PostInput(client, instanceId, new
        {
            messageId = $"submit-{instanceId}",
            type = "SubmitApplication",
            expectedRevision = 0L
        }, "/commands");
        Assert.Equal("Accepted", submitted.RootElement.GetProperty("attempt").GetProperty("status").GetString());

        await RegisterOperation(client, instanceId, $"documents-{instanceId}", "DocumentsVerified", 1,
            "gate13-test-document-service");
        using var documents = await PostInput(client, instanceId, new
        {
            messageId = $"documents-{instanceId}",
            type = "DocumentsVerified",
            expectedRevision = 1L,
            operationId = $"documents-{instanceId}",
            correlationId = $"documents-{instanceId}",
            source = "gate13-test-document-service",
            evidence = new { documentSetDigest = "sha256:gate13-test-documents" }
        });
        Assert.Equal("Accepted", documents.RootElement.GetProperty("message").GetProperty("status").GetString());
        return instanceId;
    }

    private static object DecisionObservation(
        string instanceId,
        string operationId,
        string type,
        string outcomeState,
        string? outcome,
        string evaluationId)
    {
        var response = JsonSerializer.Serialize(new
        {
            evaluationId,
            outcomeState,
            outcome,
            workflowBinding = new { workflowInstanceId = instanceId, expectedOperationId = operationId },
            originalResponse = "immutable Orion decision response"
        });
        var versionIdentity = "ORION_POLICY_VERSION_1";
        var evidence = new DecisionEvidenceRequest(
            DecisionSource,
            evaluationId,
            outcomeState,
            outcome is null ? null : JsonSerializer.SerializeToElement(outcome),
            new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.Zero),
            "2025-01-10",
            new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.Zero),
            "sha256:orion-fact-set",
            [versionIdentity],
            "Gate 13 decision API integration test",
            response,
            outcomeState == "Missing" ? ["applicantTradingMonths"] : [],
            [],
            []);

        return new
        {
            messageId = $"decision-message-{evaluationId}",
            observationId = evaluationId,
            operationId,
            type,
            expectedRevision = 1L,
            correlationId = operationId,
            source = DecisionSource,
            facts = new
            {
                checksComplete = true,
                eligibilityDecision = outcome ?? "Unresolved"
            },
            evidence = new
            {
                decisionEvaluationId = evaluationId,
                inputFactSetDigest = "sha256:orion-fact-set",
                policyVersionIdentities = new[] { versionIdentity },
                outcomeState
            },
            decisionEvidence = evidence
        };
    }

    private static async Task RegisterOperation(
        HttpClient client,
        string instanceId,
        string operationId,
        string observationType,
        long revision,
        string sourceIdentity)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/instances/{instanceId}/expected-operations",
            new
            {
                operationId,
                observationType,
                sourceIdentity,
                expectedRevision = revision
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<JsonDocument> PostInput(
        HttpClient client,
        string instanceId,
        object input,
        string routeSuffix = "/observations")
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/instances/{instanceId}{routeSuffix}",
            input);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> ReadJson(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> GetConcernState(
        HttpClient client,
        string instanceId,
        string concernId)
    {
        using var concerns = await ReadJson(client, $"/api/instances/{instanceId}/concerns");
        return concerns.RootElement.GetProperty("concerns").EnumerateArray()
            .Single(item => item.GetProperty("concernId").GetString() == concernId)
            .GetProperty("currentState").GetString();
    }

    private sealed class TestApiHost : IDisposable
    {
        private readonly string _database = Path.Combine(Path.GetTempPath(), $"dsm-gate13-{Guid.NewGuid():N}.db");
        private readonly string _providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-gate13-providers-{Guid.NewGuid():N}");
        private readonly string? _previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        private readonly string? _previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR");
        private readonly string? _previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        private readonly WebApplicationFactory<Program> _factory;

        public TestApiHost()
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", _database);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", _providerDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
            _factory = new WebApplicationFactory<Program>();
            Client = _factory.CreateClient();
            Client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
            var definition = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v6.1.json"));
            using var response = Client.PostAsync("/api/definitions",
                    new StringContent(definition, System.Text.Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException($"Could not register the Gate 13 definition: {response.StatusCode}.");
        }

        public HttpClient Client { get; }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", _previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", _previousProviderDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", _previousKey);
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                var path = _database + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(_providerDirectory))
                Directory.Delete(_providerDirectory, recursive: true);
        }
    }

    private sealed class UnavailableDecisionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Controlled Decision API outage."));
    }
}
