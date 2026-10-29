# Gate 15 supplemental measurements

## Scope and preservation

These are supplementary runs against fresh SQLite databases. The canonical
10,000-instance/100,000-delivery corpus, its manifest, and the original
performance results were not regenerated or overwritten. Each scenario used
the same seed, corpus, definition, and concurrency 4, with a unique run ID,
database, checkpoint, and report. The API enabled evaluator timing only for
Development with test controls and a temporary trusted-ingestion key.
These are single sequential local runs with no warm-up or repeated-run
variance measurement, so they are descriptive measurements rather than
capacity claims.

The scenario filters partitioned the canonical corpus into 7,000 ordinary,
1,500 adversarial, and 1,500 long-history instances. Their delivery counts
sum to the canonical 100,000 records. All three runs had zero HTTP or
transport failures.

## Profile performance

| Scenario | Instances | Deliveries | Accepted | Duplicate | Rejected | Stale | Instance creation | Delivery time | Deliveries/s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Ordinary | 7,000 | 69,399 | 9,500 | 1,000 | 38,174 | 20,725 | 16.23 s | 379.93 s | 182.66 |
| Adversarial | 1,500 | 16,001 | 1,500 | 1,000 | 13,001 | 500 | 3.81 s | 90.47 s | 176.86 |
| Long-history | 1,500 | 14,600 | 1,875 | 0 | 9,544 | 3,181 | 4.54 s | 80.91 s | 180.45 |
| **Partition total** | **10,000** | **100,000** | **12,875** | **2,000** | **60,719** | **24,406** | — | — | — |

The partition totals exactly match the canonical run's durable outcome
counts. The rates are per-profile measurements; the fresh mixed run below
provides a separate combined-throughput measurement.

A separate fresh-database `all` run exercised the full mixed corpus in one
timed execution: 10,000 instances, 100,000 deliveries, 19.52 s for instance
creation, 549.87 s delivery time, and 181.86 deliveries/s at concurrency 4.
It recorded 12,875 Accepted, 2,000 Duplicate, 60,719 Rejected, and 24,406
Stale outcomes, with zero HTTP or transport failures. API readiness was
1,280.70 ms.

## Evaluator-only latency

Evaluator timings are measured with a monotonic clock around evaluator calls,
not inferred from HTTP latency. Percentiles below are approximate histogram
bucket upper bounds; the bounded histogram has 19 buckets with upper bounds
through 1,000 ms plus an overflow bucket.

| Scenario | Outcome | Evaluations | Mean | p50 upper bound | p95 upper bound | p99 upper bound |
|---|---|---:|---:|---:|---:|---:|
| Ordinary | Accepted | 9,500 | 0.007664 ms | 0.01 ms | 0.02 ms | 0.05 ms |
| Ordinary | Rejected | 38,174 | 0.006245 ms | 0.01 ms | 0.01 ms | 0.02 ms |
| Adversarial | Accepted | 1,500 | 0.007412 ms | 0.01 ms | 0.01 ms | 0.02 ms |
| Adversarial | Rejected | 13,001 | 0.003144 ms | 0.005 ms | 0.01 ms | 0.02 ms |
| Long-history | Accepted | 1,875 | 0.008519 ms | 0.01 ms | 0.02 ms | 0.02 ms |
| Long-history | Rejected | 9,544 | 0.006784 ms | 0.01 ms | 0.02 ms | 0.02 ms |
| Fresh mixed | Accepted | 12,875 | 0.007817 ms | 0.01 ms | 0.02 ms | 0.05 ms |
| Fresh mixed | Rejected | 60,719 | 0.005925 ms | 0.01 ms | 0.01 ms | 0.02 ms |

Duplicate and stale receipts that do not invoke the evaluator are not
included in these evaluator counts. Full bucket contents, extrema, and
machine-readable aggregates are preserved in each profile's
`performance.json`.

## Storage observations

Each profile used a new database. The reported database-file growth is
`databaseBytesAfterRun - databaseBytesBeforeInstanceCreation`; post-run WAL
size is shown separately.

| Scenario | Initial DB bytes | DB bytes before deliveries | DB bytes after run | DB-file growth | Post-run WAL bytes |
|---|---:|---:|---:|---:|---:|
| Ordinary | 4,096 | 1,388,544 | 174,247,936 | 174,243,840 | 4,317,792 |
| Adversarial | 4,096 | 454,656 | 43,941,888 | 43,937,792 | 4,255,992 |
| Long-history | 4,096 | 458,752 | 38,707,200 | 38,703,104 | 4,231,272 |
| Fresh mixed | 4,096 | 1,937,408 | 257,454,080 | 257,449,984 | 4,297,192 |

The profile runner recorded the starting database file size but not a
starting WAL size. Their WAL values are therefore post-run snapshots, not
WAL growth deltas, and must not be added to their DB-file growth as exact
combined deltas. For the fresh mixed run, a separate pre-run snapshot
recorded 4,096 database bytes and 300,792 WAL bytes; the after-run snapshot
recorded 257,454,080 database bytes and 4,297,192 WAL bytes. Its measured
combined DB+WAL growth was 261,446,384 bytes. These are gross workload
observations, not per-record costs.

