# Gate 14 — Asteria Milestone 1 design

## Scope and inputs

This is a feasibility skeleton, not an executable release-governance workflow.
The Asteria source pack includes the full
[AST-BR-001 requirements](../../training-data/asteria/requirements/asteria-release-requirements-v1.md),
the [policy supplement](../../training-data/asteria/policies/asteria-release-policy-v1.md),
synthetic cases, both OpenAPI contracts, and the input
[README](../../training-data/asteria/README.md). The requirements and policy
remain authoritative; the synthetic cases and provider contracts are not
evidence that any event occurred.

The immutable definition is
[asteria-release-governance-v1.json](../../definitions/asteria-release-governance-v1.json),
identity `asteria-release-governance@1.0`, language 1.3. No generic runtime,
Generic Harness, rule engine, Orion definition, or Meridian definition was
changed.

## Declared lifecycle and permitted progress

The definition declares all ten candidate parent states: `Draft`,
`UnderAssessment`, `PreparingForDeployment`, `Reassessing`,
`ReadyForDeployment`, `DeploymentInProgress`,
`PostDeploymentVerification`, `Completed`, `Prohibited`, and `Cancelled`.
Only three parent transitions are declared:

1. `Draft -> UnderAssessment` for a `SubmitRelease` command after the
   expressible release-field checks pass.
2. `Draft -> Cancelled` after a separate cancellation-intent request, and
   only while the deployment concern remains `NotRequested`.
3. `UnderAssessment -> Cancelled` under the same guard.

The submission guard checks non-empty string values, the defined environment,
category and size vocabularies, Boolean impact facts, and a generation of at
least one. Missing, null, and wrong-typed comparison facts are unresolved, not
successful. The current operators cannot enforce integer-only generations,
RFC 3339 timestamp syntax, digest authenticity, employee identity, or the
owner's authority. Consequently this records a local submission command; it
does not establish a fully validated or authorized AST-REQ-003 submission.

All deployment, policy-outcome, reassessment, and completion states are
deliberately unreachable. There is no route to `ReadyForDeployment`,
`DeploymentInProgress`, `Completed`, or `Prohibited`; in particular, the
definition has no unguarded path to deployment or completion. `Prohibited`
cannot be selected without an authoritative policy decision, and `Completed`
cannot be selected without correlated deployment and health evidence.

## Concern dimensions

Six independent concerns are declared with finite state vocabularies:

| Concern | Locally supported transition | Outcome states |
|---|---|---|
| `FunctionalVerification` | `NotRequested -> Requested` | `Passed`, `Failed`, `Indeterminate`, and `Superseded` are not accepted from inputs |
| `SecurityVerification` | `NotRequested -> Requested` | `Passed`, `Failed`, `Indeterminate`, and `Superseded` are not accepted from inputs |
| `ReleaseReviewApproval` | `NotRequested -> Requested` | `Approved`, `Rejected`, and `Unresolved` are not accepted from inputs |
| `EmergencyApproval` | `NotRequested -> Requested` | `Approved`, `Rejected`, and `Unresolved` are not accepted from inputs |
| `DeploymentExecution` | `NotRequested -> RequestPrepared`, guarded by parent `DeploymentInProgress` | `OutcomeUnknown`, `Succeeded`, and `Failed` have no incoming input transitions |
| `CancellationIntent` | `NotRequested -> Requested` | `Requested` is terminal in this version; intent is monotonic |

The two approvals use separate concern projections and distinct command names.
Requesting Release Reviewer approval neither requests nor grants Emergency
approval. Verification requests likewise remain separate. These requests are
workflow intent, not passing checks or accepted approvals.

`RequestPrepared` means only that a local dispatch request was prepared; it
does not mean an external deployment began. Its parent-state guard is currently
unreachable because there is no transition to `ReadyForDeployment`.
`OutcomeUnknown` is kept distinct from `Failed`, but neither state is reachable
in this milestone. No synthetic provider observation or success is accepted.

The definition intentionally omits language 1.3's `cancellationEvent`
mechanism. A cancellation request does not terminate the execution concern or
imply that an external deployment stopped. The only accepted parent
cancellation is explicitly guarded and pre-deployment. Because the parent
`Cancelled` state is terminal, later inputs to any concern are refused by the
existing runtime; the concern histories already committed remain inspectable.

## Guards, trust, and revisions

Guards use only existing language 1.3 operators: `and`, `or`, `ne`, `gte`,
`eq`, `concernState`, and `parentState`. The cancellation guard reads the
committed `CancellationIntent` and `DeploymentExecution` states from the
current evaluation snapshot. A failed cancellation attempt before an intent is
recorded, followed by acceptance after it is recorded, exercises that snapshot
behavior.

Parent lifecycle revision and each concern revision remain separate. A concern
request advances only its own revision; it does not change parent state or
revision. This makes local concern request history independently inspectable
and replayable. Generic expected operations are parent-revision-bound, however,
so a parent transition can stale an outstanding expected result.

Instance facts are fixed at creation in the current persistence contract.
Although the API accepts per-attempt facts, those are not authoritative or
persisted instance facts. This definition does not use them to manufacture
assessment, approval, or generation evidence. No policy facts, authorizations,
verification results, deployment results, or health outcomes are fabricated.

## Testing and deliberate omissions

The focused tests cover definition registration and validation; initial
instance/concern state; incomplete and complete local submission attempts;
refusal to prepare deployment outside `DeploymentInProgress`; independent
verification and approval requests; duplicate command delivery; concern
history and replay; current-snapshot cancellation guards; and terminal-parent
refusal. They use no accepted approval or deployment-success observation.

Policy evaluation, policy applicability, approval identity, verification
evidence, material revision, generation-aware result correlation, deployment
execution, health verification, post-start cancellation resolution, timers,
and migrations are deliberately absent. Empty or permissive placeholder
transitions would make unsupported claims actionable, so an absent path is
safer than an assumed `Approved`, `Passed`, `NotRequired`, or successful
external outcome.

The exact feasibility classifications and all remaining work are recorded in
[gate14-milestone1-feasibility.md](./gate14-milestone1-feasibility.md).
Executed validation is recorded separately in
[gate14-milestone1-test-results.md](./gate14-milestone1-test-results.md).
