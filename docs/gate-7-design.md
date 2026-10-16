# Gate 7 design — Generic Harness execution boundary

## Scope

Gate 7 routes Orion's three synthetic external effects through the independently running Generic Harness. The state-machine runtime remains domain-independent: it binds declared capabilities to provider descriptions and operation keys through configuration, and it does not contain provider IDs, harness IDs or Orion-specific transition branches in its generic evaluator.

Gate 5 remains authoritative for expected-operation correlation, trusted observation acceptance and deterministic workflow transitions. The Generic Harness remains authoritative for provider invocation and its own execution, attempt, evidence and replay-safety records. A provider result or successful harness execution does not itself advance workflow state.

The implementation is intentionally inline and bounded to three configured capabilities:

| Capability | Provider HTTP operation | Gate 5 observation |
|---|---|---|
| `offer-document` | `PUT /offers/{operationId}` | `OfferDocumentGenerated` |
| `equipment-reservation` | `POST /reservations` | `EquipmentReserved` |
| `customer-notification` | `POST /notifications` | `CustomerNotified` |

The independently runnable provider host implements these HTTP operations over the existing synthetic provider store. It is a demonstration provider, not a production integration.

## Invocation and trust sequence

1. The API validates the capability and request fields, resolves its exact configured harness binding, and derives a stable idempotency key from the workflow instance ID and operation ID.
2. One local transaction stores the effect intent and its Gate 5 expected operation, including the chosen provider identity, provider ID, API-description version, operation key, credential binding (when configured), request digest and expected workflow revision.
3. A durable local attempt is initialized before `POST /executions` is sent with `maxAttempts: 1`. The harness controls provider dispatch and its own attempt records; the adapter has no provider retry loop.
4. The adapter persists the returned execution ID and integer-valued execution/replay enums, then reads the harness inspection, attempts, evidence and non-invoking replay-safety evaluation.
5. Only a validated successful provider response is translated into the definition-declared Gate 5 observation, with the original operation correlation, provider identity and required evidence.
6. The adapter records the Gate 5 receipt and accepted transition details separately from provider and harness outcomes.

The caller cannot supply the selected provider, harness execution ID, target state, trusted flag or observation event. The configured trusted-ingestion key remains a local API boundary, not proof of provider identity or response authenticity.

## Durable state and result distinctions

The workflow SQLite store preserves the chosen harness provider ID, API-description version ID, imported operation key, stable idempotency key, execution ID and execution state, operation replay safety, idempotency/replay decisions, outcome confidence, harness attempt/evidence identifiers, validated provider result/evidence when received, and Gate 5 receipt/transition outcome. Intent and expected operation are committed before any HTTP call; the Gate 5 evaluator itself is unchanged.

Do not conflate these independent results:

- Harness execution `201 Created` means a harness execution record was created; it does not mean a provider business operation succeeded.
- Harness execution state is an integer in the documented contract: `0 Requested`, `1 Running`, `2 Succeeded`, `3 Failed`, `4 Unknown`, `5 Rejected`.
- A succeeded harness execution only supplies an observation when inspection reports validated outcome confidence and the invocation response contains the provider response body.
- Provider result (`Succeeded`, `Failed` or `Refused`), outcome confidence, accepted/refused Gate 5 receipt and workflow transition are preserved separately.
- Gate 5 may refuse a known provider result as stale or otherwise ineligible. The provider result is not undone and the workflow is not moved to an unapproved state.

The external API schema defines integer replay/idempotency enums: operation replay safety is `0 Idempotent`, `1 NonIdempotent`, `2 Unknown`; idempotency decision is `0 SafeToRetry`, `1 UnsafeToRetry`, `2 Unknown`; replay decision is `0 Allowed`, `1 Blocked`, `2 Unknown`. These values are retained as integers, not silently mapped to locally invented labels. Current live imports classify the offer `PUT` as replay-safety value `1` (NonIdempotent), so the adapter does not infer idempotency from the HTTP verb or synthetic provider behavior. With the configured one-attempt budget, it makes no retry regardless of classification. `POST /executions/{id}/replay` evaluates historical safety only; its response confirms that replay did not invoke the provider.

