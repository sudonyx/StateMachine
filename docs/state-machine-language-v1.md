# State-machine language 1.0 and 1.1

The runtime accepts deliberately small JSON definition formats. Definitions are parsed and semantically validated before registration; unsupported or unknown properties are errors rather than ignored input. Language 1.0 retains its original event-submission semantics. Language 1.1 adds opt-in input mappings; it does not reinterpret definitions already registered as language 1.0.

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

Language 1.1 may additionally declare an input policy:

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

Both `commands` and `observations` arrays are required when `inputPolicy` is present. Each mapping name is unique within its kind and maps to a declared workflow event. Observation mappings list required evidence keys. Language 1.0 definitions must not contain `inputPolicy`; register a new immutable definition version using language 1.1 to opt in.

## Definition semantics and validation

- The initial state must be declared.
- State IDs, event IDs, transition IDs, and JSON object property names must be unique in their respective scopes.
- There must be at least one terminal state.
- Every source and target must name a declared state; every transition event must be declared.
- Terminal states cannot have outgoing transitions.
- Two unguarded transitions cannot share a source and event. Guarded overlaps are checked when facts are evaluated; runtime ambiguity is refused.
- Language versions `1.0` and `1.1` are accepted. Unknown JSON properties and unsupported guard operators are errors. Language 1.1 adds only the optional input policy described above; the flat-state and guard semantics are unchanged.
- States are flat. Hierarchy, parallel regions, entry/exit actions, timers, arbitrary code, side effects and definition migration are unsupported.

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

The transition and guard semantics are shared by both language versions. Language 1.1 adds declared command/observation-to-event mappings; it does not change transition selection or the Gate 4 immutable evaluation context.
