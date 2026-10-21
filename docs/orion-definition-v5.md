# Orion definition 5.0 — amended workflow subset

`definitions/orion-v5.json` is a new immutable version of the Orion machine, using language 1.2. The v4 source file is retained byte-for-byte. Register both versions to run old and new instances side by side; instance creation names the exact version.

## Changes

- `ManualReview` is called `SpecialistAssessment` in this definition; the historical v4 name and transitions remain unchanged.
- `Approve` from `AssessmentReady` or `SpecialistAssessment` enters `PreliminaryOfferReview`.
- `CompletePreliminaryOfferReview` with `preliminaryOfferReviewComplete=true` and `materialDiscrepancy=false` enters `OfferReady`. A material discrepancy instead enters `AwaitingRenewedBusinessAuthorisation`. `RecordRenewedBusinessAuthorisation` returns to review only when `renewedBusinessAuthorisationReceived=true`.
- `IssueOffer` is available from `OfferReady`, not directly from assessment states. It retains the existing eligibility/check guard.
- `Cancel` during `PreliminaryOfferReview` requires `cancellationReviewerAuthorised=true`; an absent fact remains unresolved and a false fact is rejected.
- New offers receive a four-day reminder and five-day expiry. New document follow-up uses seven days. Existing v4 timers remain attached to v4 with their stored UTC due times.

The review outcome, material-discrepancy determination, renewed authorisation, and reviewer cancellation authorization are submitted as per-attempt facts. The runtime preserves them as evaluation evidence; it does not authenticate an individual reviewer or itself establish whether equipment, pricing, contact verification, or authorization is factually correct.

The v5 document does not implement the changed £80,000 assessment threshold, the customer confirmation service, business-day deadlines, date-scoped new-application fields, contact-verification freshness, retrospective policy correction, or senior review for high-value changes. See [gate-11-amendment-traceability.md](./gate-11-amendment-traceability.md).

## Timer semantics

The values are ISO durations interpreted by Gate 8 from the server-side accepted transition time. Existing v4 timers retain their original `DueAt`, even after v5 registration. V5 does not use a business-calendar/time-zone service; `P5D` means five elapsed 24-hour periods in the runtime's persisted UTC model, not a local-calendar deadline.
