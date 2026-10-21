# State-machine language 1.0, 1.1, 1.2 and 1.3

The runtime accepts deliberately small JSON definition formats. Definitions are parsed and semantically validated before registration; unsupported or unknown properties are errors rather than ignored input. Language 1.0 retains its original event-submission semantics. Language 1.1 adds opt-in input mappings. Language 1.2 adds declarative durable timers. Language 1.3 adds independent durable concern dimensions and cancellation semantics while preserving input-mapping and timer capabilities.

## Structure

```json
{
  "languageVersion": "1.0",
  "id": "document-review",
  "version": "1",
  "initialState": "Draft",
  "states": [
    { "id": "Draft" },
    { "id": "Approved", "terminal": true }
  ],
  "events": ["Submit", "Approve"],
  "transitions": [
    {
      "id": "approve",
      "source": "Draft",
      "event": "Approve",
      "target": "Approved",
      "description": "Approve after a reviewer has checked the document.",
      "guard": { "op": "exists", "fact": "reviewer" }
    }
  ]
}
```

All top-level properties shown are required. `description` is optional; all other transition properties are required. A state is nonterminal unless `"terminal": true`. IDs are non-empty, case-sensitive strings. Each `(id, version)` identifies one immutable registered definition.

Language 1.1 and 1.2 may additionally declare an input policy:

```json
{
  "languageVersion": "1.1",
  "id": "example",
  "version": "2",
  "initialState": "Draft",
  "states": [{ "id": "Draft" }, { "id": "Done", "terminal": true }],
  "events": ["Finish"],
  "inputPolicy": {
    "commands": [{ "name": "RequestFinish", "event": "Finish" }],
    "observations": [{
      "name": "WorkCompleted",
      "event": "Finish",
      "requiredEvidence": ["resultDigest"]
    }]
  },
  "transitions": [{ "id": "finish", "source": "Draft", "event": "Finish", "target": "Done" }]
}
```

Both `commands` and `observations` arrays are required when `inputPolicy` is present. Each mapping name is unique within its kind and maps to a declared workflow event. Observation mappings list required evidence keys. Language 1.0 definitions must not contain `inputPolicy`; register a new immutable definition version using language 1.1 or later to opt in.

## Language 1.2 timers

Language 1.2 may additionally declare timers. A timer is scheduled only when its named transition is accepted:

```json
{
  "languageVersion": "1.2",
  "id": "timed-example",
  "version": "2",
  "initialState": "Open",
  "states": [{ "id": "Open" }, { "id": "Expired", "terminal": true }],
  "events": ["Start", "DeadlineReached"],
  "inputPolicy": {
    "commands": [{ "name": "Start", "event": "Start" }],
    "observations": []
  },
  "transitions": [
    { "id": "start", "source": "Open", "event": "Start", "target": "Open" },
    { "id": "expire", "source": "Open", "event": "DeadlineReached", "target": "Expired" }
  ],
  "timers": [{
    "id": "deadline",
    "onTransition": "start",
    "duration": "P7D",
    "event": "DeadlineReached",
    "whileState": "Open",
    "cancelOnTransitions": [],
    "blockedEvents": [],
    "invalidatedByDueTimers": []
  }]
}
```

Timer properties:

- `id` is unique within the definition.
- `onTransition` identifies the accepted transition that starts this scheduling occurrence.
- `duration` is a positive ISO-8601 duration (`P10D`, `PT12H`, etc.). It is added once to the accepted transition's server-side UTC time.
- `event` is a declared event evaluated through the ordinary deterministic transition evaluator when due.
- `whileState` is the state in which the timer remains eligible. Leaving it cancels pending timers, even if a later transition re-enters the same state.
- `cancelOnTransitions` optionally lists accepted transition IDs that cancel it even if the destination state is unchanged.
- `blockedEvents` optionally lists business events refused at or after the persisted due instant. This closes the race where a due but unprocessed deadline could otherwise be defeated by a late business event.
- `invalidatedByDueTimers` optionally lists timer declaration IDs whose persisted deadline suppresses this timer once that other timer is due, even if it has not yet been processed.
- `effectIntent` is optional. When the timer event is accepted, the configured capability's durable Gate 7 effect intent and expected observation are created in the same SQLite transaction. It does not invoke a provider.

