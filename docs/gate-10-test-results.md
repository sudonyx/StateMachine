# Gate 10 test results

## Build and automated tests

Validated on .NET SDK `10.0.100`:

- Baseline before this completion work: 83 tests passed.
- Final `dotnet build DurableStateMachine.slnx --no-restore`: passed, 0 warnings and 0 errors.
- Final `dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --no-build`: passed, 92 tests, 0 failures, 0 skipped.
- Focused tests cover staged-result identity/restart, stale results after cancellation, staged-result fault boundaries, actual child-process termination after staging commit, and two-host recovery races.
- `git diff --check`: passed.

SQLite-backed intake tests show that failures before intake commit leave no receipt, while failures after commit leave a `Received` record available for recovery. Restarted hosts continue from the stored envelope and preserve its original revision and trusted authority. Staged-result tests verify stable identity for equivalent duplicates, refusal of conflicting identity reuse, exact payload/evidence retention, no extra Harness execution during continuation, idempotent repeated resume, and refusal to apply a result made stale by cancellation. Fault injection covers before/during staging persistence, before evaluation, during finalization and after the staged-result commit.

Process-level tests terminate only their owned API child processes at coordinated boundaries, including after a verified Harness result has been durably staged. Restarting against the same disposable SQLite file resumes the staged result through Gate 5 without another Harness execution and produces exactly one accepted transition. Other child-process tests verify intake resumption, preservation of pending external work without provider invocation, and rollback to the previously committed projection/history at finalization. The Harness/provider behavior in these tests is simulated by a controlled local HTTP server; they do not demonstrate recovery from a live external provider.

Two independent API hosts sharing SQLite race staged-result continuation against a competing normal command, cancellation and due timer processing, checking both relevant commit orders. Read-only replay and recovery tests assert no calls to the Generic Harness and compare local records before and after inspection. Existing tests continue to cover projection corruption detection, decision evidence, expected-operation and timer assessment, Gate 5 authority, Gate 6/7 effect boundaries, Gate 8 timers, Gate 9 arbitration and API host restart. No historical data is synthesized or rewritten.

## Gate 9 adversarial corpus regression

The fixed corpus validated reproducibly with seed `20261019`, 5,000 deliveries and 300 applications, including 150 additional exact duplicates, 100 semantic duplicates, 450 late deliveries, 75 race pairs and 50 malformed observations.

A final run used two responsive API hosts on distinct ports sharing one temporary SQLite database, `multi-host` mode and concurrency 8. The runner reported 5,000 deliveries: 933 accepted transitions, 150 duplicates, 50 malformed inputs, 2,713 rejected inputs and 1,154 stale inputs. It reported **zero failed invariants and zero transport failures**, with exit code 0; duration was 34,254 ms. The report identified two hosts and corpus fingerprint `2C1A29D7B149E00B0E03C2AF6CB226D0470D8A37D99A98D67DE61CC7CA14C1F8`.

The corpus runner was corrected to avoid submitting the second member of an already concurrently submitted race pair a second time in `multi-host` mode. This run therefore accounts for exactly the 5,000 generated deliveries. Outcome mix remains scheduling-dependent for competing deliveries, particularly the `Rejected` versus `Stale` split; integrity and transport failures, rather than identical per-category counts across schedules, are the multi-host correctness criteria.

## Crash matrix status

| Scenario | Status |
|---|---|
| Before durable intake | Deterministic fault-injection verified; no receipt persisted. |
| After intake, before evaluation | Deterministic fault-injection and operating-system process-kill verified; explicit resume accepts once in a fresh process. |
| During guard evaluation | Deterministic fault-injection verified; transaction rollback preserves intake; resume uses stored envelope. No new Meridian fetch. |
| External work pending | Operating-system process-kill verified after durable intent commit and before provider dispatch; recovery reports it pending and does not dispatch. |
| Verified external result before local acceptance | Durable staging and Gate 5 continuation are implemented; process-kill after the staging commit and deterministic staging/finalization faults are verified. A response lost before the staging commit is unavailable. |
| During final transition/projection persistence | Operating-system process-kill verified immediately before final persistence; deterministic pre-save interruption and existing SQLite-trigger rollback are also verified. No OS kill during an individual SQL statement. |

## Remaining limitations

- Process-kill coverage after an external result is verified only after the local staging commit. It does not cover a crash between remote provider completion and that commit, or termination during an individual SQL statement.
- Two-host tests cover staged recovery versus a competing command, cancellation and timer expiry. They do not claim guarantees for multiple independent database servers or network-filesystem SQLite.
- An external response is recoverable only after the trusted adapter commits it locally. Lost in-memory responses before staging remain unresolved; no status lookup or automatic provider retry is inferred.
- Legacy attempts without intact Gate 4 evidence remain state-reconstructable but not decision-verifiable. No intake row is invented for legacy input.
- At the Gate 10 completion point, automatic processing of all unresolved inputs, timer polling, provider retries, historical side-effect replay, projection repair and Gate 11 definition migration were not implemented. Gate 11 subsequently adds only the bounded migration contract documented in [gate-11-design.md](./gate-11-design.md); the other listed limitations remain.

Accordingly, durable pre-evaluation intake, verified-result staging, explicit safe continuation, actual process-termination verification, two-host recovery arbitration, deterministic transaction-failure coverage, side-effect-free inspection assertions and the multi-host corpus regression are implemented. The response-loss interval before Stage A's durable commit, kill during an individual SQL statement, automatic provider retries and multi-server/non-SQLite coordination remain outside the verified guarantees.
