# Gate 11 test results

## Build and automated tests

Validated on .NET SDK `10.0.100`:

- `dotnet build DurableStateMachine.slnx`: passed with 0 warnings and 0 errors.
- `dotnet test DurableStateMachine.slnx --no-build`: passed, 93 tests, 0 failures, 0 skipped.
- `git diff --check`: passed.

The SQLite-backed `Gate11MigrationTests` integration test exercises immutable Orion 4.0 registration, explicit Orion 5.0 registration and version pinning, preservation of an already-issued v4 offer deadline, the v5 preliminary-review path and five-day expiry, cancellation authorization evidence, successful Draft-only migration, refusal cases, idempotency/concurrency arbitration, and replay across the recorded definition boundary. Existing timer, concurrency and replay tests remain part of the full passing suite.

## Fixed-corpus regression

The fixed Gate 9 corpus was validated with seed `20261019`, 5,000 deliveries and 300 applications. Two API hosts shared one temporary SQLite database; the run used `multi-host` mode with concurrency 8. The report recorded:

| Outcome | Count |
|---|---:|
| Accepted | 932 |
| Duplicate | 150 |
| Malformed | 50 |
| Rejected | 2,699 |
| Stale | 1,169 |

There were zero failed invariants and zero transport failures; duration was 30,437 ms. The corpus fingerprint was `2C1A29D7B149E00B0E03C2AF6CB226D0470D8A37D99A98D67DE61CC7CA14C1F8`. Competing delivery outcome counts can vary with scheduling; invariant and transport failures are the correctness criteria.

This corpus run preceded the final addition of the migration record's `ExpectedRevision` field and its existing-table compatibility path. The full build and test suite above were run after those edits; the two-host corpus was not rerun afterward.

## Scope and limitations

- Successful migration is limited to Orion 4.0 `Draft` → Orion 5.0 `Draft`. No accepted v4 business transition is claimed as a demonstrated pre-migration history.
- Migration refuses pending timers and outstanding local or external work. It does not transform facts, carry timers, invoke providers or automatically select a definition by business-effective date.
- Orion 5.0 implements only the subset listed in [gate-11-design.md](./gate-11-design.md). The deferred amendment requirements in [gate-11-amendment-traceability.md](./gate-11-amendment-traceability.md) are not covered as implemented behavior.
- The configured shared key is a Development training boundary, not production identity or authorization infrastructure. Definition retention and replay integrity checks do not protect against an administrator able to rewrite both stored content and its digest.
- Business-calendar deadlines, automatic migration/retry, timer supersession, arbitrary transformation plans, production authorization and external-effect remapping remain unimplemented. Gate 12 concerns are documented and tested separately in [gate-12-design.md](./gate-12-design.md) and [gate-12-test-results.md](./gate-12-test-results.md); no migration to Orion 6.0 is provided.
