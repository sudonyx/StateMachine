# Gate 3 design

## Boundaries

- **Core** remains responsible for definition validation, typed facts and pure deterministic transition evaluation. Gate 3 does not alter the evaluator.
- **Persistence** stores the exact definition JSON, immutable machine identity/version, current instance snapshots, input attempts and accepted-transition history in SQLite.
- **API** validates HTTP inputs, maps explicit request/response DTOs and coordinates persistence with Core. EF entities are not returned as API contracts.
- **Clients** consume only the HTTP API. The CLI is workflow-agnostic; the Orion console client supplies fictional Orion facts and events but contains no transition selection.

## Definitions and instances

Definition registration validates before insertion and stores the exact JSON representation plus a SHA-256 digest, language version and registration timestamp. The composite `(id, version)` primary key permits concurrent versions. A same-content duplicate registration succeeds idempotently; a different-content replacement conflicts. Digesting exact request content means whitespace/property-order changes count as different registered content. Clients should register and reuse the exact source JSON.

Instance records include their definition identity/version, current state, immutable typed facts encoded as JSON, revision and timestamps. Creating an instance reads its registered definition's initial state. State is never accepted directly from callers, and later definition registration does not change an existing instance's binding.

## Attempts, history and atomicity

Every well-formed event submission is evaluated against the persisted instance and its exact registered definition version. Each result is stored as an input-attempt row containing status, reason, explanation, guard findings, prior/target state and revision information. A separate transition-history row is created only when an event is accepted. Its unique attempt link and revision fields connect the result to the accepted state change.

Accepted state/revision update, attempt insertion and transition-history insertion run inside one SQLite transaction. Rejected, ambiguous and unresolved attempts only insert an attempt and leave the instance row unchanged. The history is operational evidence, not a complete event-sourced source of truth; rebuilding state by replay is not supported.

## Concurrency

Submissions to a single instance are serialized within one API process by an instance-keyed semaphore. State updates also use an optimistic conditional update on the revision observed during evaluation. The SQLite write transaction protects the accepted state, attempt and history commit together. This is intentionally a modest single-host strategy, not a distributed lock or an exactly-once guarantee. SQLite locking and revision conflicts may reject work under multi-host contention; clients should treat a conflict as requiring inspection rather than blindly asserting that no operation committed.

The design prevents two accepted changes based on the same observed revision from silently overwriting one another. It does not provide input deduplication, cross-system coordination, or a general retry policy.

## Disconnect and restart behaviour

The API persists a submission before returning the response. A client disconnect after commit does not undo the state transition or its records; clients can inspect attempts, instance state and accepted history later. Data survives API host restart when the same SQLite database is configured.

The default database is `durable-state-machine.db` in the process working directory. Set `DURABLE_STATE_MACHINE_DB` to use another SQLite path. The API creates the schema on startup with EF Core `EnsureCreated`; this gate does not introduce a schema migration tool.

## Trade-offs and limitations

The synchronous API deliberately returns workflow rejection as `200 OK` with a stable attempt result rather than conflating workflow semantics with HTTP transport errors. Database conflicts and invalid resources remain HTTP errors. Facts are exposed and immutable after instance creation, preserving Gate 2 semantics.

This is not yet a fully durable workflow engine. Timers, external effects, outbox/inbox, exactly-once semantics, deduplication, replay, fact updates, migration, authentication/authorization, distributed coordination, and hierarchical or parallel states are not implemented. Pending work is explicitly unsupported rather than invented.
