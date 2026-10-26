# Gate 14 — Asteria Milestone 1 feasibility

## Executive result

Language 1.3 safely supports a versioned Asteria definition, separate finite
concerns, concern-state inspection and replay, command deduplication, and
declarative guards over the latest committed parent/concern snapshot. It does
not by itself supply Asteria policy evaluation, authenticated human decisions,
material-generation evidence binding, or trustworthy deployment/health
observations.

The definition therefore permits local submission/request intents and
pre-deployment cancellation only. It has no transition into deployment,
completion, or prohibition. This is a deliberate fail-closed boundary, not a
claim that Asteria is ready to execute releases.

The full AST-BR-001 file is present at
[asteria-release-requirements-v1.md](../../training-data/asteria/requirements/asteria-release-requirements-v1.md).
The policy supplement, all 14 synthetic cases, both OpenAPI contracts, and
input README were also read. No missing business requirements were
reconstructed.

## Asteria requirement classification

The labels below are the milestone classifications. “Supported” means the
generic runtime already provides a relevant mechanism; it does not imply that
the complete business requirement is satisfied.

### Supported and demonstrated

| AST-BR-001 requirements | Demonstrated subset and boundary |
|---|---|
| AST-REQ-002 | Draft creation and refusal to record submission when required comparisons are unresolved. The guard does not validate every format constraint. |
| AST-REQ-012 | Functional and security verification requests change separate concern projections. |
| AST-REQ-027 | A cancellation intent can be recorded and a pre-deployment parent cancellation is accepted only while execution remains `NotRequested`. |
| AST-REQ-034 | Accepted local concern transitions can be inspected and replayed without effects. |
| AST-REQ-035 | Asteria is a new immutable definition identity/version; existing definitions and instances are not reinterpreted. |

### Supported but not yet integrated

| AST-BR-001 requirements | Available mechanism / missing integration |
|---|---|
| AST-REQ-005–009 | The generic runtime can accept bound facts/evaluation evidence and guards can coordinate on states. No Asteria policy evaluator, source/version selection, policy evidence contract, risk-derived applicability, prohibition, or policy-to-concern binding is integrated. |
| AST-REQ-010–014 | Concern dimensions, independent revisions, parent/concern guards, and input evidence/correlation mechanisms exist. No Asteria verifier or approval service is integrated, so outcome states are unreachable. |
| AST-REQ-031–032 | Language 1.2/1.3 supports persisted parent timers. No Asteria escalation timer, deadline policy, operational investigation workflow, or timer test is in this milestone. |
| AST-REQ-033 | Generic message identity/deduplication and expected-operation checks exist. Asteria has no observation mappings/provider integration to test duplicate or conflicting external decisions/results. |

### Conditional on a stated trust or correlation mechanism

| AST-BR-001 requirements | Required condition |
|---|---|
| AST-REQ-001, AST-REQ-003 | An authenticated employee/owner identity and an authorization check must establish who created/submitted and that the owner explicitly confirmed accuracy. The local command endpoint does not establish identity. |
| AST-REQ-004 | Stable instance identity and repeated-message deduplication are available. Enforcing uniqueness of the `releaseId` fact across instances needs an authoritative allocator or persistence constraint outside this definition. |
| AST-REQ-009 | Decision evidence must come from a trusted policy evaluator and be correlated to release ID, definition/version, assessment generation, material digest, and policy identity. |
| AST-REQ-010–014 | Trusted verifier/reviewer identities must provide results bound to the applicable generation and exact requirement. A pending request is not approval; one approval concern cannot satisfy the other. |
| AST-REQ-019–023 | A deployment adapter must bind the exact release/product/version/environment and stable idempotency identity to the authorized operation. Admission, timeout, or missing lookup is not success or failure. |
| AST-REQ-024–026 | A health result must be trusted and correlated to the exact deployment ID, observed version, and environment. HTTP 200 alone is insufficient. |
| AST-REQ-028–030 | Cancellation intent and deployment status must remain separate. A trusted late result must bind to the original operation; it cannot reverse cancellation or establish external compensation. |
| AST-REQ-033 | Conflicting and duplicate observations need trusted source identity, stable message/operation IDs, and exact expected-operation correlation. |

### Unsupported in the current generic runtime

