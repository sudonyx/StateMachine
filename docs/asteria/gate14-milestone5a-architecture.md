# Gate 14 — Milestone 5A: Verification and Human Approval Architecture Audit

**Audit type:** Read-only architecture and contract audit
**Scope:** Verification and approval obligations only; no implementation, deployment execution, health verification, or final completion
**Decision:** Preserve the existing generic boundaries. For synthetic verification, use the existing Generic Harness execution contract from an Asteria-specific coordinator and submit validated results through the existing trusted-observation/expected-operation path. Do not add a generic Harness feature or a deployment-ready state. Add a separate Asteria-specific synthetic authority gateway for human approvals.

## 1. Executive findings

The current Asteria implementation determines assessment outcomes and records which of four obligations are `Required`, `NotRequired`, or `Unresolved`. It does **not** establish completion of functional verification, security verification, release review approval, or emergency approval. V3 has request transitions for the four concerns, but no completion transitions. A requested activity, a successful HTTP response, or a policy result is not proof of completion.

The current architecture has useful pieces for a later implementation:

- The Asteria registry durably allocates release generations and retains material digests, workflow instance IDs, assessment work, original Decision API responses, parent revisions, expected-operation IDs, request identities, status, and recovery metadata.
- The generic State Machine can register expected operations, durably record inbound envelopes, correlate observations, check the operation's expected parent revision and declared observation type, check required evidence *presence*, serialize input processing, reject terminal parents, and identify identical versus conflicting results for a resolved operation.
- The generic Harness can execute OpenAPI-described provider operations using a configured provider, API-description version, operation key, credential binding, and idempotency key. Its existing recovery rules preserve unknown outcomes rather than retrying ambiguous mutating work.
- V3 parent supersession is terminal, and the Asteria registry checks current-generation admission before its coordinator performs work. These are complementary protections, not substitutes for work-specific identities or result binding.

The important limits are equally material:

- The generic State Machine does not know Asteria release IDs, assessment generations, material digests, policy obligation statuses, provider identity claims, human identities, or roles. Most evidence values are opaque JSON; generic required-evidence checks establish presence, not semantic correspondence.
- `sourceIdentity` is a string bound to an expected operation and compared to the caller's claimed source. The shared trusted-ingestion key establishes that a caller has the shared credential; it does not authenticate a particular person, prove provider origin, or establish roles.
- State Machine commands are not human-authenticated. Asteria's declared direct event endpoint is blocked for input-protected definitions, but command submissions remain available without the trusted-ingestion key. Expected-operation registration and trusted observation ingestion require that shared key.
- No Asteria verification provider, approval authority registry, identity provider, role policy, or completion evidence flow currently exists in this repository. Gate 14 Milestone 4C tests use controlled Decision API fixtures; the accompanying integration document reports the live policy run as owner-reported, not independently run in that authoring session.
- Generic Harness inspection does not durably archive a full provider response body. If the Harness response is lost before its execution ID is durably captured, the published interface has no lookup-by-idempotency-key route. Asteria must preserve the original usable response when it has one and retain `OutcomeUnknown` when it cannot establish one.

The suggested Milestone 5 sequence is:

1. **5B — Durable synthetic verification work and completion observations:** separate functional and security provider operations, durable Asteria work identities, Harness-backed execution through existing generic APIs, result validation, and expected-operation observations.
2. **5C — Independent synthetic human approvals:** an Asteria-specific identity/role gateway, two distinct approval authorities, immutable approval decisions, and adversarial replay/authorization checks.
3. **5D — Obligation-to-concern integration:** introduce an immutable V4 definition, explicit applicability and completion transitions, parent coordination guards, and adversarial evidence/recovery tests. This milestone must not add deployment execution or deployment readiness.

## 2. Existing capability inventory

