using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DurableStateMachine.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OrionGate9Corpus;

namespace DurableStateMachine.Tests;

[Collection("API environment")]
public sealed class Gate9ConcurrencyTests
{
    private const string Definition = """
        {
          "languageVersion": "1.1",
          "id": "gate9-race-test",
          "version": "1",
          "initialState": "Draft",
          "states": [
            { "id": "Draft" },
            { "id": "Active" },
            { "id": "Cancelled", "terminal": true }
          ],
          "events": ["Request", "Cancel"],
          "inputPolicy": {
            "commands": [
              { "name": "StartWork", "event": "Request" },
              { "name": "CancelWork", "event": "Cancel" }
            ],
            "observations": []
          },
          "transitions": [
            { "id": "request", "source": "Draft", "event": "Request", "target": "Active" },
            { "id": "cancel", "source": "Draft", "event": "Cancel", "target": "Cancelled" }
          ]
        }
        """;

    [Fact]
    public void Fixed_corpus_passes_independent_count_and_reproducibility_checks()
    {
        var corpusPath = Path.Combine(AppContext.BaseDirectory, "test-data", "orion-gate9-deliveries.ndjson");
        var definitionPath = Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v4.json");
        var bytes = File.ReadAllBytes(corpusPath);
        var report = CorpusValidator.Validate(CorpusGenerator.Deserialize(bytes), bytes,
            CorpusGenerator.DefaultSeed, definitionPath);

        Assert.Equal(5000, report.Records);
        Assert.Equal(300, report.Applications);
        Assert.Equal(150, report.ExactDuplicateAdditionalDeliveries);
        Assert.Equal(100, report.SemanticDuplicateAdditionalDeliveries);
        Assert.Equal(75, report.RacePairs);
        Assert.Equal(50, report.MalformedPayloads);
        Assert.True(report.LateDeliveries >= 100);
        Assert.True(report.PreCancellationEventsDeliveredAfterCancellation >= 100);
        Assert.True(report.Reproducible);
        Assert.Equal(
            ["AcceptOffer", "Approve", "Cancel", "CustomerNotified", "DeclineOffer", "DocumentsReceived",
             "EquipmentReserved", "IssueOffer", "ReferForReview", "Reject", "Submit"],
            report.EventVocabulary);
    }

    [Fact]
    public async Task Independent_api_hosts_serialize_revision_races_and_duplicate_receipts_in_sqlite()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-gate9-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            using var firstHost = new WebApplicationFactory<Program>();
            using var firstClient = firstHost.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await firstClient.PostAsync("/api/definitions", Json(Definition))).StatusCode);
            using var secondHost = new WebApplicationFactory<Program>();
            using var secondClient = secondHost.CreateClient();

            await CreateInstance(firstClient, "race-forward");
            var competing = await SubmitTogether(
                firstClient,
                secondClient,
                "race-forward",
                new { messageId = "start-forward", type = "StartWork", expectedRevision = 0 },
                new { messageId = "cancel-forward", type = "CancelWork", expectedRevision = 0 });
            Assert.Single(competing, result => result.Status == "Accepted");
            Assert.Single(competing, result => result.Status == "Stale");
            await AssertOneRevision(firstClient, "race-forward");

            await CreateInstance(firstClient, "race-reversed");
            using var reverseWinner = JsonDocument.Parse(await (await secondClient.PostAsJsonAsync(
                "/api/instances/race-reversed/commands",
                new { messageId = "cancel-reversed", type = "CancelWork", expectedRevision = 0 })).Content.ReadAsStringAsync());
            Assert.Equal("Accepted", reverseWinner.RootElement.GetProperty("message").GetProperty("status").GetString());
            using var reverseLoser = JsonDocument.Parse(await (await firstClient.PostAsJsonAsync(
                "/api/instances/race-reversed/commands",
                new { messageId = "start-reversed", type = "StartWork", expectedRevision = 0 })).Content.ReadAsStringAsync());
            Assert.Equal("Stale", reverseLoser.RootElement.GetProperty("message").GetProperty("status").GetString());
            await AssertOneRevision(firstClient, "race-reversed");

            await CreateInstance(firstClient, "duplicate-race");
            var duplicates = await SubmitTogether(
                firstClient,
                secondClient,
                "duplicate-race",
                new { messageId = "same-logical-input", type = "StartWork", expectedRevision = 0 },
                new { messageId = "same-logical-input", type = "StartWork", expectedRevision = 0 });
            Assert.Single(duplicates, result => result.Status == "Accepted");
            Assert.Single(duplicates, result => result.Status == "Duplicate");
            await AssertOneRevision(firstClient, "duplicate-race");
            var receipts = await firstClient.GetFromJsonAsync<JsonElement[]>(
                "/api/instances/duplicate-race/inbound-messages");
            Assert.NotNull(receipts);
            Assert.Equal(2, receipts.Length);
            Assert.Equal(1, receipts.Count(receipt => receipt.GetProperty("status").GetString() == "Accepted"));
            Assert.Equal(1, receipts.Count(receipt => receipt.GetProperty("status").GetString() == "Duplicate"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", previousDatabase);
            SqliteConnection.ClearAllPools();
            if (File.Exists(database))
            {
                File.Delete(database);
            }
        }
    }

    private static async Task CreateInstance(HttpClient client, string id)
    {
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
        {
            id,
            definitionId = "gate9-race-test",
            definitionVersion = "1",
            facts = new { },
        })).StatusCode);
    }

    private static async Task<(string Status, string State)[]> SubmitTogether(
        HttpClient first,
        HttpClient second,
        string instanceId,
        object firstCommand,
        object secondCommand)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(string Status, string State)> Submit(HttpClient client, object command)
        {
            await barrier.Task;
            var response = await client.PostAsJsonAsync($"/api/instances/{instanceId}/commands", command);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (
                document.RootElement.GetProperty("message").GetProperty("status").GetString()!,
                document.RootElement.GetProperty("instance").GetProperty("currentState").GetString()!);
        }

        var submissions = new[] { Submit(first, firstCommand), Submit(second, secondCommand) };
        barrier.SetResult();
        return await Task.WhenAll(submissions);
    }

    private static async Task AssertOneRevision(HttpClient client, string instanceId)
    {
        var instance = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
        Assert.Equal(1, instance.GetProperty("revision").GetInt64());
        var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
        Assert.NotNull(history);
        Assert.Single(history);
        Assert.Equal(0, history[0].GetProperty("revisionBefore").GetInt64());
        Assert.Equal(1, history[0].GetProperty("revisionAfter").GetInt64());
    }

    private static StringContent Json(string value) =>
        new(value, System.Text.Encoding.UTF8, "application/json");
}
