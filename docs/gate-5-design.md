# Gate 5 design — requests, observations and accepted events

## Boundary and taxonomy

Gate 5 inserts durable input eligibility checks before the existing transition evaluator:

1. Receive and persist a command or observation envelope.
2. Check message identity, declared input mapping, expected revision and (for observations) operation/source/evidence authority.
3. Resolve an eligible input to the workflow event declared by its immutable definition.
4. Call the existing deterministic evaluator.
5. Commit the receipt, evaluation attempt, accepted state/revision, accepted-event history and operation resolution in one SQLite transaction.

The evaluator remains the only component that selects transitions. The input processor does not duplicate guard semantics and never accepts a caller-supplied target state.

- **Command:** a request that the workflow perform a declared action. It is an instruction, not proof of a resulting occurrence.
- **Observation:** a report that something happened outside the runtime. It is a claim and must match a declared observation, an expected operation and the configured trusted ingestion boundary.
- **Fact:** typed data available to one immutable evaluation context. Facts can affect guards, but a fact or provenance label cannot establish input authority.
- **Accepted workflow event:** the event mapped from an eligible command or observation and accepted by the generic evaluator. Receipt, input eligibility and accepted transition are recorded as separate stages.

Language 1.1 definitions declare `inputPolicy.commands` and `inputPolicy.observations`. Each mapping names a permitted input and the existing declared event to evaluate. Existing language 1.0 definitions retain their original semantics and are not implicitly reinterpreted. At Gate 5, Orion advanced to machine version 2.0 and language version 1.1. Gate 6 advances the current demonstration to version 3.0; registered instances remain bound to their stored definition JSON.

## Authority and controlled ingestion

Commands are accepted only through the command endpoint and only for a command name declared by the instance's immutable definition. Commands cannot directly assign state or revision. A command must state the instance revision it targets. The demonstration API does not authenticate command actors; caller-supplied actor/source labels are claims and must not be treated as production identity.

An observation additionally requires:

- A recognized observation mapping.
- A pre-registered expected operation for the same instance and operation ID.
- The expected observation type, source identity and instance revision.
- A live (nonterminal) instance at the expected revision.
- A valid application-controlled ingestion credential.
- Every evidence item required by the definition/expected-operation record.

The training API uses a shared secret configured with `DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY` and presented as `X-State-Machine-Trusted-Ingestion-Key`. Only this controlled boundary can seed expected operations or submit trusted observations. It is an application-controlled test adapter, not production authentication, provider cryptographic attestation, per-principal authorization, or safe multi-tenant IAM. When the key is not configured, observations remain untrusted and cannot progress an instance. A client-provided `trusted` Boolean is not part of the contract.

The trusted adapter currently validates that required evidence names are present and non-null; it does not verify provider signatures, business meaning, or a content digest against an external provider. Facts and provenance submitted with an input remain caller-bound evidence, not authenticated truth. The separate Gate 4 decision-evidence contract remains intact and available to the legacy Meridian demonstration path.

## Input identity, correlation and duplicates

- `deliveryId` identifies a transport attempt and is stored separately from the logical message fingerprint.
- `messageId` identifies the logical command/event input; the Gate 9 corpus `event_id` maps to this value.
- Optional `operationId` and `observationId` are stored as distinct command/business-operation and external-result metadata; neither automatically deduplicates a new logical command.
- `correlationId` identifies the expected external operation for an observation; it is not a delivery or observation ID.
- The expected operation records its instance, observation type, accepted source identity, expected instance revision, required evidence and resolution state.
- The instance revision identifies the current execution position. It is not the machine-definition version.

Every received command/observation receipt is stored with its delivery and logical identities, envelope, exact definition identity, observed revision, canonical payload digest, outcome and refusal explanation. Same `messageId` and same canonical content is recorded as a duplicate and does not evaluate again, even if it arrived under another delivery ID. Reusing a message ID with different content is an identity conflict. A second observation message for a resolved operation is duplicate only when its canonical result digest agrees; a differing result is a conflict. A new command message ID does not inherit the old command's idempotency: it is checked against the current revision and evaluator, which can reject an action no longer permitted.

The Gate 5 implementation originally relied on process-local serialization for these checks. Gate 9 adds a host-scoped local gate plus a SQLite immediate write transaction that takes the database writer reservation before reading the instance. A later request from another API host reads the committed revision and records a stale or duplicate outcome instead of evaluating from a competing snapshot. This remains SQLite-specific and does not provide database-independent distributed locking or exactly-once external execution; see [gate-9-design.md](./gate-9-design.md).

## Stale and late messages

