# Gate 10 test results

## Build and automated tests

Validated on .NET SDK `10.0.100`:

- Baseline before this completion work: solution build passed and 77 tests passed.
- Updated `dotnet build DurableStateMachine.slnx --no-restore`: passed, 0 warnings and 0 errors.
- Updated `dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj`: passed, 83 tests, 0 failures, 0 skipped.
- Focused `RecoveryReplayTests`: passed, 12 tests.
- Workspace diagnostics for changed C# files: no errors.
- `git diff --check`: passed.

The new SQLite-backed intake test demonstrates that a failure before intake commit leaves no receipt, while a failure after intake commit leaves a `Received` record visible to recovery assessment. It recreates the API host over the same database, resumes the record using its receipt ID and original envelope, and verifies one accepted attempt/history row. The same test injects interruptions during guard evaluation, before final persistence, and immediately before projection persistence; each finalization rollback leaves the intake resumable. Re-resuming a completed receipt returns conflict rather than creating a second transition. Separate tests construct two independent API hosts sharing SQLite and race continuation of the same intake, and confirm that stale commands preserve their original revision and trusted observations reuse only their persisted application-established authority.

Process-level tests start the API as child operating-system processes and terminate only those owned process IDs at three coordinated points: after intake commit/before evaluation, after effect-intent commit/before provider dispatch, and immediately before final local persistence. Restarting against the same disposable SQLite file verifies receipt resumption, preservation of a pending external effect without provider invocation, and rollback to the previously committed projection/history at the finalization boundary.

The existing replay integration test now injects a counting HTTP handler for the Generic Harness and asserts zero calls across historical reconstruction, decision verification and recovery assessment. It compares instance, attempt, receipt, expected-operation, timer, effect and transition-history data before and after those read-only operations.

Existing tests continue to cover projection corruption detection, decision evidence, expected-operation and timer assessment, Gate 5 authority, Gate 6/7 effect boundaries, Gate 8 timers, Gate 9 arbitration and API test-host restart. No historical data is synthesized or rewritten.

## Gate 9 adversarial corpus regression

The checked-in seed `20261019` corpus validated reproducibly with 5,000 deliveries, 300 instances, 150 additional exact duplicates, 100 additional semantic duplicates, 450 late deliveries, 75 race pairs and 50 malformed observations.

The updated runtime completed all 5,000 deliveries against a temporary persistent SQLite database using one responsive API host, `concurrent` mode and concurrency 8. Final run results: 961 accepted transitions, 150 duplicates, 50 malformed inputs, 3,008 rejected inputs, 831 stale inputs, zero failed invariants and zero transport failures; exit code 0.

The prior recorded run had 974 accepted, 3,149 rejected and 677 stale inputs. The new two-transaction intake/evaluation boundary changes scheduling around concurrent deliveries, so the outcome mix changed (13 fewer accepted; more inputs observed as stale instead of rejected). Repeated concurrent runs also showed a small scheduler-dependent shift between `Rejected` and `Stale` (7 records) while the accepted, duplicate and malformed counts remained unchanged. The corpus runner reported no invariant failures, duplicate accepted-history revisions, timer uniqueness failures or effect-operation uniqueness failures. This was a one-host run, not a new multi-host corpus run.

## Crash matrix status

| Scenario | Status |
|---|---|
| Before durable intake | Deterministic fault-injection verified; no receipt persisted. |
| After intake, before evaluation | Deterministic fault-injection and operating-system process-kill verified; explicit resume accepts once in a fresh process. |
| During guard evaluation | Deterministic fault-injection verified; transaction rollback preserves intake; resume uses stored envelope. No new Meridian fetch. |
| External work pending | Operating-system process-kill verified after durable intent commit and before provider dispatch; recovery reports it pending and does not dispatch. |
| External result before local acceptance | Partially covered by existing Gate 6/7 exception-injected tests. No response-body staging is added; a response retained only in process memory remains unavailable. |
| During final transition/projection persistence | Operating-system process-kill verified immediately before final persistence; deterministic pre-save interruption and existing SQLite-trigger rollback are also verified. No OS kill during an individual SQL statement. |

## Remaining limitations

- Process-kill coverage does not include the boundary after an external result is observed but before acceptance, or termination during an individual finalization SQL statement.
- The new tests do not independently race recovery against a normal competing command, cancellation, or timer expiry. Existing SQLite immediate transactions, conditional revision updates, unique accepted-history revision constraints, and Gate 9 concurrency tests remain in effect.
- External response bodies are not staged between provider completion and workflow observation acceptance. Lost in-memory results remain unresolved and are not reconstructed from harness status.
- No Gate 10 multi-host corpus rerun was performed; only the full concurrent one-host corpus run was repeated.
- Legacy attempts without intact Gate 4 evidence remain state-reconstructable but not decision-verifiable. No intake row is invented for legacy input.
- Automatic processing of all unresolved inputs, timer polling, provider retries, historical side-effect replay, projection repair and Gate 11 definition migration are not implemented.

Accordingly, durable pre-evaluation intake, explicit safe continuation, representative process-kill verification, deterministic transaction-failure coverage and executable read-only non-invocation assertions are implemented. The strict Gate 10 pass condition is **not fully met**: process-kill after an external result, provider-result staging, recovery races against competing commands/cancellation/timer expiry, and a multi-host corpus run remain outstanding.
