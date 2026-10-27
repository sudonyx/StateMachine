# Gate 14 — Milestone 4B design

## Scope and boundaries

Milestone 4B adds Asteria-only durable material amendments. Definition V1 and
V2 are unchanged. New releases created by the Asteria client use immutable
`asteria-release-governance@3.0`. The generic evaluator, persistence, rule
engine, and Generic Harness are unchanged.

The checkout did not contain a Milestone 4A feasibility-audit document. The
implementation therefore verified the existing API boundary directly: generic
commands are public declared inputs, while expected-operation registration
and observations require the State Machine trusted-ingestion credential. V3
models assessment start and `SupersedeAssessment` as observations and uses that
existing trusted path; it does not add a generic command or special-case
runtime logic.

## Registry schema and authority

The Asteria SQLite registry remains authoritative for:

- stable release identity and the single current-generation pointer;
- immutable canonical material snapshots, material digests, workflow identity,
  and definition identity per generation;
- generation-scoped assessment work and its request/response/evidence records;
- stable amendment identity, request fingerprint, expected base generation,
  allocated generation, protocol phase, and per-revision supersession input
  identities;
- normal, amendment-pending, and activation-incomplete admission states.

The Milestone 3B `AsteriaReleases` and `AsteriaAssessmentWorks` tables are
preserved. Initialization backfills every existing generation-1 release and
its current work into `AsteriaGenerations` and `AsteriaReleasePointers`.
Existing history and identifiers are not regenerated. Generation snapshots
are protected by SQLite update/delete triggers; foreign keys are enabled on
registry connections. Unique constraints cover release/generation, workflow,
work, and amendment identities. The current pointer has one row per release
and a composite foreign key to the selected generation/work.

Reservation/allocation and pointer activation use immediate SQLite
transactions. Reservation checks the expected current generation, amendment
identity/fingerprint, V3 eligibility, and live coordinator lease before it
sets `AmendmentPending` and commits the new snapshot and stable identities.
Activation commits the work enablement, pointer switch, normal admission
state, and `NewGenerationActivated` phase together. The old release-table
generation-1 columns remain as migration-era fields and are not the current
pointer authority.

Assessment work can mutate only the work selected by an operational pointer.
Every assessment coordinator call renews and checks its generation/lease
before external work. Amendment leases are checked against the amendment ID,
base generation, pending/incomplete registry state, and lease expiration.
SQLite leases coordinate only processes using the same supported database
file; they are not cross-database or network-filesystem fencing.

## Definition V3 and supersession authority

V3 adds terminal `Superseded` and explicit `SupersedeAssessment` transitions
from `Draft`, `UnderAssessment`, `RequirementsIdentified`,
`ReviewObligationsIdentified`, and `AssessmentUnresolved`. Prohibited and
Cancelled remain terminal and are not reopened or amended. Supersession
evidence binds the amendment ID/fingerprint, expected base generation,
replacement generation, and replacement digest. There are no deployment,
ready-for-deployment, post-deployment, or completion transitions in V3.

The coordinator registers the assessment-start and supersession expected
operations and submits their observations with the configured trusted-ingestion
key. V3 has no `SubmitRelease` generic command, so an untrusted caller cannot
start assessment on a provisioned-but-not-current generation. The amendment
coordinator verifies the
accepted inbound receipt, resolved operation, accepted attempt, terminal
instance state, and accepted transition-history entry before progressing its
registry phase. The State Machine is authoritative for that transition,
terminality, operation outcome, and historical evidence. Generic terminal
handling rejects future ordinary commands and stores late observations without
reactivating the old instance.

The trusted-ingestion key is a shared-secret access boundary, not
role-based/user authentication or source attestation. Operators must restrict
the API and key to the trusted coordinator. The generic API does not consult
the Asteria registry pointer.

## Protocol and trade-offs

The durable phase order is:

1. `AmendmentReserved`: immutable generation snapshot and stable workflow/work
   identities committed; normal admission suspended.
2. `NewInstanceProvisioned`: replacement State Machine instance exists and is
   validated against its generation snapshot. Its assessment work remains
   `Reserved`.
3. `OldInstanceSuperseded`: an accepted V3 supersession transition is verified;
   the registry remains fail-closed in `ActivationIncomplete`.
4. `NewGenerationActivated`: one SQLite commit enables the new work and moves
   the current pointer.

Per-revision supersession operation/message identities are durably allocated
before submission. If a concurrent old-generation observation changes the
revision, the old attempt remains inspectable and recovery allocates a
deterministic identity for the newly inspected revision; an uncertain attempt
is never retried under a freshly random identity. Existing old expected
operations/evidence remain attached to the old instance. A late observation
cannot fulfil new-generation work.

There is no distributed transaction across registry and State Machine. The
replacement is provisioned before sealing the old instance; if sealing fails,
the new work cannot become current. If sealing succeeds, no old-generation
work is resumed and pointer activation is recoverable. This does not cancel
or reverse an external operation that was already dispatched.

Immutable V2 cannot accept a V3 transition, and an instance cannot be
relabelled to V3. Therefore, migrated V2 records are preserved and remain
inspectable/resumable under V2, but amendment is explicitly refused for them.
Supporting amendments of those historical instances would require a
definition/runtime migration capability or an earlier V2 supersession input;
neither is introduced in this gate.

## Explicitly out of scope

No deployment execution, completed verification, approval authentication,
post-deployment amendment, external-effect reversal, production identity
system, or generic-runtime behavior change is implemented.
