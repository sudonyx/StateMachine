# Gate 14 — Completion Architecture and Requirements Plan

## Status and scope

This is the implementation design for the remaining AST-BR-001 / AST-POL-001
release lifecycle. It is grounded in the existing Milestone 5A audit, Gate 14
Milestones 3B–4C, the language 1.3 runtime, and the published service
contracts. It does not claim that the capabilities below are implemented or
live-verified. Existing V1, V2, and V3 definitions and their instances remain
unchanged and pinned to their registered versions.

Primary sources:

- [AST-BR-001](../../training-data/asteria/requirements/asteria-release-requirements-v1.md)
- [AST-POL-001](../../training-data/asteria/policies/asteria-release-policy-v1.md)
- [Milestone 5A architecture audit](./gate14-milestone5a-architecture.md)
- [Milestone 4B design](./gate14-milestone4b-design.md), [4C integration](./gate14-milestone4c-integration.md), and [4C adversarial results](./gate14-milestone4c-adversarial-results.md)
- [State Machine language 1.3](../state-machine-language-v1.md)
- [State Machine API](../gate-3-api.md), [Generic Harness OpenAPI](../../reference/harness.openapi.yaml), and [Harness supported scope](../../reference/harness-supported-scope.md)
- [Asteria Decision API OpenAPI](../../training-data/asteria/contracts/asteria-decision-api.openapi.yaml), [binding](../../training-data/asteria/contracts/asteria-decision-binding.md), and [recovery](../../training-data/asteria/contracts/asteria-decision-recovery.md)
- [Deployment platform OpenAPI](../../training-data/asteria/contracts/asteria-deployment-platform.openapi.yaml) and [health verification OpenAPI](../../training-data/asteria/contracts/asteria-health-verification.openapi.yaml)

The State Machine repository contains the Asteria registry/coordinators and
workflow API, and the workspace also contains the independently hosted
Decision API and Generic Harness implementations. The Decision API contract
and implementation are separate; an available checkout or running process
must not be treated as proof that a live integration used current code.

## 1. Business-requirement traceability

| Source requirement | Design response | Enforcing owner |
|---|---|---|
| AST-BR-001 REQ-005–009; AST-POL-001 POL-01, POL-08–12 | Preserve versioned evaluation, exact material binding, policy outcome, selected policy source versions and original evidence; prohibition and unresolved results block. | Decision API owns evaluation. Asteria assessment coordinator validates response binding and stores the original response/evidence. State Machine stores accepted workflow observations. |
| AST-BR-001 REQ-010–014; AST-POL-001 POL-02–07 | Four separate obligations; applicability is distinct from activity completion. Required checks need accepted Passed; required approvals need authenticated, authorised Approved. Readiness is an accepted guarded workflow transition. | Decision API determines applicability; Asteria coordinator/gateway validates evidence; State Machine guards readiness against all four concern projections. |
| AST-BR-001 REQ-015–018; AST-POL-001 POL-10 | Material change creates a new generation, digest, workflow and independent work identities. No evidence is copied to the new generation. | Asteria registry and amendment coordinator; terminal supersession in the State Machine. |
| AST-BR-001 REQ-019–023 | Dispatch only from the current ready generation; deployment success requires exact provider result evidence. Uncertain mutating operations remain unknown and are not resent. | Asteria deployment coordinator and registry; Generic Harness records execution identity/attempts; deployment provider owns its response and by-key lookup contract. |
| AST-BR-001 REQ-024–026 | Health is a separate operation bound to the exact confirmed deployment; only Passed health permits Completed. | Asteria coordinator validates the health contract; State Machine guards completion. |
| AST-BR-001 REQ-027–030 | Pre-dispatch cancellation can close eligible work. Post-dispatch cancellation is a durable intent; external work and later evidence remain separately represented and never imply cancellation or completion. | State Machine owns lifecycle/cancellation history; registry and external service ledgers retain operation evidence. |
| AST-BR-001 REQ-031–035; AST-POL-001 POL-11 | UTC durable deadlines, versioned assessment history, duplicate/conflict refusal, replay and export preserve historical meaning. | State Machine timer store for transition-relative timers; Asteria ledger for planned-time deadline; registries and replay own stored evidence consistency. |

