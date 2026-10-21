# Gate 12 validation results

## Build and tests

Validated with .NET SDK `10.0.100`:

- `dotnet build DurableStateMachine.slnx`: passed, 0 warnings and 0 errors.
- Focused definition and Gate 12 integration tests: passed, 20 tests, 0 failures.
- `dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --no-build`:
  passed, 100 tests, 0 failures, 0 skipped.
- `git diff --check`: passed.

The Gate 12 tests cover language 1.3 declaration validation, independent
document and asset progress, refusal and acceptance of parent coordination,
duplicate observations, parent and concern replay, decision verification from
stored concern-state evidence, persistence across API restart, atomic
cancellation and late-observation refusal, and concurrent inputs through two
API hosts.

## Fixed Gate 9 corpus regression

The checked-in corpus was validated with seed `20261019`: 5,000 records, 300
applications, 150 additional exact duplicates, 100 additional semantic
duplicates, 450 late deliveries, 75 race pairs and 50 malformed observations
(1.00%). Regeneration was reproducible.

The API runner submitted the same 5,000 records in `concurrent` mode with
concurrency 8 to one API host. It returned exit code 0 with no failed
invariants or transport failures:

| Outcome | Count |
|---|---:|
| Accepted | 973 |
| Duplicate | 150 |
| Malformed | 50 |
| Rejected | 3,129 |
| Stale | 698 |

The run took 27,533 ms and used corpus fingerprint
`2C1A29D7B149E00B0E03C2AF6CB226D0470D8A37D99A98D67DE61CC7CA14C1F8`.
Outcome totals are scheduling-dependent for competing deliveries; the
runner's history, revision, duplicate and uniqueness invariants are the
correctness criteria. The corpus uses the unchanged Orion 4.0 definition; it
is a regression check, not a Gate 12 concern scenario. The Gate 12 integration
suite separately tests two-host concern/cancellation races.

## Scope and limitations

- Orion 6.0 is a new immutable language 1.3 definition. Orion 4.0 and 5.0 were
  not changed. No migration to 6.0 is registered or automatic.
- Concern revisions are independent from the parent revision. Concern writes
  still share the instance's SQLite transaction and failure boundary.
- The tested multi-host behavior uses two API hosts sharing SQLite; other
  database providers or network filesystems are not covered.
- Concerns have no nested dimensions, independent timers, arbitrary
  multi-concern transitions or inter-instance transactions.
- Cancellation does not compensate for or prove the outcome of external work.
  The trusted ingestion key remains a training boundary rather than production
  IAM.
- Replay validates the bound definition, stored histories, attempts and
  evidence consistency; it cannot establish that an external provider's
  original evidence was truthful.
