using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace DurableStateMachine.Tests;

public sealed class ApiDurabilityTests
{
    private const string Definition = """
        {
          "languageVersion": "1.0",
          "id": "api-test",
          "version": "1",
          "initialState": "Draft",
          "states": [{ "id": "Draft" }, { "id": "Done", "terminal": true }],
          "events": ["Complete"],
          "transitions": [
            { "id": "complete", "source": "Draft", "event": "Complete", "target": "Done" }
          ]
        }
        """;

    [Fact]
    public async Task Definitions_instances_attempts_and_transition_history_survive_a_new_api_host()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            string instanceId;
            using (var factory = new WebApplicationFactory<Program>())
            using (var client = factory.CreateClient())
            {
                var registration = await client.PostAsync("/api/definitions", Json(Definition));
                Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
                var duplicate = await client.PostAsync("/api/definitions", Json(Definition));
                Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
                using (var duplicateBody = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync()))
                {
                    Assert.True(duplicateBody.RootElement.GetProperty("alreadyRegistered").GetBoolean());
                }

                var invalid = await client.PostAsync("/api/definitions", Json("{}"));
                Assert.Equal((HttpStatusCode)422, invalid.StatusCode);
                var malformed = await client.PostAsync("/api/definitions", Json("{"));
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

                var changed = Definition.Replace("\"target\": \"Done\"", "\"target\": \"Draft\"", StringComparison.Ordinal);
                var conflict = await client.PostAsync("/api/definitions", Json(changed));
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

