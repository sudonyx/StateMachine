# Gate 4 Meridian integration

## Configuration and prerequisites

The standalone client uses:

- `MERIDIAN_DECISION_API_URL` — decision API base URL; defaults to `http://localhost:5267/`.
- `DURABLE_STATE_MACHINE_API_URL` — state-machine API base URL; defaults to `http://localhost:5000/`.
- `--definition PATH` — optional machine-definition path; defaults to `definitions/meridian-supplier-qualification-v1.json`.
- `--business-effective-at YYYY-MM-DD` — explicit effective date; defaults to today's UTC date.
- `--known-at ISO-8601` — explicit knowledge cutoff; defaults to the current UTC timestamp.
- `--decision-url URL` and `--workflow-url URL` — per-run URL overrides.

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

The command makes a real `POST /supplier-qualifications/layered` request. It supplies `businessEffectiveAt` and `knownAt` explicitly. It does not retry the request because each successful call creates a new evaluation ID and the contract offers no idempotency key.

## Mapping and persisted evidence

The adapter reads `evidence.evaluationId`, `evaluatedAt`, `factSet.digest`, `finalDecision`, `businessEffectiveAt`, `knownAt`, `unknownFacts` and `unavailableFacts`, plus top-level `selectedPolicySourceVersionIds`. It also inspects fact snapshot states to preserve missing/unknown/unavailable fact names. The complete original response is retained without reducing it to those indexed values.

The adapter maps `finalDecision` to the per-attempt fact `decisionOutcome` and submits the complete generic decision-evidence object alongside `DecisionAvailable`. `Proceed`, `Review` and `Blocked` are values in the machine definition; they are not special cases in Core, Persistence or the HTTP event handler. A null `finalDecision` is sent as an explicit null, producing an unresolved comparison rather than a successful or failed business decision.

The recorded provenance text describes the caller's claim and explicitly does not prove authenticity. The state-machine API computes its own stable digest for the stored complete response and a separate digest for the serialized evaluation evidence. Meridian's `factSet.digest` and selected policy-version IDs remain intact; the local digest algorithm does not claim to reproduce Meridian's canonical fact-set digest.

The demo checks the attempt response, retrieves persisted attempts and accepted transition history, and verifies the stored evaluation ID, input fact-set digest, policy-version IDs and original response. It then explicitly calls `POST /historical-decisions/{evaluationId}/replay`, checks that the response refers to the same evaluation and reports `matchesStoredOutcome` and differences. This verification call is not made by ordinary guard evaluation or state-machine replay.

## Tests and live-verification distinction

`MeridianAdapterTests` uses controlled HTTP responses. It verifies contract field parsing, outcome mapping, explicit null handling, temporal request fields, response preservation and that a failed/non-idempotent request is not retried. These are **Meridian contract tests**, not evidence that a live Meridian service was reached.

The console integration above is the **live end-to-end demonstration**. Its output is meaningful only when both APIs are available and the decision API retains the new evaluation for historical replay. A connection failure, non-success status, malformed response or failed historical lookup is reported as an error; it is not reported as a successful live verification.

## Retry, crash-window and troubleshooting limitations

There is no atomic transaction spanning Meridian and the local SQLite database. Meridian may persist a decision before the client receives the HTTP response. A lost response leaves the caller uncertain; repeating the layered request may create a second evaluation. Gate 4 intentionally supplies no automatic retry, idempotency mechanism, durable external scheduler or reconciliation workflow.

If the decision API is unavailable, verify its URL and health/OpenAPI endpoint, then rerun the client only after deciding how to reconcile any request whose response may have been lost. If the workflow API is unavailable after a decision was received, retain that response and evaluation ID for inspection; do not assume a new layered evaluation is equivalent. Historical replay also fails when the decision API no longer has the evaluation in its local store.
