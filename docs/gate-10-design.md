# Gate 10 design — historical reconstruction and recovery inspection

## Source of historical truth

The durable `Instances` row is the current-state projection. It retains the instance ID, bound immutable definition ID/version, current state, revision, creation/update timestamps and the original fact JSON. It is not used as the source for reconstructed state.

The reconstruction starts from the exact registered definition version's `initialState`, then reads accepted `TransitionHistory` rows in revision order. Each accepted row carries its attempt ID, transition ID, bound definition identity, previous/next state, event, before/after revision and occurrence time. The linked `InputAttempts` row retains the evaluator result, guard findings and, since Gate 4, a digested evaluation-evidence snapshot. Rejected, ambiguous and unresolved attempts remain inspectable but do not advance reconstructed state.

There is no separate append-only instance-creation history row. The initial state is therefore derived from the immutable bound definition, and the creation timestamp/facts are taken from the instance record. Historical state reconstruction can establish a state path from a valid definition and complete transition history, but it does not cryptographically prove that an administrator did not alter the database file.

## Read-only reconstruction and integrity

`GET /api/instances/{id}/replay` runs a read-only reconstruction from one SQLite serializable read transaction. Its report includes the exact definition binding, derived initial state and its source, reconstructed and projected final state/revision, examined attempts/receipts, inspected projection revision, evidence references, and structured integrity findings.

The verifier checks revision continuity from zero, previous-state continuity, valid states, terminal-state ordering, declared transition identity/event/source/target, definition binding, accepted attempt links, receipt-to-attempt references, and reconstructed-versus-projected state and revision. It reports contradictions rather than skipping rows, repairing records, or treating missing transitions as proof that no event occurred. Gate 9's unique `(InstanceId, RevisionAfter)` index remains in place.

The response classifies state reconstruction as `Consistent`, `Inconsistent`, or `UnsupportedHistoricalFormat`. Missing/malformed bound definitions are unsupported; contradictions in present records are inconsistent. State reconstruction does not need to re-run guards or reread the instance's facts.

## Historical decision verification

`GET /api/instances/{id}/replay/decision-verification` additionally re-runs the pure `TransitionEvaluator` for each accepted transition where the Gate 4 evidence snapshot is complete. It uses the evidence's bound facts, unresolved-fact classifications, decision result, guard context and the exact historical definition. It verifies the stored evidence digest and compares the reproduced evaluation and guard findings with the accepted attempt/history.

This is a distinct mode from state reconstruction. It never calls Meridian, a provider, the Generic Harness, a timer processor, an observation route, or a business-rule service. If an old attempt has no complete Gate 4 evidence, its decision result is `InsufficientEvidence`; current policies or fresh facts are never substituted. A changed external policy cannot rewrite the preserved decision result.

## Replay side effects and external work

The replay and recovery endpoints query only local SQLite records and invoke no live effect coordinators or external clients. They do not create or update attempts, receipts, history, effect intents, expected operations, timers or state projections. Timer due-ness is only reported; it is not a firing decision.

Recovery assessment reads the projected state and revision, replay integrity, input attempts/receipts, expected operations, effect intents/attempts, harness execution IDs and timer rows in a single read transaction. Unknown provider outcomes remain unknown. A known harness execution ID is a pointer to inspect separately; an unknown correlation gap is reported without inventing a lookup or retry. Notification operations are never automatically retried. Pending and overdue timers are reported without processing them.

Recovery classifications are `ConsistentNoOutstandingWork`, `ConsistentPendingLocalWork`, `ConsistentPendingExternalWork`, `ConsistentExternalOutcomeUnknown`, `InconsistentHistoryOrProjection`, and `InsufficientEvidenceForSafeContinuation`. Reasons and outstanding records accompany each classification. The assessment endpoint is inspection only; it offers no state repair or continuation command.

## Durable intake and continuation

Gate 10 reuses `InboundMessages` as both the intake record and final receipt rather than introducing another identity or deduplication system. After request structure is validated and the exact instance definition is loaded, the first SQLite immediate transaction commits the original canonical envelope, its SHA-256 digest, logical/delivery/operation identities, expected revision, definition binding, claimed source, application-established authority classification and receipt time with status `Received`. The trusted-ingestion credential itself is never persisted; only the result of validating the configured application boundary is retained. A failure before this commit leaves no intake and cannot be claimed as recoverable.

Eligible processing starts in a second SQLite immediate transaction. It reloads the projection and the exact bound immutable definition, moves the receipt to transient `Evaluating` state inside that transaction, and re-checks command revision or observation authority, expected-operation identity/generation, source binding, lifecycle, evidence and resolution status. It evaluates only from the stored envelope and original expected revision. The final receipt status, evaluation attempt/evidence, accepted state/revision/history, timers, effect intents and expected-operation resolution remain part of the same transaction. A failure rolls that transaction back to the durable `Received` row. Because `Evaluating` is never committed separately, no durable lease or stale-claim reaper is needed; SQLite immediate transaction arbitration prevents competing hosts from evaluating/finalizing concurrently.

