using DurableStateMachine.Core;

namespace DurableStateMachine.Tests;

public sealed class DefinitionLoaderTests
{
    [Fact]
    public void Loads_both_workflow_definitions()
    {
        var orion = TestSupport.LoadWorkflow("orion-v1.json");
        var documents = TestSupport.LoadWorkflow("document-review-v1.json");

        Assert.Equal("orion-equipment-lease", orion.Id);
        Assert.Equal("1.1", orion.LanguageVersion);
        Assert.Equal("2.0", orion.Version);
        Assert.Contains("AcceptOffer", orion.InputPolicy!.Commands.Keys);
        Assert.Contains("OfferDocumentGenerated", orion.InputPolicy.Observations.Keys);
        Assert.Equal("internal-document-review", documents.Id);
        Assert.NotEmpty(orion.Transitions);
        Assert.NotEmpty(documents.Transitions);
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
