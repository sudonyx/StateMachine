# Gate 14 — Milestone 3B design and implementation

## Scope and source audit

Milestone 3B implements initial Asteria release assessments only. Material
amendments, reassessment, verification completions, human approvals,
deployment, deployment health verification, and completion remain out of
scope.

The integration uses the actual exported
[Decision API OpenAPI contract](../../training-data/asteria/contracts/asteria-decision-api.openapi.yaml)
and its
[binding semantics](../../training-data/asteria/contracts/asteria-decision-binding.md)
and
[recovery semantics](../../training-data/asteria/contracts/asteria-decision-recovery.md).
The supplied deployment-platform OpenAPI export and repository copy were
checked for consistency. The authoritative business inputs are
[AST-BR-001](../../training-data/asteria/requirements/asteria-release-requirements-v1.md)
and
[AST-POL-001](../../training-data/asteria/policies/asteria-release-policy-v1.md).
The existing
[Asteria V1 definition](../../definitions/asteria-release-governance-v1.json)
and Milestone 1 reports remain unchanged.

The requested Milestone 2 implementation documentation and Milestone 3A
binding audit were not present in this checkout. The implementation therefore
uses the exported contract and binding/recovery reports as the authority for
API shape, and records its additional assumptions below rather than inferring
missing API fields.

## Responsibility and guarantee ownership

| Component | Owns | Does not establish |
|---|---|---|
| State Machine | Immutable workflow definition and instance; command/observation history; committed revisions; expected-operation registration, matching, resolution and receipt status | Release-material truth, policy correctness, identity of the caller behind a shared trusted-ingestion key, or external API persistence |
| Asteria Decision API | Policy selection, evaluation, selected policy versions, persisted evaluation identity/evidence for applicable policies, and its documented keyed replay behavior | Workflow authority, workflow instance, parent revision, expected operation, release-material digest authority, performed verification, human approval, or deployment authorization |
| Asteria coordinator and SQLite release registry | Canonical material and registry-derived digest; generation; stable workflow/work/operation identities; exact request fingerprint and request; local original response/evidence; API-to-workflow correlation; serialized processing and crash reconciliation | Atomic cross-service commit, independent authentication of policy service, distributed locking beyond a shared SQLite database, or verification/approval/deployment completion |

The Decision API request contains only fields declared by its published
contract. It includes `releaseId`, generation, material digest, temporal
context, and the nine declared policy facts. It does **not** send workflow
instance ID, expected operation ID, definition identity, or parent revision:
the Decision API has no native fields for those values. The coordinator
associates them with the assessment in its durable work record, and the State
Machine independently enforces the expected operation when the observation
is submitted. Echoed release/generation/digest binding fields are compared
with the registry; their presence in a response does not mean the Decision
API authenticated or independently verified them.

## Release registry and assessment work

`AsteriaReleaseRegistry` is an Asteria-specific SQLite store. A transaction
creates a unique release row and its generation-1 work row. It owns:

- Release ID, stable workflow instance ID, definition ID/version, canonical
  material JSON, generation, registry-derived material digest, and current
  work identity/status.
- Stable expected-operation and Decision API idempotency identities.
- Parent revision after it has been established from State Machine state.
- Exact Decision API request JSON and canonical fingerprint before invocation.
- Original response, evaluation ID where present, selected policy versions,
  original evidence where present, work status, and recovery metadata.
- A time-limited processing lease that the active coordinator renews; expired
  leases allow recovery after process termination.

The digest is calculated locally as SHA-256 over the UTF-8 canonical JSON of
all ten registry material fields, including `releaseOwner`. The JSON has a
fixed validated shape, ordinally sorted property names, invariant values, and
the deployment timestamp normalized to UTC. The API's nine policy facts do
not include `releaseOwner`; therefore its fact digest is a separate API-owned
digest and is never confused with the registry material digest.

The business-effective date is derived from `plannedDeploymentAt` as a UTC
calendar date. `knownAt` is captured at first release creation and remains
fixed for retries. Both choices are persisted with the initial work because
the absent Milestone 2 documentation did not supply further temporal rules.
No caller-supplied material digest is accepted as authoritative.

## Contract binding and response validation

The coordinator persists and resends the exact request with a deterministic
API-supported idempotency key. Before accepting a response, it validates the
published response shape, outcome and risk enums, obligation statuses,
`deploymentAuthorised == false`, temporal context, echoed release/generation/
digest binding, and the API evidence/fact snapshot against the original
request and registry material. The original response and evidence are stored
before workflow observation submission. The coordinator does not claim the
API independently established State Machine authority.

When no policy version applies, the contract returns `Unresolved` with a
nullable `evaluationId`, null evidence and an empty selected-version list.
The response may still contain an API fact digest. The coordinator does not
reinterpret that digest as an evaluation ID, does not invent evidence or a
selected version, and durably stores the original response and selection
context locally. Since the API does not persist or reserve the idempotency key
for this branch, the coordinator never silently issues a later policy
evaluation for the same historical work.

## Workflow definition V2

[Asteria release governance V2](../../definitions/asteria-release-governance-v2.json)
is a separately versioned immutable definition. It preserves V1 and adds only
the supported `AsteriaAssessmentObserved` mapping and safe outcome-specific
concern transitions. `RequirementsDetermined` and
`AdditionalReviewRequired` record that obligations were identified; neither
means an obligation was fulfilled. `Unresolved` remains an unresolved
assessment, and `Prohibited` is preserved as a prohibition.

V2 has no route to deployment-ready, deployment, post-deployment verification,
or `Completed`. Policy assessment results cannot satisfy verification or
approval concerns. The coordinator registers the expected operation against
the actual committed parent revision and submits a trusted observation with
the registered operation ID and stable message identity. The State Machine's
accepted receipt and resolved operation, rather than a successful HTTP status
alone, determine whether the observation was accepted.

## Generic-runtime changes and Asteria additions

No generic evaluation, persistence, API, Generic Harness, rule evaluator, or
existing Orion/Meridian business definition was changed. The changes are
Asteria-specific: the client/coordinator and SQLite registry, definition V2,
focused tests, demonstration fixture, and these Gate 14 reports.

## Demonstration

The real-service `prepare` then `resume` demonstration is documented in the
repository [README](../../README.md#gate-14-asteria-initial-assessment).
It uses separate CLI processes so the submission-to-operation-registration
recovery gap is exercised across a process boundary. A real run against the
Decision API at `localhost:5267` and State Machine API at `localhost:5270`
accepted a persisted `AdditionalReviewRequired` result at parent revision 1,
reaching `ReviewObligationsIdentified` at revision 2. Separate live runs
confirmed `Prohibited` and no-applicable-policy `AssessmentUnresolved`
outcomes; the latter had no evaluation ID or selected policy version.

## Limits

This milestone does not add material amendments or generation changes,
reassessment, durable execution of verification, human approval, deployment,
health verification, or completion. SQLite lease coordination requires
coordinators to share the same supported SQLite database. There is no
distributed transaction; recovery is by stable identities, durable records,
and inspection of each system before retry. The configured trusted-ingestion
key is a local trust boundary, not production authentication or proof that an
external policy service produced a response.
