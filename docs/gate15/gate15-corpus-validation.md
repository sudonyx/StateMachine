# Gate 15 corpus validation

## Result

The canonical seed `20261029` corpus was generated, validated, and submitted
to the durable API. The canonical manifest is preserved in
`gate15-corpus-manifest.json`.

| Check | Result |
|---|---:|
| Unique workflow instances | 10,000 |
| Delivery records | 100,000 |
| Exact duplicate deliveries | 2,000 |
| Demonstrably late deliveries | 2,700 |
| Competing event pairs | 500 |
| Distinct business occurrence dates | 171 |
| Delivery and record ordering | Stable |
| Referenced instances | All found |
| Event/input mappings | Matched to the bound definition |
| Timer schedule | Includes due dates before and after the planned downtime window |
| Answer/expected-outcome fields | None |
| Manifest SHA-256 checks | Passed |
| Independent-directory determinism check | Passed |
| Durable API delivery attempts | 100,000 |
| Transport / HTTP failures | 0 / 0 |
| Persisted receipts after run | 100,000 |

### Category overlap

`scenario_profile` is mutually exclusive and sums to 10,000 instances:
1,000 cancelled, 500 competing-transition, 1,000 terminal-completed,
1,500 timed-external-wait, 375 long-history-timed-wait, 4,500 pending, and
1,125 long-history-pending. The independent 60-day history attribute overlaps
these profiles: 2,001 instances have long-history delivery spans, including
some terminal, cancelled, and competing instances.

The late-delivery count is higher than the 1,000 scheduled minimum because
some exact duplicates and repeated deliveries are also late under the
validator's rule: a record is late when its occurrence time precedes the
latest earlier occurrence for that instance and its delivery time is after
that occurrence. Exactly 1,000 late records were deliberately scheduled;
the additional 1,700 arise from duplicate/repeated-record overlap. Delivery
and business occurrence timestamps are separate.

The 500 concurrency groups each contain exactly two distinct messages for
one instance and one expected revision. Each pair is a `BeginWork` versus
`CancelWork` race from `Draft`; the runner releases both requests together.
Concurrent pairs therefore represent 1,000 delivery records, not 500
individual records. The pair records are separate from the deliberately late
records; duplicate copies can overlap other delivery categories.

The fixed UTC history anchor and deterministic offsets produce 171 distinct
business occurrence dates across the configured 180-day range. Repeated
`BeginWork` records deliberately exercise refusal and stale paths rather than
encoding an expected state or outcome.

The generated timer schedule is not runtime timer evidence. Only a run against
the durable API can establish which timers were created, canceled, due, or
processed. Runtime timers are anchored to the accepted transition time, not
the corpus `occurred_at`; all 1,875 timers created by this run were still
pending at the post-restart check.