`timers` is permitted in language 1.2 and 1.3. A definition with timers must declare `inputPolicy`; timer-generated events cannot be mapped to caller-submittable commands or observations. Direct `/events` submissions are restricted for timed definitions. Ordinary clients cannot make a timer authoritative by submitting its ID or its event.

The persistence layer records the accepted trigger and history identity, originating revision, definition ID/version, trigger time, deadline calculation time and exact UTC `dueAt`. Restart processing reads that persisted value; it does not recalculate from the current definition. The due processor validates the bound definition and current `whileState`, records its processing outcome, and passes eligible events through the same evaluator as other workflow inputs. Language 1.3 retains these parent timer semantics; concerns do not declare independent timers.

## Definition semantics and validation

- The initial state must be declared.
- State IDs, event IDs, transition IDs, and JSON object property names must be unique in their respective scopes.
- There must be at least one terminal state.
- Every source and target must name a declared state; every transition event must be declared.
- Terminal states cannot have outgoing transitions.
- Two unguarded transitions cannot share a source and event. Guarded overlaps are checked when facts are evaluated; runtime ambiguity is refused.
- Language versions `1.0`, `1.1`, `1.2` and `1.3` are accepted. Unknown JSON properties and unsupported guard operators are errors. Language 1.1 adds input policies; language 1.2 adds optional timer declarations; language 1.3 adds optional concern and cancellation declarations. Existing definitions retain their original semantics.
- States are flat. Hierarchy, parallel regions, entry/exit actions and arbitrary code are unsupported. Definitions contain no migration syntax: Gate 11 supports only explicitly registered migration contracts outside the evaluator, and refuses pairs/states without a contract. Timer declarations do not execute code or call providers.

## Language 1.3 durable concerns

Language 1.3 may declare independent, named finite-state concerns alongside the
parent lifecycle. Concerns are separate projections and histories within the
same instance, not nested or orthogonal statecharts:

```json
{
  "languageVersion": "1.3",
  "id": "example",
  "version": "1",
  "initialState": "Open",
  "cancellationEvent": "Cancel",
  "states": [{ "id": "Open" }, { "id": "Closed", "terminal": true }],
  "events": ["Cancel"],
  "inputPolicy": {
    "commands": [],
    "observations": [{
      "name": "WorkCompleted",
      "event": "Complete",
      "concern": "work",
      "requiredEvidence": ["resultDigest"]
    }]
  },
  "transitions": [
    { "id": "close", "source": "Open", "event": "Cancel", "target": "Closed" }
  ],
  "concerns": [{
    "id": "work",
    "initialState": "Pending",
    "states": [{ "id": "Pending" }, { "id": "Complete" }, { "id": "Cancelled", "terminal": true }],
    "events": ["Complete", "Cancel"],
    "transitions": [
      { "id": "complete-work", "source": "Pending", "event": "Complete", "target": "Complete" },
      { "id": "cancel-work", "source": "Pending", "event": "Cancel", "target": "Cancelled" }
    ]
  }]
}
```

The optional `concern` property on an input mapping routes that command or
observation to exactly one declared concern. Without it, the mapping targets a
parent event. An accepted concern input changes only that concern: its own
revision and concern history advance, while the parent state and parent
revision remain unchanged. The input attempt and inbound receipt are still
durable and use the same input-authority, expected-operation, evidence,
deduplication and stale-input checks as other commands and observations.
Concern-only inputs cannot be submitted through the direct `/events` route.

The optional top-level `cancellationEvent` names a parent event whose accepted
transition must enter a terminal parent state. Every nonterminal state of every
declared concern must have exactly one unguarded transition for that event to a
terminal concern state. Acceptance of the parent cancellation and termination
of all currently active concerns commit atomically. After the parent is
terminal, later concern inputs are refused; this does not reverse a completed
external effect or imply that an outstanding external operation failed.

Guards may use `{"op":"concernState","concern":"work","state":"Complete"}` or
`{"op":"parentState","state":"Open"}`. These read the evaluation snapshot and
compose with the existing `and`, `or` and `not` operators. A missing concern
state is unresolved, not a successful condition. The evaluator reports the
failed or unresolved child explanations for logical guards so coordination
refusals identify their unmet prerequisite.