                var secondVersion = Definition.Replace("\"version\": \"1\"", "\"version\": \"2\"", StringComparison.Ordinal);
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(secondVersion))).StatusCode);

                instanceId = $"instance-{Guid.NewGuid():N}";
                var create = await client.PostAsJsonAsync("/api/instances", new
                {
                    id = instanceId,
                    definitionId = "api-test",
                    definitionVersion = "1",
                    facts = new { customer = "sample", priority = 4, verified = true }
                });
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);

                var accepted = await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" });
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                using (var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Accepted", acceptedBody.RootElement.GetProperty("attempt").GetProperty("status").GetString());
                    Assert.False(string.IsNullOrWhiteSpace(acceptedBody.RootElement.GetProperty("attempt").GetProperty("id").GetString()));
                    Assert.Equal(1, acceptedBody.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
                }

                var rejected = await client.PostAsJsonAsync($"/api/instances/{instanceId}/events", new { @event = "Complete" });
                Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
                using (var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
                {
                    Assert.Equal("Rejected", rejectedBody.RootElement.GetProperty("attempt").GetProperty("status").GetString());
                    Assert.Equal("terminal-state", rejectedBody.RootElement.GetProperty("attempt").GetProperty("reasonCode").GetString());
                    Assert.Equal(1, rejectedBody.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
                }

                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/instances/unknown")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
            }

            using (var restartedFactory = new WebApplicationFactory<Program>())
            using (var restartedClient = restartedFactory.CreateClient())
            {
                var instance = await restartedClient.GetFromJsonAsync<JsonElement>($"/api/instances/{instanceId}");
                Assert.Equal("Done", instance.GetProperty("currentState").GetString());
                Assert.Equal(1, instance.GetProperty("revision").GetInt64());
                Assert.Equal("sample", instance.GetProperty("facts").GetProperty("customer").GetString());
                Assert.Equal("1", instance.GetProperty("definitionVersion").GetString());

                var attempts = await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/attempts");
                Assert.NotNull(attempts);
                Assert.Equal(2, attempts.Length);
                Assert.Equal("Accepted", attempts[0].GetProperty("status").GetString());
                Assert.Equal("Rejected", attempts[1].GetProperty("status").GetString());

                var history = await restartedClient.GetFromJsonAsync<JsonElement[]>($"/api/instances/{instanceId}/history");
                Assert.NotNull(history);
                Assert.Single(history);
                Assert.Equal(attempts[0].GetProperty("id").GetString(), history[0].GetProperty("attemptId").GetString());

                var definition = await restartedClient.GetFromJsonAsync<JsonElement>("/api/definitions/api-test/1");
                Assert.Equal("Draft", definition.GetProperty("initialState").GetString());
            }
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

    [Fact]
    public async Task Concurrent_events_do_not_create_two_accepted_transitions_from_one_revision()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
            var id = $"parallel-{Guid.NewGuid():N}";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id,
                definitionId = "api-test",
                definitionVersion = "1",
                facts = new { }
            })).StatusCode);

            var events = await Task.WhenAll(
                client.PostAsJsonAsync($"/api/instances/{id}/events", new { @event = "Complete" }),
                client.PostAsJsonAsync($"/api/instances/{id}/events", new { @event = "Complete" }));
            var outcomes = await Task.WhenAll(events.Select(async response =>
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return body.RootElement.GetProperty("attempt").GetProperty("status").GetString();
            }));
            Assert.Single(outcomes, outcome => outcome == "Accepted");
            Assert.Single(outcomes, outcome => outcome == "Rejected");
            var history = await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{id}/history");
            Assert.NotNull(history);
            Assert.Single(history);
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

    [Fact]
    public async Task Api_records_ambiguous_and_unresolved_evaluations_without_mutating_instances()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        const string branchingDefinition = """
            {
              "languageVersion": "1.0",
              "id": "branching-test",
              "version": "1",
              "initialState": "Start",
              "states": [{ "id": "Start" }, { "id": "One", "terminal": true }, { "id": "Two", "terminal": true }],
              "events": ["Choose"],
              "transitions": [
                { "id": "exists", "source": "Start", "event": "Choose", "target": "One",
                  "guard": { "op": "exists", "fact": "choice" } },
                { "id": "matches", "source": "Start", "event": "Choose", "target": "Two",
                  "guard": { "op": "eq", "fact": "choice", "value": "one" } }
              ]
            }
            """;
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(branchingDefinition))).StatusCode);

            foreach (var (id, facts) in new[]
            {
                ("ambiguous", new { choice = "one" }),
                ("unresolved", (object)new { })
            })
            {
                var created = await client.PostAsJsonAsync("/api/instances", new
                {
                    id,
                    definitionId = "branching-test",
                    definitionVersion = "1",
                    facts
                });
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            }

            var ambiguous = await client.PostAsJsonAsync("/api/instances/ambiguous/events", new { @event = "Choose" });
            var unresolved = await client.PostAsJsonAsync("/api/instances/unresolved/events", new { @event = "Choose" });
            Assert.Equal(HttpStatusCode.OK, ambiguous.StatusCode);
            Assert.Equal(HttpStatusCode.OK, unresolved.StatusCode);
            using var ambiguousBody = JsonDocument.Parse(await ambiguous.Content.ReadAsStringAsync());
            using var unresolvedBody = JsonDocument.Parse(await unresolved.Content.ReadAsStringAsync());
            Assert.Equal("Ambiguous", ambiguousBody.RootElement.GetProperty("attempt").GetProperty("status").GetString());
            Assert.Equal("Unresolved", unresolvedBody.RootElement.GetProperty("attempt").GetProperty("status").GetString());
            Assert.Equal(0, ambiguousBody.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
            Assert.Equal(0, unresolvedBody.RootElement.GetProperty("instance").GetProperty("revision").GetInt64());
            Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/instances/ambiguous/history") ?? []);
            Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/instances/unresolved/history") ?? []);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(
                "/api/instances/ambiguous/events", new { @event = "" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/instances/unknown/pending-work")).StatusCode);
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

    [Fact]
    public async Task Failed_history_insert_rolls_back_instance_update_and_attempt()
    {
        var database = Path.Combine(Path.GetTempPath(), $"dsm-{Guid.NewGuid():N}.db");
        var previousDatabase = Environment.GetEnvironmentVariable("DURABLE_STATE_MACHINE_DB");
        Environment.SetEnvironmentVariable("DURABLE_STATE_MACHINE_DB", database);
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/definitions", Json(Definition))).StatusCode);
            const string id = "atomicity-check";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/instances", new
            {
                id,
                definitionId = "api-test",
                definitionVersion = "1",
                facts = new { }
            })).StatusCode);

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER fail_history_insert
                    BEFORE INSERT ON TransitionHistory
                    BEGIN
                        SELECT RAISE(ABORT, 'injected history failure');
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var result = await client.PostAsJsonAsync($"/api/instances/{id}/events", new { @event = "Complete" });
            Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
            var instance = await client.GetFromJsonAsync<JsonElement>($"/api/instances/{id}");
            Assert.Equal("Draft", instance.GetProperty("currentState").GetString());
            Assert.Equal(0, instance.GetProperty("revision").GetInt64());
            Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{id}/attempts") ?? []);
            Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>($"/api/instances/{id}/history") ?? []);
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

    private static StringContent Json(string content) => new(content, Encoding.UTF8, "application/json");
}
