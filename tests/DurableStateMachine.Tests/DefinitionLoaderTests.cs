using DurableStateMachine.Core;

namespace DurableStateMachine.Tests;

public sealed class DefinitionLoaderTests
{
    [Fact]
    public void Loads_both_workflow_definitions()
    {
        var orion = TestSupport.LoadWorkflow("orion-v1.json");
        var timedOrion = TestSupport.LoadWorkflow("orion-v4.json");
        var concernOrion = TestSupport.LoadWorkflow("orion-v6.json");
        var asteria = TestSupport.LoadWorkflow("asteria-release-governance-v1.json");
        var documents = TestSupport.LoadWorkflow("document-review-v1.json");

        Assert.Equal("orion-equipment-lease", orion.Id);
        Assert.Equal("1.1", orion.LanguageVersion);
        Assert.Equal("3.0", orion.Version);
        Assert.Equal("1.2", timedOrion.LanguageVersion);
        Assert.Equal("1.3", concernOrion.LanguageVersion);
        Assert.Equal("6.0", concernOrion.Version);
        Assert.Equal(2, concernOrion.Concerns.Count);
        Assert.Equal("Pending", concernOrion.Concerns["documentCompleteness"].InitialState);
        Assert.Equal(12, concernOrion.Transitions.Count);
        Assert.Equal("asteria-release-governance", asteria.Id);
        Assert.Equal("1.0", asteria.Version);
        Assert.Equal("1.3", asteria.LanguageVersion);
        Assert.Equal(10, asteria.States.Count);
        Assert.Equal(6, asteria.Concerns.Count);
        Assert.Null(asteria.CancellationEvent);
        Assert.DoesNotContain(asteria.Transitions, transition =>
            transition.Target is "PreparingForDeployment" or "ReadyForDeployment" or
                "DeploymentInProgress" or "PostDeploymentVerification" or "Completed");
        Assert.Equal("4.0", timedOrion.Version);
        Assert.Equal(TimeSpan.FromDays(7), timedOrion.Timers.Single(timer => timer.Id == "offer-expiry").Duration);
        Assert.Equal(TimeSpan.FromDays(6), timedOrion.Timers.Single(timer => timer.Id == "offer-reminder").Duration);
        Assert.Equal(TimeSpan.FromDays(10), timedOrion.Timers.Single(timer => timer.Id == "document-request-follow-up").Duration);
        Assert.Contains("AcceptOffer", orion.InputPolicy!.Commands.Keys);
        Assert.Contains("OfferDocumentGenerated", orion.InputPolicy.Observations.Keys);
        Assert.Equal("internal-document-review", documents.Id);
        Assert.NotEmpty(orion.Transitions);
        Assert.NotEmpty(documents.Transitions);
    }

    [Fact]
    public void Language_13_rejects_duplicate_concerns_and_unknown_concern_state_references()
    {
        var v6 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v6.json"));
        var duplicateConcern = v6.Replace(
            "\"id\": \"assetAvailability\"",
            "\"id\": \"documentCompleteness\"",
            StringComparison.Ordinal);
        var duplicateError = Assert.Throws<MachineDefinitionException>(
            () => MachineDefinitionLoader.Load(duplicateConcern));
        Assert.Contains(duplicateError.Issues, issue => issue.Code == "duplicate-concern-id");

        var unknownState = v6.Replace("\"state\": \"Reserved\"", "\"state\": \"Missing\"",
            StringComparison.Ordinal);
        var stateError = Assert.Throws<MachineDefinitionException>(
            () => MachineDefinitionLoader.Load(unknownState));
        Assert.Contains(stateError.Issues, issue => issue.Code == "unknown-guard-concern-state");
    }