AST-BR-001 and AST-POL-001 do not define detailed test plans or pass
thresholds, provider attestation, a human identity system, a policy for
retesting/re-approval, or whether one principal may satisfy both approval
authorities. These are not inferred by this design.

## 2. Existing capabilities to reuse unchanged

- V1–V3 remain immutable; V3 already models assessment, cancellation intent,
  request-only concerns, and terminal supersession.
- The Asteria registry already stores immutable material/generation snapshots,
  current pointer, assessment requests/responses, definition identity, work
  identity, and amendment/supersession recovery state.
- The assessment coordinator already validates stable Decision API response
  binding, preserves original response/evidence, and checks current-generation
  admission around external work.
- Language 1.3 already provides parent and concern projections with separate
  revisions/histories, concern-state guards, typed input mappings, expected
  operations, receipts, and atomic parent cancellation.
- State Machine persistence already provides immutable definition digests,
  expected-operation resolution, input receipts, attempt/history evidence,
  historical replay, and durable explicit timer processing.
- Generic Harness already imports OpenAPI JSON, selects provider/description/
  operation bindings, executes with idempotency identity, and provides
  execution/attempt/inspection/evidence routes. Its response body is available
  only from the invocation response; durable inspection retains sanitized
  metadata rather than the original body.
- The Decision API contract, deployment contract, and health contract are
  authoritative for their respective request/response shapes. They must not be
  extended to fit an implementation convenience.

No State Machine runtime branch, Generic Harness execution behavior, Decision
API implementation behavior, or historical definition is changed to satisfy
Asteria-specific integration.

## 3. Component ownership and trust boundaries

| Component | Owns and enforces | Does not establish |
|---|---|---|
| Decision API | Policy applicability, prohibition/unresolved outcome, evaluation identity, policy version/evidence. | Verification completion, human identity, deployment, health, or workflow readiness. |
| Asteria registry/coordinators | Canonical material and digest; current generation admission; Asteria work IDs; request/result binding; orchestration and recovery status. | Remote execution truth, provider identity cryptography, State Machine transition acceptance, or a distributed transaction. |
| State Machine | Immutable workflow definition binding; parent/concern states and histories; expected operations, accepted/refused receipts, revisions, timers and replay. | Asteria pointer/generation semantics, policy truth, provider result truth, approver identity or roles. |
| Generic Harness | Generic provider/description/operation execution identity, idempotency handling, attempts, inspection and available sanitized evidence. | Asteria authority, provider business outcome, human approval, or durable original response body after invocation. |
| Synthetic verification/deployment/health providers | Independently hosted synthetic service operation and returned response under supplied contract. | Production assurance or identity of the caller of record beyond configured synthetic setup. |
| Synthetic approval gateway | Test credential authentication, synthetic principal/role lookup, immutable decision record and validated observation submission. | Production-grade identity assurance or protection from a caller that directly holds the generic State Machine trusted key. |

The State Machine trusted-ingestion key is held only by the Asteria
coordinator/gateway. Providers and approvers never receive it. A direct caller
with that key can bypass Asteria-specific checks because the generic API does
not consult the Asteria registry. Network isolation and credential protection
are required training-grade controls, not claims of generic API enforcement.

## 4. New immutable Asteria definition versions

Create `asteria-release-governance@4.0` as a new language 1.3 definition.
V4 adds typed observation mappings and concern transitions for applicability
and verification/approval results, retaining V3 parent lifecycle and terminal
supersession semantics. It introduces explicit readiness only when all four
concerns are either policy-backed `NotRequired` or have the required accepted
result. No V3 instance is migrated or relabeled.

Add a further immutable `asteria-release-governance@5.0` only for deployment,
health, investigation and completion transitions if keeping 4.0 focused on
obligation fulfilment is necessary. The new-release coordinator must select
one version explicitly and persist it. The intended capstone path uses the
latest fully implemented version; historical instances remain pinned.

Each concern keeps separate applicability and outcome states. Proposed
applicability states are `ApplicabilityPending`, `Required`,
`NotRequired`, and `ApplicabilityUnresolved`. Required concerns progress to
`Requested` and then one immutable outcome: verification `Passed`, `Failed`,
or `Indeterminate`; approval `Approved`, `Rejected`, or `Unresolved`.
`NotRequested` is never treated as `NotRequired`. No retry, retest, appeal or
re-approval transition is introduced without a business rule.

