# Gate 13 design — compose decision, harness, and workflow boundaries

## Scope

Gate 13 composes the existing durable state-machine runtime, an independently
hosted layered Decision API, and the existing Generic Harness. Orion is the
demonstration domain, not a new runtime specialization. The only new workflow
semantics are declared in immutable Orion 6.1 (language 1.3); the runtime does
not contain Orion-specific states, event branches, decision rules, or provider
execution code. This gate does not implement the unseen-domain capstone or any
Gate 14 capability.

## Responsibility boundaries

| Responsibility | Owner | Durable evidence / interface |
|---|---|---|
| Permitted lifecycle states, guards, and transitions | State Machine definition | Registered `orion-equipment-lease` 6.1 definition and digest |
| Current workflow/concern state and accepted history | State Machine | SQLite projection, attempts, inbound receipts, expected operations, accepted transitions, replay |
| Orion eligibility decision and policy effectivity | Decision API | Existing generic layered rule evaluator, isolated Orion policy versions, fact/evaluation evidence, historical decision store |
| External provider description, execution, retry attempts, and provider evidence | Generic Harness | Existing provider/API-description/execution/evidence/inspection routes |
| Correlation and acceptance of decision/effect results | State Machine | Trusted observation with exact expected operation, source, event, revision, and required evidence |
| End-to-end demonstration/export | PowerShell runner | `scripts/gate13-demo.ps1` evidence-chain JSON |

No transaction spans the services. A service response is not itself a
workflow transition: the State Machine accepts a transition only after its
definition, current state, revision, guard, observation authority, expected
operation, and evidence checks succeed.

## HTTP composition

The live runner expects:

| Service | Default address | Gate 13 responsibility |
|---|---|---|
| Durable State Machine API | `http://localhost:5001` | Workflow state, decision/effect correlation, transition evidence, replay |
| Orion Decision API | `http://localhost:5268` | Versioned Orion layered policy evaluation and historical replay |
| Generic Harness | `http://localhost:5247` | Capability execution, remote attempts/evidence, inspection |
| Synthetic provider hosts | `http://localhost:5261`–`5263` | Offer document, equipment reservation, customer notification |

Start and bind the provider descriptions as documented in
[gate-7-integration.md](./gate-7-integration.md). Configure the State Machine
with the exact Harness provider ID, API-description version ID, operation key,
provider identity, and local trusted-ingestion key for each capability. Run the
Decision API independently with `ASPNETCORE_URLS` set to its chosen address and
`ORION_DECISION_HISTORY_ROOT` pointed at its own durable history directory.

The Decision API uses `training-data/orion-policy/` for Orion policy material.
Its Orion catalog is explicitly namespaced:

- `GET /orion-equipment-leases/policy-sources`
- `GET /orion-equipment-leases/policy-sources/{sourceId}/versions`
- `POST /orion-equipment-leases/layered`
- `POST /orion-equipment-leases/historical-decisions/{evaluationId}/replay`

Meridian's existing `/policy-sources` catalog and `training-data/policy-store/`
remain unchanged and cannot discover Orion policy versions. The Orion adapter
calls the existing temporal selector, layered evaluator, composition engine,
evidence builder, and historical store; it does not fork rule semantics.

The Decision API does not expose a generated `/openapi/v1.json`. The demo
validates its namespaced policy catalog and decision route directly; the route
and request/response contract is also described in the Decision API README.

## Request and evidence flow

1. The client creates an Orion 6.1 instance and submits `SubmitApplication`.
   The state machine accepts the declared transition from `Draft` to
   `UnderAssessment`.
2. The demonstration submits a trusted, correlated document-verification
   observation. The document concern must be complete before an assessment
   result can change the parent lifecycle.
3. The client registers an `EligibilityAssessmentResult` expected operation
   bound to the current workflow revision and the source identity
   `Orion Layered Decision API`.
4. The client calls the Decision API with explicit `businessEffectiveAt` and
   `knownAt`, an `Idempotency-Key` equal to the expected operation identity,
   and a request binding containing application/instance IDs, definition ID
   and version, workflow revision, assessment generation, and expected
   operation ID.
5. The Decision API evaluates only the supplied Orion facts and its selected
   immutable Orion policy version. It returns the workflow binding, outcome
   state/outcome, input fact-set digest, selected policy identities, missing /
   unknown / unavailable facts, and complete decision evidence. The Decision
   API does not call the State Machine or Harness.
6. The client records the original decision response in the trusted
   `EligibilityAssessmentResult` observation. The state machine binds the
   expected operation and required evidence to the accepted attempt. For an
   eligible decision, the declared guard and completed document concern permit
   progression; unresolved and referral outcomes do not fabricate approval.
7. Asset reservation, offer-document generation, and customer notification
   run through the State Machine's existing Harness adapter. The adapter
   persists its local effect correlation, calls the Generic Harness, inspects
   the remote execution/evidence, and submits the resulting trusted observation.
   The definition coordinates document and asset concerns before offer release.
8. The runner retrieves workflow history, attempts, receipts, expected
   operations, concern state, and Harness inspection. It replays the workflow
   locally and calls Decision API historical replay by evaluation ID. It
   exports the correlated evidence without replaying provider side effects.

## Uncertainty and failure behavior

- A Decision API outage does not produce an assessment observation. The
  expected operation remains `Expected`, allowing a later correlated response.
- A decision with missing, unknown, or unavailable required facts has an
  explicit non-present outcome state. It cannot be mapped to `Eligible`.
- A result bound to a stale revision or cancelled instance is refused by the
  State Machine and retained as a refusal/attempt; it does not reopen state.
- An unsupported Harness operation is rejected by the Harness and cannot be
  treated as a successful observation.
- `OutcomeUnknown`, a lost response, or missing remote Harness evidence remains
  unresolved. The adapter preserves a known local execution reference and
  reports remote-evidence unavailability; it does not advance the workflow or
  submit a second mutating execution.
- A duplicate decision/input or Harness result is deduplicated against the
  operation/message identity and cannot apply the transition twice.
- Cancellation while an external call is pending terminates the workflow
  dimensions atomically. A later result remains evidence but is refused as a
  stale transition; cancellation does not claim remote compensation.
- Replay is read-only. It verifies stored state/history/decision evidence and
  exact policy-version identities without calling providers.

The Gate 13 failure-test mapping distinguishes controlled fault-injection
coverage from the live happy-path run in
[gate-13-test-results.md](./gate-13-test-results.md).

## Limitations and trade-offs

- The live scenario uses synthetic facts, local services, and synthetic
  providers. It is not a production underwriting or leasing decision.
- The trusted-ingestion key and local HTTP topology are training controls, not
  production authentication, authorization, provider identity, or secret
  management.
- There is no distributed transaction across the three services. A response
  lost before local persistence is unresolved; callers must use the documented
  idempotency/correlation behavior rather than infer success or failure.
- The Decision API has no generated OpenAPI document for the Orion adapter.
- The decision adapter is Orion-specific at the API boundary, but delegates
  policy parsing, temporal selection, rule evaluation, composition, and
  evidence generation to the existing generic Decision API components.
- The demonstration runner uses a directly supplied synthetic
  `DocumentsVerified` observation; it does not introduce another provider
  capability for document verification.
- Production databases, distributed locks, automatic retries, provider
  compensation, and automatic workflow migration remain outside this gate.
