# Gate 15 performance report

## Result and scope

One full, mixed-profile run was executed against the published API and its
isolated SQLite database. This is a local single-run measurement, not a
production capacity claim. The workload used the domain-neutral
`gate15-generic-load` 1.0 definition to avoid fabricating provider outcomes,
approvals, or other authority evidence. No Decision API, Generic Harness, or
external provider was involved.

The base source commit was `a0851b5b59c30781d21c0480776f0fddcef94b60`; the
Gate 15 additions were uncommitted at measurement time. The machine was
Windows 11 Home build 26300, .NET SDK 10.0.100/runtime 10.0.0, AMD Ryzen 7
7840HS, with approximately 32 GB memory. The API and runner were separate
local processes, the database was on local disk, and SQLite used the existing
30-second default timeout and connection pooling. No warm-up run or repeated
run variance was recorded.

The measured delivery interval starts after definition registration and
creation of the 10,000 instances. It includes HTTP delivery, admission,
evaluation, persistence, duplicate/refusal paths, and concurrent pair
handling; it does not include instance creation or API startup.

## Throughput

| Metric | Result |
|---|---:|
| Intended / completed delivery records | 100,000 / 100,000 |
| HTTP attempts | 100,000 |
| Concurrency | 4 |
| Delivery interval | 630.05 s |
| Completed deliveries per second | 158.72 |
| Accepted transitions | 12,875 |
| Accepted transitions per second | 20.43 |
| Duplicate receipts | 2,000 |
| Rejected receipts | 60,719 |
| Stale receipts | 24,406 |
| Concurrent pairs / durable stale-revision pair outcomes | 500 / 500 |
| HTTP / transport failures | 0 / 0 |
| Retries | 0 |

The durable receipt table contained all 100,000 deliveries. There were 73,594
input attempts: 12,875 accepted and 60,719 rejected. The remaining records
were 2,000 duplicate receipts and 24,406 stale-revision receipts that did not
create evaluation attempts. Post-run verification matched all 500 competing
pairs to one Accepted receipt and one stale-revision receipt. The runner
treats HTTP 409 as a business concurrency outcome; it did not persist a
separate HTTP-status counter, so 500 is the durable conflict receipt count,
not a separately logged response-code count.

The run used one canonical mixture representing ordinary durable activity,
competing delivery, and long-history cases. Those profiles were not timed as
separate A/B/C runs, so there is no profile-specific throughput comparison.

## Latency and replay

After restarting the API against the same database, the verifier issued
10,000 current-state queries and 10,000 historical replay queries with
bounded concurrency 4. Pending-work inspection sampled 1,000 instances,
including 375 with durable pending timers.

| API operation | Count | Mean | p50 | p95 | p99 | Maximum |
|---|---:|---:|---:|---:|---:|---:|
| Current-state query | 10,000 | 3.40 ms | 3.11 ms | 5.36 ms | 7.17 ms | 334.20 ms |
| Historical replay | 10,000 | 4.65 ms | 4.34 ms | 6.89 ms | 10.66 ms | 110.81 ms |
| Pending-work query | 1,000 | 3.90 ms | 3.59 ms | 6.47 ms | 11.96 ms | 35.74 ms |

The verifier took 40.20 seconds for the all-instance current/history/receipt/
replay checks; that wall time is not the sum of the per-request percentiles
and excludes the separate 1,000-query pending-work sample. The post-restart
verification process used `Microsoft.EntityFrameworkCore` log level Warning.
Replay outcomes were consistent for all 10,000 instances. The generic
definition contains no effects, and the database contained zero effect
operations or attempts, so replay did not invoke or re-invoke a provider.

The runtime has no evaluator-only timing instrumentation in this run.
End-to-end HTTP query timings above must not be interpreted as pure
transition-evaluation latency. API startup also includes database
initialization and is reported separately in the recovery report.

## Restart and timers

A new API process opened the persisted database and returned healthy in
1,905 ms. The timer processing endpoint returned zero due timers. All 1,875
durable timers remained pending and were scheduled for seven days after their
accepted transitions. The corpus's historical `occurred_at` and planned
downtime dates do not move the runtime's `TimeProvider.System` clock. No
overdue timer processing or simulated clock advance is claimed.

## Storage and logical records

The post-run database file was 253,722,624 bytes (241.97 MiB); the WAL was
4,313,672 bytes (4.11 MiB) at post-restart inspection. The SQLite database
contained 10,000 instances, 100,000 inbound messages, 73,594 input attempts,
12,875 accepted history records, and 1,875 pending timers. It contained no
expected operations, effect operations, or effect attempts. The file-size
division is approximately 2,537 bytes per delivery record, but this is gross
database size per input, not attributable row growth.