Concern result observations carry explicit mapping names and fixed event
targets for each concern/result. The runtime validates mapping/type/required
evidence; the Asteria coordinator validates semantic fields and exact binding.
There is no caller-submittable command that directly asserts applicability,
readiness, deployment success, health success, or completion.

## 5. Policy-obligation applicability and completion semantics

The Decision API response for the current accepted assessment is the only
source for applicability. The coordinator derives the four obligation values
from the response, validates them against release ID, generation and material
digest, and persists the exact original response before sending separate
typed applicability observations. A caller cannot supply `NotRequired`.

Applicability and completion are independent dimensions:

- `Required`: request work; pending/missing evidence is not satisfaction.
- `NotRequired`: explicit accepted policy-derived state; never fabricate
  Passed/Approved.
- `Unresolved`: explicit blocked state; never coerce to false or absent.
- Required verification satisfies only on validated, accepted `Passed`.
- Required approval satisfies only on an immutable, authenticated and
  role-authorised `Approved`.
- Failed, Rejected, Indeterminate, Unresolved, Requested, pending, missing,
  refused, conflicting or unknown evidence blocks readiness.

The parent readiness transition requires policy assessment accepted for the
current generation, a non-prohibited resolved assessment, no terminal
cancel/supersession, and a conjunction over all four explicit concern states.
The generic guard enforces concern-state conjunction. The Asteria coordinator
additionally checks stored current pointer, digest, assessment response,
work identities, receipts and evidence references before registering and
submitting the readiness observation.

## 6. Verification provider contracts and execution

Functional and security providers are separate services with distinct
provider IDs, OpenAPI versions, operation keys, request/result schemas and
configured Harness bindings. Their synthetic test plans and thresholds are
explicitly training fixtures, because AST-BR-001 and AST-POL-001 do not supply
them. Provider result bodies must include and validate release ID, generation,
material digest, workflow, concern/activity identity, provider operation
identity, result enum and evidence reference. HTTP 2xx and Harness `Succeeded`
are transport/execution evidence only, not a verification pass.

For each required activity the coordinator:

1. Rechecks current-generation admission and accepted `Required` applicability.
2. Commits canonical activity/work, stable attempt, expected-operation,
   Harness idempotency and request-fingerprint identities before dispatch.
3. Registers the concern-specific expected operation at the current parent
   revision and records the concern revision separately.
4. Invokes the configured provider through `POST /executions` with the actual
   provider ID, API-description version, operation key and stable idempotency
   key. The provider does not receive State Machine credentials.
5. Persists any known Harness execution ID and the original usable response
   body/digest in Asteria storage before acknowledging result processing.
6. Validates configured provider/description/operation binding, exact
   generation/material/work binding, response schema and business result.
7. Maps a valid result to Passed, Failed or Indeterminate, submits the
   concern-specific trusted observation, then inspects receipt, operation,
   concern history and projection before acknowledging Asteria work as accepted.

If dispatch may have occurred but no execution ID or recoverable response was
captured, persist `OutcomeUnknown`, keep the original identities, do not claim
Passed/Failed, and do not retry under a new ID. The Harness contract has no
guaranteed lookup-by-idempotency-key route for a lost execution acknowledgement.
When an execution ID is known, use supported Harness execution inspection;
do not reconstruct provider body fields from sanitized evidence.

## 7. Synthetic human authority and approval contracts

The Asteria-specific gateway accepts credentials only through its
authenticated request context. It derives principal identity from a
test-only credential registry, then resolves the current authority-registry
version and required role. Body-supplied actor, name, role, or authority claims
are ignored or rejected, never trusted. Provision distinct synthetic
ReleaseReviewer and EmergencyReleaseAuthoriser principals for the success
scenario. Credentials are generated out of band and never committed or placed
in reports.

An approval decision binds release, generation, digest, workflow, concern,
expected operation, submission/idempotency identity, principal, authority
registry version, decision, server timestamp, and gateway validation evidence.
Approved, Rejected and explicit Unresolved are distinct immutable outcomes.
An exact retry is idempotent; changed payload under one submission ID,
cross-role replay, wrong generation/concern, missing/invalid credential,
unauthorised role, superseded release, or terminal overwrite is refused and
retained for inspection.

