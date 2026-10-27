# Gate 14 — Milestone 4C adversarial results

Results below describe controlled tests against the actual State Machine API
and Asteria client/registry code unless explicitly marked otherwise. Decision
API responses in these tests are fixture responses; they are not live policy
evaluations. No real Decision API was reachable for this work.

| Adversarial case | Result | Evidence and verification class |
|---|---|---|
| Delayed Generation 1 decision arrives after amendment admission begins | **Passed.** The delayed response is not persisted as Generation 2 evidence; Generation 1 remains `Superseded` and Generation 2 remains `Draft`. | `A_delayed_generation_one_decision_cannot_write_into_or_advance_generation_two`; controlled Decision API response delay and lease-expiry fault injection. |
| Generation 1 expected operation reused against Generation 2 | **Passed.** The State Machine refuses the input and Generation 2 remains `Draft`. | `Stale_generation_one_operation_and_material_digest_are_refused_for_generation_two`; actual API host. |
| Old material digest submitted for Generation 2 | **Passed.** Response binding validation rejects the stale digest before assessment evidence is accepted. | Same test; directly validates the Asteria client's persisted response binding. |
| Late old-generation command/observation after supersession | **Passed.** Generic terminal-state handling refuses advancement; the unresolved old operation remains attached to Generation 1. | `Real_api_supersession_is_terminal_and_each_generation_has_independent_history` in `AsteriaMilestone4BTests`; actual API host. |
| Pending Generation 1 inbound receipt resumed after restart and supersession | **Passed.** The receipt remains `Received` through the process restart, is later refused, and does not change `Superseded`. | `Amendment_activation_recovers_after_a_real_state_machine_process_restart`; actual State Machine child-process restart, SQLite persistence, and development-only input pause. |
| Identical amendment retry preserves allocation and identities | **Passed.** Repeating the exact amendment returns the original Generation 2 and workflow identity. | `Reservation_deduplicates_identity_conflicts_payload_and_suspends_admission` and `Real_api_supersession_is_terminal_and_each_generation_has_independent_history`; registry/API-host tests. |
| Changed-payload retry with the same amendment identity | **Passed.** Conflicting payload reuse is rejected without allocating another generation. | Same Milestone 4B tests; registry/API-host tests. |
| Concurrent amendments against one current base | **Passed.** Exactly one reservation succeeds; no Generation 3 is allocated. | `Competing_amendments_cannot_both_reserve_the_same_base_generation`; SQLite concurrency test. |
| Activation interrupted after durable supersession phase | **Passed.** A controlled SQLite trigger aborts pointer activation. The registry remains fail-closed at Generation 1/`ActivationIncomplete`, Generation 1 stays terminal, and a fresh coordinator activates Generation 2. | `Activation_failure_after_supersession_recovers_without_reopening_generation_one`; controlled persistence fault injection. This is not a literal coordinator-process crash. |
| Multi-generation amendment across process restart | **Passed for State Machine persistence/recovery.** A real State Machine API process restarts with a pending inbound receipt, then again with `OldInstanceSuperseded` committed before pointer activation. | `Amendment_activation_recovers_after_a_real_state_machine_process_restart`; the Asteria coordinator is recreated after the restart. Decision responses are fixtures. |
| Repeated Generation 2 assessment | **Passed.** The accepted work/evaluation is reused, the Decision API handler sees only two total generation requests, and Generation 3 does not exist. | `Two_generations_use_separate_material_evidence_and_terminal_history`; controlled Decision API handler plus actual State Machine API host. |
| Missing policy | **Passed.** The no-policy contract persists `Unresolved`, null evaluation ID, unresolved obligations and `deploymentAuthorised=false`; the workflow reaches only `AssessmentUnresolved`. | `Missing_policy_remains_unresolved_and_historical_replay_keeps_versioned_evidence`; controlled no-policy response plus actual State Machine API host. |
| Historical replay | **Passed for stored fixture evidence.** Replay verifies the decision using the original versioned evidence, and registry response/policy-version records remain byte-for-byte unchanged. | Same test; actual State Machine historical replay endpoint. |
| Accepted work during amendment suspension | **Passed.** Both `ResumeAsync` and `PrepareAsync` refuse admission while the registry is amendment-pending, even if the old work was already accepted. | `Accepted_work_cannot_bypass_amendment_suspension`; actual Asteria registry/coordinator and State Machine API host. |
| Inactive Generation 2 started by untrusted direct input | **Passed.** Untrusted assessment-start input cannot advance the provisioned replacement from `Draft`; expected-operation registration remains behind the existing trusted-ingestion boundary. | `Two_generations_use_separate_material_evidence_and_terminal_history` and Milestone 4B unauthorized supersession registration test; actual API host. |
| Coordinator lease fences a request already in flight across services | **Not claimed.** Leases do not retract a request already dispatched to another service. The test expires the registry lease while a response is delayed and verifies that the response cannot be stored against the replacement's work. | Controlled delay and registry fault injection; no distributed fencing guarantee. |
| Direct generic API caller with trusted-ingestion credential | **Residual risk.** The generic State Machine API has no Asteria-registry pointer check; a holder of the shared trusted key can register operations and submit trusted observations directly. | Source-boundary review and existing generic endpoint behavior; credential isolation is operationally required. |

## Trust boundary and remaining risks

- The Asteria assessment coordinator checks current-generation admission before
  external work and before accepted observations. Accepted work no longer
  bypasses that check when an amendment has suspended registry admission.
- Supersession uses V3's existing observation type and trusted
  expected-operation/observation path. No Asteria-specific runtime route,
  state, event, or branch was added to the generic State Machine API.
- The old generation's `Superseded` state is terminal in V3. Ordinary generic
  commands and observations cannot move it to a nonterminal state. Late input
  receipts remain inspectable and do not resolve Generation 2 work.
- The new instance is initially `Draft` and cannot begin assessment through
  untrusted observation input. The Asteria coordinator's trusted credential
  is still a shared secret, not authenticated user identity or proof of
  evidence origin.
- Registry leases serialize supported coordinators sharing the SQLite file;
  they do not fence in-flight HTTP requests or protect separate database
  copies. Stable request/operation/message identities, durable receipts,
  generation-bound material digests and terminal-state enforcement are the
  safeguards. External actions already dispatched are not canceled.
- The direct generic runtime API remains domain-neutral and can be called
  independently of the Asteria registry. A network boundary and careful
  trusted-key management remain necessary.
- The adversarial scenarios above use fixtures and fault injection; the
  owner-reported live run covers the happy-path two-generation flow only.
  Provider identity validation,
  production credential-management control, completed verification, human
  approval, deployment, health verification or final completion was tested or
  implemented in this milestone.
