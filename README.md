# Durable State Machine

A standalone, domain-independent finite state-machine runtime for the Gate 2 qualification: separate a reusable workflow definition from each running workflow instance.

## Build and test

Requires the .NET 10 SDK.

```powershell
dotnet build DurableStateMachine.slnx
dotnet test DurableStateMachine.slnx
```

## Repository layout

- `src/DurableStateMachine.Core/` — definition loading, validation, guard evaluation and in-memory execution.
- `tests/DurableStateMachine.Tests/` — unit and workflow execution tests.
- `definitions/` — Orion and internal document-review definitions.
- `docs/` — language and Gate 2 design documentation.
- `references/` — qualification and fictional Orion requirements source material.

## Gate 2 capabilities

The runtime supports immutable, versioned JSON machine definitions in language version 1.0, flat named states, terminal states, declared events, identified transitions, deterministic guards, validation, per-instance facts/revisions and an in-memory registry and instance store. Facts are supplied at instance creation and remain immutable for that instance's lifetime. Unknown JSON fields, unsupported syntax, invalid references and terminal-state transitions are rejected explicitly. Both bundled workflows run through the same generic evaluator and runtime.

### Minimal usage

```csharp
var definition = MachineDefinitionLoader.Load(File.ReadAllText("definitions/document-review-v1.json"));
var runtime = new MachineRuntime();
runtime.Register(definition);
runtime.Create(
    "review-123",
    definition.Id,
    definition.Version,
    new Dictionary<string, FactValue>
    {
        ["reviewer"] = FactValue.From("reviewer-1"),
        ["qualityScore"] = FactValue.From(85m)
    });

var result = runtime.Send("review-123", "Submit");
// result.Evaluation.Status == EvaluationStatus.Accepted
// result.Instance.CurrentState == "UnderReview"
```

The `Approve` event for this definition is accepted only after submission and when the reviewer fact is present and `qualityScore >= 70`. See [state-machine-language-v1.md](./docs/state-machine-language-v1.md) for the complete format and semantics and [gate-2-design.md](./docs/gate-2-design.md) for component responsibilities and trade-offs.

## Example workflows

- **Orion Equipment Leasing** represents draft submission, missing documents, readiness, manual review, approval, rejection and cancellation. Its business rules are represented as definition guards, not runtime branches. External effects, notification delivery, deadlines, offers, acceptance, persistence and lease servicing are omitted.
- **Internal Document Review** is an unrelated example with draft, review, approval and rejection paths. It uses the same loader, evaluator and runtime without workflow-specific code.

## Explicitly deferred

This project does not implement an HTTP API, durable persistence/history, replay, timers/deadlines, external-effect orchestration, outbox/inbox, retries, concurrency control, definition migration, hierarchical/parallel states, authentication or harness/decision-engine integration. In-memory instances are lost on process restart. These are later-gate concerns and must not be inferred from the current interfaces.