## Recovery and duplicate behavior

- An intent committed before any local attempt exists can be resumed with its stored request and snapshotted binding. Resume never re-resolves the binding from current configuration.
- Once a local attempt exists, resume does not submit another execution. When an execution ID is known, it refreshes inspection, attempts, evidence and replay-safety evidence from the harness.
- If the first `POST /executions` response is lost before its execution ID is persisted, the provider may have run, but the workflow cannot correlate to the harness record. The published harness contract has no lookup-by-idempotency-key route. The workflow therefore retains `OutcomeUnknown`, exposes `correlationAvailable: false`, and will not re-invoke or fabricate an execution ID.
- Repeating the same workflow effect request returns the existing operation. Reusing the same operation ID with different request data or a different configured binding is rejected as a conflict.
- A provider response body is not reconstructed from inspection: the harness intentionally does not persist full response bodies. Without the actual invocation response, the workflow will not fabricate business evidence or submit a Gate 5 observation.

The stable idempotency key is a SHA-256-derived value over instance ID and operation ID. It provides a consistent request identity, not a guarantee that the external provider deduplicates. Harness replay policy and imported operation semantics remain authoritative.

## API boundary

- Primary start, resume and inspection routes: `POST /api/instances/{id}/effects`, `POST /api/instances/{id}/effects/{operationId}/resume`, `GET /api/instances/{id}/effects`, `GET /api/instances/{id}/effects/{operationId}`.
- Harness details: `GET /api/instances/{id}/effects/{operationId}/inspection`.
- Pending intents: `GET /api/instances/{id}/pending-work`. No automatic worker dispatches them.
- Gate 6's in-process direct provider route is retained as `/api/instances/{id}/direct-effects` only in Development with `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`. It is excluded from ordinary production configuration and is not used by the Orion client.

## Provider host and operational requirements

Run one `DurableStateMachine.ProviderHost` process per capability. Its generated JSON OpenAPI document advertises the configured absolute provider URL; the Generic Harness must be able to reach that URL. Each capability has a distinct operation and independent provider data file. The URL in the OpenAPI `servers` array must be reachable from the harness host/container, not merely from the workflow API process.

The API uses `Harness:BaseUrl` (default `http://localhost:5247`) and exact `Harness:Capabilities:{capability}` settings: `ProviderIdentity`, `ProviderId`, `ApiDescriptionVersionId`, `OperationKey`, plus optional `CredentialBindingId`. The provider ID, description version ID and optional binding ID must be GUIDs. Each imported operation key must match a callable operation from that exact API-description version.

## Limitations and trade-offs

- One harness attempt is requested. There is no workflow retry loop, outbox worker, automatic retry, scheduler or compensation.
- There is no atomic transaction across workflow SQLite, harness persistence and provider persistence, and no distributed exactly-once guarantee.
- The Generic Harness only exposes the response body to the original invocation caller. If that response is lost, execution inspection may establish execution metadata but cannot reconstruct required provider business evidence.
- The published harness schema describes `providerResponse.body` as a JSON string, while the reachable live harness returned a parsed JSON object. The adapter accepts either representation and parses the contained provider JSON; other body shapes are treated as unavailable and never reconstructed from durable evidence.
- There is no lookup by idempotency key after a lost initial response, so uncertainty is deliberately preserved even when the harness may have recorded an execution.
- The synthetic host has no production authentication or provider attestation. API-key bindings are supported by the documented harness contract; current bearer-token and OAuth execution are not suitable for provider calls.
- Provider hosts expose only the three fixed synthetic operations. Arbitrary provider request schemas, production secrets, persistent credential vaults and arbitrary workflows are outside this gate.
- `EnsureGate7SchemaAsync` adds the new SQLite columns for this implementation; a general schema migration/versioning system is not introduced.
- Failure tests use deterministic injected interruption points and a test-host restart where noted. They are not evidence that an operating-system process was terminated.

The local provider setup, API binding examples, Orion run and controlled failure demonstrations are documented in [gate-7-integration.md](./gate-7-integration.md). Gate 6's historical direct-provider semantics are described in [gate-6-design.md](./gate-6-design.md).