AST-POL-001 requires distinct authorities but does not state whether one
person holding both roles may satisfy both. The success demonstration uses
separate principals. Dual-role submissions are refused or left unsupported
until an applicable Asteria authority policy resolves the question. Synthetic
credentials demonstrate gateway behavior only, not production IAM.

## 8. Generation-specific activity and evidence identity

Each activity ledger row is bound to: release ID; assessment generation;
canonical material digest; workflow instance; definition ID/version/content
digest; concern/activity type; stable work/activity/attempt IDs; stable
expected-operation ID; parent revision at registration; separately recorded
concern revision; original request, fingerprint, submission and idempotency
identities; validated original response/evidence and digest; provider/Harness
or principal/authority identities; timestamps; and receipt, attempt, history
and current recovery references.

Unique constraints apply to stable activity identity and canonical result per
logical activity attempt. Foreign keys bind work to the generation snapshot.
Same-result retries return/reconcile the existing record; a changed result
under the same identity is stored as conflict evidence and never overwrites an
accepted result. New-generation activities always receive new identities.

Parent revision is not a concern revision. Independent concern transitions do
not increment parent revision, so several concern operations may validly bind
the same parent revision. Asteria enforces one canonical result operation per
activity attempt and records each concern revision independently; it does not
use last-write-wins.

## 9. Durable orchestration and persistence schema

Extend (do not replace) the Asteria SQLite schema with generation-linked
activity, external-execution, approval, timer/escalation, and conflict records.
Every write uses parameterized SQL and explicit transactions; foreign keys and
uniqueness constraints are enabled on every connection. Registry initialization
must migrate existing data additively without rewriting release, generation,
amendment, assessment, or supersession records.

Activity states distinguish `Reserved`, `OperationRegistered`,
`DispatchPending`, `Dispatched`, `ResponsePersisted`, `ObservationSubmitted`,
`Accepted`, `OutcomeUnknown`, `Refused`, and `Conflicting`. Approval work uses
the same immutable identity protocol but records authenticated principal,
granted role, registry version, decision and gateway evidence. External work
adds configured provider, description version, operation key, Harness
execution/attempt IDs, original response and provider-side operation IDs.
Evidence export reads persisted originals and live State Machine projections
without converting missing data into success.

## 10. Parent/concern coordination and readiness

Language 1.3 concern guards are the declarative enforcement for readiness:
each of the four concerns must be in its explicit `NotRequired` or
requirement-specific satisfactory state. All non-satisfactory states fail the
guard. Readiness is a durable parent transition, not a Decision API response
or a registry assertion. The readiness input is an observation registered
and submitted only after Asteria's independent current-generation evidence
checks; it is not an ordinary public command.

The State Machine is authoritative for whether the transition and all
concern histories were accepted. The registry/gateway is authoritative for
current-pointer, generation/material binding, policy response provenance,
provider semantic validation, or human authority. Neither replaces the
other. Direct generic API access with the shared key remains a documented
boundary risk.

## 11. Deployment through Generic Harness

Deployment is a mutating operation. Persist deployment intent, exact request,
fingerprint, provider binding, Harness idempotency identity and workflow/
generation binding before dispatch. Dispatch only after guarded Ready state
and a fresh current-generation check. Invoke the deployment provider using
the imported OpenAPI operation and stable idempotency key. The provider's
202/200 response is admission/replay evidence, not success; validate every
returned field against release, generation, digest, product, version,
environment and key, then reconcile status through the contract's
`GET /deployments/{deploymentId}` or read-only
`GET /deployments/by-idempotency-key/{idempotencyKey}` lookup as available.

Only a bound `Succeeded` result is success; `Failed` is failure; Pending,
Running, transport error, timeout, lost response, unavailable lookup or
contradictory status remain unresolved/unknown. The supplied contract's
by-key lookup is a read-only reconciliation operation, not permission to
resubmit deployment. Do not retry a possibly admitted mutating request or
infer status from Harness HTTP success. Persist duplicate/conflicting/late
results and lost acknowledgements; accepted State Machine observations are
reconciled by exact identities and inspected receipts.

