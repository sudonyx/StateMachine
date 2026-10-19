# Gate 9 validation results

Validation was performed against the checked-in corpus generated with seed `20261019`.

## Build and tests

- `dotnet build DurableStateMachine.slnx --no-restore`: passed, 0 warnings and 0 errors.
- `dotnet test tests\DurableStateMachine.Tests\DurableStateMachine.Tests.csproj --no-build`: passed, 71 tests, 0 failures, 0 skipped.
- Corpus validation: passed; 5,000 records, 300 applications, 150 additional exact duplicate deliveries, 100 additional semantic duplicates, 450 late deliveries, 75 race pairs, and 50 malformed observations (1.00%). Byte-for-byte regeneration was reproducible.

## API corpus runs

Both executions used a fresh run ID, the isolated local SQLite database, and all 5,000 deliveries. Each returned exit code 0 with no failed invariants or transport/setup failures.

| Mode | API hosts | Accepted transitions | Duplicate | Malformed | Rejected | Stale | Duration |
|---|---:|---:|---:|---:|---:|---:|---:|
| `concurrent` | 1 | 955 | 150 | 50 | 2,937 | 908 | 22.6 s |
| `multi-host` | 2 | 929 | 225 | 50 | 2,657 | 1,214 | 25.0 s |

The multi-host run used two independent API processes sharing the same SQLite database file, with declared race pairs sent concurrently. Different outcome totals are expected because competing deliveries can be arbitrated in either valid order; the runner verified the accepted history/revision, duplicate, timer and effect-operation invariants for each run.

The corpus runner does not invoke live external providers, replay historical effects, or process overdue timers. Those behaviours remain covered by the existing Gate 7/8 integration tests. These results establish the tested SQLite same-file configuration only; they do not establish locking guarantees for network filesystems or other database providers.
