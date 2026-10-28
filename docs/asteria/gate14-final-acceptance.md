# Gate 14 final process-recovery and concurrency acceptance

## Disposition

**Not accepted for the full requested concurrent-delivery criteria.** The
deterministic coordinator fault harness now passes all nine specified
process-level crash windows, and a competing deployment-process test confirms
that an ambiguous result stays blocking without a duplicate deployment
mutation. The remaining cross-process races listed below were not all
executed, and the isolated Harness/provider in this harness is a controlled
test API hosted in the test process rather than the production Generic Harness
and a separate synthetic provider child. The evidence supports process
recovery against the tested contracts, not full independent-provider-process
or concurrency acceptance.

The user reports that the Standard and Emergency live synthetic lifecycles
have both passed. The Emergency run established all four Required obligations,
two distinct authenticated synthetic approval principals, deployment and
health evidence, terminal Completed, and consistent historical replay.
Those separately retained live-demo artifacts were not replaced by this
process test suite. Synthetic approvals are not real human identity proof;
synthetic deployment is not real deployment.

## Validation

| Validation | Result | Evidence |
|---|---|---|
| Full solution build | Passed, 0 warnings, 0 errors | `dotnet build .\StateMachine\DurableStateMachine.slnx --no-restore --verbosity minimal` |
| Asteria-focused tests | Passed, 85/85 | `dotnet test .\StateMachine\tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --filter "FullyQualifiedName~Asteria"` |
| Full regression suite | Passed, 191/191 | `dotnet test .\StateMachine\DurableStateMachine.slnx --no-build` |
| Process-restart acceptance harness | Passed, 12/12 | `AsteriaProcessAcceptanceTests`; nine required crash windows, one additional deployment response-loss boundary, one no-hook case, and one competing deployment process case |
| `git diff --check` | Passed | `git -C .\StateMachine diff --check` |
| Standard live synthetic lifecycle | User-reported passed; retained | [gate14-live-demo.md](./gate14-live-demo.md) |
| Emergency live synthetic lifecycle | User-reported passed; retained separately | See the existing Emergency evidence/export; this continuation did not overwrite either live run |

The process suite starts a real State Machine API child with a unique
State Machine database, hosts the controlled Harness contract endpoint in the
test process, and starts the actual AsteriaClient CLI as an independent child
process. For each crash case it kills the exact CLI PID without graceful
shutdown, then starts another CLI process against the same registry and State
Machine database paths. The tests inspect those databases through the API and
activity ledger, including receipts, expected operations, parent/concern
histories, projections, and historical replay where applicable.

The Harness fake tracks requests and deployment mutations in the parent test
process. It is not a child Generic Harness or provider, and its records are
not a provider-owned durable execution database. Thus the mutation counts
below are authoritative only for this deterministic test endpoint; they do
not establish real-provider persistence or survive termination of the test
host itself. The State Machine API process remains running during coordinator
restart cases. These are genuine coordinator OS-process tests, not merely
reconstructed coordinator objects.

Synchronization hooks are internal, disabled by default, and require both
explicit opt-in and `ASPNETCORE_ENVIRONMENT=Development`, plus a selected
boundary and an existing synchronization directory/run ID. The hook writes
an explicit reached signal after the coordinator's preceding durable
operation and waits for a separate release file; it does not hold a SQLite
transaction or registry lease. The no-hook case completes ordinary
verification without producing a signal file.

## Crash-window matrix