| Area | Existing capability | Precise limitation relevant to Milestone 5 |
|---|---|---|
| Asteria business contract | [AST-BR-001](../../training-data/asteria/requirements/asteria-release-requirements-v1.md#L40) requires independent functional and security verification, explicit human approval, repeat checks for amendments, and refusal of duplicate/conflicting evidence. [AST-POL-001](../../training-data/asteria/policies/asteria-release-policy-v1.md#L42) defines when functional, security, Release Reviewer, and Emergency Release Authoriser obligations apply. | The documents define policy obligation applicability, not evidence that an activity occurred. AST-POL-001 says reviewer authentication/roles and the scope of one person holding multiple roles are unspecified. |
| Asteria V3 definition | [V3 parent states and input policy](../../definitions/asteria-release-governance-v3.json#L1) include `Draft`, `UnderAssessment`, obligation-identification states, `AssessmentUnresolved`, and terminal `Prohibited`, `Cancelled`, and `Superseded`. It declares request commands for each verification/approval concern and assessment/supersession observations. | V3 has no verifier-result or approval-result observation mappings, no concern completion transitions, and no completion/ready parent transition. The two verification concerns have `Passed`, `Failed`, `Indeterminate`, and `Superseded` states; the approval concerns have `Approved`, `Rejected`, and `Unresolved`, but those states are unreachable through V3 transitions. `NotRequested` is not equivalent to `NotRequired`. |
| V3 parent coordination | Parent cancellation transitions require `CancellationIntent=Requested` ([V3 transition guards](../../definitions/asteria-release-governance-v3.json#L255)). Generic guards can inspect declared concern states; the runtime evaluates a concern-state vector ([`TransitionEvaluator`](../../src/DurableStateMachine.Core/TransitionEvaluator.cs#L66), [`MachineStore`](../../src/DurableStateMachine.Persistence/MachineStore.cs#L2170)). | The only V3 parent guard coordinating a concern is cancellation. There is no guard requiring satisfactory current-generation verification/approval outcomes. Generic concern transitions do not increment the parent revision; each concern has its own revision and immutable history. |
| Asteria registry and assessment coordinator | The registry persists release, generation, assessment-work, pointer, amendment, and supersession records ([registry schema](../../clients/DurableStateMachine.AsteriaClient/AsteriaReleaseRegistry.cs#L100)). `AsteriaAssessmentWork` binds release/generation/digest/workflow/definition/parent revision/expected operation/Decision API idempotency key and preserves original response/evidence ([record contract](../../clients/DurableStateMachine.AsteriaClient/AsteriaReleaseRegistry.cs#L52)). `AsteriaAssessmentCoordinator` uses leases, checkpoints, request/response persistence, and resume logic ([coordinator](../../clients/DurableStateMachine.AsteriaClient/AsteriaAssessmentCoordinator.cs#L26)). | This durable work model is for assessment, not verification or approval. A registry lease serializes cooperating coordinators sharing that registry; it does not fence an already-dispatched cross-service call or protect another registry copy. |
| Current-generation checks | `AssertCoordinatorAdmissionAsync` checks release, generation, `Operational` registry state, lease token, and expiry ([registry admission](../../clients/DurableStateMachine.AsteriaClient/AsteriaReleaseRegistry.cs#L888)). The Decision API client validates release ID, generation, and material digest in the assessment response ([response binding](../../clients/DurableStateMachine.AsteriaClient/AsteriaDecisionApiClient.cs#L88)). | Those checks are Asteria-coordinator behavior. A direct generic State Machine caller can bypass the Asteria registry. A check before a request cannot cancel or fence an already in-flight request. |
| Expected operations and observations | The API exposes command, observation, expected-operation, receipt, attempt, history, replay, and staged-result routes ([API routes](../../src/DurableStateMachine.Api/Program.cs#L167)). Expected operations bind instance ID, operation ID, observation type, source string, parent expected revision, required-evidence names, status, and result digest ([record](../../src/DurableStateMachine.Persistence/MachineDbContext.cs#L769)). | Generic expected operations are scoped to a workflow instance; they do not carry release ID, generation, material digest, concern ID, provider authentication, or actor identity. Required evidence is checked for non-null presence; its values must be checked by an Asteria-specific coordinator. |
| Input evidence and replay | Inbound envelopes persist message/operation/observation IDs, input type, authority marker, correlation, claimed source, expected revision, definition identity, payload/evidence, digest, status, and receipt time ([input record](../../src/DurableStateMachine.Persistence/MachineDbContext.cs#L729)). Evaluation attempts retain evaluation evidence and digest ([attempt record](../../src/DurableStateMachine.Persistence/MachineDbContext.cs#L704)); historical replay reconstructs accepted history from the bound immutable definition and records ([replay](../../src/DurableStateMachine.Persistence/HistoricalReplay.cs#L8)). | Replay proves consistency of stored local evidence and transitions, not truth of a provider result or authentication of an asserted identity. The generic State Machine does not independently verify policy applicability or provider/approver credentials. |
| Immutable and per-attempt facts | Instance facts are captured at creation; `TransitionEvaluationContext` separates them from per-attempt facts and rejects attempts to shadow an immutable instance fact ([context](../../src/DurableStateMachine.Core/EvaluationContext.cs#L57)). Asteria instances bind release ID, generation, digest, and material facts ([fact construction](../../clients/DurableStateMachine.AsteriaClient/AsteriaStateMachineClient.cs#L743)). | Per-attempt facts and evidence can carry a claimed result, but the generic evaluator does not prove that the claimed release/generation/digest corresponds to the Asteria instance. The Asteria coordinator must compare them to registry and immutable instance facts. |
| Generic Harness and provider extension | The Harness imports an OpenAPI provider description and executes a configured operation through a provider-agnostic pipeline; its supported extension surface is description registration, callable-operation selection, execution, inspection, evidence, and attempts ([supported scope](../../reference/harness-supported-scope.md#L1), [Harness API contract](../../reference/harness.openapi.yaml)). State Machine integration binds provider identity, provider ID, API-description version ID, operation key, and optional credential binding ([integration bindings](../../src/DurableStateMachine.Api/HarnessIntegration.cs#L18)). | There is no built-in Asteria verification capability. The State Machine `HarnessEffectCoordinator` maps only the existing generic-effect capabilities to observation types ([mapping](../../src/DurableStateMachine.Api/HarnessIntegration.cs#L942)); adding Asteria verification should be Asteria-specific orchestration/configuration, not an invented Harness feature or Asteria branch in generic runtime code. |
| Harness recovery | A local effect intent and attempt are persisted before dispatch; Harness execution identity and remote evidence are inspected on resume ([execution flow](../../src/DurableStateMachine.Api/HarnessIntegration.cs#L409)). The adapter does not automatically create a replacement execution after an unresolved dispatch. | Harness evidence includes sanitized metadata, not the full durable provider response. If no execution ID was captured, the current interface has no lookup by idempotency key. A known `OutcomeUnknown` is not safe evidence of pass or fail. |
| Orion synthetic provider precedent | The synthetic provider host exposes configured synthetic capability endpoints and OpenAPI descriptions ([provider host](../../src/DurableStateMachine.ProviderHost/Program.cs#L1)); Gate 13's runner uses the Harness and the existing State Machine adapter ([Gate 13 design](../gate-13-design.md#L1)). | Those providers implement Orion effects, not Asteria verification. Their provider names are configured identities, not cryptographic attestations. Gate 13 does not establish a human identity/approval interface. |
| Authentication boundary | The generic API compares the configured `DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY` with a supplied header ([key check](../../src/DurableStateMachine.Api/Program.cs#L610)). Expected-operation registration and observation ingestion require it; direct command submissions do not ([routes](../../src/DurableStateMachine.Api/Program.cs#L167)). | This is a shared-secret trusted-ingestion boundary, not user authentication, role authorization, or provider attestation. Direct `events` is restricted for input-protected definitions, but a key holder can call generic trusted routes without an Asteria pointer check. |
| Gate 14 status | Gate 14 4C records current-generation assessment, material amendment, terminal supersession, and evidence export ([integration record](./gate14-milestone4c-integration.md#L1), [adversarial results](./gate14-milestone4c-adversarial-results.md#L1)). | No verification completion, human approval, deployment, health verification, or final completion has been implemented or tested. Tests use Decision API fixtures; live assessment execution is owner-reported. |

## 3. Verification execution architecture

### Options compared

| Criterion | A. Independent providers invoked through Generic Harness | B. External synthetic verification submitted as trusted observations |
|---|---|---|
| Evidence provenance | Stronger training-grade chain: Asteria work identity → configured Harness provider/API-description/operation → Harness execution ID/attempt/evidence → normalized provider response archived by Asteria → accepted State Machine observation. The configured provider identity remains a binding/claim, not cryptographic source attestation. | The State Machine can prove which expected operation accepted a submitted envelope and which shared trusted-ingestion credential was used. Unless the gateway independently authenticates or invokes the provider, provider identity, execution, and report origin remain assertions in the submission. |
| Operation identity/idempotency | Use one stable identity derived from release, generation, concern, and activity attempt. Persist it before invocation and use it as the Harness idempotency key. Bind the Harness execution ID and provider operation ID to that work. | Expected-operation ID, message ID, and observation ID can correlate the result. The external performer must also supply a stable operation identity and its own idempotency/retrieval contract; the State Machine does not initiate or prove execution. |
| Independent failure handling | Harness and provider failures are separately inspectable. Asteria must distinguish provider rejection, provider-declared failed verification, transport failure, and unknown outcome. | Provider execution and submission are decoupled. Provider outages need a provider-owned work ledger/retrieval path; the Asteria coordinator otherwise sees only missing or late evidence. |
| Duplicate, late, conflicting results | Harness execution idempotency plus State Machine expected-operation result digests handle repeated results. Asteria must still reject conflicting result content and late/superseded generations. | The generic State Machine identifies identical duplicate versus conflicting results after an expected operation is resolved. Before resolution, Asteria must ensure there is one canonical operation per activity and must not treat a claimant-supplied identity as origin proof. |
| Unknown external outcome | Harness explicitly preserves uncertain remote outcomes. If the execution ID is known, inspect it; if no ID was received, current published routes cannot retrieve by idempotency key. Do not retry an ambiguous operation; require reconciliation or manual resolution. | The State Machine receipt can remain unresolved, but it does not know whether the verification ran. Safe recovery requires an external provider ledger/query interface or a durable human investigation outcome. |
| Restart recovery | Asteria registry/work record owns request and binding; Harness owns execution/attempt evidence; State Machine owns input receipt, attempt, concern state, and accepted transition. Recovery reconciles all three without re-execution of known ambiguous work. | Asteria work record and State Machine receipt survive restart. Provider-side execution recovery is outside the runtime and must be designed separately; no provider execution cannot be inferred from a missing observation. |
| Generic runtime compatibility | Compatible through existing Generic Harness OpenAPI-description/configuration/execution contracts and existing State Machine expected-operation/observation contracts. Requires an Asteria-specific orchestrator and verification contract; no generic runtime branch or new Harness feature. | Compatible with the generic input contract as-is, provided an Asteria-specific coordinator registers the operation and validates exact evidence binding. It has lower initial cost but weaker invocation provenance and moves recovery requirements to the provider/gateway. |
| Implementation cost | Higher: additional provider definitions/hosts, Harness bindings, Asteria durable work/attempt/recovery records, response validation/archive, and cross-service fault tests. | Lower for a controlled fixture path, but sufficient provider-owned idempotency, authentication, durable execution, and reconciliation can erase the apparent cost advantage. |

### Recommendation

Use **Option A for 5B**, through existing Generic Harness extension mechanisms only. Define two separately configured synthetic providers—one functional, one security—with independent provider identities, operation keys, and test evidence. Invoke them from an Asteria-specific coordinator. Do not add a provider-specific capability to the generic State Machine, and do not change Generic Harness semantics. The coordinator should submit each validated result through the existing expected-operation and trusted-observation boundary; Harness success or HTTP 2xx alone must never mean verification passed.

The reason to introduce Harness execution in Milestone 5 is that verification itself is external work: its durable intent, duplicate semantics, remote uncertainty, and restart recovery need to be exercised before the later deployment gate. The existing Harness already supports OpenAPI-described providers, configured provider/API-description/operation bindings, idempotency, execution attempts, inspection, and non-retrying unknown outcomes. This recommendation does **not** assume an Asteria integration exists today; the existing State Machine effect coordinator is Orion-oriented and cannot be reused as a drop-in verification adapter.

Option B remains useful for an explicitly controlled fixture or as a provider-facing submission contract. It must not be presented as stronger provenance than it provides: the shared ingestion key authenticates only the gateway credential, and caller-supplied provider names/results are still claims. If a provider executes outside the Harness, its durable work identity, idempotency, result retrieval, and authentication contract become additional requirements.

For either option, archive in Asteria's durable work record the original validated response/result, a canonical digest, the provider identity and configuration version, occurrence and receipt timestamps, Harness execution ID if available, and validation disposition. The Harness itself records sanitized evidence and not the full provider response. If dispatch may have occurred but result cannot be established, retain `OutcomeUnknown`; never convert it into Passed, Failed, or a safe-to-retry state by default.

## 4. Human approval architecture

### Synthetic identity and authority model

Create an Asteria-specific authority gateway and a durable authority registry. The registry maps a **gateway-authenticated subject** to an immutable synthetic principal ID and one or more explicitly granted role authorities:

- `ReleaseReviewer` for `ReleaseReviewApproval`.
- `EmergencyReleaseAuthoriser` for `EmergencyApproval`.

The incoming approval body must not establish the actor or role. The gateway authenticates the synthetic principal from a test-only credential (for example, pre-provisioned signed test tokens or distinct test client credentials), resolves roles from the registry, and records the authentication method and registry/role version. It then submits the State Machine observation using the gateway's downstream shared trusted-ingestion credential. The recorded human authority is the gateway's authenticated result, not a `source` or `role` string supplied by the browser/client.

This is **training-grade synthetic trust** only. The repository has no production identity provider, role registry, or authenticated end-user boundary. A shared key held by a gateway would prove only that the gateway credential was presented to the State Machine; it would not, on its own, prove which human acted. Production assurance would require a separately designed, deployed identity provider, authenticated gateway, credential lifecycle, protected service boundary, and audit controls.

### Decisions, duplicates, changed decisions, and separation of duties

- Record each approval as an immutable decision event: principal, authority role, `Approved`/`Rejected`/`Unresolved`, generation binding, stable approval-attempt ID, submission/message ID, authentication method, authority-registry version, timestamp, rationale/evidence digest, and accepted State Machine receipt/attempt.
- An exact repeat of the same logical submission should be idempotent and produce a duplicate receipt, not a second decision.
- A changed decision under the same approval-attempt identity is a conflict. Preserve both the original and the conflicting submission; do not overwrite or reverse the accepted decision. A new approval attempt requires an explicit policy-defined identity and transition semantics.
- A result for the wrong concern or role is refused by the Asteria gateway before State Machine submission. Different observation types/expected-operation IDs must be used for Release Review and Emergency Approval; an approval for one concern must never be replayed as evidence for the other.
- AST-POL-001 requires distinct **authority obligations** for reviewer and emergency authoriser. It says one person may fulfil both only if Asteria's separate authorisation policy explicitly permits it; it does not itself impose a universal two-person rule. The separate policy is absent. Until resolved, rejecting the same principal for both obligations is a **proposed conservative safeguard**, not an existing business requirement.
- The existing V3 `Approved` and `Rejected` concern states are terminal, while `Unresolved` is not. A later changed decision cannot be accepted as a second transition from a terminal state in V3. V4 must retain the immutable first result and state clearly whether a new attempt is allowed; do not silently reopen an approval.

### Supersession and wrong-generation approval

An approval gateway must load the current Asteria release pointer and require an exact match for release ID, generation, material digest, workflow instance, concern, expected-operation ID, and expected parent revision before it accepts a submission. It must also check that the corresponding expected operation is still outstanding. It should reject an old-generation or wrong-concern approval even if the actor is valid for the role.

The terminal parent state then independently prevents a late result from advancing the old instance. Current-generation admission prevents cooperating Asteria coordinators from beginning new work on an inactive generation. Work-specific IDs and binding validation prevent a result for another activity or concern from being reassigned. None alone suffices: terminality does not authenticate an actor or prove generation correspondence; a current pointer check cannot cancel an in-flight call; and an opaque operation ID does not prove the embedded material/result binding.

## 5. Generation-specific evidence and enforcement split

### Required evidence binding

Every verification or approval work item and its result should bind the following immutable tuple:

1. Release ID.
2. Assessment generation.
3. Material digest.
4. Workflow instance ID and immutable definition ID/version/content digest.
5. Concern ID and obligation type.
6. Expected-operation ID and stable activity-attempt ID.
7. Parent revision at registration, plus concern revision where available.
8. Original provider result or approval submission and a canonical result/evidence digest.
9. Performing provider identity/configuration version or synthetic approver principal/authority-registry version.
10. Stable submission/message/observation identities and idempotency key.
11. Provider/Harness execution ID and timestamps, where applicable; approver authentication context and timestamp for approval.
12. Asteria coordinator validation decision and State Machine receipt/attempt/history references.

The Asteria registry/work ledger is the authoritative owner of the release/generation/material/work binding. The State Machine owns accepted workflow state and transition evidence. The Harness owns provider execution attempts and sanitized Harness evidence. A synthetic identity gateway owns authentication/role resolution and approval submission records. No single component's record substitutes for the others.

### What the generic State Machine enforces

The generic State Machine currently enforces or records:

- Instance and immutable definition ID/version; definition digest at registration.
- Unique expected-operation ID **within an instance**, expected observation type, expected parent revision, required-evidence key names, source identity string, and operation status/result digest.
- For an observation: the expected operation exists, observation type matches, caller-claimed source string equals the expected source string, expected parent revision equals both current instance revision and input revision, and required evidence keys are present/non-null.
- A resolved operation treats an identical result digest as `Duplicate` and a different result as `Conflicting`.
- Parent terminality prevents further accepted parent or concern activity. Concern transitions have their own concern revision and history but do not increment the parent instance revision.
- Input receipts, attempt evaluation evidence, accepted history, and historical replay records.

It does **not** enforce:

- Authentication or role authorization of the asserted source/person/provider.
- Whether a provider actually ran a test or whether its evidence proves a pass.
- Semantic equality of `releaseId`, generation, digest, workflow/concern/activity IDs in arbitrary evidence JSON and the actual Asteria registry/instance.
- That the policy made the concern Required, NotRequired, or Unresolved.
- That the expected operation was created only for the current Asteria release pointer.
- A per-concern expected revision at operation registration (the generic expected operation binds the parent revision; accepted concern history records its own concern revision).
- That an original external result is authoritative rather than merely durable and internally consistent.

Those checks belong to the Asteria-specific verification/approval coordinator or gateway. In particular, key-presence validation is not binding validation, and a `sourceIdentity` label is not identity authentication.

### Amendment while work is outstanding

On amendment, suspend admission for the old generation, reserve a new generation with a new material digest and workflow instance, and preserve the old work and evidence as historical. Do not rewrite or migrate old verification/approval operations into the replacement. Require new expected-operation and idempotency identities for every applicable activity of the new generation.

The existing amendment protocol has a durable `AmendmentPending`/`ActivationIncomplete` gate and a terminal V3 `Superseded` state. However, its lease cannot retract a request already sent to a provider or an approval gateway. Therefore:

- An in-flight old result remains bound to the old workflow and operation.
- A current-generation check prevents the cooperating coordinator from accepting/storing it as current work.
- Terminal supersession makes the old State Machine instance unable to accept a late result, including after a pending receipt is resumed.
- Stable work-specific IDs and exact evidence binding prevent accidental reuse or cross-concern/cross-generation replay.

The concern projections of a V3 instance are not automatically rewritten to `Superseded` when the parent is superseded; they remain historical projections. Parent terminality is the refusal boundary. V3 does not declare a general cascade-supersede mechanism for its concerns.

## 6. Obligation-to-concern mapping and definition evolution

### Applicability and result semantics

| Policy applicability | Concern meaning | Counts as satisfactory? |
|---|---|---|
| `Required` | Explicitly request the activity and await an independently evidenced result. Absence of evidence means pending/not complete. | Only an accepted current-generation `Passed` (verification) or authorized `Approved` (approval). |
| `NotRequired` | Record an explicit not-applicable/not-required outcome. Do not infer this from `NotRequested` and do not fabricate a `Passed`/`Approved` result. | Yes, for obligation coordination only, if policy evidence for this generation says NotRequired. It is not proof an activity ran. |
| `Unresolved` | Preserve obligation applicability as unresolved; do not infer NotRequired or success. | No. Require policy resolution or retain blocked/unresolved status. |

For a **required verification**, `Passed` is satisfactory; `Failed` is a negative outcome and blocks; `Indeterminate` preserves unknown/unavailable outcome and blocks. For a **required approval**, `Approved` is satisfactory only after authority validation; `Rejected` blocks; `Unresolved`, missing evidence, or silence does not approve. Duplicate identical evidence is idempotent; contradictory evidence is a conflict to preserve and investigate, not an overwrite.

### V3 limitations and V4 proposal

V3 is a request-only concern definition: the four concern mappings contain only request events and each concern contains only a `NotRequested` → `Requested` transition. The states for results are present as declarations but are not reachable. There is no Asteria result observation mapping, no mapping from policy applicability to an explicit concern outcome, and no parent coordination guard for all four obligations.

Create **`asteria-release-governance@4.0`** as a new immutable definition; leave V3 registered and unchanged. V4 should, at minimum:

- Declare separate, typed observation mappings and result transitions for each verification and approval concern.
- Represent policy applicability explicitly for every concern: `Required`, `NotRequired`, or `Unresolved`. A `NotRequired` result must be evidence-bound to the assessment; `Unresolved` must never be treated as an absent/false requirement.
- Keep applicability status distinct from completion result. For example, a concern may have explicit `NotRequired`/`ApplicabilityUnresolved` states in addition to `Requested` and result states. Avoid overloading `NotRequested` as a policy result.
- Define verification results `Passed`, `Failed`, and `Indeterminate`; approval results `Approved`, `Rejected`, and `Unresolved`. Bind each result transition to an expected operation and required evidence appropriate to that concern.
- Preserve immutable accepted results. Where retry/appeal is allowed, define a distinct attempt identity and explicit transition semantics; do not rely on overwriting a terminal result.
- Include parent supersession and terminal behavior intentionally. Until such V4 transitions exist, continue to use parent terminality to refuse late results; do not assume concern projections are auto-superseded.

A future parent coordination guard can be expressed using the generic `concernState` guard and boolean composition: each concern must be in its satisfactory result state **or** its explicit `NotRequired` state. Any `ApplicabilityUnresolved`, `Indeterminate`, `Failed`, `Rejected`, `Requested`, or untouched state fails the conjunction. This is a proposed generic guard composition, not a current V3 capability result. It must be evaluated only after V4 has accepted and bound applicability/results. Milestone 5D may demonstrate a non-deployment “obligations recorded/satisfied” coordination transition if needed, but must not add `ReadyForDeployment`, deployment authorization, deployment execution, health verification, or final completion.

## 7. Security and authority matrix

| Activity | Who can request | Who performs | Who may submit result | Authentication/authority source | State Machine verifies | Asteria coordinator/gateway verifies | Still merely asserted / limitation |
|---|---|---|---|---|---|---|---|
| Functional verification | Asteria coordinator or an authorized release workflow caller; V3 currently permits an unauthenticated declared command to request it. | Independently configured synthetic functional provider through Harness (recommended 5B). | Asteria verification coordinator using trusted ingestion. | Harness provider/API-description/operation binding for training; trusted gateway-to-State-Machine shared key. | Expected operation/type/revision/source-string match, required key presence, duplicate/conflict result, terminal parent. | Current generation, digest, definition, concern, work ID, Harness execution, provider binding, result schema/semantics, evidence digest, and `Passed`/`Failed`/`Indeterminate` mapping. | Configured provider identity is not cryptographic attestation; HTTP success is not a pass. |
| Security verification | Same request path, independent of functional verification. | Separate synthetic security provider and operation. | Asteria verification coordinator. | Separate configured provider binding; gateway shared key only for downstream ingestion. | Same generic operation/receipt checks. | Separate identity and operation from functional provider; exact generation/material/concern binding; validate security report and outcome. | A provider's claim of independent security testing cannot be inferred from its name or result fields. |
| Release Review Approval | Asteria approval gateway after current required obligation is established; do not treat direct generic command access as an authenticated reviewer request. | Authenticated synthetic Release Reviewer. | Asteria authority gateway, which submits a validated immutable observation. | Synthetic principal credential resolved against Release Reviewer role registry. Production would require an actual IdP and protected gateway. | Expected operation/type/revision, claimed source string, evidence presence, duplicate/conflicting result, terminal parent. | Authenticated principal, role, current generation/digest, concern, one-time approval identity, decision immutability, current expected operation. | User-supplied name/role, shared ingestion key, or `source` header alone is not reviewer authentication. |
| Emergency Approval | Same gateway, only when policy requires emergency authority. | Authenticated synthetic Emergency Release Authoriser. | Asteria authority gateway. | Distinct registry role and credential/claim path. | Generic checks as above; no knowledge of emergency authority. | Validate Emergency role and exact concern; enforce separately authorized obligation. Apply same-person dual-role rule only after Asteria's separate authorisation policy is supplied. | AST-POL-001 does not settle whether a single person can fill both roles; requiring different people before confirmation is a proposed safeguard. |
| Direct State Machine command | Any caller able to reach the generic API; command route has no trusted-ingestion-key check. | N/A | Not a result authority. | No user authentication/role check in this route. | Declared command mapping, revision, evaluator/guards, durable attempt. | Asteria-specific requester authorization is absent today. | Command `source` values are asserted. Network isolation is not business authorization. |
| Direct trusted observation/expected-operation call | Any caller possessing the configured shared ingestion key and network access. | Caller-supplied. | Same caller. | Shared secret header only. | Key presence/value at API boundary, expected operation and generic input rules. | No Asteria registry/pointer or human-role check in generic API. | A key holder can submit Asteria-shaped results directly; the credential does not authenticate provider or approver identity. |

**Access-control consequence:** Keep the generic API network-restricted and the trusted key isolated to the Asteria coordinator/gateway. Do not expose it to verification providers, approval users, or browser clients. Route all provider and human submissions through the Asteria-specific validator. This is a training-grade control recommendation, not a claim that the current API enforces production access control.

## 8. Crash and concurrency outcome matrix

| # | Failure/race | Durable owner and expected evidence | Safe outcome |
|---:|---|---|---|
| 1 | Work request persists but provider invocation has not occurred. | Asteria work ledger records `Requested`/`DispatchPending`; Harness has no execution or attempt yet. State Machine expected operation is registered but unresolved. | Resume may dispatch only when durable records prove no dispatch was initiated; reuse the same stable operation/idempotency identity. A request record alone is not execution evidence. |
| 2 | Provider completes but coordinator loses the response. | Harness owns execution/attempt evidence; Asteria owns its invocation binding. If Harness execution ID was captured, inspect it. If not, current API has no lookup-by-idempotency-key route. | Reconcile by known execution ID/provider query. If the outcome cannot be established, persist `OutcomeUnknown`, do not infer pass/fail and do not send a new operation with a fresh ID. |
| 3 | Observation is accepted but local acknowledgement is lost. | State Machine owns receipt, resolved expected operation, result digest, attempt/history; Asteria ledger may still show submission pending. | Retry the exact immutable envelope with the same stable IDs, then inspect receipt/operation/history. Matching result is a duplicate, not a second transition. Advance Asteria acknowledgement only after reconciliation. |
| 4 | Two verification outcomes arrive concurrently. | State Machine serializes durable input processing. Independent concerns have separate concern revisions and can advance without incrementing parent revision; same expected operation is resolved once. | For one operation, first accepted result resolves it; exact duplicate is duplicate and different result conflicts. For distinct operations targeting one concern, Asteria must prevent multiple canonical result operations; preserve/refuse the race rather than choosing a “last” outcome. |
| 5 | An approval is submitted twice. | Approval gateway ledger and State Machine receipt/result digest. | Exact same decision and submission identity is idempotent. Changed decision using the same identity is a conflict; never overwrite. New attempt is permitted only by explicit policy and V4 transition rules. |
| 6 | Forged or unauthorized approval is submitted. | Gateway authentication/role audit; generic API records an untrusted/refused input if sent without key. | Gateway rejects before trusted submission. A State Machine `sourceIdentity` match is not authorization. If a shared-key holder bypasses the gateway, generic runtime alone cannot detect the forged actor; isolate the key and treat this as a trust-boundary incident. |
| 7 | One approval is replayed into the other approval concern. | Expected operation, observation type, concern mapping, authority registry, and activity-specific work identity. | Reject at the Asteria gateway when role/concern/operation binding differs. Generic type/operation checks help only when the two concerns have distinct mappings and IDs; generic runtime does not interpret approver role claims. |
| 8 | Material amendment begins while work is outstanding. | Asteria registry owns pointer/amendment state; Harness/provider owns already dispatched work; State Machine owns old instance/receipt. | Suspend old-generation admissions, allocate new generation with independent work IDs/digest, and do not transfer results. In-flight external work cannot be canceled by the registry lease. |
| 9 | Late result arrives after old instance is `Superseded`. | Old State Machine instance and inbound receipt; Asteria pointer identifies current generation. | Keep old result/receipt inspectable but refuse transition on terminal parent. Never apply it to the new instance; new generation needs new work and expected operation. |
| 10 | Pending receipt resumes after supersession. | Original inbound receipt and expected operation remain durable; parent state is terminal. | Re-evaluate the original envelope/authority context; terminal parent refuses it. Do not rewrite the receipt as accepted or relabel its generation. Gate 14 4C has tested this recovery pattern for assessment/supersession, not yet for verification/approval. |
| 11 | One required verification fails while other activities succeed. | Each concern and evidence record are independently durable. | Preserve successes and the failure. `Failed` is not satisfactory; other passes cannot compensate. Parent coordination conjunction remains false. No automatic retry unless new policy/definition semantics explicitly permit it. |
| 12 | Provider returns `OutcomeUnknown` and cannot safely be retried. | Harness/provider execution and Asteria work record; expected operation remains unresolved unless an authoritative result is accepted. | Preserve unresolved status and escalate/manual reconcile. Do not call it Passed or Failed and do not replay a possibly mutating provider operation under a new ID. |

The generic State Machine provides transaction and revision protection for local processing; it cannot create a transaction spanning the Asteria registry, Harness, provider, authority gateway, and State Machine. Recovery must therefore reconcile durable identities and status at each owner rather than infer distributed success from an HTTP response or lease.

## 9. Minimal implementation sequence and acceptance conditions

### 5B — Durable verification work and completion observations

**Scope:** Functional and security verification only. Use two independent synthetic providers via existing Generic Harness APIs, Asteria-specific orchestration, and existing State Machine expected operations/observations. No generic runtime/Harness feature and no approval/deployment logic.

**Necessary contracts/fixtures:** Two OpenAPI provider descriptions with separate IDs, API-description version IDs, operation keys, and configured provider identities; a versioned verification request/result contract; Asteria work ledger schema/contract; stable work/attempt/message identities; trusted gateway configuration isolated to the coordinator; positive, negative, malformed, duplicate, conflict, unavailable, late, and provider-unknown fixtures.

**Acceptance conditions:**

- Persist generation/digest/workflow/definition/concern/parent-revision/expected-operation/idempotency binding before dispatch.
- Use a stable Harness idempotency key for a logical attempt; persist known Harness execution identity and its binding.
- Validate the result envelope and business outcome independently of HTTP status; preserve the original response and digest in Asteria storage before acknowledging it.
- Submit `Passed`, `Failed`, or `Indeterminate` through a registered, concern-specific expected operation with exact generation binding checked by the Asteria coordinator.
- Exact duplicate result is idempotent; changed result under one operation identity is preserved as conflicting evidence and never overwrites an accepted result.
- Restart tests cover pending-before-dispatch, known execution resume, response lost before execution ID persistence, accepted observation with lost acknowledgement, concurrent results, and supersession while a call is in flight.
- An unknown outcome is never auto-retried as a fresh operation and never becomes success-shaped.

### 5C — Independent synthetic human approvals and authority checks

**Scope:** Release Reviewer and Emergency Release Authoriser approvals through an Asteria-specific synthetic authority gateway. No production identity claims and no deployment authorization.

**Necessary contracts/fixtures:** Synthetic principal/role registry; test-only credential/authentication adapter; explicit role grants; immutable approval-attempt and submission contract; exact operation/result bindings; test actors with reviewer-only, emergency-only, dual-role, unauthorized, revoked, and missing identities. Record dual-role behavior as unresolved until the separate Asteria authorisation policy is supplied.

**Acceptance conditions:**

- Actor and role are derived from authenticated synthetic gateway context, never trusted from the submission body.
- Role authorization is specific to the approval concern, release generation, digest, expected-operation ID, and workflow instance.
- Approval, rejection, and unresolved outcomes are durable and distinct; no response or pending request cannot approve.
- Exact duplicate submission is idempotent; changed decision under the same approval identity conflicts; cross-concern replay and wrong-generation approval are refused.
- Approval submitted after supersession is retained/refused and cannot advance old or new instances.
- Tests explicitly demonstrate that the shared State Machine ingestion credential does not establish the human identity.
- Documentation labels all credentials, authorities, and assurance as synthetic training trust.

### 5D — Integrated obligations, concerns, and adversarial evidence

**Scope:** New immutable `asteria-release-governance@4.0`; wire policy obligations to four concerns and define outcome-specific transitions and parent coordination guards. Do not alter V3 in place or add deployment readiness.

**Necessary contracts/fixtures:** V4 JSON definition and content digest; migration/creation strategy for new V4 instances (no silent reinterpretation of V3); decision response fixtures for all three applicability statuses; verification and approval result fixtures; accepted, refused, duplicate, conflicting, stale, and replayed evidence; parent/concern history replay cases.

**Acceptance conditions:**

- V4 exposes explicit `Required`, `NotRequired`, and `Unresolved` applicability semantics separately from activity completion states.
- Required obligations are satisfactory only for verified `Passed`/`Approved` results; `Failed`, `Rejected`, `Indeterminate`, `Unresolved`, missing, or pending evidence cannot satisfy the parent guard.
- NotRequired requires an accepted, generation-bound assessment result; it is not inferred from `NotRequested` or absence of evidence.
- The parent coordination guard requires each concern to be in its satisfactory or explicit NotRequired state. It is tested against every obligation combination and concurrent concern completion.
- Material amendment, late result, cross-generation/cross-concern evidence, conflicting result, direct command/observation bypass, and process restart are adversarially tested.
- Replays verify stored evidence binding and transition history without invoking providers or identity systems.
- No `ReadyForDeployment` state/transition, deployment call, health check, or final completion is introduced.

## 10. Unknowns, unsupported assumptions, and blockers

1. **Approval separation policy:** AST-POL-001 makes Emergency Approval a distinct authority obligation but leaves same-person dual-role authorization to a separate Asteria policy that is not supplied. It is a genuine policy blocker to accepting the same principal for both obligations; a temporary different-person rule would be a proposed safeguard.
2. **Authentication source:** No user identity provider, production reviewer authentication, role registry, revocation model, or credential-lifecycle contract exists. Synthetic credentials can test the gateway boundary only.
3. **Provider assurance:** Current synthetic provider identities are configuration labels. The repository does not establish provider signing, provider key management, execution attestation, or a production provenance policy.
4. **Verification semantics:** The supplied policy specifies required obligations and “satisfactory” verification, but no detailed functional/security test plans, evidence schemas, evidence thresholds, or test-result authority hierarchy. Those contracts need fixtures/approval before 5B can claim more than synthetic behavior.
5. **Retry/retest policy:** AST-BR-001 requires duplicate/conflicting evidence handling but does not define whether a failed/indeterminate verification or rejected/unresolved approval may be repeated, who authorizes it, or how appeals work. V4 must not invent retry semantics silently.
6. **Decision API availability:** The Asteria client validates assessment response shape/binding, but the actual Decision API implementation is not present in this repository. Gate 14 4C tests use fixtures; the live run is documented as owner-reported. Milestone 5 integration must retain this boundary and avoid claiming policy engine verification from contract fixtures.
7. **Harness recovery limitation:** Harness full provider response bodies are not durable Harness evidence, and no lookup-by-idempotency-key route exists when the execution ID is lost. 5B must test this exact window and preserve `OutcomeUnknown`; a stronger provider-side query contract would be additional work.
8. **Cross-service fencing:** Registry leases do not fence requests already in flight across registry, Harness/provider, and State Machine. Generation-bound work IDs, checks before dispatch/acceptance, terminal parent refusal, and durable reconciliation are all necessary; none makes these services transactional.
9. **Generic API access control:** Direct commands are not authenticated, expected-operation/observation access uses a shared secret, and generic callers can bypass Asteria-specific pointer/role checks if they possess that secret. Production deployment requires a separately reviewed authentication and network boundary; the current arrangement is suitable only for controlled synthetic training.
10. **Deployment-readiness boundary:** The audit proposes no deployment state or execution path. A future deployment gate must separately define its own exact-generation binding, authorization, idempotency, uncertainty, and health confirmation contracts.

## Source list

The principal evidence inspected for this audit is linked inline above. Additional Gate 14 and Gate 13 evidence:

- [Gate 14 Milestone 4C integration](./gate14-milestone4c-integration.md)
- [Gate 14 Milestone 4C adversarial results](./gate14-milestone4c-adversarial-results.md)
- [Gate 14 Milestone 4C tests](../../tests/DurableStateMachine.Tests/AsteriaMilestone4CTests.cs)
- [Gate 14 Milestone 4B recovery notes](./gate14-milestone4b-recovery.md)
- [Gate 13 integration audit](../gate-13-integration-audit.md)
- [Gate 13 design](../gate-13-design.md)
- [Gate 13 test results](../gate-13-test-results.md)
- [Generic Harness supported scope](../../reference/harness-supported-scope.md)
- [Generic Harness OpenAPI contract](../../reference/harness.openapi.yaml)

**Disposition:** Audit complete. This report does not authorize or begin Milestone 5B.
