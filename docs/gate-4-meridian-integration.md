# Gate 4 Meridian integration

## Configuration and prerequisites

The standalone client uses:

- `MERIDIAN_DECISION_API_URL` — decision API base URL; defaults to `http://localhost:5267/`.
- `DURABLE_STATE_MACHINE_API_URL` — state-machine API base URL; defaults to `http://localhost:5000/`.
- `--definition PATH` — optional machine-definition path; defaults to `definitions/meridian-supplier-qualification-v1.json`.
- `--business-effective-at YYYY-MM-DD` — explicit effective date; defaults to today's UTC date.
- `--known-at ISO-8601` — explicit knowledge cutoff; defaults to the current UTC timestamp.
- `--decision-url URL` and `--workflow-url URL` — per-run URL overrides.
- `--assessment-file PATH` — saved assessment envelope; defaults to `meridian-assessment.json`.
- `--assessment-id ID` and `--idempotency-key KEY` — set the identity for a new assessment.
- `--verify-conflict` — send a deliberately changed request with the saved key and verify the API rejects it.
- `--decision-only` — verify the decision API without submitting a workflow event.

The Meridian routes and request/response types are specified by [`../references/gate4-decision-api.openapi.yaml`](../references/gate4-decision-api.openapi.yaml). Start the Meridian API using its repository's documented launch instructions and confirm that the configured base URL serves that contract. The integration client does not start or emulate Meridian.

Start the state-machine API in a separate PowerShell terminal:

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:DURABLE_STATE_MACHINE_DB = ".\durable-state-machine.db"
dotnet run --project src\DurableStateMachine.Api
```

Then run the live integration demonstration from the repository root:

```powershell
$env:MERIDIAN_DECISION_API_URL = "http://localhost:5267"
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5000"
dotnet run --project clients\DurableStateMachine.MeridianClient
```

Before contacting Meridian, the client creates a local envelope containing the operation ID, idempotency key and exact request JSON. By default it reuses `meridian-assessment.json` on later invocations; retain that file to retry an uncertain call. A new assessment requires a new `--assessment-file` path and therefore a new operation identity/key. The client also re-sends the same key and request after a completed response and verifies that Meridian returns the original evaluation ID and response.

The supplied contract accepts an optional `Idempotency-Key` of 1-128 ASCII characters from `[A-Za-z0-9._~-]`. For keyed calls, `businessEffectiveAt` and `knownAt` must be explicit. A same-key/same-request repeat returns the original evaluation; a different request under that key is a conflict. An HTTP 409 with `Retry-After` is reported as in progress and does not submit a workflow event. A timeout, lost response, malformed success response or server error is an uncertain outcome: invoke the client again with the same saved envelope rather than creating a new key. A conflict without `Retry-After` is surfaced as a key/request conflict, not treated as success.

For example, verify the service's changed-payload conflict behavior without creating a workflow instance:

```powershell
dotnet run --project clients\DurableStateMachine.MeridianClient -- --assessment-file .\meridian-assessment.json --verify-conflict --decision-only
```

Do not change the saved request, temporal inputs or key to resolve an uncertain result. To intentionally reassess, use a new envelope path and a new operation/key. The envelope is local retry input, not a general activity journal or distributed guarantee; preserve it on a persistent filesystem and do not treat a missing/corrupt file as proof that Meridian did not process the request.

## Mapping and persisted evidence

The adapter reads `evidence.evaluationId`, `evaluatedAt`, `factSet.digest`, `finalDecision`, `businessEffectiveAt`, `knownAt`, `unknownFacts` and `unavailableFacts`, plus top-level `selectedPolicySourceVersionIds`. It also inspects fact snapshot states to preserve missing/unknown/unavailable fact names. The complete original response is retained without reducing it to those indexed values.

The adapter maps `finalDecision` to the per-attempt fact `decisionOutcome` and submits the complete generic decision-evidence object alongside `DecisionAvailable`. `Proceed`, `Review` and `Blocked` are values in the machine definition; they are not special cases in Core, Persistence or the HTTP event handler. A null `finalDecision` is sent as an explicit null, producing an unresolved comparison rather than a successful or failed business decision.

The recorded provenance text describes the caller's claim and explicitly does not prove authenticity. The state-machine API computes its own stable digest for the stored complete response and a separate digest for the serialized evaluation evidence. Meridian's `factSet.digest` and selected policy-version IDs remain intact; the local digest algorithm does not claim to reproduce Meridian's canonical fact-set digest.

The demo checks the attempt response, retrieves persisted attempts and accepted transition history, and verifies the stored evaluation ID, input fact-set digest, policy-version IDs and original response. It then explicitly calls `POST /historical-decisions/{evaluationId}/replay`, checks that the response refers to the same evaluation and reports `matchesStoredOutcome` and differences. This verification call is not made by ordinary guard evaluation or state-machine replay.

## Tests and live-verification distinction

`MeridianAdapterTests` uses controlled HTTP responses. It verifies key validation and transmission, explicit temporal inputs, exact saved request reuse, repeat response identity, conflict and in-progress handling, uncertain transport/server/malformed-response outcomes, explicit null handling and envelope reload behavior. These are **Meridian contract tests**, not evidence that a live Meridian service was reached.

The console integration above is the **live end-to-end demonstration**. Its output is meaningful only when both APIs are available and the decision API retains the new evaluation for historical replay. A connection failure, non-success status, malformed response or failed historical lookup is reported as an error; it is not reported as a successful live verification.

## Retry, crash-window and troubleshooting limitations

There is no atomic transaction spanning Meridian and the local SQLite database. Meridian's HTTP idempotency key supports deliberate same-key/same-payload retries for that API operation only, and the local envelope preserves the retry identity across client-process restarts. It does not deduplicate state-machine events, coordinate different client filesystems or replicas, or provide automatic retries, a durable external scheduler, or reconciliation. A lost or malformed response therefore remains uncertain until the caller retries with the same saved envelope or reconciles through the decision service.

If the decision API is unavailable, verify its URL and health/OpenAPI endpoint, then rerun the client only after deciding how to reconcile any request whose response may have been lost. If the workflow API is unavailable after a decision was received, retain that response and evaluation ID for inspection; do not assume a new layered evaluation is equivalent. Historical replay also fails when the decision API no longer has the evaluation in its local store.
