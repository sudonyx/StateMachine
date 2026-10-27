# Gate 14 implementation status

## Status

**Gate 14 is not complete.** The current repository contains the V4/V5
workflow implementation and controlled lifecycle paths, but required recovery,
adversarial, and live-service acceptance evidence is still incomplete. This
report distinguishes implemented code from accepted evidence; it does not
relabel controlled fixtures as production or live results.

## Implemented in this worktree

| Area | State | Evidence |
|---|---|---|
| V4/V5 workflow definitions | Implemented | Immutable versioned definitions with separate concern projections, explicit applicability/result mappings, readiness, deployment, health, completion, and cancellation guards. V1–V3 files are not changed by this continuation. |
| Durable activity ledger | Implemented | Generation-bound activity, response, provider-observation, conflict, and acceptance records share the Asteria registry database. |
| Functional/security verification | Implemented | Harness-backed coordinator validates exact provider, API-description, operation, work, release, generation, and digest bindings; execution success is not a business pass. |
| Approval gateway | Implemented | Synthetic bearer authentication derives principal and role out of band; approval records are immutable and distinct by approval concern. |
| Applicability and readiness | Implemented | Policy response is the applicability source; readiness checks explicit concern evidence, accepted receipts/history, current generation, and cancellation state. |
| Deployment | Implemented | Uses Generic Harness for mutation and both documented read-only status/by-key lookups; uncertain mutation outcomes are not resent. A durable dispatch-start marker sends post-marker recovery to reconciliation rather than repeating the mutation. Parent and concern result observations are separate. |
| Health | Implemented | Uses a separate Harness execution for the supplied read-only health operation, validates deployment/version/environment/result binding, and requires accepted Passed health for completion. |
| Deadlines | Implemented | Planned-time verification escalation and transition-time deployment-investigation deadlines are persisted once and processed explicitly. Processing records do not claim a waiver, intervention, or deployment result. |
| Cancellation | Implemented | Pre-dispatch cancellation records intent and reaches terminal cancellation; post-dispatch cancellation remains a separate durable intent and does not imply provider cancellation or rollback. |
| Evidence export | Implemented | JSON export includes generation snapshots, original decision/provider data, activities, conflicts, approvals, deadlines, parent/concern history and live projections, and historical replay. |
| CLI | Implemented | Assessment, verification, approval-gateway host, readiness, deployment, health, cancellation, deadline processing, and evidence-export commands are wired. |

## Focused validation recorded

- V5 readiness → deployment → health → Completed controlled integration,
  including exact retries and separate reviewer/emergency-authoriser
  principals: **passed**.
- Durable deadline identity/replay/conflict test: **passed**.
- A deployment dispatch claim and `OutcomeUnknown` state survive recreation
  of the activity-ledger client: **passed**.
- Eight concurrent exact reservations resolve to one durable work item, and
  eight concurrent dispatch claims yield exactly one dispatcher: **passed**.
- Pre-dispatch cancellation terminal/idempotent test: **passed**.
- V5 definition loading and lifecycle-guard test: **passed**.
- Full `DurableStateMachine.slnx` build: **passed**, 0 warnings and 0 errors.
- All Asteria tests: **passed**, 54/54.
- Full regression suite: **passed**, 160/160.
- `git diff --check`: **passed**.

All integration responses above are controlled test-handler results, not
live service outcomes. The dispatch-marker check validates ledger persistence;
it is not a multi-process crash-window test of a live provider.

## Remaining acceptance gaps

The implementation must not be declared complete until these items are
executed and evidenced:

1. Full restart/crash-window and multi-process concurrency coverage, plus
   stale-generation, duplicate, conflicting-result, and historical replay
   coverage across verification, approvals, deployment, and health.
2. Adversarial end-to-end branches for failed, indeterminate, malformed,
   mismatched-deployment, lost-acknowledgement, and cancellation-race outcomes
   across each lifecycle stage. The current suite does not establish these
   branches comprehensively.
3. Live integration against independently started Decision API, Generic
   Harness, synthetic verification/deployment/health providers, and the
   approval gateway, with their current builds, registrations, configuration,
   and original evidence captured.

The Decision API, Generic Harness, functional/security provider
implementations, and deployment/health provider processes are not available
in this repository checkout. Therefore their live startup, registration, and
cross-service behavior remain unverified. The exact repository-owned startup
and client steps, plus required external configuration, are in
[gate14-live-demo.md](./gate14-live-demo.md).

## Boundaries preserved

- No Asteria-specific behavior was added to Generic Harness or State Machine
  generic runtime engines.
- V1/V2/V3 definitions are preserved.
- Supplied deployment and health API contracts were not extended.
- HTTP success is not treated as policy, verification, approval, deployment,
  health, readiness, or completion success.
- Ambiguous non-idempotent deployment requests are never automatically
  resent; unavailable evidence remains unknown.
- Assessment generations do not share activity evidence.
- Parent and concern revisions, histories, receipts, and results remain
  distinct.
- Synthetic credential and provider labels are not described as production
  identity or cryptographic provider attestation.