| Fault point | Processes killed/restarted | Original database preserved | Expected outcome | Observed outcome | External mutation count | State Machine receipt/operation/history result | Replay result | Pass/fail/blocker | Relevant test or evidence artifact |
|---|---|---|---|---|---|---|---|---|---|
| A. Activity intent persisted before dispatch | Coordinator CLI PID killed; new CLI PID resumes verification; State Machine API remains up | Yes; same registry and State Machine DB paths | Resume the original work identity; no unauthorized transition or duplicate dispatch | Work remained `DispatchPending` at kill, then became blocking `OutcomeUnknown`; no remote execution was fabricated or retried | Deployment mutations 0; verification Harness calls 0 | No accepted result receipt; original expected operation remains unresolved/expected; concern remains Requested; history is consistent | Consistent | **PASS** — conservative, safe recovery | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` / `verification-after-dispatch-pending` |
| B. Dispatch marker persisted before confirmed remote execution identity | Coordinator CLI PID killed after dispatch claim; restarted CLI performs supported by-key lookup | Yes | Do not blindly resend; if the authoritative lookup cannot establish result, preserve `OutcomeUnknown` | By-key lookup returned no execution; work remains `OutcomeUnknown`, parent remains DeploymentInProgress, and no deployment result was accepted | Deployment requests 0; mutations 0 | Start operation remains resolved; no accepted deployment-result receipt; Deployment concern remains Running | Consistent | **PASS** — unknown remains blocking | `Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely` / `deployment-after-dispatch-claim` |
| C. Harness accepted verification execution, response lost before Asteria archival | Coordinator CLI PID killed after response reached the coordinator but before execution identity/response archival; restarted CLI does not redispatch | Yes | Recover only from authoritative persisted evidence; otherwise keep unknown | One remote verification execution occurred, but no Harness execution ID or response was archived; restart preserved `OutcomeUnknown` without retry | Deployment mutations 0; verification Harness calls exactly 1 | No accepted verification receipt; concern remains Requested; original operation remains unresolved/expected | Consistent | **PASS** — unknown correctly blocks; no unsupported recovery claim | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` / `verification-after-remote-response` |
| C. Deployment response reached coordinator, before Harness identity/response archival (additional C variant) | Deployment coordinator CLI PID killed after fake Harness accepted mutation and returned its response but before archival; restarted CLI reconciles by the persisted idempotency key | Yes | Recover only from authoritative read-only status; never resend the deployment mutation | By-key lookup returned matching Succeeded status; original activity became Accepted | Deployment request 1; external mutations exactly 1 | One accepted deployment-result receipt; original operation resolved; histories/projections consistent | Consistent | **PASS** for this deterministic Harness contract | `Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely` / `deployment-after-remote-response` |
| D. Original response archived, observation not submitted | Coordinator CLI PID killed after response archival; restarted CLI reuses archived response and original work identity | Yes | Submit/reconcile the same observation without new external work | Restart accepted the original result without another Harness call | Deployment mutations 0; verification Harness calls exactly 1 | One accepted concern receipt, expected operation resolved, concern Passed, concern history present | Consistent | **PASS** | Same test / `verification-after-response-archived` |
| E. State Machine accepted observation, local acknowledgement not persisted | Coordinator CLI PID killed after State Machine accepted observation; restarted CLI reconciles receipt/history | Yes | Mark the original work Accepted from the same receipt, not a second transition | Work recovered to Accepted from the accepted receipt | Deployment mutations 0; verification Harness calls exactly 1 | Exactly one accepted result receipt; expected operation resolved; concern history remains consistent | Consistent | **PASS** | Same test / `verification-after-observation-accepted` |
| F. Expected operation resolved, coordinator restarted before work marked Accepted | Coordinator CLI PID killed at the pre-Accepted boundary; restarted CLI reuses resolved operation and accepted evidence | Yes | Finish local acknowledgement for the same operation and receipt | Original work became Accepted; no second accepted transition was created | Deployment mutations 0; verification Harness calls exactly 1 | One accepted result receipt; resolved original expected operation; consistent concern history | Consistent | **PASS** | Same test / `verification-before-work-accepted` |
| G. Deployment admitted, coordinator killed before status reconciliation | Coordinator CLI PID killed after admission response was archived; restarted CLI uses read-only deployment-ID status lookup | Yes | Reconcile authoritative status; never resend the non-idempotent deployment request | Status lookup returned Succeeded; original deployment activity became Accepted | Deployment request 1; external mutations 1 | One accepted deployment-result receipt; expected operation resolved; parent and Deployment concern histories agree; projection advanced to PostDeploymentVerification | Consistent | **PASS** for the test Harness contract | `Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely` / `deployment-after-admission-archived` |
| H. Deployment result accepted, process terminated before health verification | Deployment CLI PID killed after accepted result; restarted deployment CLI reads accepted evidence | Yes | Retain deployment identity/result; do not repeat mutation; health remains a separate next action | Accepted deployment was returned from persisted evidence without new mutation; parent remained PostDeploymentVerification | Deployment request 1; external mutations exactly 1 | Deployment acceptance and history retained; replay consistent | Consistent | **PASS** for the test Harness contract | `Accepted_deployment_process_restart_does_not_repeat_external_mutation` / `deployment-after-result-accepted` |
| I. Health observation accepted, process terminated before final Completed reconciliation | Health CLI PID killed after accepted health observation; restarted health CLI reconciles and completes | Yes | Require existing readiness, successful deployment, and accepted Passed health before Completed | Restart completed the same workflow; no deployment replay occurred | Deployment request 1; external mutations exactly 1 | Health concern Passed, accepted result reconciled, parent reached Completed, histories and projections remain consistent | Consistent | **PASS** for the test Harness contract | `Accepted_health_observation_recovers_before_final_completion_reconciliation` / `health-after-observation-accepted` |
| Ordinary execution with no fault injection | Coordinator child completes normally with synchronization variables removed | Yes | No pause, marker, or behavior change when hooks are not explicitly enabled | Verification completed normally; no marker was created | Deployment mutations 0; verification Harness calls 1 | One accepted verification result and consistent replay | Consistent | **PASS** | `Coordinator_process_runs_without_test_synchronization_by_default` |

## Concurrent and late-result race matrix

“Controlled” means an existing in-process/API-test-host test; it is not
counted as an independent-process race. The remaining rows prevent declaring
the requested full concurrency acceptance.

