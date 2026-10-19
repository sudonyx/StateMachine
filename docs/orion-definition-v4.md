# Orion definition 4.0 — timer-enabled workflow

`definitions/orion-v4.json` is the immutable timer-enabled Orion definition. It uses machine version `4.0` and language version `1.2`. The prior `orion-v1.json` remains version `3.0` / language `1.1` and is retained as the Gate 7 baseline; register a new version rather than replacing stored definition content.

## Declared time rules

| Timer | Accepted trigger | Anchor and duration | Event and eligibility | Cancellation/suppression |
|---|---|---|---|---|
| Document request follow-up | `submit-missing-documents` | Server transition time + 10 calendar days | `DocumentFollowUpDue` while `AwaitingDocuments`; records a same-state workflow transition and a durable customer-notification effect intent | Cancelled when the document-complete transition or application cancellation is accepted, and whenever the instance leaves `AwaitingDocuments` |
| Offer reminder | `issue-offer-after-eligibility` | Server transition time + 6 calendar days | `OfferReminderDue` while `AwaitingOfferAcceptance`; records a same-state workflow transition and notification effect intent | Cancelled when accepted, declined, cancelled, or when state is otherwise exited. If expiry is already due when the reminder is processed, it is stale/suppressed |
| Offer expiry | `issue-offer-after-eligibility` | Same server transition time + 7 calendar days | `OfferExpired` while `AwaitingOfferAcceptance`; accepted transition moves to terminal `OfferExpired` | Cancelled when accepted, declined, cancelled, or when state is otherwise exited. `AcceptOffer` and `DeclineOffer` are refused at or after the stored deadline |

The reminder and document follow-up timer events leave the business state unchanged but add a normal accepted transition-history entry. Their notification requests are durable `EffectOperations` plus `ExpectedOperations`, not provider calls. Gate 7 remains responsible for dispatch/resume, and Gate 5 remains responsible for accepting a trusted delivery observation.

Orion input mappings keep timer-generated events out of ordinary commands and observations. A caller cannot submit `OfferExpired`, `OfferReminderDue` or `DocumentFollowUpDue` as an authoritative timer input.

The current definition uses controlled synthetic customer/request values for demonstrations. Production recipient and content resolution, a trusted timestamp for delayed customer actions, and automatic effect dispatch are outside Gate 8.