For this original canonical run, a pre-run database size was not captured,
and no separate accepted-only and rejected-only database comparisons were
performed. Its incremental growth per accepted versus rejected delivery
cannot be defended; those fields are null in the machine-readable results.
The later isolated outcome-specific measurements are reported below and in
`gate15-supplemental-results.md`.

The runtime uses the `Instances` table as its current-state projection and
retains transition history. No separate snapshot subsystem or controlled
snapshot comparison exists in this workload.

## Analysis and limitations

- The measured rate is the complete local API/SQLite path at concurrency 4;
  it does not isolate HTTP, JSON, evaluator, transaction, or lock-wait time.
- SQLite write serialization is a plausible constraint, but no lock-wait or
  transaction-duration evidence was collected. Measure those boundaries
  before considering batching or writer changes.
- Receipt persistence is a major part of the exercised workload: every
  delivery produced a durable receipt, while duplicate and stale paths
  avoided a new transition. Do not reduce retained evidence or weaken
  idempotency to improve throughput.
- The runner performs four-worker bounded processing and executed each
  corpus delivery exactly once. Concurrency pairs were released together.
- This was one run with no repeated-run variance measurement. Machine,
  runtime, corpus and execution details are preserved in
  `gate15-performance-results.json`.
- No separate warm-up phase was measured; cache state may affect comparisons
  with later runs.

Future performance work may isolate transaction/lock wait and persistence
duration. Any optimization should retain receipt identity, revision
arbitration, complete history, and the existing concurrency/replay
regression coverage.

## Supplemental profile measurements

The canonical figures above remain unchanged. A follow-up used the same
corpus in fresh databases, partitioned by mutually exclusive scenario, with
separate run IDs, checkpoints, and reports. The ordinary, adversarial, and
long-history profiles measured 182.66, 176.86, and 180.45 deliveries/s,
respectively, at concurrency 4. Instance-creation intervals were 16.23 s,
3.81 s, and 4.54 s. Outcome totals across the partitions exactly match the
canonical 12,875 Accepted, 2,000 Duplicate, 60,719 Rejected, and 24,406
Stale receipts.

Development-only monotonic evaluator instrumentation recorded Accepted and
Rejected evaluation durations in bounded histograms. Mean durations ranged
from 0.003144 to 0.008519 ms; percentile estimates are reported as histogram
bucket upper bounds, not exact quantiles. Each profile was restarted and
verified independently: all 10,000 selected instances replayed consistently,
all profile receipts and histories were checked, and there were zero
invariant failures.

Profile database files grew by 174,243,840 bytes (ordinary), 43,937,792 bytes
(adversarial), and 38,703,104 bytes (long-history), from 4,096-byte starting
database files. Post-run WAL sizes were 4,317,792, 4,255,992, and 4,231,272
bytes. Starting WAL size was not captured in these three profile reports;
the WAL snapshots are not exact WAL growth deltas. The profile outcome mixes
also do not support per-Accepted or per-Rejected storage attribution. Full
methodology and all per-profile measurements are in
`gate15-supplemental-results.md`.

The follow-up also ran the complete mixed corpus in one fresh database:
100,000 deliveries in 549.87 s (181.86 deliveries/s), with instance creation
measured separately at 19.52 s. The outcomes exactly matched the canonical
counts. The API measured 0.007817 ms mean Accepted and 0.005925 ms mean
Rejected evaluator duration; percentile estimates remain histogram bucket
upper bounds. A pre-run snapshot captured 4,096 DB bytes and 300,792 WAL
bytes; post-run sizes were 257,454,080 DB bytes and 4,297,192 WAL bytes, a
combined DB+WAL growth of 261,446,384 bytes. After restart, all 10,000
instances replayed consistently and there were zero invariant failures.

## Outcome-specific storage attribution

The final isolated experiment measured two fresh-database runs per receipt
outcome, each with 512 measured deliveries, matching instance IDs, payloads,
and post-setup baselines. Across the two runs, the estimated checkpointed
increment was 3,240–3,248 bytes per Accepted delivery, 2,368–2,384 per
Rejected delivery, 1,408–1,424 per exact Duplicate, and 1,208–1,232 per
Stale-revision delivery. All runs had identical 1,806,336-byte database
baselines, zero WAL at baseline and after checkpoint, 4096-byte pages,
512/512 verified measured receipts, and 512/512 consistent historical
replays. These are configuration-specific allocation estimates: Accepted
includes its attempt and history, Rejected includes its attempt, and SQLite
indexes, shared pages, and page allocation contribute. See the table,
per-run deltas, and caveats in `gate15-supplemental-results.md`.
