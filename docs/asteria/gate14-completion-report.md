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
- Baseline validation before process-hardening: full solution build
  **passed** with 0 warnings and 0 errors; Asteria-focused tests **passed**
  (73/73); full solution regression suite **passed** (179/179);
  `git diff --check` **passed**.

The controlled test-handler results are not live service outcomes. The
dispatch-marker check validates durable ledger persistence; it is not, by
itself, a multi-process crash-window test of a live provider.

## Process-level acceptance hardening

Added internal Asteria coordinator synchronization hooks that are disabled by
default and require explicit opt-in in Development. Each hook signals its
exact boundary and waits for test-controlled release after the prior durable
operation. The hooks do not hold SQLite transactions or registry leases.
Added an isolated process harness that launches the real State Machine API
and AsteriaClient CLI as child processes with unique databases and kills only
the exact coordinator PID selected for each case.

The harness passes all nine specified crash windows: verification intent,
dispatch marker, remote response before archival, archived response,
accepted State Machine observation, resolved operation before local
acceptance, deployment admission before reconciliation, accepted deployment
before health, and accepted health before final completion. Recovery reuses
persisted work identities and never retries an ambiguous deployment mutation.
The no-hook execution also passes. A two-coordinator deployment race confirmed
one dispatch request and one external mutation maximum; because the competing
lookup could not establish an authoritative result, the work correctly
remained `OutcomeUnknown`.

The process harness hosts a deterministic fake Harness API in its test
process. It does not launch the Generic Harness or a separate provider child
for these fault cases, and its mutation counter is not provider-owned durable
evidence. The State Machine API stays running while coordinator CLI processes
are killed/restarted. These limitations are explicitly retained in the
acceptance matrix; the process tests are not described as end-to-end
provider-process recovery.

Final validation after the hardening changes: full solution build **passed**
with 0 warnings and 0 errors; all Asteria-focused tests **passed** (85/85);
full solution regression suite **passed** (191/191). The separate
`git diff --check` result is recorded in
[gate14-final-acceptance.md](./gate14-final-acceptance.md).

The user reports that Standard and Emergency live synthetic lifecycles both
passed. The Emergency report included four Required obligations, distinct
synthetic approval principals, accepted deployment and health evidence,
Completed, and consistent historical replay. These separately retained live
scenario results are not replaced by the process-test harness and do not
establish real human identity or deployment.

The individual expected/observed results, test artifacts, and exact blockers
are recorded in [gate14-final-acceptance.md](./gate14-final-acceptance.md).
Gate 14 remains **not accepted for the full concurrency criteria** because
the independent competing verification, concern, approval, amendment,
cancellation, late-result, readiness, and completion races have not all been
executed. The verified process cases and passing test suites do not justify
weakening or reclassifying those remaining gaps.

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

The nine coordinator crash boundaries now have process-level restart evidence.
The remaining concurrency gaps and the narrower provider-process limitation
are enumerated in
[gate14-final-acceptance.md](./gate14-final-acceptance.md). Production
acceptance with real human authorities or deployment providers remains
outside the synthetic training scope and is not claimed.
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
