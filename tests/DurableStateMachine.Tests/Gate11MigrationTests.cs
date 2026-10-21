using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate11MigrationTests
{
    private const string TrustedKey = "gate11-test-administration-key";

    [Fact]
    public async Task Definitions_remain_pinned_and_migration_is_explicit_audited_and_replayable()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate11-{Guid.NewGuid():N}.db");
        var providerDirectory = Path.Combine(Path.GetTempPath(), $"dsm-gate11-providers-{Guid.NewGuid():N}");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        var previousProviderDirectory = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR");
        var previousKey = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", providerDirectory);
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", TrustedKey);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-State-Machine-Trusted-Ingestion-Key", TrustedKey);
            var v4 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v4.json"));
            var v5 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v5.json"));

            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(v4))).StatusCode);
            var v4Before = await client.GetFromJsonAsync<JsonElement>(
                "/api/definitions/orion-equipment-lease/4.0");
            const string oldOfferInstanceId = "gate11-old-v4-offer";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = oldOfferInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "4.0",
                facts = new
                {
                    documentsComplete = true,
                    riskScore = 90,
                    equipmentValue = 1000,
                    checksComplete = true,
                    eligibilityDecision = "Eligible"
                }
            })).StatusCode);
            Assert.Equal("Accepted", await SendCommand(client, oldOfferInstanceId, "old-offer-submit",
                "SubmitApplication", 0));
            Assert.Equal("Accepted", await SendCommand(client, oldOfferInstanceId, "old-offer-issue",
                "IssueOffer", 1));
            var oldOfferTimersBefore = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{oldOfferInstanceId}/timers");
            Assert.NotNull(oldOfferTimersBefore);
            var originalExpiry = oldOfferTimersBefore.Single(timer =>
                timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry")
                .GetProperty("dueAt").GetDateTimeOffset();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(v5))).StatusCode);
            var v4After = await client.GetFromJsonAsync<JsonElement>(
                "/api/definitions/orion-equipment-lease/4.0");
            Assert.Equal(v4Before.GetProperty("definition").GetRawText(),
                v4After.GetProperty("definition").GetRawText());
            var oldOfferTimersAfter = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{oldOfferInstanceId}/timers");
            Assert.NotNull(oldOfferTimersAfter);
            Assert.Equal(originalExpiry, oldOfferTimersAfter.Single(timer =>
                timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry")
                .GetProperty("dueAt").GetDateTimeOffset());
            var v5Details = await client.GetFromJsonAsync<JsonElement>(
                "/api/definitions/orion-equipment-lease/5.0");
            Assert.Contains(v5Details.GetProperty("states").EnumerateArray(),
                state => state.GetProperty("id").GetString() == "SpecialistAssessment");

            const string oldInstanceId = "gate11-old-v4";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = oldInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "4.0",
                facts = new { documentsComplete = false }
            })).StatusCode);
            const string newInstanceId = "gate11-new-v5";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = newInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "5.0",
                facts = new
                {
                    documentsComplete = true,
                    riskScore = 90,
                    equipmentValue = 1000,
                    checksComplete = true,
                    eligibilityDecision = "Eligible"
                }
            })).StatusCode);

            var oldSubmit = await client.PostAsJsonAsync($"/api/instances/{oldInstanceId}/commands", new
            {
                messageId = "old-submit",
                type = "SubmitApplication",
                expectedRevision = 0
            });
            Assert.Equal(HttpStatusCode.OK, oldSubmit.StatusCode);
            var oldCurrent = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{oldInstanceId}");
            Assert.Equal("4.0", oldCurrent.GetProperty("definitionVersion").GetString());
            Assert.Equal("AwaitingDocuments", oldCurrent.GetProperty("currentState").GetString());
            var oldTimers = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{oldInstanceId}/timers");
            Assert.NotNull(oldTimers);
            Assert.Equal(TimeSpan.FromDays(10),
                oldTimers.Single().GetProperty("dueAt").GetDateTimeOffset() -
                oldTimers.Single().GetProperty("triggeredAt").GetDateTimeOffset());

            Assert.Equal("Accepted", await SendCommand(client, newInstanceId, "new-submit",
                "SubmitApplication", 0));
            Assert.Equal("Accepted", await SendCommand(client, newInstanceId, "new-approve",
                "ApproveAssessment", 1));
            var beforeReviewIssue = await SendCommand(client, newInstanceId, "premature-offer",
                "IssueOffer", 2);
            Assert.Equal("Rejected", beforeReviewIssue);
            Assert.Equal("Accepted", await SendCommand(client, newInstanceId, "complete-preliminary-review",
                "CompletePreliminaryOfferReview", 2, new
                {
                    preliminaryOfferReviewComplete = true,
                    materialDiscrepancy = false
                }));
            Assert.Equal("Accepted", await SendCommand(client, newInstanceId, "new-issue-offer",
                "IssueOffer", 3));
            var newTimers = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{newInstanceId}/timers");
            Assert.NotNull(newTimers);
            var expiry = newTimers.Single(timer =>
                timer.GetProperty("timerDeclarationId").GetString() == "offer-expiry");
            Assert.Equal(TimeSpan.FromDays(5),
                expiry.GetProperty("dueAt").GetDateTimeOffset() -
                expiry.GetProperty("triggeredAt").GetDateTimeOffset());

            const string discrepancyInstanceId = "gate11-v5-discrepancy";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = discrepancyInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "5.0",
                facts = new
                {
                    documentsComplete = true,
                    riskScore = 90,
                    equipmentValue = 1000,
                    checksComplete = true,
                    eligibilityDecision = "Eligible"
                }
            })).StatusCode);
            Assert.Equal("Accepted", await SendCommand(client, discrepancyInstanceId, "discrepancy-submit",
                "SubmitApplication", 0));
            Assert.Equal("Accepted", await SendCommand(client, discrepancyInstanceId, "discrepancy-approve",
                "ApproveAssessment", 1));
            Assert.Equal("Accepted", await SendCommand(client, discrepancyInstanceId, "record-discrepancy",
                "CompletePreliminaryOfferReview", 2, new
                {
                    preliminaryOfferReviewComplete = true,
                    materialDiscrepancy = true
                }));
            var discrepancyState = await client.GetFromJsonAsync<JsonElement>(
                $"/api/instances/{discrepancyInstanceId}");
            Assert.Equal("AwaitingRenewedBusinessAuthorisation",
                discrepancyState.GetProperty("currentState").GetString());

            const string cancellationInstanceId = "gate11-v5-cancellation";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = cancellationInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "5.0",
                facts = new
                {
                    documentsComplete = true,
                    riskScore = 90,
                    equipmentValue = 1000,
                    checksComplete = true,
                    eligibilityDecision = "Eligible"
                }
            })).StatusCode);
            Assert.Equal("Accepted", await SendCommand(client, cancellationInstanceId, "cancel-submit",
                "SubmitApplication", 0));
            Assert.Equal("Accepted", await SendCommand(client, cancellationInstanceId, "cancel-approve",
                "ApproveAssessment", 1));
            Assert.Equal("Unresolved", await SendCommand(client, cancellationInstanceId, "cancel-without-authority",
                "CancelApplication", 2));
            Assert.Equal("Rejected", await SendCommand(client, cancellationInstanceId, "cancel-explicitly-refused",
                "CancelApplication", 2, new { cancellationReviewerAuthorised = false }));
            Assert.Equal("Accepted", await SendCommand(client, cancellationInstanceId, "cancel-authorised",
                "CancelApplication", 2, new { cancellationReviewerAuthorised = true }));

            const string migrationInstanceId = "gate11-migrated-v4";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = migrationInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "4.0",
                facts = new { }
            })).StatusCode);
            var migration = MigrationRequest(migrationInstanceId, "migration-success");
            var preview = await client.PostAsJsonAsync(
                $"/api/instances/{migrationInstanceId}/migration-preview", migration);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using (var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()))
            {
                Assert.Equal("Eligible", previewJson.RootElement.GetProperty("status").GetString());
            }
            var beforeExecution = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{migrationInstanceId}");
            Assert.Equal("4.0", beforeExecution.GetProperty("definitionVersion").GetString());
            Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{migrationInstanceId}/migrations") ?? []);

            var execute = await client.PostAsJsonAsync($"/api/instances/{migrationInstanceId}/migrations", migration);
            Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
            using (var executed = JsonDocument.Parse(await execute.Content.ReadAsStringAsync()))
            {
                Assert.Equal("Committed", executed.RootElement.GetProperty("migration").GetProperty("status").GetString());
                Assert.Equal("5.0", executed.RootElement.GetProperty("instance").GetProperty("definitionVersion").GetString());
                Assert.Equal(0, executed.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
            }
            var identicalRetry = await client.PostAsJsonAsync(
                $"/api/instances/{migrationInstanceId}/migrations", migration);
            Assert.Equal(HttpStatusCode.OK, identicalRetry.StatusCode);
            var conflictingRetry = await client.PostAsJsonAsync(
                $"/api/instances/{migrationInstanceId}/migrations",
                migration with { AuthorisationReason = "different migration content" });
            Assert.Equal(HttpStatusCode.Conflict, conflictingRetry.StatusCode);
            Assert.Equal("Accepted", await SendCommand(client, migrationInstanceId, "migrated-submit",
                "SubmitApplication", 0, new
                {
                    documentsComplete = true,
                    riskScore = 90,
                    equipmentValue = 1000,
                    checksComplete = true,
                    eligibilityDecision = "Eligible"
                }));
            var replay = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{migrationInstanceId}/replay");
            Assert.Equal("Consistent", replay.GetProperty("outcome").GetString());
            Assert.Equal("5.0", replay.GetProperty("definitionVersion").GetString());
            Assert.Equal("AssessmentReady", replay.GetProperty("reconstructedFinalState").GetString());
            Assert.Single(await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{migrationInstanceId}/migrations") ?? []);

            const string raceInstanceId = "gate11-migration-race";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = raceInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "4.0",
                facts = new { }
            })).StatusCode);
            var raceResponses = await Task.WhenAll(
                client.PostAsJsonAsync($"/api/instances/{raceInstanceId}/migrations",
                    MigrationRequest(raceInstanceId, "migration-race-a")),
                client.PostAsJsonAsync($"/api/instances/{raceInstanceId}/migrations",
                    MigrationRequest(raceInstanceId, "migration-race-b")));
            var raceStatuses = new List<string?>();
            foreach (var response in raceResponses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                raceStatuses.Add(body.RootElement.GetProperty("migration").GetProperty("status").GetString());
            }
            Assert.Single(raceStatuses, status => status == "Committed");
            Assert.Single(raceStatuses, status => status == "Refused");
            Assert.Equal(2,
                (await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{raceInstanceId}/migrations") ?? []).Length);
            var raceReplay = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{raceInstanceId}/replay");
            Assert.Equal("Consistent", raceReplay.GetProperty("outcome").GetString());

            const string blockedInstanceId = "gate11-blocked-v4";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id = blockedInstanceId,
                definitionId = "orion-equipment-lease",
                definitionVersion = "4.0",
                facts = new { documentsComplete = false }
            })).StatusCode);
            Assert.Equal("Accepted", await SendCommand(client, blockedInstanceId, "blocked-submit",
                "SubmitApplication", 0));
            var blockedRequest = MigrationRequest(blockedInstanceId, "migration-pending-timer");
            var blockedPreview = await client.PostAsJsonAsync(
                $"/api/instances/{blockedInstanceId}/migration-preview", blockedRequest);
            using (var blockedJson = JsonDocument.Parse(await blockedPreview.Content.ReadAsStringAsync()))
            {
                var findings = blockedJson.RootElement.GetProperty("findings")
                    .EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Equal("Refused", blockedJson.RootElement.GetProperty("status").GetString());
                Assert.Contains("pending-timer-with-definition-specific-semantics", findings);
            }
            var refused = await client.PostAsJsonAsync(
                $"/api/instances/{blockedInstanceId}/migrations", blockedRequest);
            using (var refusedJson = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()))
            {
                Assert.Equal("Refused", refusedJson.RootElement.GetProperty("migration").GetProperty("status").GetString());
                Assert.Equal("4.0", refusedJson.RootElement.GetProperty("instance").GetProperty("definitionVersion").GetString());
                Assert.Equal(1, refusedJson.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
            }
            var pendingTimer = await client.GetFromJsonAsync<JsonElement[]>(
                $"/api/instances/{blockedInstanceId}/timers");
            Assert.Contains(pendingTimer ?? [], timer => timer.GetProperty("status").GetString() == "Pending");
            var blockedReplay = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{blockedInstanceId}/replay");
            Assert.Equal("Consistent", blockedReplay.GetProperty("outcome").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_PROVIDER_DB_DIR", previousProviderDirectory);
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY", previousKey);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    private static async Task<string?> SendCommand(
        HttpClient client,
        string instanceId,
        string messageId,
        string type,
        long expectedRevision,
        object? facts = null)
    {
        var response = await client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", new
        {
            messageId,
            type,
            expectedRevision,
            facts
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return result.RootElement.GetProperty("message").GetProperty("status").GetString();
    }

    private static DurableStateMachine.Persistence.MigrationRequest MigrationRequest(
        string instanceId,
        string migrationId) =>
        new(migrationId,
            "orion-equipment-lease", "4.0",
            "orion-equipment-lease", "5.0",
            0, $"Gate 11 verified training migration for {instanceId}.");

    private static StringContent Json(string content) =>
        new(content, Encoding.UTF8, "application/json");
}
