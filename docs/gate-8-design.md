# Gate 8 design — durable timers, deadlines and expiry

## Scope and boundary

Gate 8 adds a bounded timer subsystem outside the pure deterministic evaluator. The evaluator still receives a definition, current state, declared event and immutable facts; it does not read a clock. A timer becoming due only makes a declared event eligible for evaluation. It does not directly set instance state.

The current implementation has no continuously running in-memory timer and no hosted polling worker. A caller with the configured local administrative credential can invoke the development-only due processor. A restart discovers overdue work by querying the durable SQLite timer table; no process-local schedule must be reconstructed first.

## Definition and version stability

Language 1.2 adds an optional `timers` array. Existing 1.0 and 1.1 parsing and runtime semantics remain unchanged. A timed definition must also declare Gate 5 input mappings, and a timer-generated event cannot be mapped to an ordinary command or observation. Timed instances therefore cannot receive timer events from the legacy `/events` endpoint or by naming a timer event as a command.

Each timer declaration identifies:

- a unique declaration ID;
- the accepted transition that starts an occurrence;
- a positive ISO-8601 duration;
- the event submitted when due;
- the state in which it remains relevant;
- optional transitions that cancel it;
- optional business events barred at/after the deadline;
- optional due timers that supersede it; and
- optionally, a Gate 7 effect-intent template.

Orion's immutable 3.0/language 1.1 definition remains unchanged. Orion 4.0/language 1.2 supplies the deadlines. Instances keep their registered definition ID and version. A deadline is calculated exactly once from the accepted triggering transition's UTC server time and is not looked up again from the current definition after restart. No in-flight migration is implemented.

## Time and anchors

The API uses .NET `TimeProvider`; production registration uses `TimeProvider.System`, and tests replace it with a manually advanced provider. All persisted timer instants are UTC (`DateTimeOffset` converted to UTC ticks in SQLite).

For each schedule, the record preserves:

- `TriggeredAt`: the accepted transition's recorded time;
- `CalculatedAt`: the authoritative UTC value used for deadline calculation;
- `DueAt`: the persisted calculated instant;
- `ProcessedAt`: the authoritative UTC value used by the processor;
- the triggering workflow event and definition transition/history IDs; and
- the originating instance revision and bound definition ID/version.

Today the accepted transition timestamp and calculation time are sampled from the same server `TimeProvider` value. Separate columns keep the two concepts explicit. The processor compares only `DueAt <= currentTime`. A backward wall-clock step cannot cause an early firing; it may postpone discovery until the clock catches up. A forward step makes the timer overdue, and evidence records the actual later processing value; no earlier processing time is invented. This is a wall-clock model, not a monotonic-clock or time-synchronisation guarantee.

For Orion's offer, the anchor is the server-side accepted `issue-offer-after-eligibility` transition. The expiry due time is the recorded transition time plus seven calendar days, and the reminder is six days from that same transition. The document follow-up is ten days after the server-side transition into `AwaitingDocuments`.

Caller-supplied `occurredAt` is not used for deadline eligibility. In particular, the Gate 8 policy does not accept a late-arriving offer acceptance based solely on a claimed earlier business occurrence time; trusted business-time evidence is a future extension.

## Durable records and atomicity

`Timers` is a SQLite table created/ensured during API startup. Each row carries a deterministic ID derived from the instance, declaration ID and accepted transition-history ID. A unique index on the same logical schedule prevents duplicate creation. Rows are retained with one of these statuses:

- `Pending`
- `Fired`
- `Cancelled`
- `Stale`
- `Refused`
- `Unresolved`

Timer scheduling and cancellation happen inside the same SQLite transaction that records the accepted state transition, input attempt and transition history. If scheduling or cancellation persistence fails, the transaction rolls back the state/revision/history change as well. No external HTTP call occurs in this transaction.

The trigger context is immutable. A timer is not rescheduled on restart. Re-entering a state does not revive a cancelled occurrence; a new occurrence must be started by its declared trigger transition.

## Cancellation, eligibility and firing

Every accepted workflow transition examines pending timers for that instance and bound definition. A timer is cancelled when its `whileState` is exited or when the transition is listed in `cancelOnTransitions`; the reason, time and transition evidence are retained. This covers accepting/declining an offer, completing the document request, cancelling the application and other lifecycle movement.

