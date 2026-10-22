# Gate 13 test results

## Build and regression suites

Validation was run against the current State Machine and Decision API sources.
Build output was isolated from the API processes already running on the default
development ports.

| Repository | Validation | Result |
|---|---|---|
| State Machine | `dotnet build DurableStateMachine.slnx` | Passed; 0 warnings, 0 errors |
| State Machine | `dotnet test DurableStateMachine.slnx` | 106 passed, 0 failed |
| Decision API | `dotnet build Meridian.sln` | Passed; 0 warnings, 0 errors |
| Decision API | `dotnet test Meridian.sln` | 304 passed, 0 failed (46 RuleEngine + 258 Meridian tests) |
| Decision API | Orion-focused filter `FullyQualifiedName~OrionEligibilityDecisionTests` | 9 passed, 0 failed |

The Orion-focused suite includes policy outcomes, required-fact missing and
unavailable handling, idempotency conflict/reuse, historical replay, and a
regression assertion that the Orion source is not discoverable from Meridian's
generic policy store.

## Fixed Gate 9 corpus

The checked-in seed-20261019 corpus validated as reproducible: 5,000
deliveries, 300 applications, 150 additional exact duplicates, 100 additional
semantic duplicates, 450 late deliveries, 75 race pairs, and 50 malformed
payloads (1%). The corpus execution used a fresh State Machine API process on
port 5002 and a dedicated SQLite database, separate from the Gate 13 live
database.

| Measure | Result |
|---|---:|
| Instances / deliveries | 300 / 5,000 |
| Accepted transitions | 970 |
| Accepted / duplicate / malformed / rejected / stale outcomes | 970 / 150 / 50 / 3,088 / 742 |
| Failed invariants | 0 |
| Transport failures | 0 |
| Mode / concurrency | Concurrent / 8 |

The machine-readable run report was saved outside the repository in the
session's evidence directory. The corpus run used one API host; it is not a
multi-host concurrency claim.

## Live Gate 13 HTTP composition

The final runner used a freshly built State Machine API on port 5003 and a
freshly built Orion Decision API on port 5269, with the existing Generic
Harness on port 5247 and synthetic provider hosts on ports 5261–5263. State
Machine, Orion decision-history, and Gate 9 corpus data used separate SQLite or
history paths. The existing default services were not stopped or overwritten.

| Check | Live result |
|---|---|
| Policy isolation | Orion catalog returned exactly `ORION_EQUIPMENT_LEASE_ELIGIBILITY`; Meridian `/policy-sources` did not contain that source |
| Definition | Orion `6.1` registered and used by the instance |
| Decision binding | `Eligible`, `Present`; expected operation, workflow revision and definition version matched |
| Decision replay | Matched stored outcome; no differences |
| Workflow replay | `Consistent` |
| Workflow | `AwaitingOfferAcceptance`; 5 accepted transitions, 8 attempts and 8 inbound receipts |
| Harness-backed effects | Reservation, offer-document and notification each returned `ObservationAccepted` |
| Remote Harness evidence | Retrieved for all 3 effects (11 evidence records each); replay-safety inspection did not call a provider |

The evidence-chain JSON was written outside the repository. This live run
verifies the successful integration path only; it does not simulate outages or
fault injection at the independently hosted services.

## Required cross-system failure scenarios

Failure behavior is tested with controlled HTTP handlers, isolated SQLite
hosts, and restart/fault-injection boundaries. These are deterministic tests,
not claims of live service outages.

| Required scenario | Coverage |
|---|---|
| Decision result unavailable | `Gate13DecisionBindingTests.Unresolved_decision_and_unavailable_decision_service_leave_assessment_unapproved` leaves the expected operation unresolved after a controlled HTTP failure, then accepts a later correlated result |
| Decision unresolved for missing required facts | `OrionEligibilityDecisionTests.MissingAndUnavailableFactsRemainUnresolved` and the unresolved branch in `Gate13DecisionBindingTests.Unresolved_decision_and_unavailable_decision_service_leave_assessment_unapproved` preserve missing/unavailable state without approval |
| Harness refuses unsupported capability/operation | `Gate7HarnessTests.Unsupported_imported_operation_is_sent_to_the_harness_and_remains_rejected` |
| Harness reports unknown/lost outcome for a mutating effect | `Gate7HarnessTests.Lost_execution_response_remains_unknown_and_resume_does_not_submit_a_second_execution` and `Gate7HarnessTests.Harness_accepted_response_lost_before_local_persistence_stays_unresolved_after_restart` |
| State Machine restarts while waiting for Harness result | `Gate7HarnessTests.Pending_intent_survives_api_host_restart_and_is_resumed_with_its_persisted_binding` and `RecoveryReplayTests.Operating_system_process_termination_preserves_a_pending_external_effect` |
| Duplicate Harness result | `Gate13DecisionBindingTests.Duplicate_harness_reservation_result_advances_its_expected_concern_once` |
| Stale decision after cancellation | `Gate13DecisionBindingTests.Decision_received_after_cancellation_is_preserved_as_a_refusal` |
| Cancellation while external execution is pending | `Gate7HarnessTests.Cancellation_during_harness_execution_preserves_and_refuses_the_late_result` |

## Limitations

- The external decision outage and Harness faults above use controlled test
  doubles. Only the success path was run across the independently hosted local
  services.
- The corpus run used one API host and the existing fixed corpus; it does not
  establish multi-host behavior for this Gate 13 deployment.
- The live document-verification observation is synthetic and submitted
  directly by the demo client. The three mutating capabilities—reservation,
  offer document, and notification—are Harness-backed.
- The local trusted-ingestion key, synthetic providers, and filesystem/SQLite
  data paths are training controls, not production security or deployment
  guarantees.
