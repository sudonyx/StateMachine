# Gate 12 architecture decision

## Problem

Orion's application lifecycle, document completeness and equipment reservation have
distinct progress. A flat state for every combination would make unrelated work
appear to be one lifecycle transition and multiply state declarations unnecessarily.
The existing runtime is intentionally a deterministic, flat-state evaluator with
SQLite-backed instance revisions, immutable definitions, input receipts and history.
The design must control combination growth without moving coordination into hidden
Orion-specific application code.

## Alternatives

| Dimension | Hierarchical/orthogonal statecharts | Separate cooperating machines | Independent concern dimensions in one instance |
|---|---|---|---|
| Legal-state complexity | Orthogonal regions avoid declaring every combination, but hierarchy, entry/exit, event bubbling and region lifecycle multiply language semantics. | Each machine has a small legal state set; the global product still exists across machine instances. | Each concern declares its own finite legal states; no combined parent state is declared. |
| Transition determinism | Requires specified dispatch, region ordering, conflict resolution and simultaneous-transition semantics. | Each machine can be deterministic, but message ordering and cross-machine coordination affect outcomes. | Normal inputs route to one named concern or the parent; the declared parent cancellation is the sole atomic multi-concern update. Existing deterministic guard evaluation is reused. |
| Evidence and replay | Must record region selection and the exact compound transition set, then replay versioned statechart semantics. | Requires correlated histories and definition epochs across records, including partial completion. | Parent history uses the instance revision; each concern history uses its own concern revision and records the parent revision. Replay validates both histories and their stored evidence without effects. |
| Cross-concern coordination | Parent/region guards need explicit semantics and a representation for missing region state. | Coordination is a protocol across machine histories and requires durable messages and recovery. | Declarative parent guards read named concern states from the same instance snapshot. |
| Cancellation | Exit propagation and region-specific cancellation rules need formal statechart semantics. | Cancellation must propagate and can race with each machine or remote effect. | A declared parent cancellation transition atomically terminates all active concerns. Later concern inputs are refused; pending expected operations remain evidence and completed external effects are not reversed. |
| Testing complexity | Must cover hierarchy, dispatch, transition ordering, exit semantics and replay combinations. | Must cover message duplication, partial delivery, cross-instance recovery and independent commits. | Tests cover each concern transition, parent coordination and serialized revision races within one transaction. |
| Failure isolation | Regions share a statechart execution boundary; region failure semantics must be specified. | Stronger process/transaction isolation, at the cost of coordination uncertainty and partial updates. | Concerns intentionally share the instance transaction and failure boundary; SQLite writer arbitration serializes them. |

## Decision

Select **independent durable concern dimensions within one workflow instance**.
The parent remains authoritative for business lifecycle. A concern has a stable
identity, declared states and initial state, terminal markers, declared events and
transitions, and its own durable projection and transition history. Language 1.3
adds bounded declarations and concern-state guard references; this is not a UML
statechart implementation.

Input mappings explicitly identify either a parent event or one concern event.
An accepted input can transition exactly one dimension, except the declared
parent cancellation event, which atomically terminates every active concern.
A parent transition can consult the concern-state vector through declarative
guards. Parent transitions consume the instance revision and append parent
history. Concern transitions instead increment that concern's own revision and
append concern history; they leave the parent state and parent revision
unchanged. Every accepted change is committed with its attempt, receipt and
appropriate history under the existing SQLite transaction. Different concerns
are logically independent but are **not** independently committed or
parallelized.

Cancellation remains a parent transition. Once the parent is terminal, new
concern observations cannot change any concern or revive the parent. Existing
expected operations and external uncertainty remain inspectable; cancellation
does not claim remote reversal. Existing Gate 5 correlation and authority checks
remain prerequisites to accepting observations.

## Complexity evidence

The Orion 6.0 demonstration declares 9 parent states, 5 document states and
5 asset states: **19 declared individual states** across three dimensions.
Their theoretical product is `9 × 5 × 5 = 225` configurations before considering
reachability; the runtime does not declare those 225 as parent states. The
definition contains parent transitions and independent concern transitions,
plus explicit parent coordination guards. See the definition and Gate 12 design
for the exact counts and the supported reachable examples.

If a third concern has `k` states, the theoretical configuration product becomes
`225 × k`, while the declared state count grows additively by `k`. Reachable
combinations remain constrained by transitions, parent lifecycle and guards;
decomposition does not make the runtime's state vector cease to have a Cartesian
product. Complexity moves into explicit input routing, guard references,
transition evidence, and combinations that need testing. This is preferable to
duplicating parent lifecycle names and hidden application-level coordination.

### Rejected alternatives

- **Orthogonal statecharts** are rejected for this gate because the runtime has no
  hierarchy, dispatch ordering, or atomic multi-region transition model. Adding
  those would be a disproportionate expansion of language/evaluator semantics.
- **Separate cooperating machines** are rejected because these concerns share
  one application identity and must coordinate atomically with cancellation and
  parent progression. Independent histories and transactions would introduce
  partial-completion states and a durable messaging protocol that this gate does
  not require.

## Responsibilities and trade-offs

The generic runtime owns declaration validation, deterministic routing,
concern-state guard evaluation, revision arbitration, durable projections,
transition evidence and read-only replay. Orion's definition owns the vocabulary,
input mapping, legal transitions, observation evidence, and business coordination
rules. No Orion identity or event is embedded in generic execution logic.

This choice deliberately serializes updates to one instance and shares one
failure boundary. It avoids cross-instance protocols and makes cancellation,
history and recovery unambiguous. It does not provide concurrent commits for
different concerns, nested concerns, arbitrary multi-concern transitions, or
safe automatic migration from flat Orion 4.0/5.0 instances.
