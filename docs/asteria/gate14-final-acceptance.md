# Gate 14 final process-recovery and concurrency acceptance

## Disposition

**Accepted for the V5 synthetic training scope only.** The nine specified
crash windows, the previously blocked concurrency races, and targeted
independent Generic Harness/synthetic-provider durability cases pass in
isolated test databases. Standard and Emergency live local lifecycle evidence
remains retained separately; the Emergency run established all four Required
obligations, distinct authenticated synthetic approval principals, deployment
and health evidence, Completed, and consistent historical replay.

This acceptance does not establish real human identity, production approval
authority, cryptographic provider attestation, or a real deployment.

## Validation

| Validation | Result | Evidence |
|---|---|---|
| Full StateMachine solution build | Passed; 0 errors. Two NU1903 warnings are reported by Generic Harness for existing `Microsoft.OpenApi` and `SQLitePCLRaw.lib.e_sqlite3` package versions. | `dotnet build .\StateMachine\DurableStateMachine.slnx --no-restore --verbosity minimal` |
| Process recovery, concurrency, and provider durability harness | Passed, 30/30 | `AsteriaProcessAcceptanceTests` |
| Asteria-focused tests | Passed, 103/103 | `dotnet test .\StateMachine\tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --filter "FullyQualifiedName~Asteria"` |
| Full StateMachine regression suite | Passed, 209/209 | `dotnet test .\StateMachine\DurableStateMachine.slnx --no-build` |
| `git diff --check` | Passed | `git -C .\StateMachine diff --check` |
| Standard and Emergency live synthetic lifecycles | User-reported passed; prior exports retained | [gate14-live-demo.md](./gate14-live-demo.md) |

## Harness and evidence boundaries

The deterministic crash-window harness starts the real State Machine API and
AsteriaClient CLI as child processes with isolated, unique SQLite databases.
It tracks and terminates only the exact child PIDs it starts. Most fault
boundaries use the existing controlled Harness endpoint hosted in the test
process; those cases test genuine coordinator process termination and restart,
but their Harness mutation count is not provider-owned durable evidence.

Targeted provider tests additionally start independent Generic Harness and
synthetic deployment-provider child processes on isolated ports and databases.
The deployment recovery case kills and restarts the coordinator while keeping
the original Harness and provider databases, then reconciles by the persisted
idempotency key. Provider-owned `SyntheticDeployments`, Generic Harness
execution, inspection, evidence, and attempt records are inspected directly.
These targeted cases do not claim that every crash window ran against an
independent provider process.

Synchronization hooks are internal, disabled by default, and active only
with explicit opt-in in `Development`. Each pause writes a boundary-reached
signal and waits for a test-controlled release after the preceding durable
operation. No SQLite transaction or registry lease is held across the pause.
The no-hook and `Production`-environment opt-in checks complete without
creating a signal file.

## Crash-window matrix

