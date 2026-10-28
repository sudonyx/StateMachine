# Gate 14 implementation status

## Status

**Gate 14 is complete for its V5 synthetic training scope only.** The V4/V5
workflow, synthetic provider hosts, registration/bootstrap automation, and
live Standard and Emergency local lifecycles are accompanied by process-level
crash recovery, independent-client concurrency, and targeted
Generic-Harness/provider durability acceptance evidence. This does not
establish real human identity, production approval authority, provider
attestation, or a real deployment.

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
- Baseline validation before process-hardening: full solution build
  **passed** with 0 warnings and 0 errors; Asteria-focused tests **passed**
  (73/73); full solution regression suite **passed** (179/179);
  `git diff --check` **passed**.

The controlled test-handler results are not live service outcomes. The
dispatch-marker check validates durable ledger persistence; it is not, by
itself, a multi-process crash-window test of a live provider.

## Process-level and concurrency acceptance closure

Added internal Asteria coordinator synchronization hooks that are disabled by
default and require explicit opt-in in Development. Each hook signals its
exact boundary and waits for test-controlled release after the prior durable
operation. The hooks do not hold SQLite transactions or registry leases.
The isolated harness launches the real State Machine API and AsteriaClient
CLI as child processes with unique databases and kills only the exact
coordinator PID selected for each case. Both ordinary execution with no hook
variables and explicit hook variables in `Production` complete without a
pause or signal file.

The harness passes all nine specified crash windows and the listed
cross-process races: conflicting verification results in both controlled
winner orders, concurrent independent concerns, both approval payload winner
orders, both supersession/verification orderings, cancellation on either side
of dispatch, late deployment and health results, concurrent readiness, and
concurrent completion. The assertions inspect the persisted work identity,
generation and digest, expected operations, receipts, attempts where
available, parent/concern histories, projections, and historical replay.
Unknown outcomes remain blocking; accepted evidence is not replaced; terminal
or disqualifying workflow state does not advance to Completed without its
required evidence.

Most deterministic fault windows retain the controlled Harness endpoint
hosted in the test process; its mutation counter is not represented as
provider-owned durable evidence. Targeted isolated tests also start independent
Generic Harness and synthetic deployment-provider child processes. They kill
the coordinator after provider admission and before Asteria archival, restart
against the same databases, reconcile read-only by the original idempotency
key, inspect actual Harness execution/inspection/evidence/attempt records and
the provider's SQLite row, then verify repeated/competing coordinators still
produce one provider-owned logical deployment. The complete nine-window
matrix does not claim that every crash window ran against those separate
provider processes.

Final validation: the full StateMachine solution build passed with 0 errors
and two NU1903 warnings from existing Generic Harness dependency versions;
Asteria-focused tests passed (103/103); process-recovery, concurrency, and
provider durability tests passed (30/30); full StateMachine regression passed
(209/209); and `git diff --check` passed. The warning details and per-case
evidence are recorded in [gate14-final-acceptance.md](./gate14-final-acceptance.md).

The user-reported Standard and Emergency live scenarios remain separately
retained and are not replaced by the process-test harness. The Emergency
scenario included four Required obligations, distinct authenticated
synthetic approval principals, accepted deployment and health evidence,
Completed, and consistent historical replay.

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

## Remaining boundaries

The controlled Harness and independent provider-process coverage distinctions,
including the supported-state limit on amendment after deployment, are
itemized in [gate14-final-acceptance.md](./gate14-final-acceptance.md).
Production acceptance with real human authorities or deployment providers
remains outside the synthetic training scope and is not claimed.
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
