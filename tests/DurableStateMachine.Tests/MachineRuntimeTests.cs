using DurableStateMachine.Core;

namespace DurableStateMachine.Tests;

public sealed class MachineRuntimeTests
{
    [Fact]
    public void Creates_independent_instances_bound_to_a_specific_definition_version()
    {
        var firstVersion = MachineDefinitionLoader.Load(TestSupport.MinimalDefinition(
            "{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }"));
        var secondVersionJson = TestSupport.MinimalDefinition(
            "{ \"id\": \"go\", \"source\": \"A\", \"event\": \"Go\", \"target\": \"B\" }")
            .Replace("\"version\": \"1\"", "\"version\": \"2\"", StringComparison.Ordinal);
        var runtime = new MachineRuntime();
        runtime.Register(firstVersion);
        runtime.Register(MachineDefinitionLoader.Load(secondVersionJson));
        var one = runtime.Create("one", "test-machine", "1");
        var two = runtime.Create("two", "test-machine", "2");

        runtime.Send("one", "Go");

        Assert.Equal("B", runtime.GetInstance("one").CurrentState);
        Assert.Equal("A", runtime.GetInstance("two").CurrentState);
        Assert.Equal("1", one.DefinitionVersion);
        Assert.Equal("2", two.DefinitionVersion);
    }

    [Fact]
    public void Only_accepted_transitions_mutate_state_and_increment_revision()
    {
        var runtime = new MachineRuntime();
        runtime.Register(TestSupport.LoadWorkflow("document-review-v1.json"));
        runtime.Create("doc", "internal-document-review", "1.0", TestSupport.Facts(("reviewer", FactValue.From("reviewer-1"))));

        var rejected = runtime.Send("doc", "Approve");
        Assert.Equal(EvaluationStatus.Rejected, rejected.Evaluation.Status);
        Assert.Equal("Draft", rejected.Instance.CurrentState);
        Assert.Equal(0, rejected.Instance.Revision);

        var submitted = runtime.Send("doc", "Submit");
        Assert.Equal(EvaluationStatus.Accepted, submitted.Evaluation.Status);
        Assert.Equal("UnderReview", submitted.Instance.CurrentState);
        Assert.Equal(1, submitted.Instance.Revision);

        var unresolved = runtime.Send("doc", "Approve");
        Assert.Equal(EvaluationStatus.Unresolved, unresolved.Evaluation.Status);
        Assert.Equal("UnderReview", unresolved.Instance.CurrentState);
        Assert.Equal(1, unresolved.Instance.Revision);
    }

    [Fact]
    public void Runs_orion_happy_manual_review_rejection_and_cancellation_paths()
    {
        var definition = TestSupport.LoadWorkflow("orion-v1.json");
        var runtime = new MachineRuntime();
        runtime.Register(definition);

        runtime.Create("happy", definition.Id, definition.Version,
            TestSupport.Facts(("documentsComplete", FactValue.From(true)), ("riskScore", FactValue.From(75m)), ("equipmentValue", FactValue.From(50000m)), ("checksComplete", FactValue.From(true)), ("eligible", FactValue.From(true))));
        Assert.Equal("AssessmentReady", runtime.Send("happy", "Submit").Instance.CurrentState);
        Assert.Equal("Approved", runtime.Send("happy", "Approve").Instance.CurrentState);

        runtime.Create("review", definition.Id, definition.Version,
            TestSupport.Facts(("documentsComplete", FactValue.From(true)), ("riskScore", FactValue.From(50m)), ("equipmentValue", FactValue.From(50000m)), ("checksComplete", FactValue.From(true)), ("reviewerJustification", FactValue.From("reviewed"))));
        Assert.Equal("ManualReview", runtime.Send("review", "Submit").Instance.CurrentState);
        Assert.Equal("Approved", runtime.Send("review", "Approve").Instance.CurrentState);

        runtime.Create("rejected", definition.Id, definition.Version,
            TestSupport.Facts(("documentsComplete", FactValue.From(true)), ("riskScore", FactValue.From(20m))));
        Assert.Equal("Rejected", runtime.Send("rejected", "Submit").Instance.CurrentState);
        Assert.Equal(EvaluationStatus.Rejected, runtime.Send("rejected", "Cancel").Evaluation.Status);

        runtime.Create("cancelled", definition.Id, definition.Version);
        Assert.Equal("Cancelled", runtime.Send("cancelled", "Cancel").Instance.CurrentState);
    }

    [Fact]
    public void Runs_document_review_happy_and_rejection_paths_with_generic_runtime()
    {
        var definition = TestSupport.LoadWorkflow("document-review-v1.json");
        var runtime = new MachineRuntime();
        runtime.Register(definition);
        runtime.Create("approved-doc", definition.Id, definition.Version,
            TestSupport.Facts(("reviewer", FactValue.From("reviewer-1")), ("qualityScore", FactValue.From(70m))));
        runtime.Create("rejected-doc", definition.Id, definition.Version);

        Assert.Equal("UnderReview", runtime.Send("approved-doc", "Submit").Instance.CurrentState);
        Assert.Equal("Approved", runtime.Send("approved-doc", "Approve").Instance.CurrentState);
        Assert.Equal("UnderReview", runtime.Send("rejected-doc", "Submit").Instance.CurrentState);
        Assert.Equal("Rejected", runtime.Send("rejected-doc", "Reject").Instance.CurrentState);
        Assert.Equal("undeclared-event", TransitionEvaluator.Evaluate(definition, "Draft", "Unknown", TestSupport.Facts()).ReasonCode);
    }

    [Fact]
    public void Rejects_duplicate_registration_and_unknown_definition_or_instance()
    {
        var definition = TestSupport.LoadWorkflow("document-review-v1.json");
        var runtime = new MachineRuntime();
        runtime.Register(definition);
        Assert.Throws<InvalidOperationException>(() => runtime.Register(definition));
        Assert.Throws<KeyNotFoundException>(() => runtime.Create("missing", "unknown", "1"));
        Assert.Throws<KeyNotFoundException>(() => runtime.GetInstance("missing"));
    }
}
