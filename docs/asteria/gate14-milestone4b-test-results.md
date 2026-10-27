# Gate 14 — Milestone 4B Test Results

## Final validation

Validation was run after the Milestone 4B implementation and documentation
updates:

| Check | Result |
|---|---|
| `dotnet build DurableStateMachine.slnx --no-restore` | Passed; 0 warnings, 0 errors |
| Asteria Milestone 3B and 4B focused tests | Passed; 28/28 |
| `dotnet test DurableStateMachine.slnx --no-build --no-restore` | Passed; 135/135 |

Focused command:

```powershell
dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~AsteriaMilestone3BTests|FullyQualifiedName~AsteriaMilestone4BTests'
```

Full regression command:

```powershell
dotnet test DurableStateMachine.slnx --no-build --no-restore
```

## Coverage

The focused Asteria coverage exercises legacy generation-1 migration,
generation snapshot immutability and allocation, idempotent amendment retry
and identity conflict, competing and stale amendments, admission fencing,
real-API-host provisioning and supersession, terminal direct-command and
late-observation rejection, recovery after lost create/supersession
acknowledgements, pointer-activation recovery, a concurrent old-generation
observation, preservation of historical assessment data, and refusal to amend
an unsupported V2 instance.

V3 definition validation and the test suite confirm that the definition
contains no deployment or completion path. Generic runtime, persistence,
rule-engine, and Generic Harness source files were not modified.

## Limitations

- Migrated V2 instances retain their historical definition and are deliberately
  not amendable. V2 has no supersession transition, and neither its immutable
  definition nor generic definition-version semantics are changed to retrofit
  one.
- Supersession uses the existing trusted-ingestion API. Its shared credential
  is an access boundary and must be restricted to the Asteria coordinator;
  the generic runtime does not consult the Asteria registry.
- Terminal supersession prevents future workflow transitions but does not
  reverse or cancel external operations that were already dispatched. Their
  evidence remains historical.
- The tests validate recovery at the protocol boundaries using the real State
  Machine API host. They do not establish deployment, completed verification,
  approval authentication, or post-deployment amendment behavior; these remain
  outside this milestone.
