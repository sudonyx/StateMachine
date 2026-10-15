using DurableStateMachine.Core;

namespace DurableStateMachine.Tests;

public sealed class TransitionEvaluatorTests
{
    [Fact]
    public void Evaluates_boolean_numeric_string_presence_and_logical_guards()
    {
        var definition = MachineDefinitionLoader.Load("""
            {
              "languageVersion":"1.0", "id":"guards", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}], "events":["Go"],
              "transitions":[{"id":"go","source":"A","event":"Go","target":"B","guard":{
                "op":"and","args":[
                  {"op":"eq","fact":"enabled","value":true},
                  {"op":"gte","fact":"score","value":50},
                  {"op":"eq","fact":"label","value":"exact"},
                  {"op":"exists","fact":"note"},
                  {"op":"not","arg":{"op":"missing","fact":"enabled"}}
                ]
              }}]
            }
            """);
        var facts = TestSupport.Facts(
            ("enabled", FactValue.From(true)),
            ("score", FactValue.From(50m)),
            ("label", FactValue.From("exact")),
            ("note", FactValue.Null));

        var result = TransitionEvaluator.Evaluate(definition, "A", "Go", facts);

        Assert.Equal(EvaluationStatus.Accepted, result.Status);
        Assert.Equal("go", result.TransitionId);
    }

