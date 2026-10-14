# Gate 2 design

## Definition and instance

A `MachineDefinition` contains an ID/version, initial state, named states, declared events and transitions with optional guards and explanatory text. It is validated once by `MachineDefinitionLoader` and registered by `(id, version)`.

A `MachineInstance` contains its own ID, the exact definition ID/version, current state, bound facts and revision. Creating another instance from the same definition does not share evolving state. The included repository is in-memory only; instances do not survive process restart.

## Components and responsibilities

- `MachineDefinitionLoader` handles JSON syntax, unknown properties and semantic validation, producing structured validation issues.
- `TransitionEvaluator` is pure with respect to supplied definition, current state, event and facts. It returns a structured evaluation without changing an instance.
- `MachineRuntime` registers validated definitions, creates instances, invokes the evaluator and applies only accepted results. A successful transition increments the revision once.
- Machine definitions hold permitted behaviour; instances hold execution position and bound facts. The runtime contains no Orion or document-review branches.

Evaluation and mutation are separate so a refused attempt cannot partially update execution state, and so future persistence can place an explicit commit boundary around accepted results without changing the transition-selection algorithm.

## Worked outcomes

With an Orion instance in `Draft` and facts `documentsComplete=true`, `riskScore=75`, and `equipmentValue=50000`, `Submit` enables only `submit-ready-for-assessment`. The result is `Accepted`, targets `AssessmentReady`, and the runtime increments the revision.

With an instance in `Draft`, `Cancel` has no declared transition for document review. The result is `Rejected` with `no-applicable-transition`; its state and revision stay unchanged.

If two guarded transitions both evaluate true, the result is `Ambiguous` and no transition is selected. If one transition is true while another guard needs a missing fact, the result is `Unresolved`: the unknown guard could be a competing enabled transition. Neither result changes state.

## Determinism and ambiguity

Guard evaluation is deterministic and uses only the supplied typed facts. Missing, null and incompatible values do not silently become success or false; comparison results are unresolved. Logical expressions use decisive three-valued `and`/`or` semantics. Runtime refuses multiple enabled transitions and unresolved guards that may affect selection. The loader additionally rejects multiple unguarded transitions with the same source/event.

Side-effect-free guards make repeated evaluation with identical inputs reproducible and avoid embedding business service calls or policy execution in the interpreter. Business facts and decisions must be supplied by callers; this runtime does not obtain them.

Instances remain bound to an exact definition version even if another version is registered. Registration does not migrate or reinterpret existing instances.

## Limitations and evolution

Gate 2 initially had only an in-memory store and no transition history, durability, replay, locking, cross-process concurrency protection or API. Gate 3 adds SQLite persistence, an HTTP API, immutable definition versions and separate attempt and accepted-transition history; see [gate-3-design.md](./gate-3-design.md). Instance facts remain immutable after creation; there is no fact-update operation or evidence describing changing input data. The format supports flat states and a fixed scalar guard subset; it has no hierarchy, parallel states, timers, side effects, migrations or authentication. Orion models only an illustrative application subset and intentionally omits external services, deadlines, offers and lease servicing.
