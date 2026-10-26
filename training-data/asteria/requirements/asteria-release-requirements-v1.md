# Asteria Software
## Release Governance — Business Requirements V1

**Document ID:** AST-BR-001  
**Version:** 1.0  
**Recorded:** 26 October 2026  
**Classification:** Fictional / Synthetic Training Material

### A. Release creation and submission

**AST-REQ-001 — Release creation**  
An authorised employee may create a release request identifying the product, target version, deployment environment, release category, change summary, planned deployment time and accountable release owner.

**AST-REQ-002 — Incomplete drafts**  
Release requests may be saved with incomplete information. Incomplete releases may not be submitted for assessment.

**AST-REQ-003 — Submission**  
A release may be submitted only when all mandatory information has been provided and the release owner confirms its accuracy.

**AST-REQ-004 — Stable release identity**  
Each release must have a unique identity that remains stable throughout its lifetime. Repeated submission must not create multiple releases.

### B. Policy assessment

**AST-REQ-005 — Release policy assessment**  
Submitted releases must be assessed against the applicable version of Asteria's fictional release-governance policy.

**AST-REQ-006 — Assessment outcomes**  
An assessment may permit progression, require additional review, prohibit release or remain unresolved because necessary information is missing or unavailable.

**AST-REQ-007 — Risk-dependent requirements**  
The applicable policy determines whether functional verification, security verification and human authorisation are mandatory.

**AST-REQ-008 — Policy prohibitions**  
A release prohibited by a mandatory policy rule must not proceed to deployment through ordinary approval.

**AST-REQ-009 — Assessment evidence**  
The business must retain the facts, applicable policy versions, decision outcome and evaluation evidence associated with each assessment.

### C. Verification and approval

**AST-REQ-010 — Functional verification**  
Where functional verification is required, satisfactory evidence must be recorded before deployment is permitted.

**AST-REQ-011 — Security verification**  
Releases requiring security verification must not proceed until satisfactory security evidence has been accepted.

**AST-REQ-012 — Independent verification**  
Functional and security verification may proceed independently. Completion of one does not imply completion of the other.

**AST-REQ-013 — Human authorisation**  
Where human approval is required, an authorised reviewer must explicitly approve the assessed release. Silence, a missing response or a pending request does not constitute approval.

**AST-REQ-014 — Deployment readiness**  
A release may proceed to deployment only when the current applicable assessment permits it and all mandatory verification and authorisation requirements have been satisfied.

### D. Material changes and reassessment

**AST-REQ-015 — Material changes**  
A release owner may request a material amendment before deployment begins, including changes to the target version, environment, affected product components or declared security impact.

**AST-REQ-016 — New assessment generation**  
An accepted material amendment establishes a new assessment generation associated with the revised release information.

**AST-REQ-017 — Repeat mandatory checks**  
After a material amendment, all checks and human authorisations required by the new assessment must be repeated. Previously accepted evidence remains available historically but cannot, by itself, authorise the amended release.

**AST-REQ-018 — Reassessment restrictions**  
While reassessment and renewed verification remain incomplete, deployment must not be authorised. Results associated with an obsolete assessment must not satisfy the new assessment's requirements.

### E. Deployment

**AST-REQ-019 — Deployment authorisation**  
A deployment request must identify the exact approved product, version, environment and release identity.

**AST-REQ-020 — External deployment execution**  
Deployment is performed by a separate fictional deployment platform. A release-governance request is not itself proof that deployment succeeded.

**AST-REQ-021 — Deployment confirmation**  
Deployment may be confirmed only through trustworthy evidence correlating the external operation to the authorised release, version and environment.

**AST-REQ-022 — Uncertain deployment outcomes**  
A timeout, lost response or unavailable confirmation must not automatically establish deployment success or failure. The release must retain its unresolved operational status pending authoritative evidence or manual investigation.

**AST-REQ-023 — Duplicate deployment requests**  
Repeated delivery of the same logical deployment request must not unintentionally produce an additional independent deployment.

### F. Post-deployment verification

**AST-REQ-024 — Application health verification**  
Following confirmed deployment, a separate verification capability must check the health of the deployed application.

**AST-REQ-025 — Verified completion**  
A release is complete only after the exact deployment has been confirmed and its subsequent health verification has passed.

**AST-REQ-026 — Failed health verification**  
A failed or indeterminate health check must not be treated as successful release completion. A failed check requires operational investigation; no automatic rollback is implied.

### G. Cancellation and intervention

**AST-REQ-027 — Pre-deployment cancellation**  
A release may be cancelled before deployment begins. Cancellation must prevent subsequent deployment authorisation and preserve historical evidence.

**AST-REQ-028 — Cancellation after deployment starts**  
Once an external deployment has begun, the business may record a cancellation request, but that request does not establish that the external deployment stopped or was reversed.

**AST-REQ-029 — Unresolved cancellation**  
Where deployment status remains uncertain, the cancellation request and deployment uncertainty must remain separately identifiable until sufficient evidence permits a justified business outcome.

**AST-REQ-030 — Late external results**  
A late confirmation must not bypass a cancellation request, reactivate an improperly concluded release or automatically establish that a cancelled release was successfully cancelled.

### H. Deadlines and operational reliability

**AST-REQ-031 — Verification escalation**  
If mandatory verification remains incomplete 48 hours before the planned deployment, the release must be escalated for review. Escalation does not waive the outstanding requirement.

**AST-REQ-032 — Unconfirmed deployment escalation**  
Where deployment has not been authoritatively confirmed within 30 minutes of its recorded initiation, Asteria must flag the release for investigation. This does not establish failure.

**AST-REQ-033 — Duplicate and conflicting evidence**  
Duplicate notifications must not cause duplicate business outcomes. Conflicting evidence must be refused or referred for investigation rather than silently overwriting an accepted result.

**AST-REQ-034 — Recovery and historical inspection**  
The system must preserve enough information to explain release decisions, outstanding work, external operations and final outcomes after process interruption.

**AST-REQ-035 — Definition evolution**  
New release-governance requirements must not silently reinterpret the history or current obligations of existing releases.

### I. Scope and assumptions

- All products, users, release versions, environments and deployment targets are synthetic.
- Policy thresholds and reviewer permissions are fictional.
- The deployment platform and verification service are separate external capabilities.
- External responses may be delayed, duplicated, unavailable or lost.
- A successful HTTP response is not necessarily sufficient evidence of business completion.
- No automatic production rollback, real cloud deployment or live security scanning is required.
- The business may require manual intervention where external uncertainty cannot be resolved.

**End of requirements — 35 numbered requirements.**