| Fault point | Processes killed/restarted | Original database preserved | Expected outcome | Observed outcome | External mutation count | State Machine receipt/operation/history result | Replay result | Disposition | Test/evidence |
|---|---|---|---|---|---|---|---|---|---|
| A. Activity intent persisted before dispatch | Verification CLI killed at `verification-after-dispatch-pending`; a new CLI resumes. State Machine API remains up. | Yes; same Asteria registry and State Machine DB | Do not fabricate a remote identity or retry an unconfirmed dispatch. | Activity was `DispatchPending`, then became blocking `OutcomeUnknown`; no remote call was made. | Verification calls 0; deployment mutations 0 | No accepted result receipt; original expected operation remains unresolved; concern stays Requested; histories remain consistent. | Consistent | **PASS** | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` |
| B. Dispatch marker persisted before confirmed remote identity | Deployment CLI killed at `deployment-after-dispatch-claim`; new CLI performs supported read-only by-key reconciliation. | Yes | Never blindly resend; preserve unknown if lookup cannot establish the result. | Lookup found no execution; work remains `OutcomeUnknown`; parent remains `DeploymentInProgress`. | Mutations 0 | No accepted deployment-result receipt; start operation remains resolved; Deployment concern stays Running. | Consistent | **PASS** | `Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely` |
| C. Harness accepted verification work, response lost before Asteria archival | Verification CLI killed at `verification-after-remote-response`; new CLI starts against the same registry. | Yes | Recover only from authoritative evidence; otherwise retain unknown and do not redispatch. | One Harness verification occurred, but no execution identity or response was archived; restart preserved `OutcomeUnknown`. | Verification calls 1; deployment mutations 0 | No accepted result receipt; expected operation remains unresolved; concern stays Requested. | Consistent | **PASS** | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` |
| C. Deployment admitted, coordinator killed before Asteria response archival (controlled Harness) | Deployment CLI killed at `deployment-after-remote-response`; new CLI reconciles by the original key. | Yes | Reconcile authoritative status without repeating the non-idempotent mutation. | By-key lookup returned matching `Succeeded`; original activity became Accepted. | Deployment requests 1; mutations 1 | One accepted deployment-result receipt; expected operation resolved; histories and projection agree. | Consistent | **PASS** | `Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely` |
| C. Deployment admitted, coordinator killed before Asteria response archival (independent services) | AsteriaClient CLI killed; independent Generic Harness and synthetic provider processes remain up. Restarted CLI uses both original service databases. | Yes; same Harness and provider SQLite files | Read-only lookup by the original idempotency key; no second provider mutation. | Harness retained its execution; provider returned the original Succeeded deployment; restart accepted the original work. A further coordinator process returned accepted evidence without a new dispatch. | Provider-owned logical deployment rows: exactly 1 | Harness POST and lookup executions, execution inspection/evidence/attempts, Asteria activity and accepted State Machine result were inspected. | Consistent | **PASS** | `Generic_harness_and_provider_process_recover_admitted_deployment_by_original_key` |
| D. Original response archived, observation not submitted | Verification CLI killed at `verification-after-response-archived`; new CLI resumes from the archived response. | Yes | Submit/reconcile the same observation and work identity without new external work. | Original response was reused; activity became Accepted. | Verification calls 1; deployment mutations 0 | One accepted concern receipt; original operation resolved; Passed concern history present. | Consistent | **PASS** | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` |
| E. State Machine accepted observation, local acknowledgement not persisted | Verification CLI killed at `verification-after-observation-accepted`; restarted CLI reconciles the receipt. | Yes | Persist local acknowledgement for the same accepted receipt, not another transition. | Activity became Accepted from the existing receipt. | Verification calls 1; deployment mutations 0 | Exactly one accepted result receipt; operation resolved; concern history consistent. | Consistent | **PASS** | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` |
| F. Expected operation resolved, coordinator restarted before work becomes Accepted | Verification CLI killed at `verification-before-work-accepted`; restarted CLI reads the resolved operation and accepted evidence. | Yes | Complete only local acknowledgement for the original operation and receipt. | Work became Accepted without another accepted transition. | Verification calls 1; deployment mutations 0 | One accepted result receipt; original expected operation resolved; histories consistent. | Consistent | **PASS** | `Verification_coordinator_process_recovers_only_from_authoritative_persisted_evidence` |
| G. Deployment admitted, coordinator killed before status reconciliation (controlled Harness) | Deployment CLI killed at `deployment-after-admission-archived`; a new CLI performs read-only status lookup. | Yes | Reconcile authoritative status; never resend the deployment mutation. | Original deployment became Accepted and parent advanced to PostDeploymentVerification. | Deployment requests 1; mutations 1 | One accepted deployment-result receipt; operation resolved; parent and concern histories agree. | Consistent | **PASS** | `Deployment_process_crash_preserves_mutation_identity_and_reconciles_safely` |
| H. Deployment result accepted, process terminated before health verification | Deployment CLI killed at `deployment-after-result-accepted`; new deployment CLI reads accepted evidence. | Yes | Keep the accepted deployment identity/result and do not repeat the mutation. | Restart returned accepted work; health remained a separate action. | Deployment requests 1; mutations 1 | Deployment acceptance and history retained; parent remains PostDeploymentVerification. | Consistent | **PASS** | `Accepted_deployment_process_restart_does_not_repeat_external_mutation` |
| I. Health observation accepted, process terminated before final Completed reconciliation | Health CLI killed at `health-after-observation-accepted`; a new health CLI reconciles. | Yes | Require prior readiness, successful deployment, and accepted Passed health before Completed. | Restart completed the same workflow without deployment replay. | Deployment requests 1; mutations 1 | Health concern Passed; accepted result reconciled; parent Completed; histories and projection consistent. | Consistent | **PASS** | `Accepted_health_observation_recovers_before_final_completion_reconciliation` |
| Ordinary execution; hooks omitted | Coordinator CLI runs without hook variables. | Yes | Normal behavior; no pause or marker. | Verification completed normally. | Verification calls 1 | One accepted result receipt; history consistent. | Consistent | **PASS** | `Coordinator_process_runs_without_test_synchronization_by_default` |
| Explicit hook opt-in outside Development | Coordinator CLI receives test hook variables but runs with `ASPNETCORE_ENVIRONMENT=Production`. | Yes | Development-only hook must remain inactive. | Process completed without creating a reached signal. | Verification calls 1 | Work Accepted; no unauthorized transition. | Consistent | **PASS** | `Synchronization_opt_in_is_ignored_outside_development_environment` |