The scenario labels do not isolate accepted-only versus rejected-only
storage. Ordinary records include 20,725 stale receipts; adversarial records
include 13,001 rejected receipts and 1,000 duplicate receipts; long-history
records also contain both accepted and rejected transitions. The observed
outcome mixes are not sufficiently isolated to assign a defensible number of
bytes per accepted, rejected, duplicate, or stale record.

## Profile correctness and replay

Every instance in each isolated profile was checked after restarting the API
against the same database. All 10,000 selected instances replayed
consistently, all selected receipts and histories were inspected, and each
report had zero invariant failures. The adversarial profile verified all 500
competing pairs as one Accepted and one stale-revision receipt each.

| Scenario | Instances checked / replayed | Receipts checked | Histories checked | Duplicate receipts | Concurrent pairs | Invariant failures |
|---|---:|---:|---:|---:|---:|---:|
| Ordinary | 7,000 | 69,399 | 9,500 | 1,000 | 0 | 0 |
| Adversarial | 1,500 | 16,001 | 1,500 | 1,000 | 500 | 0 |
| Long-history | 1,500 | 14,600 | 1,875 | 0 | 0 | 0 |
| Fresh mixed | 10,000 | 100,000 | 12,875 | 2,000 | 500 | 0 |

Current-state, replay, and pending-work latency distributions are in the
profile `correctness.json` files. They are separate post-restart verification
measurements, not part of delivery throughput.

| Scenario | Current-state queries (count / mean / p95) | Replay queries (count / mean / p95) | Pending-work queries (count / mean / p95) |
|---|---:|---:|---:|
| Ordinary | 7,000 / 5.50 ms / 9.25 ms | 7,000 / 11.49 ms / 16.88 ms | 1,000 / 9.81 ms / 14.70 ms |
| Adversarial | 1,500 / 6.40 ms / 9.92 ms | 1,500 / 12.35 ms / 18.41 ms | 1,000 / 10.22 ms / 15.86 ms |
| Long-history | 1,500 / 6.65 ms / 10.26 ms | 1,500 / 11.62 ms / 17.30 ms | 1,000 / 9.96 ms / 14.81 ms |
| Fresh mixed | 10,000 / 3.69 ms / 6.12 ms | 10,000 / 6.98 ms / 10.29 ms | 1,000 / 6.11 ms / 9.31 ms |

## Controlled-clock timer recovery

The timer experiment used a dedicated marked database and fixed process-start
clocks; the canonical workload database was not changed. The test clock is
accepted only in Development with test controls and a trusted-ingestion key,
an explicitly matching absolute database path, and an isolation marker for
an existing database.

At T0 = 2026-01-01 00:00 UTC, the experiment created a seven-day timer due
2026-01-08 00:00 UTC. After process restarts and simulated downtime:

- The API restarted at 2026-01-08 01:00 UTC and processed exactly one overdue
  timer in 685.02 ms. The timer became `Fired`; its `DeadlineElapsed` input
  was `Accepted`, and the instance reached `TimedOut`.
- The second due-processing request returned zero timers.
- A further API restart and due-processing request also returned zero.
- The timer competing with a business transition just before its deadline
  was `Cancelled`; the business transition remained Accepted.
- The timer due on 2026-01-10 remained `Pending`; the independently
  business-cancelled timer remained `Cancelled`.
- Replay was `Consistent` for all four instances; invariant failures: zero.

The repeat-after-restart result is zero, confirmed by a direct request to the
restarted API. This avoids counting PowerShell's wrapper object for an empty
JSON array as one processed timer.

## Evidence files

- [Ordinary performance](../../artifacts/gate15/supplemental/profiles/ordinary/performance.json)
  and [correctness](../../artifacts/gate15/supplemental/profiles/ordinary/correctness.json)
- [Adversarial performance](../../artifacts/gate15/supplemental/profiles/adversarial/performance.json)
  and [correctness](../../artifacts/gate15/supplemental/profiles/adversarial/correctness.json)
- [Long-history performance](../../artifacts/gate15/supplemental/profiles/long-history/performance.json)
  and [correctness](../../artifacts/gate15/supplemental/profiles/long-history/correctness.json)
- [Fresh mixed performance](../../artifacts/gate15/supplemental/profiles/mixed/performance.json),
  [correctness](../../artifacts/gate15/supplemental/profiles/mixed/correctness.json),
  [storage baseline](../../artifacts/gate15/supplemental/profiles/mixed/storage-baseline.json),
  and [storage after run](../../artifacts/gate15/supplemental/profiles/mixed/storage-after-run.json)
- [Controlled-clock timer recovery](../../artifacts/gate15/supplemental/timer-recovery/timer-recovery-final-results.json)