An input with a revision different from the current revision is recorded as stale and is not evaluated. An expected operation is bound to the revision at which it was registered. Terminal state is authoritative: a late observation cannot reopen a cancelled, approved, or rejected instance, even if its claimed occurrence time predates cancellation. Its receipt and stale reason are retained for inspection. No attempt or accepted transition is fabricated for an ineligible message.

Definition ID/version remains the one bound to the instance at creation. Mapping is loaded from that stored definition; registration of another definition version does not reinterpret existing instances.

## Persistence and inspection

`InboundMessages` stores receipts, including refused/duplicate inputs, malformed typed envelopes and legacy-bypass attempts. `ExpectedOperations` stores expected results and their resolution identity. For an eligible input, the message receipt and regular Gate 4 evaluation attempt are linked. An accepted input, instance state/revision update, transition history and operation resolution commit in the same database transaction. Refused input never creates accepted transition history. JSON that cannot be parsed or bound to the HTTP request DTO is rejected by ASP.NET before the receipt handler can persist it.

Inspect with:

- `GET /api/instances/{id}/inbound-messages`
- `GET /api/instances/{id}/inbound-messages/{receiptId}`
- `GET /api/instances/{id}/expected-operations`
- Existing `attempts`, `history` and instance inspection routes

Input eligibility outcomes are distinct from evaluator outcomes. For example, `Untrusted`, `Unexpected`, `Malformed`, `Stale`, `Duplicate`, and `IdentityConflict` are input outcomes; `Rejected`, `Ambiguous`, and `Unresolved` describe a mapped event evaluated by the transition evaluator.

## API compatibility and legacy event restrictions

Definitions with an input policy reject direct `POST /api/instances/{id}/events` with `403 Forbidden`, preserve the attempted bypass as a receipt, and do not evaluate its event. Language 1.0 definitions without an input policy retain their existing endpoint behavior; this preserves Document Review and Meridian examples while allowing a definition to migrate deliberately to the new contract. The API OpenAPI output is generated at `GET /openapi/v1.json`; route shapes and examples are documented in [gate-3-api.md](./gate-3-api.md).

## Worked examples

### Successful command

An instance is at revision 0 in `Draft`. The client submits `StartWork`, message ID `cmd-1`, and expected revision 0. The definition maps the command to event `Request`. The evaluator accepts that event; the receipt is `Accepted`, an attempt and transition are stored, and the instance advances once. Repeating the same envelope yields `Duplicate` and no second evaluation.

### Invalid command

The same command is submitted under a new message ID after the instance has advanced to `Active`. Its expected revision is current, but the evaluator has no `Request` transition from `Active`. The receipt is `Rejected`, the attempt explains `no-transition`, and neither state nor revision changes. A stale revision is rejected before evaluation.

### Accepted external observation

Orion expects operation `offer-generation-123` at the current revision from `synthetic-offer-provider`. The trusted adapter submits `OfferDocumentGenerated`, includes the matching operation ID, source and required `documentId`/`offerDigest` evidence. The declared mapping resolves it to `IssueOffer`; only then does the generic evaluator decide whether the transition is enabled.

### Forged observation

An ordinary caller posts the same observation with `source: "synthetic-offer-provider"` but no trusted ingestion key. The message is durably recorded as `Untrusted`. The source label is only a claim; no evaluator attempt or state change occurs. An incorrect source under the trusted boundary is also refused.

### Duplicate result

After an operation result has been accepted, a second delivery ID with the same canonical operation result is recorded as `Duplicate`. A second payload for that operation is `Conflicting`. Neither causes another event evaluation or transition.

### Late result after cancellation

An expected operation is registered at revision 2. The application is cancelled and advances to terminal revision 3. When the provider later reports a result referring to revision 2, the runtime stores it as `Stale` with reason `terminal-instance`; the earlier `occurredAt` claim does not restore the workflow.

## Orion demonstration and limitations

At Gate 5, the Orion console client exercised command acceptance, an untrusted forged observation, a trusted expected `OfferDocumentGenerated` observation, cancellation while another operation was pending and late-result refusal. The Gate 6 client now runs the three synthetic effects as described in [gate-6-design.md](./gate-6-design.md). The document-review definition remains language 1.0. Meridian continues to use its Gate 4 direct-event/evidence integration and tests; it is not routed through Orion's policy or business logic.

At Gate 5, no external effect was executed or dispatched. Expected operations were seeded explicitly through the controlled demonstration endpoint; there was no scheduler, outbox/inbox, provider integration, automatic retries, timer worker, full replay, cross-service transaction, distributed locking, or production authentication. Gate 6 adds the bounded synthetic effect coordinator and independent providers without changing Gate 5's authority or evaluator semantics. A shared local credential establishes a bounded training boundary only. The runtime deliberately does not claim that caller-supplied evidence or timestamps are independently verified.
