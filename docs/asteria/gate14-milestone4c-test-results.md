# Gate 14 — Milestone 4C test results

## Validation executed

| Check | Result |
|---|---|
| `dotnet build DurableStateMachine.slnx --no-restore` | Passed; 0 warnings, 0 errors. |
| Asteria Milestone 3B/4B/4C focused tests | Passed; 35/35. |
| `dotnet test DurableStateMachine.slnx --no-build --no-restore` | Passed; 142/142. |
| PowerShell parser validation of `scripts/gate14-milestone4c-demo.ps1` | Passed. |

Focused Asteria command:

```powershell
dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj `
  --no-restore `
  --filter 'FullyQualifiedName~AsteriaMilestone3BTests|FullyQualifiedName~AsteriaMilestone4BTests|FullyQualifiedName~AsteriaMilestone4CTests'
```

Full regression command:

```powershell
dotnet test DurableStateMachine.slnx --no-build --no-restore
```

The Milestone 4C suite has seven tests covering separate generation snapshots
and policy evidence, delayed old-generation decisions, stale operation/digest
refusal, unavailable policy and historical replay, admission suspension,
activation fault recovery, evidence export, and a real State Machine API
process restart with a pending old inbound receipt. Policy responses in
these tests come from controlled HTTP handlers and are explicitly not live
Decision API verification.

The process-restart test restarts the actual State Machine API assembly twice
using the same SQLite database. It inspects the pending `Received` receipt,
the `OldInstanceSuperseded` registry phase, the still-current old generation,
expected operations and terminal instance state before recovery. The test
then confirms that the resumed stale receipt is refused and Generation 2 is
assessed with its own fixture response. A separate SQLite trigger injects an
activation failure after supersession and proves recovery without reopening
Generation 1. This is controlled fault injection rather than a literal
coordinator process crash.

## Live verification status

**Passed (reported by the repository owner).** The live demonstration was run
by the owner against the real State Machine and Decision API; it was not run
inside the authoring session, which had no Decision API available. The owner
reports that the exported evidence confirms both real policy assessments,
correct Generation 2 obligations, accepted observations and terminal
Generation 1 supersession. The exported evidence file is not reproduced here,
so no live IDs or policy versions are quoted in this document.

- Tests in this suite still use fixture Decision API responses; they are not
  live evidence.
- Export field-semantics finding: the original export reported top-level
  `parentState`/`parentRevision` (Generation 1: `UnderAssessment`/1,
  Generation 2: `UnderAssessment`/1) beside `instance.currentState`/`revision`
  (Generation 1: `Superseded`/3, Generation 2: `ReviewObligationsIdentified`/2).
  This was not an inconsistency. `parentRevision` is the write-once
  `AsteriaAssessmentWorks.ParentRevision` recorded when the expected operation
  was bound; `parentState` is the accepted-history state at that revision.
  The fields are now `assessmentBinding.boundParentRevision/boundParentState`
  (historical) and `currentStateMachineProjection` (live State Machine read at
  export time). Stored records and generic runtime were not changed.
- The export-consistency assertions (binding revision equals persisted work
  and expected-operation revision, bound state exists in history at that
  revision, projection revision equals the latest history revision and exceeds
  the binding revision) are in
  `Amendment_activation_recovers_after_a_real_state_machine_process_restart`.

## Scope and limits

- The only runtime-behavior change is in Asteria coordinator admission:
  already-accepted assessment work now still has to pass current-generation
  and amendment-suspension checks before returning. Generic runtime, Decision
  API implementation, rule-engine, persistence and Generic Harness source
  were not changed.
- Asteria definitions V1, V2 and V3 were not modified by this milestone.
- V3 remains pre-deployment. No completed verification, human approval,
  deployment, health verification or final completion was implemented.
- Trusted-ingestion credentials remain a shared-secret boundary. Generic
  runtime endpoints do not consult the Asteria registry; direct access and
  credential management remain operational risks.
- Coordinator leases do not fence requests already in flight across
  services. The tests exercise stale response refusal but do not claim a
  distributed transaction or universal crash-window coverage.

See the [integration procedure](./gate14-milestone4c-integration.md) and
[adversarial results](./gate14-milestone4c-adversarial-results.md).
