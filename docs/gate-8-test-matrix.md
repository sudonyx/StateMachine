# Gate 8 test matrix

All automated cases use SQLite and a manually controlled `TimeProvider`; no test waits for a real deadline. Host recovery cases close one `WebApplicationFactory` and create another against the same database. That is a controlled host restart, not a process-kill test.

| Scenario | Setup and action | Expected invariant | Evidence and observed verification |
|---|---|---|---|
| Language version compatibility | Load Orion 3.0/language 1.1 and Orion 4.0/language 1.2; try a timer in 1.1 and an undeclared due event | Legacy definitions remain valid; timers require 1.2 and valid declarations | `DefinitionLoaderTests`; v4 declaration durations and invalid timer diagnostics asserted |
| Offer timer schedule | Accept the offer-issue transition under Orion 4.0 | Reminder and expiry are both persisted from the same trigger time at +6 and +7 days | `Offer_reminder_creates_a_durable_harness_intent_without_invoking_a_provider` |
| Document follow-up schedule | Submit with documents outstanding | A P10D follow-up is created with definition/version, trigger history, origin revision, calculation time and due instant | `Document_follow_up_deadlines_are_versioned_durable_and_recovered_after_restarts` |
| Before-deadline and backward clock | Inspect/process before due; move test clock backwards | No timer fires while current time is earlier than persisted `DueAt` | `Document_follow_up_deadlines_are_versioned_durable_and_recovered_after_restarts` |
| Forward clock jump | Move test clock to ten days after scheduling while host is disposed | The overdue timer fires once after restart; processing evidence records the later actual clock value | Same restart test; current processing time and fired state asserted |
| Restart and version stability | Schedule with 4.0 P10D, then register 4.1 P5D; restart after the old deadline | Old dueAt remains P10D, new instance uses P5D, instance bindings do not migrate, and repeated restarts do not duplicate transitions | Same restart test; both versions, persisted dueAt, transition revisions and empty second processing batch asserted |
| Reminder action boundary | Process reminder at day 6 | Timer event passes through the evaluator; the effect intent/expected operation is durable, with no harness execution ID or provider call | `Offer_reminder_creates_a_durable_harness_intent_without_invoking_a_provider` |
| Reminder suppression at expiry | Delay processing until day 7 with both reminder and expiry pending | A due expiry invalidates the unprocessed reminder; no reminder effect is created; expiry transitions through the evaluator | `Acceptance_before_deadline_cancels_timers_and_at_deadline_expiry_wins` |
| Acceptance wins before deadline | Accept at day 6 + 23 hours | Acceptance advances the revision and cancels pending reminder and expiry in the same transaction; later due scan does not expire the instance | `Acceptance_before_deadline_cancels_timers_and_at_deadline_expiry_wins` |
| Exact boundary arbitration | Submit acceptance at exactly `DueAt`, then process due timers | Equality is expired: acceptance is refused independently of processing order, expiry is accepted once, and no contradictory acceptance appears in history | Same acceptance/expiry test; reason code, terminal state, timer statuses and history asserted |
| Competing request at boundary | Release an API acceptance and due-timer process together through a barrier at exactly `DueAt` | Under either local ordering, only expiry commits; a stale or deadline-refused acceptance cannot overwrite it | `Simultaneous_acceptance_and_due_expiry_cannot_commit_conflicting_transitions` |
| Document completion/cancellation | Complete documents or cancel the application before P10D | Follow-up is cancelled and cannot revive the exited lifecycle state | `Receiving_documents_cancels_follow_up_and_terminal_cancellation_cannot_be_reversed` |
| Timer event authority | Remove trusted header for processing and attempt direct `/events` timer input | Administrative processing is denied; a client cannot submit an authoritative timer ID/event | `Offer_reminder_creates_a_durable_harness_intent_without_invoking_a_provider` |
| Atomic scheduling failure | Install a SQLite trigger that aborts timer insertion during offer issue | Workflow state/revision/history roll back with timer persistence; no half-scheduled transition remains | `Failed_atomic_timer_insert_rolls_back_offer_state_and_history` |
| Atomic cancellation failure | Install a SQLite trigger that aborts timer cancellation during acceptance | Acceptance state/revision/history roll back and both timer rows remain pending | `Failed_atomic_timer_cancellation_rolls_back_acceptance_and_preserves_pending_rows` |
| Duplicate processing | Re-run due discovery after completed timer rows and after another API-host restart | No accepted transition or logical effect intent is repeated | Restart and offer-expiry tests assert empty later batches and stable revisions |
| Legacy regressions | Run existing Gate 2–7 evaluator, API, Gate 5 authority, effects, harness and Meridian tests | Existing semantics and integrations remain intact | Full `dotnet test DurableStateMachine.slnx`; final result reported in the task response |

## Not claimed as verified

- No operating-system process was killed. Restart coverage is a test-host restart against a shared SQLite database.
- The Gate 8-only scenarios use the host-local per-instance gate and do not themselves establish cross-host concurrency. Gate 9's independent-host arbitration coverage and SQLite limitations are recorded in [gate-9-design.md](./gate-9-design.md) and [gate-9-test-results.md](./gate-9-test-results.md).
- The exact-deadline case is deterministically ordered as an API business submission followed by due processing, and the rule blocks acceptance before any timer outcome is committed.
- No live Generic Harness/provider is called by Gate 8 timer tests. They verify a persisted Gate 7-compatible intent and expected operation only.
- No production background worker automatically polls timers; the explicit controlled processor discovers overdue rows.