| AST-BR-001 requirements | Current limitation |
|---|---|
| AST-REQ-003 (full validity) | Guards can check vocabulary, numeric lower bounds, and non-empty string comparisons, but cannot validate RFC 3339 syntax, integer-only generation values, all domain formats, or employee authority. |
| AST-REQ-006–008 (Asteria policy result) | No Asteria executable policy evaluator is supplied or implemented. The machine cannot safely derive unresolved, requirements-determined, additional-review, or prohibited outcomes. |
| AST-REQ-015–018 | Instance facts are immutable after creation. There is no Asteria material-amendment transition or atomic generation reset, and no guard binding accepted verification/approval evidence to a new generation. |
| AST-REQ-021–026 | External deployment and health providers are explicitly out of scope. No execution, reconciliation, health-observation, or completion transition exists. |
| AST-REQ-028–030 (post-start outcomes) | No provider execution/status semantics or safe compensation mechanism exists. A cancellation concern cannot establish that deployment stopped, failed, or reversed. |
| AST-REQ-034 (external history) | Local transition replay is supported; truthful provider history, decision evidence, and recovery of remote execution are not established by local replay alone. |

## Explicit feasibility tests

| Question | Result | Evidence and conclusion |
|---|---|---|
| Can a parent guard require both approval concerns when applicable? | **Conditionally expressible; not integrated** | `and` can combine two `concernState` expressions. The definition keeps approval outcome states unreachable because policy applicability and trusted approval evidence are absent. A two-approval deployment-readiness route is intentionally absent. |
| Can reassessment prevent deployment during sequential concern resets? | **Not safely enforceable in this milestone** | Concern updates are independent and sequential. There is no atomic all-concern reset, generation-aware evidence binding, or executable readiness policy. Deployment remains unreachable, preventing an unsafe interim authorization. |
| Are old verification observations refused after material changes? | **No, not by generation** | The runtime binds expected operations to definition and parent revision, not Asteria assessment generation/material digest. Instance facts cannot be amended by this definition. Revision staleness is not a substitute for generation correlation. |
| Do cancellation-intent changes prevent subsequent deployment authorization? | **Guard mechanism supported; authorization path absent** | The pre-deployment cancellation guard reads the latest committed concern snapshot. The tests observe refusal before intent and acceptance after it is recorded. There is no deployment authorization transition against which to demonstrate the same rule. |
| Can deployment-result observations remain valid while a separate cancellation concern changes? | **Conditionally possible, not demonstrated as an Asteria result** | Concern-only updates do not change parent revision, while expected operations are revision-bound. Exact operation/source/evidence correlation and a trusted deployment adapter are still required. No deployment-result observation is mapped in this definition. |
| Can a required policy fact be supplied authoritatively without modifying instance facts? | **No Asteria authority mechanism** | Instance facts are set at creation. Per-attempt facts are not authoritative and are not merged into instance facts. A trusted, generation-bound policy decision/evidence integration is required; caller-supplied Booleans are not a substitute. |
| Can a parent transition avoid inadvertently staling an outstanding expected operation? | **Not generally** | Parent transitions advance the parent revision; expected operations are bound to a parent revision and an older result can be stale. Concern-only transitions do not advance that revision. The safe interim rule is to have no external expected operation during these local parent transitions; a full deployment protocol must define this explicitly. |

## Deliberate omissions and remaining work

1. Integrate the versioned policy evaluator and authoritative policy source
   identity; derive applicability and prohibited/unresolved outcomes without
   caller assertions.
2. Establish authenticated release-owner and reviewer identity, including the
   policy that governs a person holding multiple roles.
3. Define material amendment semantics and a trustworthy, monotonically bound
   generation/material digest. Refuse old verifier/approval evidence after
   amendment, including during sequential resets.
4. Add trusted, correlated verifier observations and explicit resolution
   semantics for pass, fail, indeterminate, and superseded results.
5. Add deployment execution only after readiness requirements are
   authoritatively determined. Bind idempotency key, exact request, operation
   identity, admission, status lookup, uncertainty, and late results.
6. Model post-deployment health evidence and permit completion only for the
   exact confirmed deployment and version/environment.
7. Define post-start cancellation as separate intent and external status;
   preserve unresolved outcomes and prevent a late result from bypassing the
   accepted lifecycle.
8. Resolve expected-operation behavior across parent transitions. Either
   prohibit unrelated parent transitions while an operation is outstanding
   or introduce a generic, reviewed correlation rule in a later gate.
9. Add the two deadline/escalation paths and verify restart/replay behavior
   before declaring AST-REQ-031/032 implemented.

No item above is implemented by this milestone. No Gate 15/Milestone 2
execution was started.