    [Fact]
    public void Language_13_rejects_invalid_concern_transition_target_and_cancellation_contract()
    {
        var v6 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "definitions", "orion-v6.json"));
        var unknownTarget = v6.Replace("\"target\": \"Complete\"", "\"target\": \"Missing\"",
            StringComparison.Ordinal);
        var targetError = Assert.Throws<MachineDefinitionException>(
            () => MachineDefinitionLoader.Load(unknownTarget));
        Assert.Contains(targetError.Issues, issue => issue.Code == "unknown-transition-target");

        var invalidCancellation = v6.Replace("\"target\": \"Cancelled\"", "\"target\": \"Complete\"",
            StringComparison.Ordinal);
        var cancellationError = Assert.Throws<MachineDefinitionException>(
            () => MachineDefinitionLoader.Load(invalidCancellation));
        Assert.Contains(cancellationError.Issues, issue => issue.Code == "invalid-concern-cancellation");
    }

    [Fact]
    public void Rejects_input_policy_in_language_1_0_and_undeclared_mapped_events()
    {
        var legacyLanguage = TestSupport.MinimalDefinition()
            .Replace("\"transitions\": [", "\"inputPolicy\": { \"commands\": [], \"observations\": [] }, \"transitions\": [", StringComparison.Ordinal);
        var legacyError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(legacyLanguage));
        Assert.Contains(legacyError.Issues, issue => issue.Code == "input-policy-version");

        var language11 = TestSupport.MinimalDefinition()
            .Replace("\"languageVersion\": \"1.0\"", "\"languageVersion\": \"1.1\"", StringComparison.Ordinal)
            .Replace("\"transitions\": [",
                "\"inputPolicy\": { \"commands\": [{ \"name\": \"Attempt\", \"event\": \"Missing\" }], \"observations\": [] }, \"transitions\": [",
                StringComparison.Ordinal);
        var mappedEventError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(language11));
        Assert.Contains(mappedEventError.Issues, issue => issue.Code == "undeclared-input-event");
    }

    [Fact]
    public void Timer_declarations_require_language_1_2_and_validate_trigger_and_due_event()
    {
        var language11 = """
            {
              "languageVersion":"1.1", "id":"legacy", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}],
              "events":["Go"],
              "transitions":[{"id":"go","source":"A","event":"Go","target":"B"}],
              "timers":[]
            }
            """;
        var versionError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(language11));
        Assert.Contains(versionError.Issues, issue => issue.Code == "timer-version");

        var unknownEvent = """
            {
              "languageVersion":"1.2", "id":"timed", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}],
              "events":["Go"],
              "transitions":[{"id":"go","source":"A","event":"Go","target":"B"}],
              "timers":[{"id":"timer","onTransition":"go","duration":"P1D","event":"Missing","whileState":"A"}]
            }
            """;
        var eventError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(unknownEvent));
        Assert.Contains(eventError.Issues, issue => issue.Code == "undeclared-timer-event");
        Assert.Contains(eventError.Issues, issue => issue.Code == "timer-trigger-state-mismatch");
    }

    [Fact]
    public void Language_13_preserves_parent_timer_support()
    {
        var definition = """
            {
              "languageVersion":"1.3", "id":"timed-concerns", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}],
              "events":["Start","Deadline"],
              "inputPolicy":{"commands":[{"name":"Start","event":"Start"}],"observations":[]},
              "transitions":[
                {"id":"start","source":"A","event":"Start","target":"A"},
                {"id":"expire","source":"A","event":"Deadline","target":"B"}
              ],
              "timers":[{
                "id":"deadline","onTransition":"start","duration":"P1D","event":"Deadline",
                "whileState":"A","cancelOnTransitions":[],"blockedEvents":[],
                "invalidatedByDueTimers":[]
              }]
            }
            """;

        var loaded = MachineDefinitionLoader.Load(definition);

        Assert.Single(loaded.Timers);
        Assert.Equal("deadline", loaded.Timers[0].Id);
    }

    [Fact]
    public void Language_13_cancellation_and_parent_state_guards_are_rejected_in_older_versions()
    {
        var cancellation = TestSupport.MinimalDefinition()
            .Replace("\"events\": [\"Go\"],",
                "\"events\": [\"Go\"], \"cancellationEvent\": \"Go\",",
                StringComparison.Ordinal);
        var cancellationError = Assert.Throws<MachineDefinitionException>(
            () => MachineDefinitionLoader.Load(cancellation));
        Assert.Contains(cancellationError.Issues, issue => issue.Code == "cancellation-version");

        var parentGuard = TestSupport.MinimalDefinition("""
            {
              "id": "guarded",
              "source": "A",
              "event": "Go",
              "target": "B",
              "guard": { "op": "parentState", "state": "A" }
            }
            """);
        var guardError = Assert.Throws<MachineDefinitionException>(
            () => MachineDefinitionLoader.Load(parentGuard));
        Assert.Contains(guardError.Issues, issue => issue.Code == "parent-state-guard-version");
    }

    [Fact]
    public void Rejects_malformed_json_with_structured_issue()
    {
        var error = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load("{"));
        Assert.Contains(error.Issues, issue => issue.Code == "invalid-json");
    }

    [Theory]
    [InlineData("\"languageVersion\": \"2.0\"", "unsupported-language-version")]
    public void Rejects_unsupported_or_invalid_definition_fields(string replacement, string expectedCode)
    {
        var definition = TestSupport.MinimalDefinition("{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }")
            .Replace("\"languageVersion\": \"1.0\"", replacement, StringComparison.Ordinal);
        var error = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(definition));
        Assert.Contains(error.Issues, issue => issue.Code == expectedCode);
    }

    [Fact]
    public void Rejects_an_unknown_initial_state()
    {
        var definition = TestSupport.MinimalDefinition("{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }")
            .Replace("\"initialState\": \"A\"", "\"initialState\": \"Missing\"", StringComparison.Ordinal);
        var error = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(definition));
        Assert.Contains(error.Issues, issue => issue.Code == "unknown-initial-state");
    }

    [Fact]
    public void Rejects_unknown_and_duplicate_json_properties()
    {
        var unknownProperty = TestSupport.MinimalDefinition("{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }")
            .Replace("\"version\": \"1\",", "\"version\": \"1\", \"unknown\": true,", StringComparison.Ordinal);
        var unknownError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(unknownProperty));
        Assert.Contains(unknownError.Issues, issue => issue.Code == "unknown-property");

        var duplicateIdentity = TestSupport.MinimalDefinition("{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }")
            .Replace("\"id\": \"test-machine\"", "\"id\": \"test-machine\", \"id\": \"another-machine\"", StringComparison.Ordinal);
        var duplicateError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(duplicateIdentity));
        Assert.Contains(duplicateError.Issues, issue => issue.Code == "duplicate-property");
    }

    [Fact]
    public void Rejects_duplicate_states_and_events()
    {
        var definition = TestSupport.MinimalDefinition("{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }")
            .Replace("[{ \"id\": \"A\" }, { \"id\": \"B\", \"terminal\": true }]", "[{ \"id\": \"A\" }, { \"id\": \"A\" }, { \"id\": \"B\", \"terminal\": true }]", StringComparison.Ordinal)
            .Replace("\"events\": [\"Go\"]", "\"events\": [\"Go\", \"Go\"]", StringComparison.Ordinal);
        var error = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(definition));
        Assert.Contains(error.Issues, issue => issue.Code == "duplicate-state");
        Assert.Contains(error.Issues, issue => issue.Code == "duplicate-event");
    }

    [Fact]
    public void Rejects_missing_terminal_state_and_undeclared_references()
    {
        var definition = TestSupport.MinimalDefinition("{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Other\", \"target\": \"Missing\" }")
            .Replace("{ \"id\": \"B\", \"terminal\": true }", "{ \"id\": \"B\" }", StringComparison.Ordinal);
        var error = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(definition));
        Assert.Contains(error.Issues, issue => issue.Code == "missing-terminal-state");
        Assert.Contains(error.Issues, issue => issue.Code == "unknown-transition-target");
        Assert.Contains(error.Issues, issue => issue.Code == "undeclared-transition-event");
    }

    [Fact]
    public void Rejects_duplicate_transition_identifiers_terminal_outgoing_and_static_ambiguity()
    {
        var duplicateIds = TestSupport.MinimalDefinition(
            "{ \"id\": \"same\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }, { \"id\": \"same\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }");
        var duplicateError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(duplicateIds));
        Assert.Contains(duplicateError.Issues, issue => issue.Code == "duplicate-transition-id");
        Assert.Contains(duplicateError.Issues, issue => issue.Code == "ambiguous-unguarded-transitions");

        var terminalOutgoing = $$"""
            {
              "languageVersion":"1.0", "id":"terminal", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}],
              "events":["Go"], "transitions":[{"id":"bad","source":"B","event":"Go","target":"A"}]
            }
            """;
        var terminalError = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(terminalOutgoing));
        Assert.Contains(terminalError.Issues, issue => issue.Code == "terminal-state-transition");
    }

    [Theory]
    [InlineData("{ \"op\": \"execute\", \"code\": \"true\" }")]
    [InlineData("{ \"op\": \"gt\", \"fact\": \"score\", \"value\": \"high\" }")]
    [InlineData("{ \"op\": \"exists\", \"fact\": \"x\", \"surprise\": true }")]
    public void Rejects_unsupported_or_malformed_guards(string guard)
    {
        var definition = TestSupport.MinimalDefinition($"{{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\", \"guard\": {guard} }}");
        var error = Assert.Throws<MachineDefinitionException>(() => MachineDefinitionLoader.Load(definition));
        Assert.NotEmpty(error.Issues);
    }
}
