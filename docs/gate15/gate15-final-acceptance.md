# Gate 15 final acceptance

## Disposition: PASS — synthetic training scope

The complete 10,000-instance/100,000-delivery corpus ran against the real
durable API. Corpus integrity, receipts, histories, replay, and process
restart passed. Supplementary isolated runs now demonstrate overdue-timer
recovery, evaluator timing, creation timing, profile throughput, and
outcome-specific storage attribution. This PASS is limited to the documented
synthetic training scope and is not a production capacity or universal
per-record storage claim.

| Requirement | Status | Evidence / limitation |
|---|---|---|
| Exact corpus size and lifecycle mix | PASS | 10,000 instances; 100,000 deliveries; manifest and category counts preserved. |
| Deterministic generation and validator | PASS | Fixed seed `20261029`; independent-directory hash check; canonical SHA-256 manifest. |
| Duplicate, late, and concurrent delivery constraints | PASS | 2,000 exact duplicates; 2,700 late by documented rule; 500 real concurrent pairs. |
| Full workload through durable runtime | PASS | 100,000 actual HTTP attempts; 100,000 durable receipts; zero HTTP/transport failures. |
| Durable correctness under load | PASS | 10,000 instances, 12,875 histories, replay and state chains checked; zero invariant failures. |
| Process restart against persisted state | PASS | New API process; same SQLite database; healthy in 1,905 ms. |
| Historical replay | PASS | All 10,000 instances replayed consistently; no provider/effect rows exist. |
| Overdue timer processing across downtime | PASS (supplemental) | A dedicated fixed-clock database fired one overdue timer after restart; repeat processing returned zero; replay and invariants passed. The canonical database was not changed. |
| Evaluator-only latency | PASS (supplemental) | Bounded histograms measured actual Accepted/Rejected evaluator calls in the three isolated profiles and fresh full mixed run; percentile values are histogram bucket upper bounds. |
| Instance-creation elapsed time | PASS (supplemental) | Instance-creation intervals were measured separately for ordinary, adversarial, long-history, and full mixed runs. |
| Profile-separated throughput | PASS (supplemental) | Three mutually exclusive corpus partitions were timed in separate databases; a separate fresh full mixed run covered the same 100,000 records. |
| Storage growth comparison | PASS (supplemental) | Two fresh 512-delivery runs per outcome, fixed IDs and matched setup; actual durable receipts and row deltas verified. Estimates range from 1,208–3,248 bytes/delivery; SQLite-specific limitations and all per-run evidence are in `gate15-supplemental-results.md`. |
| Full build and regression tests | PASS | Solution build succeeded; all 7 focused Gate 15 tests and all 216 regression tests passed. Existing NU1903 warnings in GenericHarness remain. |
| Gate 14 preservation | PASS | Gate 14 implementation/report were not changed. |

The canonical mixed-run throughput was 158.72 records/s at concurrency 4.
Supplemental profile rates ranged from 176.86 to 182.66 records/s; these are
separate single-run measurements, not a production capacity claim. Detailed
timer, evaluator, profile, storage, and replay evidence is in
`gate15-supplemental-results.md`. The four storage estimates are incremental
checkpointed DB+WAL changes over a common prerequisite baseline, not universal
logical row sizes.
Post-restart current-state and replay latency distributions, durable row
counts, timer status, and exact corpus digests are recorded in
`gate15-performance-results.json`. See the performance and recovery reports
for methodology and limitations.
