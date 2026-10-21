# Gate 11 design — versioned definitions and bounded migration

## Versioning policy

An instance is pinned to the exact immutable `(definitionId, definitionVersion)` used at creation. Registering another version does not change that binding, its current state, facts, revision, timers, operations or transition history. Definition registration remains idempotent only for identical content; different content at an existing identity conflicts. Each registered definition retains its JSON and SHA-256 content digest. There is no definition deletion or replacement API.

Orion 4.0 remains unchanged. Orion 5.0 is an independently registered definition. Selecting a definition version when creating an instance is explicit; this runtime does not choose a version from the wall clock. Registration time is operational metadata, not a business-effective date. Operators must deploy/select Orion 5.0 only for activities to which the applicable amendments apply. A future effective-date policy selector is not implemented.

Historical transition rows retain their own definition ID/version and are never rewritten. An explicit migration is a separate audit record and definition-epoch boundary, not a synthetic business transition. It preserves the instance revision and facts; the migration row identifies both definitions, digests, states, revisions, plan, authorisation context/reason, findings, amendment references and commit time. A migration therefore changes the definition epoch without consuming a business revision.

## Orion 5.0 subset

`definitions/orion-v5.json` uses the existing language 1.2 and evaluator. Its implemented subset is:

- **ORN-AMD-001:** approval enters `PreliminaryOfferReview`; offer issue is available only after a completed, consistent review. A material discrepancy moves to `AwaitingRenewedBusinessAuthorisation`; returning from that state requires an explicit recorded authorisation fact and the application re-enters review. No external approval is fabricated.
- **ORN-AMD-002:** the offer-expiry timer is five days for offers issued under Orion 5.0. Existing Orion 4.0 offer timers keep their persisted seven-day due instant and original definition identity.
- **ORN-AMD-003:** cancellation during `PreliminaryOfferReview` requires a reviewer-authorisation fact. That fact is per-attempt evidence, not a caller identity or production authorisation claim.
- **ORN-AMD-004:** the state and command/event identifiers use Specialist Assessment terminology in v5. Orion 4.0 state/history names remain untouched.
- **ORN-AMD-011 (partial):** document follow-up uses a seven-day timer for requests created by a v5 transition. Existing v4 request deadlines remain persisted as-is.

The evaluator remains domain-independent. V5 only adds declared states, events, guards, commands and timers. No fake Customer Confirmation Service behavior was added.

## Explicit migration contract

The API registers one bounded application-level contract in `OrionMigrationPlans`: `orion-4-to-5-early-draft@1`, mapping Orion 4.0 `Draft` to the same business state in Orion 5.0. This is the only source state accepted by the contract. The generic persistence layer receives a declared contract and verifies:

- exact source and target identities and stored content digests;
- exact expected revision and current source binding;
- non-terminal state correspondence present in both definitions;
- the contract's business meaning and unchanged outgoing transition IDs, events, mapped targets and guards;
- required target facts declared by the plan; and
- absence of durable in-flight intake, unresolved staged results, expected operations, effect operations and pending timers.

The migration performs no fact transformation and does not mark any new target requirement satisfied. A target requirement that applies later must still be established by its normal input/evidence path. The supported Draft path has no existing accepted transition or deadline to reinterpret and no outstanding work to carry across.

Preview at `POST /api/instances/{id}/migration-preview` is read-only. Execution at `POST /api/instances/{id}/migrations` requires both Development hosting and the configured `X-State-Machine-Trusted-Ingestion-Key`. The key itself is not persisted. The saved authorisation context is the application-established label `configured-controlled-administration`; the supplied reason is audit text, not proof of a named actor. This is a training boundary, not production IAM.

Successful execution obtains SQLite's immediate writer reservation, reloads the instance and work records, revalidates eligibility, conditionally updates the instance projection by instance/revision/source binding/state, and saves the projection and migration record in the same transaction. Revision stays the same; the committed migration row marks the epoch boundary. A repeated identical migration ID returns its original result. Reusing that ID with different content conflicts. A refused execution persists a structured refusal record but changes no state, definition binding, revision, timer, effect, or accepted history.

Unknown targets, unsupported source/target pairs, already-migrated or changed source bindings, stale revisions, unmapped/unsupported/terminal states, incompatible outgoing transitions, missing target facts, pending local intake, staged results, expected operations, any existing effect operation, and pending timers are refused. The current policy deliberately refuses rather than transforms/carries timers or external work. This is stricter than necessary for some completed operations, but avoids changing their definition identity or replay meaning.

## Historical reconstruction and decision verification

Replay begins with the first committed migration's exact source definition, or the current bound definition when no migration committed. It validates retained digests and reconstructs accepted history in revision order. At a migration's recorded source revision it verifies source epoch/state/revision, target definition and digest, target state, authorisation context and recorded outcome before switching definition epochs. Each following transition is checked against the active definition and its stored definition identity. Decision verification uses the same epoch-specific definition and the original Gate 4 evidence. Refused migrations are evidence references only; they do not alter reconstruction.

Durable timers are verified against their own recorded definition and origin transition. Replay does not recalculate due times, fire timers, invoke providers or mutate records. A missing historical definition or invalid digest is reported; the latest definition is never substituted.

The supported migration starts from Draft, so the demo has no pre-migration accepted transition. The replay implementation supports transition epochs around recorded boundaries, but a successful Orion 4.0→5.0 migration after an accepted v4 business transition is intentionally not claimed. The plan refuses every other source state.

## Amendments and effective dates

The amendment pack distinguishes recorded date, business-effective date, application creation, offer/request issue, assessment start and current lifecycle position. The current definition format has no general business-effective-date selector, no business-calendar arithmetic and no immutable per-transition amendment-context field. V5 is an explicit definition choice, not an automatic date-based policy. The traceability document records the supported envelope and all deferred requirements. In particular, date-scoped thresholds/fields, contact-verification freshness, customer confirmation service calls, business-day escalation, retrospective correction semantics and high-value additional authorisation are not approximated.

Definition registration timestamps and migration commit timestamps are retained separately. Migration records preserve amendment references. Existing accepted evidence always remains attached to the definition version under which it was accepted.

## Concurrency, retention and limitations

SQLite `BEGIN IMMEDIATE` serializes migration against other database writers. The conditional projection update and unique `(InstanceId, MigrationId)` index arbitrate concurrent migration attempts; no separate distributed lock is introduced. The supported contract refuses pending timers and external work, so it performs no timer changes, effect changes, provider calls or replacements. SQLite's existing single-writer and filesystem constraints remain.

Definitions and migration records are retained in the same SQLite database; there is no pruning or external registry. A missing required definition produces an explicit replay limitation. Database-file administrators can still alter records; the local digest/evidence checks detect inconsistency but are not cryptographic protection against an administrator able to rewrite both content and digest.

The migration API is intentionally narrow. There is no arbitrary JSON transformation DSL, automatic migration, startup upgrade, production authorization system, date-aware version resolver, timer supersession, external-effect remapping or automatic retry. Gate 12 adds language 1.3 concern dimensions but does not change the Orion 4.0/5.0 definitions or add a migration to Orion 6.0.