No unsupported provider endpoint is assumed. The actual deployment and
health OpenAPI descriptions must be registered with Generic Harness; adapter
workarounds or Asteria branches in generic runtime are prohibited.

## 12. Post-deployment health verification

Health verification is a distinct, read-only provider operation and is
scheduled only after accepted deployment success. It binds deployment ID and
operation, release/generation/digest, workflow, target version/environment,
health work/operation identity, Harness execution and original response.
Validate HTTP result body independently: `Passed`, `Failed`, and
`Indeterminate` are business values; mismatch of deployment ID, observed
version, or environment invalidates the response. HTTP 200 is not health
success.

Only confirmed deployment plus accepted Passed health can reach terminal
Completed. Failed, missing, unknown or mismatched health blocks completion.
Cancellation intent, supersession, conflicting authority evidence or other
disqualifying state also blocks completion. Health execution is not merged
with deployment execution and is not inferred from deployment status.

## 13. Deadline and cancellation behaviour

The verification escalation due instant is the planned deployment instant
minus 48 hours. It is conditional on mandatory verification still being
incomplete at processing time and records an escalation, not a waiver or
automatic decision. Since current language timers schedule a fixed duration
from an accepted transition and cannot calculate from a future material fact,
persist this exact UTC due instant in an Asteria deadline ledger. Provide an
explicit due-processing operation; there is no always-on scheduler guarantee.

Deployment investigation is due 30 minutes after the accepted deployment
start/dispatch transition. This is a fixed transition-relative duration and
can use the supported durable State Machine timer. Processing is explicit via
the documented timer-due processor unless a separately verified scheduler is
configured. A due event records investigation if no authoritative deployment
result has been accepted; it does not establish failure or prove intervention.
Duplicate and late timer deliveries reconcile against durable timer identity
and status. UTC instants are persisted once and never recomputed on restart.

Before any deployment dispatch could have begun, cancellation may transition
the workflow and active concerns to terminal Cancelled under definition
semantics; every future dispatch rechecks current state and pointer. After
dispatch may have begun, cancellation is a nonterminal intent. Preserve
deployment identity, unknown status, receipts and later provider evidence;
do not imply provider cancellation, rollback, or cancellation success. A late
success cannot satisfy Completed after cancellation intent. Business policy
does not define final disposition of cancellation plus confirmed remote
outcome; keep it unresolved rather than inventing one.

## 14. External uncertainty and crash recovery

There is no cross-service transaction. Persist intent and stable IDs before
dispatch, then reconcile by the authoritative owner:

| Crash window | Recovery |
|---|---|
| Intent persisted, no dispatch recorded | Recheck dispatch evidence/admission; only dispatch with original stable identity if durable records establish it was not already sent. |
| Provider may have run; Harness ID absent | Persist OutcomeUnknown, retain identities, do not issue a fresh mutating operation. For verification, inspect is unavailable by unknown ID; surface manual reconciliation. For deployment, use the contract's read-only idempotency-key lookup through Harness if available. |
| Harness ID known, response not durably archived | Inspect supported execution/attempt/evidence. Use only a response body actually available from the invocation; metadata cannot recreate it. |
| Original response archived, observation not sent | Validate stored bytes and binding; submit the same stable envelope. |
| Observation accepted, Asteria acknowledgement lost | Inspect receipt, resolved operation, accepted concern/parent history and projection; resend only the exact stable envelope if reconciliation requires it. |
| Amendment/supersession races with external work | Current-generation checks before dispatch/result/acknowledgement; old operation remains old-generation history. Terminal supersession refuses late workflow advancement. No lease is treated as a fencing token. |
| Conflicting concurrent result | Preserve both submissions and refuse conflicting identity/result; never replace the first accepted result. |

State Machine replay verifies local consistency relative to stored definitions
and evidence; it does not prove external truth or human identity.

## 15. Concurrent activity and revision arbitration

Expected operations bind the parent revision. Independent concern-only
transitions leave that revision unchanged and have their own revisions and
histories. Thus independent concern operations may share the same parent
revision. Same-concern competing completion is arbitrated by unique durable
activity/attempt and expected-operation identity in Asteria plus State Machine
input serialization. First accepted result remains immutable; identical
retries are duplicates and a conflicting result is refused. No concurrent
result is selected by timestamp or last-write-wins.