## Concurrent and late-result race matrix

| Race | Processes/clients and synchronization | Expected outcome | Observed durable evidence | External mutation count | Replay | Disposition | Test/evidence |
|---|---|---|---|---|---|---|---|
| Duplicate deployment dispatch for one identity | Two CLI processes share the Asteria/State Machine databases. Controlled fake-Harness case orders a competing lookup before dispatch; independent-service case pauses after provider admission while a second coordinator reconciles. | At most one mutation. Unknown remains blocking if no authoritative result exists; if provider evidence exists, preserve the one result. | Controlled case preserves `OutcomeUnknown` after a missing lookup with one attempted mutation maximum. Independent case has one provider-owned row, one Harness deployment execution, one accepted activity and one accepted State Machine deployment result. | Exactly 1 in provider-backed case; 1 maximum in unknown fake case | Consistent | **PASS** | `Competing_deployment_processes_preserve_unknown_outcome_without_duplicate_dispatch`; `Competing_deployment_coordinators_share_provider_owned_idempotency_record` |
| Different results for one verification operation; either winner | One verifier CLI is held after response archival. Two independently created API clients share the State Machine database and are released in controlled order; theory runs Passed-first and Failed-first. | First accepted evidence is immutable; different second result is conflicting/rejected. | Both orders retain exactly one accepted result receipt, one resolved operation, one winning concern projection, and a refused/conflicting second receipt. Activity identity, generation, digest, histories and replay are checked. | Verification Harness calls 1; deployment mutations 0 | Consistent | **PASS** | `Concurrent_verification_results_preserve_the_first_accepted_evidence` |
| Concurrent exact duplicate and changed verification result | Two independent API clients submit behind a common barrier after a valid accepted result. | Exact retry is Duplicate; changed payload does not replace accepted result. | Duplicate plus rejected/conflicting response; one accepted receipt, unchanged projection and resolved operation; accepted/refused receipts, histories and replay inspected. | Verification Harness calls 1 | Consistent | **PASS** | `Independent_api_clients_competing_verification_results_do_not_overwrite_accepted_evidence` |
| Functional and security concerns complete at the same parent revision | Two independent CLI child processes pause after response archival, then are released; both use the same release revision but distinct concern/work identities. | Both independent concerns may complete without cross-concern evidence reuse. | Separate activities, operations, accepted receipts, concern projections and histories; both results Passed and parent history/replay consistent. | Two verification Harness calls; deployment mutations 0 | Consistent | **PASS** | `Functional_and_security_verification_processes_complete_concurrently_with_independent_evidence` |
| Exact duplicate versus changed-payload approval, either winner | Two independently created authenticated HTTP clients share the approval Gateway and durable registries; release order is controlled, tested with original-first and changed-first payloads. | One valid submission accepted; the changed competing payload conflicts; no last-write-wins. | Winning evidence reference and synthetic principal persist; one immutable approval activity, one conflict record, one accepted receipt, resolved operation, histories and replay inspected. | Deployment mutations 0 | Consistent | **PASS** | `Independent_approval_clients_preserve_exact_duplicate_and_refuse_changed_payload`; `Approval_payload_conflict_preserves_whichever_valid_submission_wins` |
| Amendment/supersession versus old-generation verification, either winner | Independent CLI processes share the original registry and State Machine DB. One ordering supersedes while the verifier is paused after archival; the other accepts verification before amendment. | A late old-generation result cannot progress a superseded generation; an already accepted result stays in old-generation history. | Supersession-first leaves old activity ResponsePersisted and no accepted old result; accepted-result-first retains its old receipt/history. Generation 2 remains Draft with no reused activity; digests, operations, parent/concern histories and both replays are checked. | One verification Harness call; no deployment mutation | Both generations Consistent | **PASS** | `Supersession_fences_late_old_generation_verification_result`; `Accepted_verification_result_remains_in_old_generation_when_supersession_follows` |
| Pre-dispatch cancellation versus deployment reservation | Deployment CLI pauses before reservation; separate cancel CLI completes before deployment is released. Shared original databases. | Terminal cancellation wins; no dispatch or mutation. | Parent is Cancelled; cancellation intent Requested; only a Reserved/OperationRegistered deployment activity exists; no dispatched activity or deployment mutation. History/replay checked. | 0 | Consistent | **PASS** | `Pre_dispatch_cancellation_wins_against_deployment_reservation` |
| Deployment dispatch/result versus post-dispatch cancellation intent | Deployment CLI pauses after remote response; separate cancel CLI records intent; deployment is then released. | Do not manufacture rollback or erase dispatched work; accept only authoritative result. | Cancellation intent remains Requested; one deployment result is accepted; parent remains PostDeploymentVerification, not Cancelled or falsely rolled back. Receipt, resolved operation, history and replay checked. | Exactly 1 | Consistent | **PASS** | `Post_dispatch_cancellation_preserves_late_deployment_result_without_rollback` |
| Late Passed health after accepted disqualifying HealthFailed result | Health CLI pauses after archiving its provider response. An independent API client accepts a Failed health result and HealthFailed parent transition before the CLI is released. | Late Passed result cannot replace accepted Failed evidence or reach Completed. | HealthFailed parent and Failed concern remain; one accepted Failed receipt, no accepted Passed receipt; original late provider response remains unaccepted in the activity ledger. Expected operation, receipts, histories and replay are inspected. | Deployment mutations 1; no new deployment | Consistent | **PASS** | `Late_passed_health_result_cannot_replace_accepted_health_failure` |
| Late Passed health after post-dispatch cancellation intent | Health CLI pauses after response archival; separate cancellation CLI records post-dispatch intent; health then resumes. | Health result may be retained, but cancellation intent must prevent final Completed reconciliation. | Health concern Passed and its result receipt are accepted, while cancellation intent remains Requested and parent stays PostDeploymentVerification. No rollback or Completed transition; histories/replay checked. | Deployment mutations 1 | Consistent | **PASS** | `Late_health_result_after_cancellation_intent_does_not_complete_release` |
| Concurrent readiness attempts | Two CLI processes share workflow state and pause before readiness observation; release order is controlled. | One stable readiness transition; duplicate is idempotent. | One readiness activity, one accepted receipt, resolved expected operation, ReadyForDeployment projection, attempts/history/replay checked. | Deployment mutations 0 | Consistent | **PASS** | `Concurrent_readiness_processes_accept_one_stable_readiness_transition` |
| Concurrent completion attempts | Two health CLI processes share workflow state and pause after health observation acceptance; each has its own synchronization directory. | One Completed transition; exact duplicate cannot add another accepted transition. | One health activity and accepted result receipt, Completed projection, resolved operation, attempts and histories consistent. | Deployment mutations 1 | Consistent | **PASS** | `Concurrent_health_processes_reconcile_to_one_completion_transition` |

## Remaining boundaries

The controlled Harness fake remains appropriate for deterministic fault
injection and is not represented as a durable external provider. The
independent Generic Harness/provider tests cover admitted deployment recovery
and competing dispatch with provider-owned SQLite evidence, not all nine
fault boundaries. No new retry or recovery guarantee was added for a
verification execution whose execution identity is unavailable.

Amendment from `PostDeploymentVerification` is rejected by the current
coordinator's supported-state guard. The late-health acceptance case therefore
uses accepted `HealthFailed` and post-dispatch cancellation intent as the
disqualifying states; it does not claim that a post-deployment amendment or
supersession is supported.

All acceptance conclusions remain restricted to synthetic training scope.
