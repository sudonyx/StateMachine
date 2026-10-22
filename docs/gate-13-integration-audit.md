# Gate 13 integration audit

## Audit outcome

The composition preserves the three independently owned execution boundaries.
The State Machine owns workflow transitions and evidence acceptance; the
Decision API owns Orion policy evaluation and historical decision evidence;
the Generic Harness owns external capability invocation and provider evidence.
Orion 6.1 and service adapters configure the composition without adding
Orion-specific branches to the generic State Machine runtime or changing the
Generic Harness implementation.

## Findings and changes

| Area | Finding | Gate 13 resolution |
|---|---|---|
| Decision API capability | The existing service exposed Meridian supplier routes only; no Orion equipment-lease decision contract existed. | Added a thin Orion request/response adapter that uses the existing temporal policy selector, layered rule evaluator, composition, evidence builder, idempotency, and historical store. |
| Shared policy catalog | Placing Orion rules under Meridian's generic `training-data/policy-store/` caused supplier evaluations to load Orion facts and reject them as unknown. | Moved the pack to `training-data/orion-policy/`, injected a dedicated filesystem policy repository into the Orion adapter, and added namespaced Orion policy discovery routes. The generic Meridian catalog remains isolated. |
| Workflow binding | A decision value alone would not prove which application, definition version, revision, assessment generation, or expected operation it addressed. | The Orion request and response preserve the complete workflow binding; the runner registers a matching expected operation and submits the original response and policy/fact evidence in the correlated observation. |
| External execution | Provider-specific code in the workflow runtime would collapse the Harness boundary. | The Gate 13 runner uses the existing State Machine `/effects` adapter and Generic Harness for reservation, offer-document, and notification capabilities. No Generic Harness source changes were needed. |
| Remote uncertainty | A local effect reference could be lost from inspection when Harness remote evidence was unavailable. | The State Machine Harness adapter now retains the local execution reference and reports remote-evidence availability explicitly; it does not accept an unverified result or retry a mutating call. |
| Replay | A final state alone would not demonstrate the cross-service evidence chain. | The live runner checks State Machine replay and Decision API historical replay, then exports the original request/response, correlation, transitions, concerns, and Harness inspection evidence. |

## Responsibility audit

| Question | Answer |
|---|---|
| Where do API/provider retry semantics live? | Decision API keyed evaluation retries live in its idempotency/history store. Provider execution attempts and retry semantics live in the Generic Harness. The State Machine does not independently reissue a known Harness execution. |
| Where do layered business rules live? | In the Decision API's versioned Orion policy pack and existing generic layered evaluator. |
| Where do permitted workflow transitions live? | In the registered, immutable Orion 6.1 machine definition. |
| Where does current workflow state live? | In the State Machine's SQLite-backed instance and concern projections, with accepted transition/input/effect evidence. |
| What evidence links a decision to a transition? | Expected-operation identity/revision/source, decision evaluation ID, exact policy version identities, fact-set digest, outcome state, and preserved original Decision API response. |
| What evidence links an external effect to a transition? | Persisted effect operation ID and capability binding, Harness execution ID, provider result/evidence, expected-operation correlation, and accepted observation/attempt. |
| Which component can refuse work? | Each component refuses within its contract: the Decision API rejects invalid or conflicting requests/policy history; the Harness rejects unsupported descriptions or execution; the State Machine rejects undeclared, stale, untrusted, uncorrelated, unresolved, or guard-failing transitions. |

## Contract and configuration boundaries

The Decision API adds only namespaced Orion endpoints:

- `GET /orion-equipment-leases/policy-sources`
- `GET /orion-equipment-leases/policy-sources/{sourceId}/versions`
- `POST /orion-equipment-leases/layered`
- `POST /orion-equipment-leases/historical-decisions/{evaluationId}/replay`

The existing Meridian routes continue to use `training-data/policy-store/`.
Orion executable/source snapshots are under `training-data/orion-policy/`, and
Orion historical decisions are written to
`ORION_DECISION_HISTORY_ROOT` (default:
`training-data/orion-decision-history/`). The Orion route requires exactly one
`Idempotency-Key`; the same key with a changed canonical request conflicts.

Harness capabilities remain configuration-bound. For each capability, the
State Machine is given the exact provider identity, provider ID,
API-description version ID, and operation key. No provider IDs, URLs, operation
keys, or credentials are embedded in the State Machine runtime. The trusted
ingestion key remains a local training boundary.

The Decision API does not serve `/openapi/v1.json`; service discovery in the
demo uses its Orion-specific policy catalog route and live request. No
OpenAPI contract is inferred from the Meridian Gate 4 specification.

## Validation boundary

The end-to-end PowerShell runner verifies the successful three-service
composition over HTTP. The eight required failure scenarios are exercised by
deterministic tests across the State Machine and Decision API, using controlled
HTTP handlers, SQLite-backed test hosts, and process-restart/fault-injection
boundaries. Those tests establish refusal and recovery semantics but are not
represented as live outages of the independently hosted production-like
services. Exact test names and results are recorded in
[gate-13-test-results.md](./gate-13-test-results.md).
