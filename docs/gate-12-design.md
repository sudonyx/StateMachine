# Gate 12 design — durable workflow concerns

## Scope

Gate 12 adds language 1.3 support for bounded, independent concern dimensions
within one durable instance. It implements the current gate only: declaration
validation, parent/concern event routing, declarative cross-dimension guards,
SQLite persistence and history, atomic cancellation, inspection, and replay.
It does not introduce hierarchical statecharts, separate cooperating machine
instances, or future-gate infrastructure.

## Runtime model

The existing parent state remains the authoritative application lifecycle.
Each concern has an immutable definition identity, initial state, finite state
set, events and transitions, plus a SQLite projection and append-only
transition history. An input mapping without a `concern` targets the parent;
one with a `concern` targets exactly that concern. Accepted concern inputs use
the same command/observation ingestion, authority, expected-operation,
evidence, deduplication and stale-input checks already used by Gate 5.

The revisions intentionally have separate meanings:

- Parent transitions advance the instance revision and use the existing parent
  history.
- Concern transitions advance only the addressed concern revision, record the
  unchanged parent revision, and leave the parent's state/revision untouched.
- A declared parent cancellation transition advances the parent revision and
  atomically advances every active concern to its terminal cancellation state.
  Those concern-history rows link to the same accepted parent attempt.

SQLite transactions serialize writes for an instance. This keeps the durable
vector and all relevant histories coherent, but does not provide independent
parallel commits for separate concerns. Revisions arbitrate concurrent
same-concern updates, while the transaction commits a cancellation as one
logical operation.

`concernState` and `parentState` guards read the immutable persisted evaluation
snapshot. They compose with existing logical guards; missing state is
unresolved. Logical findings include child explanations so a refusal can
identify an unmet coordination prerequisite. Transition selection remains
deterministic and is not delegated to the Orion application layer.

Concern inspection and replay are read-only. Concern replay verifies definition
identity/digest, revision continuity, state continuity, linked input attempts,
stored evaluation-evidence digests, deterministic concern transition
evaluation, and final projections. Parent replay continues to reconstruct only
parent transitions and excludes concern-only accepted attempts from the
parent-history completeness check. Neither replay path invokes providers or
repeats side effects.

Cancellation is intentionally explicit in the definition. Each nonterminal
state of every concern must provide one unguarded transition for the named
parent cancellation event to a terminal state. Once the parent is terminal,
later concern inputs are rejected even if an external operation remains
outstanding. The operation and its unknown result remain evidence; cancellation
does not imply remote compensation or failure.

## Orion 6.0 demonstration

[orion-v6.json](../definitions/orion-v6.json) preserves the Orion 4.0 and 5.0
definitions and binds 6.0 to language 1.3. It separates document completeness
and asset availability from the parent leasing lifecycle:

- Assessment cannot complete until mandatory documents are complete and the
  eligibility facts satisfy the declared conditions.
- Offer document preparation may progress independently of asset reservation.
- Offer release requires both complete documents and confirmed reservation.
- A reservation request moves the asset concern to `Pending`; only a trusted,
  correlated `EquipmentReserved` result moves it to `Reserved`.
- Parent cancellation atomically cancels the parent and every active concern.

The definition declares 9 parent states and 5 states in each of the two
concerns (19 state declarations total; theoretical state-vector product 225).
The runtime does not materialize that Cartesian product as flat parent states.
There is no implicit conversion or migration from existing 4.0/5.0 instances.

## Alternatives and trade-offs

The comparison and selection rationale are recorded in
[gate-12-architecture-decision.md](./gate-12-architecture-decision.md).
Orthogonal statecharts were not selected because hierarchy, dispatch ordering
and atomic region-transition semantics are outside the current evaluator.
Separate machines were not selected because durable messaging and partial
completion would complicate this single-instance cancellation/coordination
requirement. The selected model shares a transaction/failure boundary and
therefore deliberately gives up independent concern commits.

## Limitations

- An accepted input changes one dimension, except the declared all-active
  concern cancellation performed with a parent cancellation transition.
- Concerns cannot be nested and do not have their own timers.
- There are no arbitrary multi-concern business transitions, inter-instance
  transactions, automatic retries, or automatic migrations.
- SQLite is the only implemented persistence provider; multi-host guarantees
  apply to hosts sharing the same supported SQLite database configuration.
- The shared trusted-ingestion key is a training boundary, not production IAM.
- External effects remain governed by the existing effect-intent and
  observation boundaries. Cancellation does not reverse completed effects.
- Replay establishes consistency relative to stored history and evidence; it
  cannot prove that external evidence was truthful or protect against an
  administrator able to rewrite both stored content and its digest.

See [gate-12-test-results.md](./gate-12-test-results.md) for build, test and
corpus validation evidence.