`POST /api/instances/{id}/inbound-messages/{receiptId}/resume` continues only one unresolved durable intake. It accepts no replacement payload, verifies the stored envelope digest and saved application authority classification, then routes the original identities/data through the normal Gate 5 checks. The original revision is never rewritten. A stale command or observation, a cancelled/terminal instance, a resolved or changed expected operation, or missing authority/evidence is refused explicitly. Exact retries reuse the unresolved intake; once final, existing logical identity behavior returns a duplicate/conflict without creating a second transition. Legacy data is not given synthetic intake rows.

Commands retain their declared-command endpoint context. An observation is resumable only if its original receipt records the trusted controlled-ingestion boundary; a caller's `source` value alone is insufficient. The source remains a claim that must match the expected operation. Submitted request payload/facts/evidence are retained for deterministic reevaluation, but authentication header secrets are not part of that envelope.

## Durable verified external results

The Gate 7 Generic Harness adapter now places a durable boundary between receipt of a verified successful Harness result and Gate 5 observation evaluation. Once the adapter has validated the Harness response and its operation correlation, it calls `StageVerifiedExternalResultAsync`. The staging transaction checks the durable expected operation and its binding, then persists the original observation payload, evidence and evidence references, provider/Harness identities, definition binding, execution revision, occurrence time, source authority, and payload digest as a `Received` staged observation. The record is application-created by the trusted adapter; there is no public route for callers to inject a staged result or assert trusted metadata.

`GET /api/instances/{id}/staged-observations` inspects staged records without evaluation or external calls. `POST /api/instances/{id}/staged-observations/{resultId}/resume` continues only the stored result ID; it accepts no replacement payload. Continuation re-verifies the stored digest and operation binding, then passes the stored observation through the existing Gate 5 authority, revision, source, evidence and lifecycle checks. Final staged status and the Gate 5 receipt/attempt/history/projection and expected-operation/effect updates are committed together. A verified result received before cancellation remains evidence, but continuation is refused as stale and does not reactivate a terminal instance. Equivalent duplicate staging reuses the stable result identity; conflicting reuse is refused.

This closes the local crash window after the successful verified result has reached and been staged by the adapter. It does not close the remote-response-loss window between provider completion and local staging commit: if the API process dies before Stage A commits, response bytes held only in memory are not recoverable, and the current Harness API does not provide a lookup by idempotency key. The recovery path never fabricates a response from Harness execution status and never automatically repeats external work.

## Crash boundaries and test controls

The API exposes interruption controls only in Development when `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`. One-shot exception controls interrupt before intake commit, after intake commit/before evaluation, during guard evaluation, before final persistence, after evaluation has staged a state/projection update but before its atomic save, and at verified-result staging/continuation boundaries. A separately armed pause at the post-intake boundary and after staged-result commit supports child-process termination tests. The SQLite transaction is the rollback boundary for state, history, receipt outcome, staged-result status, timer/effect intent and expected-operation changes.

The durable intake boundary is exercised across `WebApplicationFactory` disposal/recreation. Child operating-system processes are killed after intake commit/before evaluation, after a local effect-intent commit/before provider dispatch, immediately before final local persistence, and after the verified external result's staging transaction commits. In the last case, a fresh process resumes the stored result without another Harness execution and accepts at most one transition. Deterministic fault injection also covers failure during staging persistence, before evaluation, during finalization, and after the staged-result transaction commits. Process-kill during an individual SQL statement and before Stage A commits is not established.

## Historical compatibility

- Gate 4+ attempts with intact evaluation evidence and a retained bound definition can be decision-verified.
- Earlier attempts whose evidence columns were added with empty defaults can still contribute state through accepted transition history, but decision verification returns `InsufficientEvidence`.
- Existing instances rely on their `Instances` creation/projection row and retained immutable definition; there is no separate creation event to reconstruct if that row or definition is missing.
- No historical records are migrated, repaired or rewritten by replay. No new schema migration or event-sourcing rewrite is introduced.

## Concurrency and trade-offs

Each report gathers its data in a SQLite serializable read transaction and identifies the inspected projection revision, so a concurrent legitimate commit cannot cause a history read at one revision to be compared with a later projection. Writes retain the existing SQLite immediate transaction, conditional revision update and unique accepted-history revision index. Two independent API hosts sharing SQLite are exercised with staged-result continuation interleaved against a competing command, cancellation, and timer expiry; both relevant commit orders are checked.

The design reuses the existing evidence model and Gate 5 input pipeline, keeping recovery independent from provider execution and avoiding duplicate business-rule evaluation. The trade-off is that incomplete older evidence and an external response lost before the staging commit remain explicit limitations rather than being guessed or repaired.