    [Fact]
    public void Rejects_false_guard_and_reports_absent_null_and_incompatible_inputs_as_unresolved()
    {
        var definition = MachineDefinitionLoader.Load("""
            {
              "languageVersion":"1.0", "id":"guard-errors", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}], "events":["Go"],
              "transitions":[{"id":"go","source":"A","event":"Go","target":"B","guard":{"op":"gte","fact":"score","value":60}}]
            }
            """);

        Assert.Equal(EvaluationStatus.Unresolved, TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts()).Status);
        Assert.Equal(EvaluationStatus.Unresolved, TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("score", FactValue.Null))).Status);
        Assert.Equal(EvaluationStatus.Unresolved, TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("score", FactValue.From("60")))).Status);
        Assert.Equal(EvaluationStatus.Rejected, TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("score", FactValue.From(59m)))).Status);
        Assert.Equal(EvaluationStatus.Accepted, TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("score", FactValue.From(60m)))).Status);
    }

    [Fact]
    public void Returns_unresolved_when_a_guard_could_compete_with_an_enabled_transition()
    {
        var definition = MachineDefinitionLoader.Load("""
            {
              "languageVersion":"1.0", "id":"ambiguity", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true},{"id":"C","terminal":true}],
              "events":["Go"], "transitions":[
                {"id":"known","source":"A","event":"Go","target":"B","guard":{"op":"eq","fact":"ready","value":true}},
                {"id":"unknown","source":"A","event":"Go","target":"C","guard":{"op":"eq","fact":"risk","value":true}}
              ]
            }
            """);

        var result = TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("ready", FactValue.From(true))));
        Assert.Equal(EvaluationStatus.Unresolved, result.Status);
        Assert.Equal("unresolved-competing-guard", result.ReasonCode);
    }

    [Fact]
    public void Reports_multiple_enabled_guarded_transitions_as_ambiguous()
    {
        var definition = MachineDefinitionLoader.Load("""
            {
              "languageVersion":"1.0", "id":"ambiguous", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true},{"id":"C","terminal":true}],
              "events":["Go"], "transitions":[
                {"id":"first","source":"A","event":"Go","target":"B","guard":{"op":"exists","fact":"flag"}},
                {"id":"second","source":"A","event":"Go","target":"C","guard":{"op":"eq","fact":"flag","value":true}}
              ]
            }
            """);

        var result = TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("flag", FactValue.From(true))));
        Assert.Equal(EvaluationStatus.Ambiguous, result.Status);
    }

    [Fact]
    public void Uses_three_valued_logic_to_allow_decisive_or_and_false_and_results()
    {
        var definition = MachineDefinitionLoader.Load("""
            {
              "languageVersion":"1.0", "id":"logic", "version":"1", "initialState":"A",
              "states":[{"id":"A"},{"id":"B","terminal":true}], "events":["Go"],
              "transitions":[{"id":"go","source":"A","event":"Go","target":"B","guard":{
                "op":"or","args":[
                  {"op":"eq","fact":"known","value":true},
                  {"op":"eq","fact":"missing","value":true}
                ]
              }}]
            }
            """);

        var result = TransitionEvaluator.Evaluate(definition, "A", "Go", TestSupport.Facts(("known", FactValue.From(true))));
        Assert.Equal(EvaluationStatus.Accepted, result.Status);
    }

    [Fact]
    public void Identical_inputs_produce_identical_results_and_refuses_invalid_events_and_terminal_state()
    {
        var definition = TestSupport.LoadWorkflow("document-review-v1.json");
        var facts = TestSupport.Facts();
        var first = TransitionEvaluator.Evaluate(definition, "Draft", "Submit", facts);
        var second = TransitionEvaluator.Evaluate(definition, "Draft", "Submit", facts);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.PreviousState, second.PreviousState);
        Assert.Equal(first.Event, second.Event);
        Assert.Equal(first.TransitionId, second.TransitionId);
        Assert.Equal(first.TargetState, second.TargetState);
        Assert.Equal(first.GuardFindings, second.GuardFindings);
        Assert.Equal("undeclared-event", TransitionEvaluator.Evaluate(definition, "Draft", "Unknown", facts).ReasonCode);
        Assert.Equal("terminal-state", TransitionEvaluator.Evaluate(definition, "Approved", "Submit", facts).ReasonCode);
    }

    [Fact]
    public void Preserves_unknown_and_unavailable_fact_states_as_unresolved_guard_findings()
    {
        var definition = MachineDefinitionLoader.Load(TestSupport.MinimalDefinition(
            """{ "id": "go", "source": "A", "event": "Go", "target": "B", "guard": { "op": "eq", "fact": "decision", "value": "Proceed" } }"""));
        var unknownEvidence = DecisionEvidence(DecisionOutcomeState.Unknown, unknownFacts: ["decision"]);
        var unavailableEvidence = DecisionEvidence(DecisionOutcomeState.Unavailable, unavailableFacts: ["decision"]);

        var unknown = TransitionEvaluator.Evaluate(
            definition, "A", "Go",
            TransitionEvaluationContext.Create(TestSupport.Facts(), decisionEvidence: unknownEvidence));
        var unavailable = TransitionEvaluator.Evaluate(
            definition, "A", "Go",
            TransitionEvaluationContext.Create(TestSupport.Facts(), decisionEvidence: unavailableEvidence));

        Assert.Equal(EvaluationStatus.Unresolved, unknown.Status);
        Assert.Equal("unknown-fact", Assert.Single(unknown.GuardFindings).Code);
        Assert.Equal(EvaluationStatus.Unresolved, unavailable.Status);
        Assert.Equal("unavailable-fact", Assert.Single(unavailable.GuardFindings).Code);
    }

    [Fact]
    public void Rejects_fact_collisions_and_provenance_for_unbound_per_attempt_facts()
    {
        var instanceFacts = TestSupport.Facts(("score", FactValue.From(4m)));
        Assert.Throws<ArgumentException>(() => TransitionEvaluationContext.Create(
            instanceFacts,
            TestSupport.Facts(("score", FactValue.From(5m)))));
        Assert.Throws<ArgumentException>(() => TransitionEvaluationContext.Create(
            instanceFacts,
            attemptFactProvenance: new Dictionary<string, string> { ["missing"] = "caller label" }));
    }

    [Fact]
    public void Copies_external_evidence_collections_into_the_immutable_evaluation_context()
    {
        var unknownFacts = new List<string> { "decision" };
        var evidence = DecisionEvidence(DecisionOutcomeState.Unknown, unknownFacts: unknownFacts);

        var context = TransitionEvaluationContext.Create(TestSupport.Facts(), decisionEvidence: evidence);
        unknownFacts.Add("later-mutation");

        Assert.Equal(["decision"], context.DecisionEvidence!.UnknownFacts);
        Assert.DoesNotContain("later-mutation", context.UnresolvedFacts.Keys);
    }

    private static ExternalDecisionEvidence DecisionEvidence(
        DecisionOutcomeState outcomeState,
        IReadOnlyList<string>? unknownFacts = null,
        IReadOnlyList<string>? unavailableFacts = null) =>
        new(
            "example-decision-system",
            "external-evaluation-1",
            outcomeState,
            null,
            DateTimeOffset.Parse("2026-10-14T12:00:00Z"),
            "2026-10-14",
            DateTimeOffset.Parse("2026-10-14T12:00:00Z"),
            "source-fact-digest",
            ["policy-version-1"],
            "local-evidence-digest",
            """{"evidence":{}}""",
            "unverified caller-provided provenance",
            [],
            unknownFacts ?? [],
            unavailableFacts ?? []);
}