| Race | Processes/clients and original database | Expected outcome | Observed outcome | External mutation count | State Machine receipt/operation/history result | Replay result | Pass/fail/blocker | Relevant test or evidence artifact |
|---|---|---|---|---|---|---|---|---|
| Duplicate dispatch attempts for one deployment identity | Two independent AsteriaClient CLI child processes share the same State Machine and Asteria DBs; first is held after dispatch claim | One logical mutation maximum; second caller is duplicate/conflicting/stale or unknown, never a second mutation | Second caller observes unavailable authoritative status and exits blocking; first resumes; work remains `OutcomeUnknown` rather than adopting an unarchived result | Deployment request count 1; external mutations 1 | No accepted deployment-result receipt; parent remains DeploymentInProgress; replay consistent | Consistent | **PASS** for duplicate dispatch safety; result intentionally remains unknown | `Competing_deployment_processes_preserve_unknown_outcome_without_duplicate_dispatch` |
| Different results for one verification operation | No two coordinator processes delivering competing verification results | First accepted evidence must remain immutable; second different result must be explicitly conflicting/refused | Not exercised as a concurrent verification-result race; generic immutable/conflict semantics have controlled tests | Not measured | No raced verification receipt/operation/history inspected | Not race-tested | **BLOCKED** — no independent competing verification-result delivery | No dedicated process race test |
| Independent verification concerns completing concurrently | Coordinators were run sequentially in the process fixture | Each concern must retain its own generation/work identity and history | Individual concerns are exercised; concurrent completion was not run | Not measured concurrently | Sequential histories checked only | Not race-tested | **BLOCKED** | `AsteriaProcessAcceptanceTests` setup is sequential |
| Approval duplicate versus changed-payload submission | Existing approval gateway and test client share an in-process test host | Exact duplicate is duplicate/idempotent; changed payload is conflicting/refused; no last-write-wins | Controlled API test accepts original decision and refuses changed-payload retry with HTTP 409 | No deployment mutation | Approved concern and immutable approval records checked; no competing-process history inspected | Not race-tested | **BLOCKED** for independent clients/processes; controlled contract semantics pass | `Approval_gateway_authenticates_roles_and_preserves_immutable_decisions` |
| Amendment/supersession racing with late verification | Controlled concurrent operations against a test State Machine host | Late old-generation result must be fenced/refused; it cannot advance a superseded generation | Existing controlled test verifies fencing and recovery with a new stable revision identity | No provider mutation measured | Generation and history protections checked in controlled host | Separate replay coverage exists, not this race | **BLOCKED** for independent coordinator processes | `Concurrent_old_observation_fences_supersession_and_recovery_uses_new_stable_revision_identity` |
| Cancellation racing with deployment dispatch | Existing cancellation and deployment guards are sequential test calls | Cancellation/dispatch serialize; terminal cancellation cannot be bypassed | Pre-dispatch cancellation is terminal/idempotent; simultaneous process race not executed | Not race-measured | No concurrent cancellation/deployment receipt or history | Not race-tested | **BLOCKED** | `Cancellation_before_dispatch_is_terminal_and_idempotent` |
| Deployment result arriving after cancellation intent | No independent late-result client was run | Preserve cancellation intent; accept only authoritative result evidence without implying rollback or bypassing terminal state | Post-dispatch intent is covered sequentially; late delivery race was not run | Not race-measured | No raced receipt/history | Not race-tested | **BLOCKED** | Controlled cancellation/deployment lifecycle tests |
| Late health result after a disqualifying workflow state | No competing health process/API client | Result must be stale/refused and cannot produce Completed | Health binding and cancellation guards pass controlled tests; late concurrent delivery was not run | No health-caused mutation; not race-measured | No raced health receipt/history | Not race-tested | **BLOCKED** | `AsteriaMilestone3BTests` health/cancellation coverage |
| Concurrent readiness attempts | No competing readiness child processes | One exact readiness operation accepted; duplicate is idempotent and changed/stale inputs do not mutate readiness | Not exercised concurrently | No deployment mutation expected; not measured | No concurrent receipt/history inspected | Not race-tested | **BLOCKED** | No dedicated process/API race test |
| Concurrent completion attempts | No competing completion child processes/API clients | One Completed transition accepted; duplicate is idempotent; terminal state cannot be bypassed | Completion evidence guards are tested sequentially; simultaneous completion was not run | No deployment mutation expected; not measured | No concurrent completion receipt/history inspected | Not race-tested | **BLOCKED** | Controlled V5 lifecycle and completion guards |

## Final recommendation

All nine requested crash windows and the ordinary no-hook path now have
genuine coordinator OS-process termination/restart evidence against isolated
databases. The duplicate-deployment race also ran with two independent CLI
processes and confirmed one external mutation maximum with `OutcomeUnknown`
preserved when the authoritative result was unavailable.

Do **not** declare full Gate 14 concurrency acceptance yet. The smallest
remaining action is to add independent-process or independently hosted API
client tests for the blocked rows above, with deterministic response ordering
and explicit accepted/duplicate/conflicting/stale/refused outcomes. If
provider-process durability is part of the acceptance claim, replace or
augment the in-process Harness fake with the isolated real Generic Harness
and synthetic provider processes and inspect their own durable execution
records. Keep all conclusions within synthetic training scope.
