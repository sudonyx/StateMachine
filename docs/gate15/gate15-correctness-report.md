# Gate 15 correctness and replay report

## Durable evidence inspected

The 100,000 delivery records were submitted to the real local State Machine
API and persisted in `artifacts\gate15\runtime\gate15.db`. After stopping the
first API process, a new process opened the same database. The verifier then
checked every corpus instance, receipt collection, accepted history chain,
and replay result.

| Invariant evidence | Checked |
|---|---:|
| Corpus instances | 10,000 |
| Current instances inspected | 10,000 |
| Delivery receipts inspected | 100,000 |
| Accepted transition histories inspected | 12,875 |
| Durable historical replays | 10,000 |
| Exact duplicate receipts | 2,000 |
| Concurrent pairs verified | 500 |
| Stale-revision loser receipts across those pairs | 500 |
| Durable timers created | 1,875 |
| Invariant failures | 0 |

## Checks and results

- Every instance had a contiguous history revision chain; the last durable
  history target agreed with the current projection.
- Accepted receipt counts agreed with accepted history counts. Each of the
  2,000 repeated message identities had one original receipt and duplicate
  receipts for its re-delivery records.
- All 10,000 replay reports were `Consistent`; reconstructed state, revision,
  definition identity/version, receipts, and accepted history agreed with the
  persisted records.
- No accepted transition followed an accepted terminal-state transition.
- The 500 shared-revision `BeginWork`/`CancelWork` groups were submitted as
  actual concurrent request pairs. Each persisted as exactly one Accepted
  receipt and one Stale `stale-revision` receipt. Replay and history checks
  found no conflicting accepted revision chain.
- Each timed-wait instance had one durable timer. Timers were pending at the
  post-restart check; no timer was due, so none was counted as processed.
- There were zero effect operations and zero effect attempts. No external
  result was generated, and replay did not call a provider.
- All records remained bound to `gate15-generic-load` version `1.0`.

The persisted receipt statuses were 12,875 Accepted, 2,000 Duplicate,
60,719 Rejected, and 24,406 Stale. Rejections were 50,094
`no-applicable-transition` and 10,625 `terminal-state`; stale records were
`stale-revision`. These represent the selected input semantics, not transport
failures.

The verifier report is preserved at
`artifacts\gate15\runtime\correctness-results.json`. This runtime artifact is
gitignored; exact counts are summarized above. The machine-readable
corpus/run digests are retained in `gate15-corpus-manifest.json` and
`gate15-performance-results.json`.

## Supplemental isolated-profile replay

The same corpus was also run in three separate scenario-selected databases.
After restarting the API against each database, the verifier inspected all
selected instances, receipts, and histories and replayed every selected
instance. The profiles covered 10,000 instances and 100,000 delivery records
in total; each had zero invariant failures. The adversarial profile verified
500 concurrent pairs and 500 stale-revision conflict receipts. Detailed
per-profile counts, query timings, and reports are summarized in
`gate15-supplemental-results.md` and preserved under
`../../artifacts/gate15/supplemental/profiles`.

A second fresh database also received the complete 100,000-delivery mixed
corpus. After restart, all 10,000 instances, 100,000 receipts, and 12,875
histories were checked; all 10,000 replays were consistent, all 500
concurrent pairs matched the expected accepted/stale outcome, and there were
zero invariant failures. Its verification report is at
`../../artifacts/gate15/supplemental/profiles/mixed/correctness.json`.
