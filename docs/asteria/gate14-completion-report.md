# Gate 14 implementation status

## Status

**Gate 14 is not complete.** The repository now contains the V4/V5 workflow
implementation, synthetic provider hosts, registration/bootstrap automation,
and a successful live local V5 lifecycle across the Decision API, State
Machine, Generic Harness, and synthetic services. Required crash-window,
multi-process, and adversarial acceptance evidence remains incomplete. The
synthetic live result is explicitly not production verification, human
approval, or deployment evidence.

## Implemented in this worktree

| Area | State | Evidence |
|---|---|---|
| V4/V5 workflow definitions | Implemented | Immutable versioned definitions with separate concern projections, explicit applicability/result mappings, readiness, deployment, health, completion, and cancellation guards. V1–V3 files are not changed by this continuation. |
| Durable activity ledger | Implemented | Generation-bound activity, response, provider-observation, conflict, and acceptance records share the Asteria registry database. |
| Functional/security verification | Implemented | Harness-backed coordinator validates exact provider, API-description, operation, work, release, generation, and digest bindings; execution success is not a business pass. |
| Synthetic provider hosts/contracts | Implemented | Independent functional, security, deployment, and health hosts publish HTTP/OpenAPI contracts; deployment records use an isolated SQLite store and complete asynchronously. Results are deterministic local fixtures. |
| Approval gateway | Implemented | Synthetic bearer authentication derives principal and role out of band; approval records are immutable and distinct by approval concern. |
| Applicability and readiness | Implemented | Policy response is the applicability source; readiness checks explicit concern evidence, accepted receipts/history, current generation, and cancellation state. |
| Deployment | Implemented | Uses Generic Harness for mutation and both documented read-only status/by-key lookups; bounded polling remains read-only and uncertain mutation outcomes are not resent. A durable dispatch-start marker sends post-marker recovery to reconciliation rather than repeating the mutation. Parent and concern result observations are separate. |
| Health | Implemented | Uses a separate Harness execution for the supplied read-only health operation, validates deployment/version/environment/result binding, and requires accepted Passed health for completion. |
| Deadlines | Implemented | Planned-time verification escalation and transition-time deployment-investigation deadlines are persisted once and processed explicitly. Processing records do not claim a waiver, intervention, or deployment result. |
| Cancellation | Implemented | Pre-dispatch cancellation records intent and reaches terminal cancellation; post-dispatch cancellation remains a separate durable intent and does not imply provider cancellation or rollback. |
| Evidence export | Implemented | JSON export includes generation snapshots, original decision/provider data, activities, conflicts, approvals, deadlines, parent/concern history and live projections, and historical replay. |
| CLI | Implemented | Assessment, verification, approval-gateway host, readiness, deployment, health, cancellation, deadline processing, and evidence-export commands are wired. |
| Bootstrap/live demo | Implemented | `tools/Start-AsteriaGate14Demo.ps1` verifies local services, starts isolated databases/services, registers V5 and actual supported Harness operations, runs the successful lifecycle, and exports evidence without printing/persisting generated credentials. |

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
- Controlled deployment integration exercises Pending then Succeeded status
  using separate read-only Harness lookups; no second deployment mutation is
  issued. Lost acknowledgement plus unavailable first lookup remains
  `OutcomeUnknown`; a reconstructed coordinator later reconciles by the
  stable idempotency key without resending the deployment mutation. Exhausted
  bounded Pending lookups also remain unknown, and a terminal Failed result
  cannot advance to health.
- Verification provider JSON parsing, malformed evidence, mismatched release,
  generation and activity identity, unsupported results, and missing evidence
  are tested to refuse without accepting a concern result.
- Health Passed, Failed, and Indeterminate results are tested separately.
  Wrong deployment/version evidence is refused, and neither unresolved health
  nor cancellation intent can produce Completed.
- Pre-dispatch cancellation from ReadyForDeployment is terminal and
  idempotent. Post-dispatch cancellation remains a nonterminal intent and
  blocks further health admission.
- Post-dispatch cancellation uncovered and fixed a State Machine client
  validation gap: cancellation operations now validate their supported
  lifecycle states without broadening the initial-assessment state guard.
- Final validation: full solution build **passed** with 0 warnings and 0
  errors; all Asteria-focused tests **passed** (73/73); full solution
  regression suite **passed** (179/179); `git diff --check` **passed**.

The controlled test-handler results are not live service outcomes. The
dispatch-marker check validates durable ledger persistence; it is not a
multi-process crash-window test of a live provider.

## Live synthetic lifecycle

On 2026-10-27, the repeatable demo was run again after the recovery and
cancellation changes. The Decision API at `5267` was reachable. The demo
started fresh isolated State Machine and Harness instances on `5001` and
`5248`, with per-run SQLite databases; it did not alter existing service
databases.

The live run registered Asteria V5 and supported Generic Harness operations
discovered from successful registrations for functional/security verification,
deployment request/status/idempotency lookup, and health. It completed release
`AST-G14-LIVE-20261027194635` with the following persisted evidence:

- AST-POL-001 policy assessment: accepted and operational, generation 1.
- Functional verification: `Passed`; security verification: `Passed`.
- Release review approval: `Approved` by the separate authenticated synthetic
  reviewer principal; emergency approval was explicitly `NotRequired`.
- Readiness: `Ready`.
- Deployment: admitted once, reconciled through read-only status lookups from
  `Pending` to `Succeeded`.
- Post-deployment health: `Passed` for the exact deployment, version, and
  environment; State Machine projection: `Completed`.
- Structured evidence export:
  `C:\\Users\\joshw\\AppData\\Local\\Temp\\asteria-g14-20261027-194628689\\gate14-evidence.json`.
- Logs, isolated databases, and generated service process IDs are under
  `C:\\Users\\joshw\\AppData\\Local\\Temp\\asteria-g14-20261027-194628689`.

The independent Decision API was live. The State Machine, Harness, synthetic
providers, and approval gateway were independently hosted live local
processes, but their provider results and approval identities were synthetic
fixtures. This establishes the successful local V5 cross-service lifecycle
and evidence export only; it is not production acceptance.

## Remaining acceptance gaps

The implementation must not be declared complete until these items are
executed and evidenced:

1. True multi-process crash-window and concurrency acceptance remains
   unestablished. Tests recreate coordinators and ledger clients over durable
   SQLite state, and the live demo uses independent service processes, but no
   test kills/restarts all external processes at every dispatch/response/
   observation boundary.
2. Adversarial branches are substantially covered, but not exhaustive across
   every stage and race. Remaining examples include late concurrent conflicting
   provider results and process-level cancellation races; no last-write-wins
   behavior is inferred.
3. Production acceptance with independently authenticated real provider and
   human authorities, if required by the deployment environment. The local
   synthetic providers intentionally do not establish that trust.

The local V5 business lifecycle is established; full process-level
restart/crash-window, multi-process race, and exhaustive adversarial branch
acceptance are not.
The demo leaves services running on its isolated ports and prints exact child
PIDs for cleanup. Startup, evidence inspection, limitations, and safe cleanup
instructions are in [gate14-live-demo.md](./gate14-live-demo.md).

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
