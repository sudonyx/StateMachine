# Asteria Software — Release Governance Policy V1

**Policy reference:** AST-POL-001  
**Version:** 1.0  
**Recorded:** 2026-10-26  
**Business effective from:** 2027-02-01T00:00:00Z  
**Scope:** Fictional Asteria software release requests evaluated for deployment to declared synthetic environments.  
**Authority:** Fictional Release Governance Board.  
**Status:** Synthetic capstone source; business requirements, not executable rules.

## 1. Scope and interpretation

This policy supplements **AST-BR-001 Release Governance — Business Requirements V1**. All facts, actors and services are synthetic. It governs the *requirements and eligibility of a release assessment*, not proof that any required check, approval, deployment or health verification actually occurred.

A release assessment is specific to a **release ID, assessment generation, immutable material-content digest, target product/version and environment**. A materially amended release requires a new assessment and repetition of all requirements applicable to the new generation. Evidence accepted for an earlier generation remains historical but does not itself satisfy the new generation.

Release categories are `Standard` and `Emergency`. Deployment environments are `development`, `staging`, `production`, and `unmanaged-production`. `unmanaged-production` is explicitly prohibited. Asteria uses a synthetic change-size classification `Small`, `Medium` or `Large`. All comparisons below are case-sensitive canonical values after input validation.

A **present** fact is one supplied in the expected type and in the declared vocabulary. An **explicitly unknown** or **unavailable** fact must not be treated as false, absent, low risk or approved. A malformed, unsupported or contradictory value requires explicit refusal/invalid input handling. An unresolved assessment must not be silently converted into `Permitted` or `Prohibited`.

## 2. Mandatory assessment facts

| Fact | Type | Valid examples/constraints |
|---|---|---|
| releaseId | non-empty string | `AST-REL-0001` |
| assessmentGeneration | integer | 1 or greater |
| materialDigest | string | synthetic immutable content fingerprint, e.g. `sha256:asteria-fixture-001` |
| product | non-empty string | `AsteriaPortal` |
| targetVersion | non-empty string | `3.8.2` |
| environment | enum | `development`, `staging`, `production`, `unmanaged-production` |
| releaseCategory | enum | `Standard`, `Emergency` |
| changeSize | enum | `Small`, `Medium`, `Large` |
| authenticationChange | boolean | true/false |
| customerDataImpact | boolean | true/false |
| releaseSummary | non-empty string | Synthetic description |
| plannedDeploymentAt | RFC 3339 timestamp | Explicit timezone |

The assessment request must preserve a stable binding to the release generation. The facts above represent the **declared proposed release**, not trusted proof of testing or of human approval.

## 3. Policy requirements

**AST-POL-01 — Environment prohibition.** A release targeting `unmanaged-production` is prohibited, regardless of release category, change size or reviewer preference. Ordinary approval cannot override it. An explicitly known prohibited environment may be reported as an adverse finding even if other unrelated facts are unavailable.

**AST-POL-02 — Functional verification.** Every release to an allowed environment requires satisfactory functional verification for the current assessment generation. This requirement is never waived by emergency classification.

**AST-POL-03 — Security verification.** Security verification is required when `authenticationChange = true` **or** `customerDataImpact = true`, irrespective of environment or release category. Otherwise security verification is not required by this policy. An unknown required input yields unresolved applicability, not `false`.

**AST-POL-04 — Derived high-risk classification.** A release is high risk when at least one of the following is present: `changeSize = Large`, `authenticationChange = true`, or `customerDataImpact = true`. Otherwise, where all three facts are known, it is not high risk. No caller-provided risk score or risk label is authoritative.

**AST-POL-05 — High-risk authorisation.** A high-risk release requires an explicitly recorded approval from an authorised Release Reviewer. The policy determines the requirement; it does not certify that approval was given.

**AST-POL-06 — Emergency authorisation.** Every `Emergency` release additionally requires explicit Emergency Release Authoriser approval. This is a distinct authorisation obligation. For a high-risk emergency release, both the normal high-risk approval and emergency approval are required; one authorised person may fulfil both only if Asteria's separate authorisation policy explicitly permits it. This policy does not establish that permission.

**AST-POL-07 — No emergency verification waiver.** Emergency designation never removes mandatory functional or security verification and never overrides AST-POL-01. Expedited scheduling does not constitute evidence that a check passed.

**AST-POL-08 — Unresolved required facts.** If mandatory assessment facts are missing, explicitly unknown or unavailable, the affected requirement remains unresolved. Other independently established adverse findings, including AST-POL-01, remain reportable. A final permission-to-proceed must not be issued until all applicable mandatory requirements can be determined.

**AST-POL-09 — Assessment result meaning.** The assessment distinguishes `Prohibited`, `Unresolved`, `RequirementsDetermined`, and `AdditionalReviewRequired`. `RequirementsDetermined` means applicable check obligations can be established, **not** that checks have passed or deployment is authorised. `AdditionalReviewRequired` means required human approval obligation(s) have been identified, **not** that approval has occurred. If a prohibition is established, its restrictive effect remains authoritative regardless of other findings.

**AST-POL-10 — Material change and evidence applicability.** A material change to product, target version, environment, authentication impact, customer-data impact, change size, release category or the substantive release content requires a new assessment generation and material digest. All verification and human approvals applicable to the new generation must be repeated. Earlier evidence remains inspectable but must not be deemed applicable solely by matching the release ID.

**AST-POL-11 — Policy and time context.** Preserve the policy source ID, version, recorded date and business-effective date used for evaluation. An assessment must be interpreted under the selected applicable policy version, without retroactively rewriting accepted historical evaluations. Selection for dates outside this version's effective scope is not defined by this document.

**AST-POL-12 — Separation of decision and execution.** Policy assessment determines requirements and prohibition/referral outcomes; it does not invoke deployments, complete health checks, issue an approval or change release lifecycle status. Such outcomes need separately verified evidence.

## 4. Decision evidence expectations

An evaluation should allow an inspector to determine: the input fact set and digest; unknown/missing/unavailable fact names; applicable requirements and their applicability; explicitly adverse findings; recorded/effective policy source identity; risk derivation inputs; assessment generation/material digest; and the resulting policy outcome. This section specifies the business evidence contract, not executable rule syntax or API fields.

## 5. Deadlines from AST-BR-001

The governance process requires escalation when mandatory verification is incomplete **48 hours before planned deployment**, and operational investigation if an initiated deployment remains unconfirmed after **30 minutes**. These deadlines do not establish approval, failure or deployment success, and are not standalone eligibility-policy results.

## 6. Explicit exclusions and unresolved stakeholder questions

- No actual reviewer authentication/roles infrastructure is specified here; a trusted authorisation source must establish approval.
- No policy for scheduling a release in the past is supplied; validation/business treatment requires a separate decision.
- No automatic rollback or automatic cancellation of an external deployment is authorised.
- The allowed scope of approvals by one person holding multiple roles is not specified.
- This document does not define exact machine states, guards, transition identifiers, timer declarations, migration plans, provider integrations or executable rules.

**End of AST-POL-001 V1.**
