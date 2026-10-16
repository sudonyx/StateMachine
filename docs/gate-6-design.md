# Gate 6 design — external effects and truthful outcomes

## Scope

Gate 6 adds three Orion-only synthetic capabilities outside Core's transition evaluator:

- offer-document generation;
- equipment reservation for a limited fictional asset;
- customer notification delivery.

The generic evaluator remains deterministic and side-effect free. Orion maps each provider observation to a declared event in the immutable machine definition. The shipped definition is now machine version `3.0`, language `1.1`; previously registered versions, including Orion `2.0`, remain immutable and instances stay bound to their registered JSON.

The coordinator is a small API application service, not a scheduler or generic activity engine. Its sequence is:

1. Validate the requested capability and synthetic inputs.
2. Commit a workflow-side effect intent and Gate 5 expected operation in one SQLite transaction.
3. Commit an attempt-initialized record locally.
4. Invoke the synthetic provider, which commits independently.
5. Persist the returned provider outcome locally.
6. Submit successful provider evidence through Gate 5's controlled observation processing.
7. Record whether Gate 5 accepted or refused the observation.

The API does not accept a target state from the effect caller. Only Gate 5 and the existing evaluator can accept the mapped workflow event.

## Persistence boundaries

`DURABLE_STATE_MACHINE_DB` contains instances, definitions, expected operations, effect intents, effect attempts, input receipts and accepted transition history. An intent and its expected operation are inserted in one local transaction. The expected operation uses the same stable `operationId` as its effect intent, binds the current definition ID/version and execution revision, and obtains its observation type, event mapping and required evidence from that immutable definition.

Each provider owns an independent SQLite database under `DURABLE_STATE_MACHINE_PROVIDER_DB_DIR` (default `synthetic-provider-data`):

| Capability | Provider identity | File | Provider semantics |
|---|---|---|---|
| `offer-document` | `synthetic-offer-provider` | `offer-document.db` | Idempotent by operation ID; repeat returns the original document ID/evidence. |
| `equipment-reservation` | `synthetic-reservation-provider` | `equipment-reservation.db` | Asset uniqueness prevents two active reservations. A repeat for the same operation and asset resolves to the existing reservation; a different operation targeting an occupied asset is refused and identifies the existing reservation. |
| `customer-notification` | `synthetic-notification-provider` | `customer-notification.db` | Deliberately non-idempotent. Each provider request creates a new delivery ID, including repeated operation IDs. An uncertain notification is not automatically resent. |

Provider request records and provider effects are persisted together in their provider's database. There is no shared EF transaction, shared SQLite file or distributed transaction across workflow and provider stores. Provider inspection reports what the provider recorded; workflow inspection reports what the API durably observed. The provider SQLite stores are synthetic local boundaries, not independently deployed production services.

The operation ID exposed to Gate 5 remains scoped to the workflow instance. The provider request also receives a stable instance-qualified identity (`{instanceId}:{operationId}`) so two instances cannot accidentally collide on a provider-wide idempotency key.

## Lifecycle and evidence

The effect status progresses through concepts including `Pending`, `AttemptInitiated`, `OutcomeUnknown`, `ProviderSucceeded`, `ProviderFailed`, `ProviderRefused`, `ObservationAccepted` and `ObservationRefused`. Each attempt separately records:

- start/completion timestamps and attempt identity;
- transport result (`AwaitingResponse` or `ResponseReceived`);
- provider result (`Unknown`, `Succeeded`, `Failed` or `Refused`);
- provider effect ID and provider evidence, when locally persisted;
- local persistence outcome;
- Gate 5 observation result and receipt identity.

An inspection response projects an attempt that remains `AwaitingResponse` with provider outcome `Unknown` as operation status `OutcomeUnknown`. That is a conservative interpretation of absent local evidence, not proof that the provider failed or did nothing. A known provider success does not imply an accepted observation, and an accepted observation is the only route from provider evidence to an accepted state transition.

