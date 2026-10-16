# Gate 3 HTTP API

The service exposes the generic state-machine runtime over JSON/HTTP. It does not expose a state-update endpoint: state changes are accepted only through an evaluator result, either from a legacy event submission or from an eligible command/observation mapped to an event. The OpenAPI document is available at `GET /openapi/v1.json`; `GET /health` is a process health check.

## Base URL and serialization

Routes are rooted at `/api`. JSON property names are camel case and evaluation/status enums are strings. Definition IDs, versions, instance IDs and event names are case-sensitive. API v1 is implicit in the `/api` prefix and the machine-definition language version is carried by each definition; a future incompatible HTTP contract should use a new route prefix.

## Definitions

### Register an immutable version

`POST /api/definitions` accepts a complete language-version 1.0 or 1.1 definition as the request body. Language 1.1 may opt into declared command and observation mappings; see [state-machine-language-v1.md](./state-machine-language-v1.md).

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
  "events": ["Approve"],
  "transitions": [
    { "id": "approve", "source": "Draft", "event": "Approve", "target": "Approved" }
  ]
}
```

The response includes definition metadata and `alreadyRegistered`. Re-registering the same exact JSON content for the same identity is idempotent. Different content for an already registered `(id, version)` returns `409 Conflict`. Invalid definitions return `422 Unprocessable Entity`, with validation `issues` containing `code`, `path` and `message`.

### Inspect definitions

- `GET /api/definitions` lists definition IDs, versions, language versions, SHA-256 digests and registration times, sorted by ID and version.
- `GET /api/definitions/{id}/{version}` returns the registered definition JSON and parsed summary metadata, including its initial state, states, events and transition count.

Definition versions are exact, immutable identities. Register a new version rather than replacing an existing one.

## Instances

### Create

`POST /api/instances` creates an instance in the registered definition's initial state:

```json
{
  "id": "review-123",
  "definitionId": "document-review",
  "definitionVersion": "1",
  "facts": { "reviewer": "reviewer-1", "qualityScore": 85, "documentsComplete": true }
}
```

Fact values must be scalar strings, numbers, booleans or `null`. The response is `201 Created` and contains the instance's definition identity, state, revision, immutable facts and timestamps.

### Inspect

`GET /api/instances/{id}` returns the durable instance record. Unknown IDs return `404 Not Found`.

## Event submission

`POST /api/instances/{id}/events` accepts the original event-only shape:

```json
{ "event": "Submit" }
```

It also accepts optional per-evaluation facts, provenance labels for those facts, and a generic external-decision evidence snapshot:

```json
{
  "event": "DecisionAvailable",
  "facts": { "decisionOutcome": "Proceed" },
  "factsProvenance": {
    "decisionOutcome": "Mapped from a received decision response; not independently verified."
  },
  "decisionEvidence": {
    "decisionSystemIdentity": "Example deterministic decision system",
    "externalEvaluationId": "evaluation-123",
    "outcomeState": "Present",
    "outcome": "Proceed",
    "evaluatedAt": "2026-10-14T15:37:02Z",
    "businessEffectiveAt": "2026-10-14",
    "knowledgeCutoff": "2026-10-14T15:37:00Z",
    "inputFactSetDigest": "upstream-digest",
    "policyVersionIdentities": ["policy-version-1"],
    "provenance": "Caller supplied; authenticity is not verified.",
    "originalResponseJson": "{\"evidence\":{\"finalDecision\":\"Proceed\"}}",
    "missingFacts": [],
    "unknownFacts": [],
    "unavailableFacts": []
  }
}
```

`outcomeState` is `Present`, `ExplicitNull`, `Missing`, `Unknown` or `Unavailable`. `outcome` is a scalar fact value; an explicit null is represented by `ExplicitNull` and a JSON `null` outcome. Facts for a single attempt cannot shadow instance facts: any name collision returns `400 Bad Request`. Provenance labels are recorded claims, not authenticated evidence. Existing callers that submit only `event` continue to evaluate against instance facts.

The context is immutable for that evaluation: durable instance facts and attempt facts are resolved without mutation, and only the former remain on the instance. A missing or null comparison remains unresolved. Facts marked unknown or unavailable by attached evidence remain unresolved and retain their classification.

Valid workflow evaluations return `200 OK`, including refusals. This distinguishes a valid request that the workflow rejects from malformed HTTP input. The response envelope includes the persistent `attempt`, resulting `instance` snapshot and an accepted `transition` record (or `null` when none was accepted).

An attempt includes `status` (`Accepted`, `Rejected`, `Ambiguous`, or `Unresolved`), `reasonCode`, `explanation`, `guardFindings`, previous/target state and before/after revisions. Gate 4 also exposes `evaluationEvidence` and `evaluationEvidenceDigest`. The evidence snapshot records the exact definition version, instance revision, timestamp, instance/attempt/resolved fact values, per-fact provenance, decision evidence including the complete received response, guard findings, evaluator result, selected transition/refusal, and local commit status. The digest is SHA-256 over the serialized local evidence snapshot; it is not the external decision service's fact-set digest.

A rejected, ambiguous or unresolved attempt has no accepted transition history and leaves the instance state and revision unchanged, while retaining its evaluation evidence.

For example, an event refused in a terminal state is still a successful HTTP operation:

```json
{
  "attempt": {
    "status": "Rejected",
    "reasonCode": "terminal-state",
    "previousState": "Approved",
    "targetState": null,
    "revisionBefore": 2,
    "revisionAfter": 2
  },
  "instance": { "currentState": "Approved", "revision": 2 },
  "transition": null
}
```

An accepted evaluation includes the selected transition and the new durable snapshot:

```json
{
  "attempt": { "status": "Accepted", "reasonCode": "transition-enabled", "transitionId": "approve", "revisionBefore": 0, "revisionAfter": 1 },
  "instance": { "currentState": "Approved", "revision": 1 },
  "transition": { "transitionId": "approve", "previousState": "Draft", "newState": "Approved", "revisionBefore": 0, "revisionAfter": 1 }
}
```

Guard overlap and missing comparison facts produce results like:

```json
{
  "attempt": {
    "status": "Ambiguous",
    "reasonCode": "multiple-enabled-transitions",
    "guardFindings": [
      { "transitionId": "route-a", "status": "Enabled", "code": "guard-true" },
      { "transitionId": "route-b", "status": "Enabled", "code": "guard-true" }
    ],
    "revisionBefore": 0,
    "revisionAfter": 0
  },
  "instance": { "currentState": "Draft", "revision": 0 },
  "transition": null
}
```

An unresolved guard has `status: "Unresolved"` and a reason such as `unresolved-guard` or `unresolved-competing-guard`; its guard finding identifies the missing, null or incompatible fact. In all four evaluation outcomes, `guardFindings` provides detail when transitions had guards. A concurrent revision conflict returns `409 Conflict` and an inspectable attempt result.

For example, an absent comparison fact remains unresolved instead of being treated as a false guard:

```json
{
  "attempt": {
    "status": "Unresolved",
    "reasonCode": "unresolved-guard",
    "guardFindings": [
      { "transitionId": "approve", "status": "Unresolved", "code": "missing-fact", "explanation": "Fact 'qualityScore' is absent." }
    ],
    "revisionBefore": 0,
    "revisionAfter": 0
  },
  "instance": { "currentState": "Draft", "revision": 0 },
  "transition": null
}
```

## Commands and observations (language 1.1)

For a definition declaring `inputPolicy`, use the distinct input endpoints:

- `POST /api/instances/{id}/commands` submits an action request.
- `POST /api/instances/{id}/observations` submits a claim about an external occurrence.
- `POST /api/instances/{id}/expected-operations` registers a permitted external result.
- `GET /api/instances/{id}/expected-operations` inspects expected and resolved operations.
- `GET /api/instances/{id}/inbound-messages` and `GET /api/instances/{id}/inbound-messages/{receiptId}` inspect input receipts and their outcomes.

Command and observation bodies use an envelope such as:

```json
{
  "messageId": "command-or-delivery-identity",
  "type": "AcceptOffer",
  "expectedRevision": 3,
  "correlationId": "optional-command-correlation-or-required-operation-id",
  "source": "caller-claimed-source",
  "occurredAt": "2026-10-15T12:00:00Z",
  "payload": { "offerId": "synthetic-offer-1" },
  "facts": { "offerValidAtEvaluation": true },
  "evidence": {},
  "factsProvenance": {}
}
```

For commands, `type` must match a declared command mapping and `expectedRevision` must match the current instance revision. For observations, `correlationId` is the expected operation ID; the definition mapping, expected-operation record, source identity, required evidence, and instance revision must all match. The revision, definition identity, payload/evidence digests, mapped event, evaluator attempt and outcome are recorded. Receipt itself does not accept an event or transition.

Observations without a valid trusted-ingestion key are retained as `Untrusted` and cannot evaluate an event. The controlled training boundary uses `DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY`, supplied in the `X-State-Machine-Trusted-Ingestion-Key` header. The same configured boundary is required to register expected operations. This shared local demonstration credential is not production authentication or source attestation; do not expose it to ordinary clients. A claimed `source` value alone is never authority.

The API returns `200 OK` with an inspectable message result for well-formed but ineligible inputs. Outcomes include `Accepted`, evaluator outcomes such as `Rejected` and `Unresolved`, `Duplicate`, `IdentityConflict`, `Unexpected`, `Untrusted`, `Malformed`, `Stale`, and `Conflicting`. Invalid typed fields are returned as `400 Bad Request` and, where the instance and envelope can be parsed, retained as a `Malformed` receipt. JSON that cannot be parsed or bound to the request contract is rejected before a receipt can be persisted. A command identity (`messageId`) reused with different content is a conflict; same content is a duplicate. A second delivery ID for an already resolved operation is duplicate only when its canonical result digest matches. Conflicting results are preserved and refused. An accepted message includes an `attempt`; other inputs do not create evaluator attempts. Only an accepted evaluator result creates an accepted workflow event and transition history.

Definitions with an input policy reject `POST /api/instances/{id}/events` with `403 Forbidden`; the rejected legacy call is retained as an inbound receipt. Definitions without an input policy, including existing language 1.0 definitions and the Meridian integration workflow, retain the legacy event endpoint semantics. This is the explicit compatibility boundary; migrating a workflow to language 1.1 requires clients to use the declared input paths.

The generated OpenAPI document at `GET /openapi/v1.json` includes the command, observation, expected-operation, inbound-message, effect-coordination and provider-inspection routes.

## Gate 6 synthetic effects

Gate 6 adds a controlled application-service path for three Orion effects. `POST /api/instances/{id}/effects` requires the trusted-ingestion header and accepts a stable operation ID plus fictional request data:

```json
{
  "operationId": "offer-generation-123",
  "capability": "offer-document",
  "customerReference": "synthetic-customer-1",
  "offerReference": "synthetic-offer-1"
}
```

The other capabilities are `equipment-reservation` (requires `assetId`) and `customer-notification` (requires `customerReference` and `messageTemplate`). The endpoint persists the effect intent and corresponding Gate 5 expected operation together before invoking a provider. A successful provider response is stored locally and submitted to Gate 5; provider success alone does not accept a workflow event or transition. Synthetic provider IDs are selected by capability, not supplied by the caller.

Inspect workflow-side intent and attempts with:

- `GET /api/instances/{id}/effects`
- `GET /api/instances/{id}/effects/{operationId}`
- `GET /api/instances/{id}/pending-work` (unresolved Gate 6 effects; no timers)

Inspect provider-side request evidence separately with `GET /api/providers/{providerIdentity}/requests?operationId=...`. Provider files are independent SQLite databases selected by `DURABLE_STATE_MACHINE_PROVIDER_DB_DIR`. A provider record may show success while workflow-side status remains `OutcomeUnknown`.

`POST /api/instances/{id}/effects/{operationId}/resume` explicitly retries/reconciles one operation. Offer generation is idempotent by operation ID. Equipment reservations enforce unique assets and resolve a same-operation retry to the original reservation. Customer notifications are non-idempotent; an uncertain notification retry is refused with `409 Conflict` because it might send twice. No general retry worker is present.

Fault injection endpoints are only registered when the API runs in Development and `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`. They are test/demo controls, not normal production routes. See [gate-6-design.md](./gate-6-design.md) for the boundary contract and [gate-6-crash-matrix.md](./gate-6-crash-matrix.md) for all seven tested failure windows and the distinction between injected faults and process termination.

## Attempts and accepted-transition history

- `GET /api/instances/{id}/attempts` lists every recorded event attempt in received-time order.
- `GET /api/instances/{id}/attempts/{attemptId}` retrieves one attempt.
- `GET /api/instances/{id}/history` lists only accepted state transitions, ordered by resulting revision.
- `GET /api/instances/{id}/history/{historyId}` retrieves one accepted transition, including its attempt ID and definition version.

Attempts have stable GUID identifiers. History entries link back to their originating attempt. Rejected inputs are never represented as accepted state transitions.

## Pending work

`GET /api/instances/{id}/pending-work` reports unresolved Gate 6 effect operations. Timers and scheduled background work remain unsupported; pending effects are not automatically dispatched or retried.

## Errors

Application errors use `{ "code": "...", "message": "...", "issues": [...] }` where appropriate.

| HTTP status | Meaning |
| --- | --- |
| `400` | Malformed request, invalid scalar facts, missing required request values, unsupported evidence values, or fact-binding collision |
| `403` | Missing trusted-ingestion credential for an observation or effect-control path |
| `404` | Unknown definition, instance, attempt, history entry or effect operation |
| `409` | Immutable identity conflict, duplicate instance ID, concurrency conflict, or retry refused by a non-idempotent provider |
| `422` | Invalid or unsupported machine definition |
| `503` | A configured development-only Gate 6 interruption was injected at a named boundary |
| `500` | Unexpected server error; details are logged server-side and not returned |

Malformed JSON bodies are rejected by ASP.NET Core as `400 Bad Request`. Workflow-level rejections are not HTTP errors; inspect the response attempt status and reason.

## Clients

Both sample clients use only HTTP and have no Core or Persistence project references.

Set `DURABLE_STATE_MACHINE_API_URL` to configure the service URL (default `http://localhost:5000`). The CLI also accepts `--url <base-url>` and allows `create` facts inline or from `@facts.json`. The Orion demonstration accepts an optional path to a definition JSON file; by default it reads `definitions/orion-v1.json` from its working directory.

See the repository [README](../README.md) for run examples.
