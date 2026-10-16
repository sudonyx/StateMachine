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
            TestSupport.Facts(("documentsComplete", FactValue.From(true)), ("riskScore", FactValue.From(75m)), ("equipmentValue", FactValue.From(50000m)), ("checksComplete", FactValue.From(true)), ("eligibilityDecision", FactValue.From("Eligible"))));
        Assert.Equal("AssessmentReady", runtime.Send("happy", "Submit").Instance.CurrentState);
        Assert.Equal("Approved", runtime.Send("happy", "Approve").Instance.CurrentState);

        runtime.Create("review", definition.Id, definition.Version,
            TestSupport.Facts(("documentsComplete", FactValue.From(true)), ("riskScore", FactValue.From(50m)), ("equipmentValue", FactValue.From(50000m)), ("checksComplete", FactValue.From(true)), ("manualReviewComplete", FactValue.From(true)), ("reviewerJustification", FactValue.From("reviewed"))));
        Assert.Equal("ManualReview", runtime.Send("review", "Submit").Instance.CurrentState);
        Assert.Equal("Approved", runtime.Send("review", "Approve").Instance.CurrentState);

        runtime.Create("rejected", definition.Id, definition.Version,
            TestSupport.Facts(("documentsComplete", FactValue.From(true)), ("riskScore", FactValue.From(20m))));
        Assert.Equal("Rejected", runtime.Send("rejected", "Submit").Instance.CurrentState);
        Assert.Equal(EvaluationStatus.Rejected, runtime.Send("rejected", "Cancel").Evaluation.Status);

        runtime.Create("cancelled", definition.Id, definition.Version);
        Assert.Equal(EvaluationStatus.Unresolved, runtime.Send("cancelled", "Cancel").Evaluation.Status);
        Assert.Equal("Cancelled", runtime.Send("cancelled", "Cancel",
            TestSupport.Facts(("cancellationPermitted", FactValue.From(true)))).Instance.CurrentState);
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

    [Fact]
    public void Per_attempt_facts_are_immutable_evaluation_inputs_and_never_change_instance_facts()
    {
        var definition = MachineDefinitionLoader.Load(TestSupport.MinimalDefinition(
            """{ "id": "go", "source": "A", "event": "Go", "target": "B", "guard": { "op": "eq", "fact": "approved", "value": true } }"""));
        var runtime = new MachineRuntime();
        runtime.Register(definition);
        var created = runtime.Create("context", definition.Id, definition.Version,
            TestSupport.Facts(("owner", FactValue.From("team-a"))));

        var result = runtime.Send("context", "Go",
            TestSupport.Facts(("approved", FactValue.From(true))));

        Assert.Equal(EvaluationStatus.Accepted, result.Evaluation.Status);
        Assert.Equal("B", result.Instance.CurrentState);
        Assert.Equal(TestSupport.Facts(("owner", FactValue.From("team-a"))), created.Facts);
        Assert.Equal(TestSupport.Facts(("owner", FactValue.From("team-a"))), runtime.GetInstance("context").Facts);
        Assert.Throws<ArgumentException>(() => runtime.Send("context", "Go",
            TestSupport.Facts(("owner", FactValue.From("different")))));
    }

    [Fact]
    public void Orion_offer_validity_and_cancellation_use_explicit_evaluation_facts()
    {
        var definition = TestSupport.LoadWorkflow("orion-v1.json");
        var runtime = new MachineRuntime();
        runtime.Register(definition);
        runtime.Create("offer", definition.Id, definition.Version,
            TestSupport.Facts(
                ("documentsComplete", FactValue.From(true)),
                ("riskScore", FactValue.From(75m)),
                ("equipmentValue", FactValue.From(50000m)),
                ("checksComplete", FactValue.From(true)),
                ("eligibilityDecision", FactValue.From("Eligible"))));
        runtime.Create("cancel", definition.Id, definition.Version);

        Assert.Equal("AssessmentReady", runtime.Send("offer", "Submit").Instance.CurrentState);
        Assert.Equal("AwaitingOfferAcceptance", runtime.Send("offer", "IssueOffer").Instance.CurrentState);
        Assert.Equal(EvaluationStatus.Rejected, runtime.Send("offer", "AcceptOffer",
            TestSupport.Facts(("offerValidAtEvaluation", FactValue.From(true)))).Evaluation.Status);
        Assert.Equal("AssetReservationPending", runtime.Send("offer", "AcceptOffer",
            TestSupport.Facts(
                ("offerValidAtEvaluation", FactValue.From(true)),
                ("offerEvaluationTime", FactValue.From("2026-10-14T15:37:00Z"))),
            decisionEvidence: null,
            attemptFactProvenance: new Dictionary<string, string>
            {
                ["offerValidAtEvaluation"] = "bound from trusted offer check",
                ["offerEvaluationTime"] = "explicit evaluation timestamp"
            }).Instance.CurrentState);

        Assert.Equal(EvaluationStatus.Unresolved, runtime.Send("cancel", "Cancel").Evaluation.Status);
        Assert.Equal(EvaluationStatus.Rejected, runtime.Send("cancel", "Cancel",
            TestSupport.Facts(("cancellationPermitted", FactValue.From(false)))).Evaluation.Status);
        Assert.Equal("Cancelled", runtime.Send("cancel", "Cancel",
            TestSupport.Facts(("cancellationPermitted", FactValue.From(true)))).Instance.CurrentState);
        Assert.DoesNotContain("offerEvaluationTime", runtime.GetInstance("offer").Facts.Keys);
    }
}