Due rows are selected by `(DueAt, Id)` order. The processor takes the same per-instance gate used by ordinary submissions, starts the normal SQLite transaction, re-reads the timer and instance, verifies that the timer is still pending/due, confirms the instance remains bound to the timer's definition, and checks the declared active state. A definition mismatch or exited state is marked stale. If an `invalidatedByDueTimers` declaration has itself reached its persisted due time, the dependent timer is marked stale rather than issuing a now-inappropriate action.

An eligible timer event is sent through `EvaluateEventWithinTransactionAsync`, which invokes the existing `TransitionEvaluator`. The evaluator can accept, reject or leave the event unresolved. The attempt, history (if accepted), timer outcome and any generated effect intent commit together. A rejected timer is durably `Refused`; an unresolved or ambiguous evaluation is retained as `Unresolved`. It is not retried as a new logical timer event.

Already completed or cancelled timer rows are never selected as due work. A repeated processor call therefore cannot advance the workflow again or duplicate a logical effect intent. This is local SQLite transaction/idempotency behavior, not distributed exactly-once delivery.

## Deadline boundary and competing inputs

Orion's expiry declaration lists `AcceptOffer` and `DeclineOffer` in `blockedEvents`. A business event is rejected if the current server receipt time is greater than or equal to the persisted expiry `DueAt`, even when the timer processor has not yet run. Equality is deliberately expired. The current instance revision and SQLite transaction arbitrate the state change:

- acceptance committed before the deadline changes state and cancels expiry/reminder timers;
- at/after the deadline, acceptance or decline is refused and the expiry timer remains processable;
- expiry committed first moves to terminal `OfferExpired`, after which a competing event cannot move it elsewhere;
- reminder processing at/after the expiry deadline is suppressed by its declared `invalidatedByDueTimers` relation unless the reminder was already processed before expiry.

No event handler directly assigns a target state outside the definition. Gate 8 originally used a process-local semaphore and revision compare-and-update. Gate 9 scopes the semaphore to one API host and adds a SQLite `BEGIN IMMEDIATE` transaction before state reads, plus a unique transition-history revision index. Independent API hosts sharing a SQLite file therefore serialize writes at the database boundary. This is not a database-independent distributed lock and does not claim support for SQLite over a network filesystem; see [gate-9-design.md](./gate-9-design.md).

## Reminder and document follow-up effects

Orion declares a `customer-notification` effect intent for its offer reminder and document follow-up. When the timer event is accepted, the persistence transaction creates the ordinary Gate 7 `EffectOperations` row and matching `ExpectedOperations` row, including the capability binding snapshot, stable idempotency key, expected observation revision and request digest. The timer stores that action operation ID.

Timer processing does not invoke the Generic Harness, call a provider, retry an action or mark a notification delivered. Existing Gate 7 routes remain responsible for controlled resume/dispatch and Gate 5 remains responsible for accepting the provider observation. An accepted timer event with a pending effect intent is evidence that the action is due, not evidence that it was delivered.

## API and recovery

`GET /api/instances/{id}/timers` and `GET /api/instances/{id}/timers/{timerId}` expose current and historical outcomes, schedule/processing evidence, associated attempt/transition and action IDs. `/pending-work` also returns pending timers.

`POST /api/test-controls/timers/process-due?limit=100` is mapped only in Development when `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`. It requires the configured `X-State-Machine-Trusted-Ingestion-Key`, accepts only a bounded batch limit, and chooses all due timers itself; it accepts no timer ID, event or claimed timestamp. The persistence processing method is separately callable for controlled host/test integration.

There is no automatic production polling/worker in this gate. Operators or a future host worker must call the processor. A timer due during shutdown remains pending and is discoverable on restart. This keeps the gate bounded and makes the recovery action explicit.

## Verification and limitations

The automated SQLite/API tests cover language 1.2, immutable old/new definition deadlines, no early processing, backward and forward clock movement, host restart recovery, repeated processing, accepted-before-deadline cancellation, exact-boundary refusal, stale reminder suppression, follow-up cancellation, trusted processing authority, Gate 7 intent creation without provider invocation and a forced timer-insert rollback. Gate 9 adds cross-host database arbitration and corpus coverage in its own tests.

Restart tests dispose one `WebApplicationFactory` and create another against the same SQLite file. They are controlled in-process host restarts, not operating-system process-kill coverage. Gate 9's multi-host test uses independent API-host service providers and their separate local lock registries, with SQLite transactions as the shared arbitration mechanism; it is not an OS-process-kill test. No background scheduler, event-history reconstruction, trusted delayed-business-time exception or automatic external-effect dispatcher is implemented.