The parent revision tracks parent lifecycle transitions only; each concern has
its own monotonically increasing revision. Concern history records both the
unchanged parent revision and the concern revision before/after. Replay
reconstructs each concern from its bound definition, linked durable input
attempt and stored evaluation evidence, then checks its projection. Parent
replay excludes concern-only accepted attempts from the parent-history
one-to-one check. Both replay paths are read-only and never repeat effects.
Concerns have no independent timers, nested concerns, arbitrary multi-concern
transitions or cross-instance transactions in this language version.

## Gate 11 and Gate 12 definition versioning

Each `(id, version)` is immutable, and a created instance remains bound to its exact version unless an explicitly requested migration contract is validated and durably committed. Gate 12 adds no migration from Orion 4.0 or 5.0 to Orion 6.0. There is no automatic latest-version selection, effective-date policy selector, general migration DSL or fact transformation language. A migration contract is application-registered and checked by the persistence boundary; see [gate-11-design.md](./gate-11-design.md). Orion 5.0 remains a separate language 1.2 definition; see [orion-definition-v5.md](./orion-definition-v5.md).

Validation exceptions expose a list of `ValidationIssue` values (`Code`, JSON `Path`, and `Message`). Invalid JSON is reported as `invalid-json`.

## Guard syntax

Guards are JSON expression trees. Fact names identify supplied, immutable instance facts.

| Operator | Shape | Meaning |
| --- | --- | --- |
| `eq`, `ne` | `{"op":"eq","fact":"score","value":50}` | Strictly typed equality / inequality. |
| `gt`, `gte`, `lt`, `lte` | Same shape | Decimal numeric comparison only. |
| `exists`, `missing` | `{"op":"exists","fact":"reviewer"}` | Tests whether a key is present, independent of its value. |
| `and`, `or` | `{"op":"and","args":[guard,guard,...]}` | Requires at least two child guards. |
| `not` | `{"op":"not","arg":guard}` | Negates one child guard. |

Fact values use explicit `FactValue` factories: `From(string)`, `From(decimal)`, `From(bool)`, and `Null`. Strings compare ordinally and case-sensitively; numeric values use `decimal`; booleans only compare with booleans. Objects, arrays and non-finite numbers are not supported.

Presence checks distinguish absent from null: an absent fact passes `missing` and fails `exists`; an explicitly null fact passes `exists` and fails `missing`. Null is not a comparison literal. Comparing an absent, null, or wrong-typed fact produces an unresolved guard result rather than false.

Logical expressions use three-valued, decisive evaluation:

- `false AND unresolved` is false; otherwise an unresolved operand makes `and` unresolved.
- `true OR unresolved` is true; otherwise an unresolved operand makes `or` unresolved.
- `not unresolved` is unresolved.

Guards read only the immutable resolved facts supplied for one evaluation. These comprise the instance's long-lived facts and any explicitly submitted per-attempt facts. A per-attempt fact is never merged into or retained as an instance fact. If both sources bind the same name, the evaluation request is refused; attempt facts do not override instance facts.

Missing, explicitly null, unknown and unavailable inputs remain distinct. Missing comparison facts, null comparisons and incompatible types are unresolved rather than false. Where decision evidence identifies a fact as unknown or unavailable, a comparison remains unresolved with that specific finding. Presence checks distinguish a known missing fact from null; unknown or unavailable facts do not become a false presence result.

Guards cannot read time, call external services, mutate data, or execute code. Time-sensitive facts such as offer validity must be calculated before submission and bound explicitly, with their evaluation time preserved as evidence. External business decisions are ordinary bound data to the generic runtime; decision-system-specific interpretation stays outside Core.

## Transition selection

The evaluator first refuses unknown current states, terminal states and undeclared events. It then considers transitions matching the current state and submitted event and evaluates every matching guard:

1. More than one enabled transition is `Ambiguous`.
2. Any unresolved guard is `Unresolved`, including when one transition is enabled, because the unresolved guard could also enable a competing transition.
3. No enabled transition and no unresolved guard is `Rejected`.
4. Exactly one enabled transition and no unresolved guard is `Accepted`.

Definition order never breaks ties. Results include the prior state, event, selected transition and target when accepted, guard findings, a reason code, and an explanation. Rejected, ambiguous and unresolved results do not change an instance.

The transition and guard semantics are shared by all language versions. Language 1.1 adds declared command/observation-to-event mappings; language 1.2 adds persisted timer inputs outside the evaluator. Neither changes transition selection or the Gate 4 immutable evaluation context.