Readiness and deployment admission serialize via registry transactions and
current-pointer checks. A local lease coordinates only cooperating processes
sharing the same registry. In-flight external requests cannot be recalled.
After a race, reconcile workflow receipt/history, registry activity, current
generation and remote operation before acknowledgement.

## 16. Historical replay and final evidence export

Export the immutable release/generation snapshots, material digests/current
pointer, Decision API evaluation and policy evidence, each obligation's
applicability and result, original provider responses where captured, Harness
execution/attempt/evidence references, immutable approval decisions and
authority-registry version, expected operations, receipts, attempts, parent
and concern histories/replays, deployment/health linkage, timer/cancellation
records, unresolved/conflicting outcomes and test/live verification class.

Keep `assessmentBinding` (write-once parent revision/state at operation
registration) separate from `currentStateMachineProjection` (live state,
revision and facts at export time), as required by Milestone 4C. Historical
replay is read-only and never invokes an external provider, approval system or
Decision API. Missing instances/results remain explicitly missing/unknown.

## 17. Test and live-integration strategy

Controlled fixtures may provide deterministic Decision API, Harness,
provider, authority and failure responses; reports label each such result
controlled, never live. Use isolated temporary SQLite files and the actual
State Machine API host for concern histories, receipts, replay, timer and
process-restart tests. Run concurrency tests with independent API processes
only where supported. Preserve prior Asteria and Orion/Gate 13 regressions.

Required coverage follows AST-BR-001 and this design: policy applicability,
prohibition/unresolved, amendments and stale generation; verification
pass/fail/indeterminate/malformed/duplicate/conflicting/unknown; approvals
correct/wrong role, spoof, replay, rejection and unresolved; every readiness
blocker and NotRequired case; deployment success/failure/unknown/cancellation
race; health healthy/unhealthy/unknown and wrong-deployment binding; exact
deadline and timer restart/duplicate processing; historical replay/export.
Assert persisted bindings, identities, receipts, concern histories, authority
and provider evidence, not only HTTP status.

Live integration is claimed only when current service builds/process versions,
configuration, isolated databases, provider registration and original
evidence are verified. If any independent service is unavailable, report
controlled integration results and exact startup/configuration instructions;
do not substitute fixtures for live results.

## 18. Explicit limitations and unresolved business policy

- Synthetic user credentials and role registry are training controls, not
  production identity assurance.
- Shared State Machine trusted ingestion can bypass Asteria gateway checks if
  exposed; network/credential isolation remains required.
- Provider identity/configuration labels are not cryptographic attestation.
- The supplied requirements do not define verification plans/thresholds,
  business retest/re-approval policy, provider assurance hierarchy, or a
  final post-cancellation disposition.
- AST-POL-001 leaves dual-role approval by one person unresolved; the positive
  demonstration uses two distinct principals and no universal two-person
  rule is inferred.
- Harness does not guarantee lookup by idempotency key after a lost execution
  ID and does not durably retain the full original response body.
- SQLite leases do not fence dispatched external calls or provide a
  cross-service atomic transaction.
- The State Machine timer processor requires explicit due processing unless
  an independently configured scheduler is demonstrated.
- An unknown external operation remains blocked for manual reconciliation;
  no ambiguous non-idempotent operation is automatically retried.
- Policy and service history is only replayable relative to preserved
  evidence; neither local digests nor replay prove external evidence truth.

## Implementation order and acceptance gates

1. This architecture and requirements plan, reviewed against actual source.
2. New immutable definition(s) and additive Asteria activity persistence.
3. Verification providers/configuration and Harness-backed coordinator with
   controlled response and crash-window tests.
4. Synthetic approval gateway and authority registry with separate
   principals and replay/adversarial tests.
5. Applicability observations, four-concern readiness guard and exhaustive
   combination tests.
6. Deployment and health orchestration with contract-valid provider results,
   no ambiguous resend, and exact operation correlation.
7. Deadline/cancellation records and restart-safe processing.
8. Evidence export, demonstration setup, regression suite and honest
   controlled/live verification report.

An acceptance gate is not passed merely because a definition registers or an
HTTP request succeeds. The exact persisted authority and evidence chain must
be inspected at each stage.
