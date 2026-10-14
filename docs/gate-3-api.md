# Gate 3 HTTP API

The service exposes the generic state-machine runtime over JSON/HTTP. It does not expose a state-update endpoint: all state changes are made by submitting events to the evaluator. The OpenAPI document is available at `GET /openapi/v1.json`; `GET /health` is a process health check.

## Base URL and serialization

Routes are rooted at `/api`. JSON property names are camel case and evaluation/status enums are strings. Definition IDs, versions, instance IDs and event names are case-sensitive. API v1 is implicit in the `/api` prefix and the machine-definition language version is carried by each definition; a future incompatible HTTP contract should use a new route prefix.

## Definitions

### Register an immutable version

`POST /api/definitions` accepts a complete language-version 1.0 definition as the request body:

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

`POST /api/instances/{id}/events` takes:

```json
{ "event": "Submit" }
```

Valid workflow evaluations return `200 OK`, including refusals. This distinguishes a valid request that the workflow rejects from malformed HTTP input. The response envelope includes the persistent `attempt`, resulting `instance` snapshot and an accepted `transition` record (or `null` when none was accepted).

An attempt includes `status` (`Accepted`, `Rejected`, `Ambiguous`, or `Unresolved`), `reasonCode`, `explanation`, `guardFindings`, previous/target state and before/after revisions. A rejected, ambiguous or unresolved attempt has no accepted transition history and leaves the instance state and revision unchanged.

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

## Attempts and accepted-transition history

- `GET /api/instances/{id}/attempts` lists every recorded event attempt in received-time order.
- `GET /api/instances/{id}/attempts/{attemptId}` retrieves one attempt.
- `GET /api/instances/{id}/history` lists only accepted state transitions, ordered by resulting revision.
- `GET /api/instances/{id}/history/{historyId}` retrieves one accepted transition, including its attempt ID and definition version.

Attempts have stable GUID identifiers. History entries link back to their originating attempt. Rejected inputs are never represented as accepted state transitions.

## Pending work

`GET /api/instances/{id}/pending-work` explicitly returns `supported: false` and an empty item list. The current definition language/runtime does not implement timers or external effects, so there is no pending work to schedule or inspect.

## Errors

Application errors use `{ "code": "...", "message": "...", "issues": [...] }` where appropriate.

| HTTP status | Meaning |
| --- | --- |
| `400` | Malformed request, invalid scalar facts, or missing required request values |
| `404` | Unknown definition, instance, attempt or history entry |
| `409` | Immutable definition identity conflict, duplicate instance ID, or concurrency conflict |
| `422` | Invalid or unsupported machine definition |
| `500` | Unexpected server error; details are logged server-side and not returned |

Malformed JSON bodies are rejected by ASP.NET Core as `400 Bad Request`. Workflow-level rejections are not HTTP errors; inspect the response attempt status and reason.

## Clients

Both sample clients use only HTTP and have no Core or Persistence project references.

Set `DURABLE_STATE_MACHINE_API_URL` to configure the service URL (default `http://localhost:5000`). The CLI also accepts `--url <base-url>` and allows `create` facts inline or from `@facts.json`. The Orion demonstration accepts an optional path to a definition JSON file; by default it reads `definitions/orion-v1.json` from its working directory.

See the repository [README](../README.md) for run examples.