Provider evidence includes synthetic identifiers and required evidence fields. The workflow stores the exact effect request JSON and digest locally. A digest is useful for correlation; it is not proof of provider execution or authenticity. The provider occurrence timestamp and observation identity are preserved across explicit recovery. If Gate 5 committed an accepted observation but the coordinator did not persist its acknowledgement, replaying that same observation yields a duplicate receipt; the coordinator links that duplicate to the original accepted evaluator attempt rather than evaluating a second transition. The trusted-ingestion key is a controlled single-host training boundary, not provider attestation or production authentication.

## Why either call order is insufficient

Saving intent before calling the provider prevents losing the workflow's intention, but a crash after the local commit and before the provider call leaves pending work that still needs recovery. Calling the provider first avoids a durable pending item but creates the converse window: the provider can commit an effect while the workflow has no durable intent or result. Saving the response afterwards still leaves a window where the effect succeeded but the response or local persistence did not.

The Gate 6 implementation makes these windows visible. It does not eliminate them. The provider's own evidence, operation identity and idempotency contract determine whether a repeated call can safely recover the result.

## Retry and duplicate rules

`POST /api/instances/{id}/effects/{operationId}/resume` is explicit; there is no background retry. It retains the original operation identity and request. Offer generation can be called again with that same identity because the provider guarantees idempotency. Reservation retries use the same asset and operation identity; the provider resolves its existing reservation or refuses a conflicting reservation. An uncertain notification retry returns `409 Conflict` because that provider does not deduplicate delivery.

Calling the notification provider twice with the same logical identity can create two distinct delivery records. Duplicate transport attempts are therefore not assumed to be duplicate-free. The provider semantics and separate request/effect records make that risk inspectable.

## Late observations and authority

Successful provider evidence is submitted internally through the same `MachineStore.SubmitInputAsync` processing used by the trusted Gate 5 application boundary. The coordinator binds operation ID, source identity, observation mapping, expected revision and provider-required evidence. A caller cannot provide `trusted: true` or choose an event/target state. If cancellation or another transition makes the result no longer eligible, Gate 5 retains a `Stale` receipt and the effect attempt is marked `ObservationRefused`; the provider-side result remains unchanged.

## API and fault controls

- `POST /api/instances/{id}/effects` starts one of the three declared synthetic capabilities.
- `POST /api/instances/{id}/effects/{operationId}/resume` explicitly resumes a specific operation.
- `GET /api/instances/{id}/effects` and `GET /api/instances/{id}/effects/{operationId}` inspect workflow-side evidence.
- `GET /api/providers/{providerIdentity}/requests?operationId=...` inspects independent provider-side request records.
- `GET /api/instances/{id}/pending-work` lists unresolved Gate 6 effects; timers remain unsupported.

Start, resume and expected-operation registration require `DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY` in `X-State-Machine-Trusted-Ingestion-Key`. Deterministic one-shot fault controls are only mapped when the API runs in Development and `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`; they are not available from normal production configuration. Supported points include before intent creation, after local commit, after provider-boundary dispatch, after provider commit/before response, after response/before local persistence, before observation submission, after Gate 5 observation commit/before coordinator acknowledgement persistence, and a provider-confirmed synthetic failure.

## Limitations and trade-offs

- The coordinator runs inline in the API request. There is no scheduler, durable worker, automatic retry, outbox dispatcher or long-running execution harness.
- The synthetic provider call is an in-process service boundary backed by a separate SQLite file, not a real network protocol. Fault hooks model transport and persistence boundaries deterministically.
- Crash tests use controlled fault injection. A representative test restarts a new API host over the same database, but no test kills an operating-system process.
- The shared trusted key is not multi-tenant authorization, authentication of human actors, provider signatures or external evidence verification.
- Per-instance concurrency remains governed by Gate 5's single-process serialization model; there is no cross-process lock or distributed exactly-once guarantee.
- Recovery is explicit and provider-specific. Historical evidence is not rewritten to imply a response that the workflow never persisted.
- Timers, compensation, automatic retries, replay/reconstruction, execution-harness integration and Gate 7 are not implemented.

The Gate 6 crash outcomes and their tests are detailed in [gate-6-crash-matrix.md](./gate-6-crash-matrix.md).
