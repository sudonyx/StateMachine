# Gate 14 — Milestone 3B test results

## Implemented scope

Milestone 3B implements a durable Asteria-specific release registry and
assessment coordinator, published Decision API request/response binding, a
new Asteria definition V2, expected-operation observation submission, crash
reconciliation, focused tests, and a live cross-service demonstration.
Material amendments, reassessment, verification completion, approval,
deployment, health verification, and completion remain unimplemented.

The targeted Asteria test suite passed after the latest coordinator and test
changes:

| Command | Result |
|---|---|
| `dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --filter "FullyQualifiedName~AsteriaMilestone3BTests"` | Passed: 18, failed: 0, skipped: 0. |

The full solution build and regression test results are recorded after
execution below.

## Targeted coverage

`AsteriaMilestone3BTests` exercises:

- Registry-derived canonical material digests, stable generation-1 release
  identity, rejection of duplicate IDs with changed material, and active lease
  renewal/takeover behavior.
- Request shape against the published contract; workflow-only fields are not
  sent to the Decision API.
- V2's safe outcome mappings and absence of deployment/completion transitions.
- `RequirementsDetermined`, `AdditionalReviewRequired`, `Prohibited`, and
  `Unresolved` result handling through a registered expected operation.
- No-applicable-policy results with null evaluation ID/evidence and no
  fabricated selected policy identity; durable local result reuse.
- Wrong release identity, generation, digest, workflow instance, expected
  operation, and stale parent revision refusal.
- Duplicate and conflicting evaluation requests and observations.
- Decision API response loss after persistence, coordinator restart at the
  submission/operation-registration gap, and recovery after accepted
  observation with a lost acknowledgement.
- Inability to progress to deployment or completion.

The tests use the real State Machine API host with a controlled Decision API
HTTP handler for repeatable contract and failure injection. Actual Decision
API service behavior and cross-process `prepare`/`resume` are separately
documented as live integration evidence.

## Live integration evidence

The real Decision API at `http://localhost:5267` was checked for registered
AST-POL-001 V1 metadata and used for Asteria assessment requests. A temporary
State Machine API using its own SQLite database was run at
`http://localhost:5270`. Separate `prepare` and `resume` CLI processes
recovered a persisted `AdditionalReviewRequired` evaluation, registered the
expected operation at parent revision 1, and reached
`ReviewObligationsIdentified` at revision 2. Additional live releases
verified `Prohibited` and no-applicable-policy `AssessmentUnresolved`; the
no-policy result retained a null evaluation ID and empty selected-version
list.

The separate-process scenario verifies recovery across process lifetime but
does not simulate a forced termination at every individual network boundary.
The focused fault-injection tests cover the documented lost-response and
acknowledgement windows. The Decision API's no-policy response is specifically
not persisted or idempotency-reserved by that service; the coordinator records
the received response locally and never silently re-evaluates durable work.
After the lease-renewal change, a final separate-process `prepare`/`resume`
run against both live APIs again reached registry status `Accepted`, with
evaluation ID and selected AST-POL-001 V1 identity recorded.

## Full solution validation

| Command | Result |
|---|---|
| `dotnet build DurableStateMachine.slnx` | Passed: 0 warnings, 0 errors. |
| `dotnet test DurableStateMachine.slnx` | Passed: 125, failed: 0, skipped: 0. |

## Boundaries and known limitations

No generic evaluation or persistence semantics, Generic Harness runtime,
generic rule evaluator, Orion/Meridian business definitions, or Asteria V1
were changed. The requested Milestone 2 documentation and Milestone 3A audit
were not in this checkout; see the design report for the source and
assumption record. The coordinator relies on a shared SQLite registry for
lease concurrency and cannot provide a distributed transaction. The
State Machine trusted-ingestion key is a configured local trust boundary, not
production authentication. Policy outcomes identify obligations or
prohibitions; they are not proof of completed checks, approvals, deployment
permission, or release completion.

See [design](./gate14-milestone3b-design.md) for architecture and
[recovery](./gate14-milestone3b-recovery.md) for crash-window guarantees.
