# Orion definition 6.0 — durable concerns

[definitions/orion-v6.json](../definitions/orion-v6.json) is a new immutable
Orion version using state-machine language 1.3. Orion 4.0 and 5.0 remain
unchanged and available for existing instances; new instances still select an
exact definition version explicitly.

## Independent dimensions

The parent lifecycle declares nine states: `Draft`, `UnderAssessment`,
`Approved`, `OfferReady`, `AwaitingOfferAcceptance`, `Rejected`, `Cancelled`,
`OfferDeclined` and `OfferExpired`. Two concerns carry their own state,
revision and history:

| Concern | Initial state | States |
| --- | --- | --- |
| `documentCompleteness` | `Pending` | `Pending`, `Incomplete`, `Complete`, `Invalid`, `Cancelled` |
| `assetAvailability` | `NotRequested` | `NotRequested`, `Pending`, `Reserved`, `Unavailable`, `Cancelled` |

The resulting vector has a theoretical `9 × 5 × 5 = 225` state combinations;
only the individual states and permitted transitions are declared. Document
verification can progress independently. Asset reservation requests and
verified outcomes update only the asset concern. Requesting a reservation is
not treated as proof that the equipment was reserved.

## Coordination

The generic runtime evaluates the parent assessment transition only when
`documentCompleteness` is `Complete`, in addition to the declared eligibility
facts. Offer generation is permitted only with complete documents. Offer
release is guarded by both `documentCompleteness=Complete` and
`assetAvailability=Reserved`. Refused coordination inputs retain guard
findings that identify the unmet state prerequisite.

All concern observations use the existing Gate 5 trusted ingestion path,
including expected-operation correlation, source authority, required evidence,
idempotency and revision checks. The definition does not treat a request or a
caller-provided source string as evidence of a completed external operation.

## Cancellation and versioning

`cancellationEvent` declares `Cancel`. A successful parent cancellation and
transitions from every active concern state to that concern's terminal
`Cancelled` state are committed atomically. Later concern inputs are refused
after the parent becomes terminal. Expected operations remain inspectable and
completed external actions are not automatically reversed.

No Orion 4.0/5.0 to 6.0 migration is registered. The runtime does not choose
6.0 based on a date, automatically upgrade instances, transform facts, or
rewrite pending external work.

## Limits

This definition uses elapsed runtime behavior rather than business-calendar
deadlines and does not add concern-specific timers, nested concerns, or
cross-instance coordination. The generic runtime records and evaluates the
declared facts; it does not establish the truth of eligibility facts or remote
provider evidence. See [gate-12-design.md](./gate-12-design.md) and
[state-machine-language-v1.md](./state-machine-language-v1.md).
