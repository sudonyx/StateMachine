# Gate 15 recovery and timer results

## Process restart

The original API process was stopped after the full delivery run. A new
process (PID 894924) started against the same database:

`artifacts\gate15\runtime\gate15.db`

The restarted API returned `/health` successfully in 1,905 ms. The
post-restart verifier inspected all 10,000 instances and replayed all 10,000
histories without clearing or rebuilding the database. It completed its
all-instance checks in 40.20 seconds; this includes current-state, receipt,
history, and replay requests and is not a timer-processing duration. No
unresolved external operations existed. A stratified sample of 1,000
pending-work queries found 375 instances with pending durable timers.

## Timer result

The durable database contains 1,875 timers, all in `Pending` status. The
development-only `/api/test-controls/timers/process-due?limit=500` endpoint
was called after restart using a temporary local trusted-ingestion key. It
returned an empty result:

| Measure | Result |
|---|---:|
| Due timers found | 0 |
| Timers processed | 0 |
| Accepted timer transitions | 0 |
| Stale/refused timer outcomes | 0 |
| Duplicate timer deliveries | 0 |
| Pending timers remaining | 1,875 |

The API computes `DueAt` from the accepted transition time plus the
definition's `P7D` duration. Corpus business timestamps and the generated
before/after downtime schedule do not control the runtime clock. At the
measured restart all timers were still seven days from their actual runtime
deadlines. The canonical run did not have a supported test clock; a guarded,
process-start fixed clock was added and exercised later in a separate
database.

Accordingly, the canonical run demonstrates a real process restart with
durable pending timers preserved, but it does **not** demonstrate a timer
deadline crossing downtime, an overdue timer firing, or timer recovery
elapsed time. Those criteria were partial for that run. See the supplemental
experiment below for the separate overdue-timer recovery evidence. The
runtime has explicit due processing; no always-on scheduler is claimed.

The measured restart and timer endpoint values are in
`gate15-performance-results.json`. Detailed run artifacts, including the
database, are under the ignored `artifacts\gate15\runtime` directory.

## Supplemental controlled-clock recovery

A separate, explicitly marked database was initialized at 2026-01-01 UTC.
After process restarts advanced the configured fixed time, one timer became
overdue during simulated downtime and fired at 2026-01-08 01:00 UTC. Its
transition was Accepted and moved the instance to `TimedOut`. A competing
business transition accepted one minute before its deadline cancelled its
timer; a future timer remained Pending; an independently cancelled timer
remained Cancelled.

The first due-processing request processed one timer in 685.02 ms. The second
request returned zero; after another process restart, a repeated request also
returned zero. All four instances replayed consistently and there were zero
invariant failures. The full response, due instants, statuses, and replay
results are preserved in
`../../artifacts/gate15/supplemental/timer-recovery/timer-recovery-final-results.json`;
the supplementary implementation and profile results are summarized in
`gate15-supplemental-results.md`.
