# State-machine language 1.0

The runtime accepts a deliberately small JSON definition format. Definitions are parsed and semantically validated before registration; unsupported or unknown properties are errors rather than ignored input.

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

## Definition semantics and validation

- The initial state must be declared.
- State IDs, event IDs, transition IDs, and JSON object property names must be unique in their respective scopes.
- There must be at least one terminal state.
- Every source and target must name a declared state; every transition event must be declared.
- Terminal states cannot have outgoing transitions.
- Two unguarded transitions cannot share a source and event. Guarded overlaps are checked when facts are evaluated; runtime ambiguity is refused.
- Only language version `1.0` is accepted. Unknown JSON properties and unsupported guard operators are errors.
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

Guards read only supplied facts. They cannot read time, call external services, mutate data, or execute code.

## Transition selection

The evaluator first refuses unknown current states, terminal states and undeclared events. It then considers transitions matching the current state and submitted event and evaluates every matching guard:

1. More than one enabled transition is `Ambiguous`.
2. Any unresolved guard is `Unresolved`, including when one transition is enabled, because the unresolved guard could also enable a competing transition.
3. No enabled transition and no unresolved guard is `Rejected`.
4. Exactly one enabled transition and no unresolved guard is `Accepted`.

Definition order never breaks ties. Results include the prior state, event, selected transition and target when accepted, guard findings, a reason code, and an explanation. Rejected, ambiguous and unresolved results do not change an instance.
