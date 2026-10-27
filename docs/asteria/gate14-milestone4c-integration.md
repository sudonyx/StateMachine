# Gate 14 — Milestone 4C integration procedure

## Verification status

**The live end-to-end demonstration was run by the repository owner and
passed** (owner-reported): both generations were assessed by the real Decision
API, Generation 2 obligations were correct, observations were accepted and
Generation 1 became terminally Superseded. The repository contains no Decision
API implementation, so the authoring session could not run it; the automated
suite does not claim that a mocked Decision API response is a live policy
evaluation.

The controlled integration tests use the actual State Machine API, V3
definition, Asteria registry and coordinators, but return deterministic
Decision API contract responses from an in-memory HTTP handler. A separate
test starts the actual State Machine API assembly as a child process, stops
and restarts it against the same SQLite database, and resumes the amendment
protocol. That process restart proves State Machine persistence/recovery in the
test; it does not prove a live Decision API call or that every crash window was
exercised.

## Live procedure

Run this procedure only when both services are reachable and use isolated,
disposable databases. The script does not reset or delete either database.
Use a new registry path and a new State Machine database for each run because
the release and amendment identifiers are deliberately stable:

- Release: `AST-GATE14-4C-001`
- Amendment: `AST-GATE14-4C-AMEND-001`
- Generation 1: Standard, Small, no authentication or customer-data impact.
- Generation 2: Emergency, Large, authentication and customer-data changes.

Start the real State Machine API and the actual Asteria Decision API according
to their service-specific procedures. Configure:

```powershell
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5000"
$env:ASTERIA_DECISION_API_URL = "http://localhost:5267"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "<local trusted-ingestion key>"
$env:ASTERIA_RELEASE_REGISTRY_PATH = "$env:TEMP\asteria-gate14-4c-registry-new.db"
```

Choose a registry path that does not already exist. The State Machine API must
also use a fresh database for these fixed IDs. Then run from the repository
root:

```powershell
.\scripts\gate14-milestone4c-demo.ps1 -OutputDirectory "$env:TEMP\asteria-gate14-4c-run"
```

The script calls the real Asteria client through four separate `dotnet run`
processes: assess Generation 1, amend to Generation 2, resume Generation 2,
and resume Generation 2 again. It verifies the old terminal state, new
generation's independent material digest and four required obligation
projections, and absence of Generation 3. It writes the inspectable export to
`asteria-gate14-milestone4c-evidence.json` under the output directory.

The script intentionally fails if the actual service's policy result does not
produce the specified four Generation 2 obligations. A failure, unavailable
policy or no-applicable-policy response is not converted into a successful
demonstration.

### Export field semantics

Each generation entry separates historical binding from current state:

- `assessmentBinding.boundParentRevision` / `boundParentState`: the immutable
  revision (and the accepted-history state at that revision) at which the
  expected operation was registered and the assessment bound. For both
  generations this is revision 1, `UnderAssessment`. It is never updated.
- `currentStateMachineProjection`: the live instance read at export time
  (`currentState`, `revision`, `facts`). It advances after binding, e.g.
  Generation 1 `Superseded`/3 and Generation 2 `ReviewObligationsIdentified`/2
  in the owner's live run.
- `acceptedTransitionHistory` links the two. `expectedOperation.expectedRevision`
  equals `boundParentRevision`.

## Evidence export

The Asteria client supports read-only export after a release is created:

```powershell
dotnet run --project clients\DurableStateMachine.AsteriaClient -- `
  export-evidence <release-id> <output.json>
```

The export uses the registry's persisted release/generation snapshots and
original Decision API responses, not reconstructed or newly generated policy
evidence. It includes the current pointer and registry state, amendment IDs,
phases and durable supersession attempts, each generation's material/digest
and workflow/definition identity, assessment work, evaluation ID, selected
policy versions, original response/evidence, the independent four-obligation
projection, parent state/revision, expected-operation resolution, current
instance projection, accepted transition history, and inbound receipts. It
includes all receipts for each instance so duplicate, stale, refused and
unresolved inputs remain inspectable.

The export is a diagnostic snapshot; a missing State Machine instance is
represented as `null` with empty State Machine evidence collections. This is
not evidence that work was accepted. The persisted work status and registry
phase are included so incomplete generations remain distinguishable.

## Controlled process-restart demonstration

`AsteriaMilestone4CTests.Amendment_activation_recovers_after_a_real_state_machine_process_restart`
uses the built State Machine API assembly and a real SQLite-backed child
process. It:

1. Persists and inspects a Generation 1 synthetic assessment.
2. Uses a development-only pause after durable intake to leave a Generation 1
   inbound receipt in `Received`.
3. Stops and restarts the State Machine API process, confirming the receipt is
   still pending.
4. Provisions Generation 2, confirms Generation 1 is terminally
   `Superseded`, and commits amendment phase `OldInstanceSuperseded` while the
   pointer remains on Generation 1 and the registry is
   `ActivationIncomplete`.
5. Inspects the committed registry phase, instance, expected operation and
   inbound receipts, then stops and restarts the API process a second time.
6. Runs a fresh amendment coordinator, verifies the old state and receipt,
   activates Generation 2, and attempts to resume the pending old-generation
   receipt. The receipt is refused and Generation 1 remains `Superseded`.
7. Assesses Generation 2 through the test Decision API handler and exports the
   stored test evidence through the client executable.

The test uses the same fixed generation material profile and the actual State
Machine API code, but its decision responses are controlled fixtures. The
separate activation-failure test injects a SQLite trigger failure before
pointer activation and verifies that a fresh coordinator recovers from
`OldInstanceSuperseded`; this is controlled fault injection, not a process
crash. These demonstrations do not imply that all possible crash windows are
covered.

## Boundaries

- V1, V2 and V3 definitions are unchanged. V3 still has no deployment or
  completion transitions.
- No completed verification, human approval, deployment, health verification
  or final completion is added.
- The generic runtime, Decision API implementation, rule engine, persistence
  semantics and Generic Harness are unchanged.
- The trusted-ingestion key is a shared-secret boundary. It is not user
  authentication, source attestation or role-based authorization. Direct
  callers with the key can use generic trusted-ingestion routes; secure the
  API and key accordingly.
- Coordinator leases coordinate supported registry users but do not fence
  requests already in flight across the Decision API and State Machine
  services. Stable identities, current-generation checks, durable receipts and
  terminal-state refusal are the recovery controls.

See [adversarial results](./gate14-milestone4c-adversarial-results.md) and
[test results](./gate14-milestone4c-test-results.md).
