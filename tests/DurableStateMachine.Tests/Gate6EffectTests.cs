using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DurableStateMachine.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate6EffectTests
{
    [Fact]
    public async Task Orion_runs_three_separate_effects_through_expected_observations()
    {
        using var runtime = await TestRuntime.StartAsync();
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();

        var offer = await runtime.StartEffectAsync(instanceId, "offer-document", "offer-op-1",
            customerReference: "synthetic-customer-1", offerReference: "offer-reference-1");
        Assert.Equal("ObservationAccepted", offer.GetProperty("status").GetString());
        Assert.Equal("offer-op-1", offer.GetProperty("operationId").GetString());
        Assert.Equal("AwaitingOfferAcceptance",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
        var expectedOperations = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/expected-operations");
        Assert.NotNull(expectedOperations);
        var expectedOffer = Assert.Single(expectedOperations, operation =>
            operation.GetProperty("operationId").GetString() == "offer-op-1");
        Assert.Equal("Resolved", expectedOffer.GetProperty("status").GetString());
        Assert.Equal("synthetic-offer-provider", expectedOffer.GetProperty("sourceIdentity").GetString());

        var inbound = await runtime.GetInboundMessageArrayAsync(instanceId);
        var offerReceipt = Assert.Single(inbound, message =>
            message.TryGetProperty("correlationId", out var correlationId) &&
            correlationId.GetString() == "offer-op-1");
        Assert.Equal("Accepted", offerReceipt.GetProperty("status").GetString());
        Assert.Equal("IssueOffer", offerReceipt.GetProperty("acceptedEvent").GetString());
        Assert.Equal(offer.GetProperty("attempts")[0].GetProperty("observationReceiptId").GetString(),
            offerReceipt.GetProperty("id").GetString());

        var acceptOffer = await runtime.Client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
        {
            messageId = "accept-offer-1",
            type = "AcceptOffer",
            expectedRevision = 2,
            facts = new { offerValidAtEvaluation = true, offerEvaluationTime = DateTimeOffset.UtcNow.ToString("O") }
        });
        Assert.Equal(HttpStatusCode.OK, acceptOffer.StatusCode);

        var reservation = await runtime.StartEffectAsync(instanceId, "equipment-reservation", "reserve-op-1",
            assetId: "fictional-asset-1");
        Assert.Equal("ObservationAccepted", reservation.GetProperty("status").GetString());
        Assert.Equal("CustomerNotificationPending",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        var notification = await runtime.StartEffectAsync(instanceId, "customer-notification", "notify-op-1",
            customerReference: "synthetic-customer-1", messageTemplate: "Synthetic offer status update");
        Assert.Equal("ObservationAccepted", notification.GetProperty("status").GetString());
        Assert.Equal("Approved", (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        foreach (var (provider, operationId) in new[]
                 {
                     ("synthetic-offer-provider", "offer-op-1"),
                     ("synthetic-reservation-provider", "reserve-op-1"),
                     ("synthetic-notification-provider", "notify-op-1")
                 })
        {
            var requests = await runtime.GetProviderRequestsAsync(provider, operationId);
            Assert.Single(requests);
            Assert.Equal("Succeeded", requests[0].GetProperty("outcome").GetString());
        }
        Assert.Equal(3, Directory.GetFiles(runtime.ProviderDirectory, "*.db").Length);

        var effects = await runtime.Client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/effects");
        Assert.NotNull(effects);
        Assert.Equal(3, effects.Length);
        Assert.All(effects, effect => Assert.Equal("ObservationAccepted", effect.GetProperty("status").GetString()));
        await runtime.RestartApiAsync();
        Assert.Equal("Approved", (await runtime.GetInstanceAsync(instanceId))
            .GetProperty("currentState").GetString());
        Assert.Single(await runtime.GetProviderRequestsAsync("synthetic-notification-provider", "notify-op-1"));
    }

    [Fact]
    public async Task Crash_boundaries_preserve_intent_provider_and_local_uncertainty_separately()
    {
        using var runtime = await TestRuntime.StartAsync();
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();

        const string beforeCommit = "fault-before-intent";
        await runtime.ArmFaultAsync(beforeCommit, "BeforeLocalIntentCommit");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "offer-document", beforeCommit,
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);
        Assert.Equal("[]", await runtime.GetEffectCollectionAsync(instanceId));
        Assert.Empty(await runtime.GetProviderRequestsAsync("synthetic-offer-provider", beforeCommit));

        const string afterCommit = "fault-after-intent";
        await runtime.ArmFaultAsync(afterCommit, "AfterLocalCommitBeforeProvider");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "offer-document", afterCommit,
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);
        var pending = await runtime.GetEffectAsync(instanceId, afterCommit);
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Empty(pending.GetProperty("attempts").EnumerateArray());
        Assert.Empty(await runtime.GetProviderRequestsAsync("synthetic-offer-provider", afterCommit));
        await runtime.RestartApiAsync();
        var pendingAfterRestart = await runtime.GetEffectAsync(instanceId, afterCommit);
        Assert.Equal("Pending", pendingAfterRestart.GetProperty("status").GetString());
        Assert.Empty(pendingAfterRestart.GetProperty("attempts").EnumerateArray());
        Assert.Empty(await runtime.GetProviderRequestsAsync("synthetic-offer-provider", afterCommit));
        var expectedAfterRestart = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/expected-operations");
        Assert.Contains(expectedAfterRestart ?? [], operation =>
            operation.GetProperty("operationId").GetString() == afterCommit &&
            operation.GetProperty("status").GetString() == "Expected");

        const string afterTransmission = "fault-after-transmission";
        await runtime.ArmFaultAsync(afterTransmission, "AfterRequestTransmissionBeforeProviderExecution");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "offer-document", afterTransmission,
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);
        var unknownBeforeProvider = await runtime.GetEffectAsync(instanceId, afterTransmission);
        Assert.Equal("OutcomeUnknown", unknownBeforeProvider.GetProperty("status").GetString());
        Assert.Equal("Unknown",
            unknownBeforeProvider.GetProperty("attempts")[0].GetProperty("providerOutcome").GetString());
        Assert.Empty(await runtime.GetProviderRequestsAsync("synthetic-offer-provider", afterTransmission));

        const string providerCommitted = "fault-provider-committed";
        await runtime.ArmFaultAsync(providerCommitted, "AfterProviderCommitBeforeResponse");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "offer-document", providerCommitted,
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);
        var providerEvidence = await runtime.GetProviderRequestsAsync("synthetic-offer-provider", providerCommitted);
        Assert.Single(providerEvidence);
        Assert.Equal("Succeeded", providerEvidence[0].GetProperty("outcome").GetString());
        var locallyUnknown = await runtime.GetEffectAsync(instanceId, providerCommitted);
        Assert.Equal("OutcomeUnknown", locallyUnknown.GetProperty("status").GetString());
        Assert.Equal("Unknown", locallyUnknown.GetProperty("attempts")[0].GetProperty("providerOutcome").GetString());
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        var recovered = await runtime.ResumeEffectAsync(instanceId, providerCommitted);
        Assert.Equal("ObservationAccepted", recovered.GetProperty("status").GetString());
        var repeatedProviderRequests = await runtime.GetProviderRequestsAsync("synthetic-offer-provider", providerCommitted);
        Assert.Equal(2, repeatedProviderRequests.Length);
        Assert.Equal(
            repeatedProviderRequests[0].GetProperty("effectId").GetString(),
            repeatedProviderRequests[1].GetProperty("effectId").GetString());

        using (var acceptedOffer = await runtime.Client.PostAsJsonAsync(
                   $"/api/instances/{instanceId}/commands", new
                   {
                       messageId = "accept-after-recovery",
                       type = "AcceptOffer",
                       expectedRevision = 2,
                       facts = new
                       {
                           offerValidAtEvaluation = true,
                           offerEvaluationTime = DateTimeOffset.UtcNow.ToString("O")
                       }
                   }))
        {
            Assert.Equal(HttpStatusCode.OK, acceptedOffer.StatusCode);
        }

        const string responseNotPersisted = "fault-response-not-persisted";
        await runtime.ArmFaultAsync(responseNotPersisted, "AfterProviderResponseBeforeLocalPersistence");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "equipment-reservation", responseNotPersisted,
                assetId: "asset-response-not-persisted")).StatusCode);
        Assert.Single(await runtime.GetProviderRequestsAsync("synthetic-reservation-provider", responseNotPersisted));
        var noLocalResult = await runtime.GetEffectAsync(instanceId, responseNotPersisted);
        Assert.Equal("OutcomeUnknown", noLocalResult.GetProperty("status").GetString());
        Assert.Equal("Pending", noLocalResult.GetProperty("attempts")[0].GetProperty("localPersistenceOutcome").GetString());
        var messagesWithoutResult = await runtime.GetInboundMessageArrayAsync(instanceId);
        Assert.DoesNotContain(messagesWithoutResult, message =>
            message.TryGetProperty("correlationId", out var correlationId) &&
            correlationId.GetString() == responseNotPersisted);

        var recoveredAfterPersistenceFault = await runtime.ResumeEffectAsync(instanceId, responseNotPersisted);
        Assert.Equal("ObservationAccepted", recoveredAfterPersistenceFault.GetProperty("status").GetString());
        var afterRecovery = await runtime.GetProviderRequestsAsync("synthetic-reservation-provider", responseNotPersisted);
        Assert.Equal(2, afterRecovery.Length);
        Assert.Equal(afterRecovery[0].GetProperty("effectId").GetString(),
            afterRecovery[1].GetProperty("effectId").GetString());

        var postObservationInstance = await runtime.CreateAssessmentReadyInstanceAsync();
        const string acceptedBeforeEffectSave = "fault-after-observation-commit";
        await runtime.ArmFaultAsync(acceptedBeforeEffectSave, "AfterObservationCommitBeforeLocalPersistence");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(postObservationInstance, "offer-document", acceptedBeforeEffectSave,
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);
        Assert.Equal("AwaitingOfferAcceptance",
            (await runtime.GetInstanceAsync(postObservationInstance)).GetProperty("currentState").GetString());

        var recoveredObservation = await runtime.ResumeEffectAsync(postObservationInstance, acceptedBeforeEffectSave);
        Assert.Equal("ObservationAccepted", recoveredObservation.GetProperty("status").GetString());
        Assert.Equal("Duplicate",
            recoveredObservation.GetProperty("attempts")[0].GetProperty("observationStatus").GetString());
        Assert.Equal(2, (await runtime.GetInstanceAsync(postObservationInstance))
            .GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Duplicate_provider_calls_follow_each_providers_declared_semantics()
    {
        using var runtime = await TestRuntime.StartAsync();
        var offer = new SyntheticEffectRequest("same-offer", "offer-document", "customer", null, "offer", null);
        var firstOffer = await runtime.Providers.ExecuteAsync(offer, forceFailure: false, CancellationToken.None);
        var secondOffer = await runtime.Providers.ExecuteAsync(offer, forceFailure: false, CancellationToken.None);
        Assert.Equal(firstOffer.EffectId, secondOffer.EffectId);
        Assert.Equal(2, (await runtime.GetProviderRequestsAsync("synthetic-offer-provider", "same-offer")).Length);

        var firstReservation = await runtime.Providers.ExecuteAsync(
            new SyntheticEffectRequest("reserve-1", "equipment-reservation", null, "asset-shared", null, null),
            forceFailure: false, CancellationToken.None);
        var secondReservation = await runtime.Providers.ExecuteAsync(
            new SyntheticEffectRequest("reserve-2", "equipment-reservation", null, "asset-shared", null, null),
            forceFailure: false, CancellationToken.None);
        Assert.Equal("Succeeded", firstReservation.Outcome);
        Assert.Equal("Refused", secondReservation.Outcome);
        Assert.Equal(firstReservation.EffectId, secondReservation.EffectId);

        var notification = new SyntheticEffectRequest(
            "same-notification", "customer-notification", "customer", null, null, "Synthetic notice");
        var firstDelivery = await runtime.Providers.ExecuteAsync(notification, forceFailure: false, CancellationToken.None);
        var secondDelivery = await runtime.Providers.ExecuteAsync(notification, forceFailure: false, CancellationToken.None);
        Assert.NotEqual(firstDelivery.EffectId, secondDelivery.EffectId);
        Assert.Equal(2, (await runtime.GetProviderRequestsAsync("synthetic-notification-provider", "same-notification")).Length);

        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        await runtime.StartEffectAsync(instanceId, "offer-document", "notify-path-offer",
            customerReference: "synthetic-customer", offerReference: "offer");
        using (var accept = await runtime.Client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
               {
                   messageId = "notify-path-accept",
                   type = "AcceptOffer",
                   expectedRevision = 2,
                   facts = new
                   {
                       offerValidAtEvaluation = true,
                       offerEvaluationTime = DateTimeOffset.UtcNow.ToString("O")
                   }
               }))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }
        await runtime.StartEffectAsync(instanceId, "equipment-reservation", "notify-path-reservation",
            assetId: "asset-for-notification");
        const string uncertainNotification = "notify-uncertain";
        await runtime.ArmFaultAsync(uncertainNotification, "AfterProviderCommitBeforeResponse");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "customer-notification", uncertainNotification,
                customerReference: "synthetic-customer", messageTemplate: "Synthetic status")).StatusCode);
        using var refusedRetry = await runtime.Client.PostAsync(
            $"/api/instances/{instanceId}/effects/{uncertainNotification}/resume", content: null);
        Assert.Equal(HttpStatusCode.Conflict, refusedRetry.StatusCode);
        Assert.Single(await runtime.GetProviderRequestsAsync(
            "synthetic-notification-provider", uncertainNotification));
        Assert.Equal("CustomerNotificationPending",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Provider_failure_and_forged_observation_do_not_advance_the_workflow()
    {
        using var runtime = await TestRuntime.StartAsync();
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        const string operationId = "provider-fails";
        runtime.Client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await runtime.PostEffectAsync(instanceId, "offer-document", "untrusted-effect",
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);
        runtime.Client.DefaultRequestHeaders.Add(
            "X-State-Machine-Trusted-Ingestion-Key", runtime.TrustedKey);
        var noUnauthorizedEffects = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/effects");
        Assert.Empty(noUnauthorizedEffects ?? []);
        await runtime.ArmFaultAsync(operationId, "ProviderConfirmedFailure");

        using var response = await runtime.PostEffectAsync(instanceId, "offer-document", operationId,
            customerReference: "synthetic-customer", offerReference: "offer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var effect = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ProviderFailed", effect.GetProperty("status").GetString());
        Assert.Equal("Failed", effect.GetProperty("attempts")[0].GetProperty("providerOutcome").GetString());
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        var providerRecords = await runtime.GetProviderRequestsAsync("synthetic-offer-provider", operationId);
        Assert.Single(providerRecords);
        Assert.Equal("Failed", providerRecords[0].GetProperty("outcome").GetString());

        runtime.Client.DefaultRequestHeaders.Remove("X-State-Machine-Trusted-Ingestion-Key");
        using var forged = await runtime.Client.PostAsJsonAsync($"/api/instances/{instanceId}/observations", new
        {
            messageId = "forged-provider-result",
            type = "OfferDocumentGenerated",
            expectedRevision = 1,
            correlationId = operationId,
            source = "synthetic-offer-provider",
            evidence = new { documentId = "forged-document", offerDigest = "sha256:forged" }
        });
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        var receipt = await forged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Untrusted", receipt.GetProperty("message").GetProperty("status").GetString());
        Assert.Equal("AssessmentReady",
            (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());
    }

    [Fact]
    public async Task Late_provider_success_is_retained_but_gate5_refuses_it_after_cancellation()
    {
        using var runtime = await TestRuntime.StartAsync();
        var instanceId = await runtime.CreateAssessmentReadyInstanceAsync();
        const string operationId = "late-offer-operation";
        await runtime.ArmFaultAsync(operationId, "BeforeObservationAcceptance");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await runtime.PostEffectAsync(instanceId, "offer-document", operationId,
                customerReference: "synthetic-customer", offerReference: "offer")).StatusCode);

        var cancel = await runtime.Client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
        {
            messageId = "cancel-before-result",
            type = "CancelApplication",
            expectedRevision = 1
        });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("Cancelled", (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        var lateResult = await runtime.ResumeEffectAsync(instanceId, operationId);
        Assert.Equal("ObservationRefused", lateResult.GetProperty("status").GetString());
        var attempt = lateResult.GetProperty("attempts")[0];
        Assert.Equal("Succeeded", attempt.GetProperty("providerOutcome").GetString());
        Assert.Equal("Stale", attempt.GetProperty("observationStatus").GetString());
        Assert.NotNull(attempt.GetProperty("providerEffectId").GetString());
        Assert.Equal("Cancelled", (await runtime.GetInstanceAsync(instanceId)).GetProperty("currentState").GetString());

        var receiptList = await runtime.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/instances/{instanceId}/inbound-messages");
        Assert.NotNull(receiptList);
        Assert.Contains(receiptList, receipt =>
            receipt.GetProperty("correlationId").GetString() == operationId &&
            receipt.GetProperty("status").GetString() == "Stale");
    }

    private sealed class TestRuntime : IDisposable
    {
        private readonly string _previousDatabase;
        private readonly string _previousProviderDirectory;
        private readonly string _previousKey;
        private readonly string _previousControls;
        private WebApplicationFactory<Program> _factory;

        private TestRuntime(string directory)
        {
            DirectoryPath = directory;
            ProviderDirectory = Path.Combine(directory, "providers");
            DatabasePath = Path.Combine(directory, "workflow.db");
            TrustedKey = $"test-key-{Guid.NewGuid():N}";
            _previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB") ?? "";
            _previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR") ?? "";
            _previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY") ?? "";
            _previousControls = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS") ?? "";
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", DatabasePath);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", ProviderDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", "true");
            _factory = new WebApplicationFactory<Program>();
            CreateClient();
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ProviderDirectory { get; }
        public string TrustedKey { get; }
        public HttpClient Client { get; private set; } = null!;
        public SyntheticProviderStore Providers { get; private set; } = null!;

        public static Task<TestRuntime> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"dsm-gate6-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return Task.FromResult(new TestRuntime(directory));
        }

        public async Task<string> CreateAssessmentReadyInstanceAsync()
        {
            var definitionPath = Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v1.json");
            var definition = await File.ReadAllTextAsync(definitionPath);
            using var registration = await Client.PostAsync("/api/definitions", new StringContent(
                definition, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
            var id = $"orion-gate6-{Guid.NewGuid():N}";
            var create = await Client.PostAsJsonAsync("/api/instances", new
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
            var submit = await Client.PostAsJsonAsync($"/api/instances/{id}/commands", new
            {
                messageId = $"submit-{Guid.NewGuid():N}",
                type = "SubmitApplication",
                expectedRevision = 0
            });
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
            var submitted = await submit.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Accepted", submitted.GetProperty("message").GetProperty("status").GetString());
            Assert.Equal("AssessmentReady", submitted.GetProperty("instance").GetProperty("currentState").GetString());
            return id;
        }

        public Task<HttpResponseMessage> PostEffectAsync(
            string instanceId,
            string capability,
            string operationId,
            string? customerReference = null,
            string? assetId = null,
            string? offerReference = null,
            string? messageTemplate = null)
        {
            var request = JsonSerializer.SerializeToElement(new
            {
                customerReference,
                assetId,
                offerReference,
                messageTemplate
            });
            var values = request.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone());
            values["operationId"] = JsonSerializer.SerializeToElement(operationId);
            values["capability"] = JsonSerializer.SerializeToElement(capability);
            return Client.PostAsJsonAsync($"/api/instances/{instanceId}/effects", values);
        }

        public async Task<JsonElement> StartEffectAsync(
            string instanceId,
            string capability,
            string operationId,
            string? customerReference = null,
            string? assetId = null,
            string? offerReference = null,
            string? messageTemplate = null)
        {
            using var response = await PostEffectAsync(instanceId, capability, operationId,
                customerReference, assetId, offerReference, messageTemplate);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async Task ArmFaultAsync(string operationId, string point)
        {
            using var response = await Client.PostAsync(
                $"/api/test-controls/effects/{operationId}/{point}", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public Task RestartApiAsync()
        {
            Client.Dispose();
            _factory.Dispose();
            _factory = new WebApplicationFactory<Program>();
            CreateClient();
            return Task.CompletedTask;
        }

        public async Task<JsonElement> GetEffectAsync(string instanceId, string operationId) =>
            await Client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}/effects/{operationId}");

        public async Task<JsonElement> ResumeEffectAsync(string instanceId, string operationId)
        {
            using var response = await Client.PostAsync(
                $"/api/instances/{instanceId}/effects/{operationId}/resume", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public async Task<JsonElement> GetInstanceAsync(string instanceId) =>
            await Client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");

        public async Task<JsonElement[]> GetProviderRequestsAsync(string provider, string operationId)
        {
            var result = await Client.GetFromJsonAsync<JsonElement[]>(
                $"/api/providers/{provider}/requests?operationId={Uri.EscapeDataString(operationId)}");
            return result ?? [];
        }

        public async Task<string> GetEffectCollectionAsync(string instanceId)
        {
            var result = await Client.GetStringAsync($"/api/instances/{instanceId}/effects");
            return result;
        }

        public async Task<JsonElement[]> GetInboundMessageArrayAsync(string instanceId)
        {
            var result = await Client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{instanceId}/inbound-messages");
            return result ?? [];
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", EmptyToNull(_previousDatabase));
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", EmptyToNull(_previousProviderDirectory));
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", EmptyToNull(_previousKey));
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS", EmptyToNull(_previousControls));
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }

        private void CreateClient()
        {
            Client = _factory.CreateClient();
            Client.DefaultRequestHeaders.Add("X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
            Providers = _factory.Services.GetRequiredService<SyntheticProviderStore>();
        }

        private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
    }
}
