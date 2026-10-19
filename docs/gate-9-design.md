# Gate 9 design — duplicate, stale and concurrent inputs

## Scope

Gate 9 strengthens the existing SQLite persistence boundary. `TransitionEvaluator` remains pure and unchanged; commands, observations and timer inputs still reach it only through their established services. No event broker, replay engine or domain-specific transition logic was added.

The API's per-instance semaphore is now held by an `InstanceLockRegistry` scoped to one API host. It reduces contention within that host but is not shared between hosts and is not a correctness boundary. Every state-changing `MachineStore` transaction obtains a SQLite `BEGIN IMMEDIATE` writer reservation before reading the instance. Independent hosts therefore serialize at the database file and the later transaction reads the committed revision rather than evaluating against a stale read snapshot. The accepted state update remains a conditional `WHERE Id = ... AND Revision = ...` update. A failed conditional update is recorded as a conflict attempt; a command arriving with an old expected revision is persisted as `Stale` without evaluation.

The database also enforces unique `(InstanceId, RevisionAfter)` transition history. The constraint is a final integrity guard: a revision cannot have two accepted history rows. State, receipt, attempt, accepted history, expected-operation resolution, timer changes and generated effect intents remain within their existing transaction boundaries. No incompatible operation is silently retried against a newly read state.

SQLite has one writer per database, so this is serialized, file-backed concurrency rather than per-instance parallel writes. It is supported only where all hosts use the same SQLite database with working SQLite locking; it does not establish guarantees for a network filesystem or another database provider. The configured SQLite busy timeout can still be exceeded under sustained contention; such an error rolls back the transaction and is surfaced as an API failure, not converted to a successful transition.

## Identity model and duplicate policy

The identities are deliberately separate:

| Identity | Runtime representation | Use |
|---|---|---|
| Delivery ID | Optional `deliveryId` on the input and nullable receipt column | Identifies a transport attempt; different deliveries may refer to the same logical input. Excluded from the canonical message digest. |
| Event/message ID | `messageId` in the HTTP contract; the corpus `event_id` maps to it | Durable logical input identity, scoped by instance and input kind. Same ID and same immutable envelope returns `Duplicate` linked to the original attempt. Reuse with changed content returns `IdentityConflict`. |
| Command operation ID | Optional `operationId` | Separately retained command/business-operation metadata. It is not treated as a delivery ID or automatically deduplicated; business semantics still decide whether a new command is applicable. |
| Observation ID | Optional `observationId` | Separately retained external-result identity. Exact message replay uses `messageId`; a distinct message reporting an already-resolved expected operation is checked against the stored result digest. |
| Expected-operation ID | Observation `correlationId` | Correlates a claimed result to a registered external activity, source, expected revision and evidence contract. It is not the observation ID. |
| Instance revision | `expectedRevision` and persisted `Revision` | Optimistic workflow position. Commands with a revision other than the current one are stale; caller timestamps do not change this check. |
| Timer ID | Existing durable timer row ID | Stable identity of a scheduled logical time input. The declaration/origin-history unique index prevents duplicate schedules; a timer is re-read and claimed inside the database transaction before evaluation. |
| Harness execution ID | Existing Gate 7 `HarnessExecutionId` | Correlates the external Generic Harness execution. It is not a workflow event or message identity. |

Exact input retries preserve the original transition/attempt relationship. A delivery with the same logical message ID but a different canonical envelope is refused as `IdentityConflict`. A command with a new message ID is not relabeled an exact duplicate: it is checked against the current revision and deterministic evaluator. Depending on state and revision it is `Stale` or an evaluator refusal. The runtime does not impose a universal semantic-deduplication rule on business commands.

Observation idempotency has two layers: exact message identity first, then expected-operation resolution/result-digest comparison for a new message identity. A resolved operation cannot advance the workflow a second time. The first accepted or refused result remains authoritative; later conflicting claims are preserved as refused receipts.

## Ordering, authority and terminal instances

Committed history is ordered by accepted revision, never by caller-supplied `occurredAt`. The timestamp is evidence about a claimed occurrence and does not let an old request bypass current revision, expected-operation, source-authority, evidence or deadline checks. A stale observation is retained with its reason; an operation without its required correlation remains `Unexpected` rather than being inferred.

Observations still require a registered expected operation and the configured trusted ingestion boundary. Source identity, expected revision, lifecycle eligibility and required evidence are checked before evaluator invocation. Expected operations resolve in the same transaction as the accepted receipt and history.

Terminal state remains authoritative. Commands can be refused by the evaluator and late observations are recorded `Stale`; a pre-cancellation occurrence time does not reactivate an instance. Provider/Harness evidence is not removed when a result is late. Existing Gate 8 deadline arbitration still uses the persisted due time and API clock; the corpus runner does not forge server time from `deliveredAt`.

## Timers and effects

Timer discovery is not a claim/commit boundary. Each due timer is re-read under the same SQLite immediate transaction as its state transition; only a still-pending, eligible timer is evaluated. Existing unique timer schedule keys and status changes prevent duplicate firing. Deadline-blocked business commands remain refused even if the expiry worker has not yet processed the timer. Timer scheduling/cancellation and transition history continue to commit atomically.

Duplicate workflow receipts do not re-evaluate the event and therefore do not create another effect intent. Existing stable effect-operation IDs and Gate 7 Harness execution correlation remain unchanged. The corpus runner records intents/timers and checks their identities; it does not call live providers or automatically dispatch/retry notifications.

## Malformed and unknown requests

An unknown instance returns `404`; the runtime does not create an instance from an input. A typed malformed request that reaches the command/observation handler for an existing instance is persisted as a `Malformed` receipt. Missing required observation evidence is likewise a durable malformed receipt. Invalid JSON or model-binding failures rejected before the endpoint runs cannot be associated with a durable workflow receipt. Malformed input for an unknown instance remains `404` because there is no instance to attach the receipt to.

## Persistence and migration

Gate 9 adds nullable `DeliveryId`, `OperationId` and `ObservationId` columns to `InboundMessages`, preserving existing rows, and adds a non-unique delivery lookup index. Delivery ID is an audit identity, not a deduplication key; several network receipts may report a reused delivery ID. Gate 9 also adds the unique transition-revision index. If historical data contains two transitions for one instance revision, index creation fails at startup instead of hiding or rewriting that history.

## Guarantees and limitations

- Separate API hosts have separate process-local lock registries; SQLite's immediate transaction and revision constraint are the cross-host correctness mechanisms.
- Exact input identity, accepted state, transition history, timer mutation and operation resolution commit atomically.
- A duplicate receipt references the first attempt; a new semantic command remains subject to normal evaluator and revision rules.
- SQLite serializes writers across the database, not just one instance. Lock contention and its timeout remain operational limits.
- The integration test constructs two API hosts in one test process, each with an independent lock registry and context scope. It validates database-mediated arbitration; it is not an OS-process-kill test.
- The full corpus is run against one real API host in concurrent mode. A separate-host runner mode is provided but was not exercised by this run.
- There is no delivery audit for JSON rejected before endpoint binding, no universal command-operation idempotency policy, no general observation reorder buffer, no distributed lock service, no network-filesystem guarantee and no Gate 10 history reconstruction.
