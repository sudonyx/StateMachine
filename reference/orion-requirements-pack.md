# Orion Equipment Leasing
## Equipment Lease Application — Business Requirements

**Document reference:** ORN-BR-001  
**Version:** 1.0  
**Classification:** Fictional / Training Material

### 1. Purpose and Scope

Orion Equipment Leasing is a fictional business offering equipment leases to commercial customers. Customers apply to lease equipment from Orion's fictional equipment catalogue, provide supporting information, undergo eligibility checks and, where appropriate, receive an offer.

This document describes the business requirements for managing an application from initial creation through to its eventual conclusion.

All organisations, customers, equipment, financial information, eligibility thresholds and business policies described in this document are entirely fictional and use synthetic data.

### 2. Application Creation and Editing

**ORN-REQ-001 — Application Creation**  
A customer or authorised Orion representative must be able to create a new equipment-lease application. Each application must receive a unique reference that remains unchanged throughout its lifetime.

**ORN-REQ-002 — Application Information**  
An application must capture the applicant's business name, fictional registration identifier, contact information, requested equipment, proposed lease duration, estimated equipment value and intended business use.

**ORN-REQ-003 — Saving Incomplete Applications**  
Customers must be able to save an application without completing every required field. Information already provided must remain available when the customer returns.

**ORN-REQ-004 — Editing Application Details**  
Customers may amend application information until the application has been formally submitted. After submission, changes to material information require authorisation from an Orion reviewer.

**ORN-REQ-005 — Abandonment of Unsubmitted Applications**  
An application that has not been submitted within 30 calendar days of its creation must be closed as abandoned. The customer must receive a notification before the application is closed.

### 3. Submission and Completeness Checking

**ORN-REQ-006 — Application Submission**  
A customer may submit an application only after providing all mandatory application fields and confirming the accuracy of the information supplied.

**ORN-REQ-007 — Duplicate Submission Protection**  
Repeated submission requests for the same application, including requests caused by slow responses or repeated button presses, must not create additional applications or duplicate business assessments.

**ORN-REQ-008 — Completeness Checking**  
Following submission, Orion must check whether all required information and supporting documents have been supplied. The required documents depend on the equipment category, requested value and applicant characteristics.

**ORN-REQ-009 — Missing Information Requests**  
Where information or documents are missing, Orion must communicate the outstanding requirements to the customer. A customer must not be asked to provide a document already received and accepted as valid.

**ORN-REQ-010 — Document Submission and Validation**  
Customers must be able to supply requested supporting documents. Orion must check that each document is readable, relates to the correct applicant and meets the applicable fictional freshness requirements.

**ORN-REQ-011 — Document Request Deadlines**  
Customers must provide outstanding documents within 10 calendar days of the initial request. Orion may grant one extension of five calendar days where an authorised reviewer records a justification.

**ORN-REQ-012 — Late and Duplicate Documents**  
A document received after the applicable deadline must not automatically restore an application that has already been closed. Repeated delivery of the same document must not create duplicate accepted evidence records.

### 4. Eligibility Assessment

**ORN-REQ-013 — Eligibility Criteria**  
Orion must assess completed applications against the following fictional business criteria:

- The applicant must have operated for at least 12 months.
- The requested equipment value must be between £2,000 and £150,000.
- The proposed lease duration must be between 12 and 60 months.
- The applicant's synthetic Orion Risk Score must be at least 50 on a scale of 0–100.
- The requested equipment must belong to an approved Orion equipment category.

**ORN-REQ-014 — Consistent Eligibility Decisions**  
Applications with identical relevant information, supporting evidence and applicable Orion business criteria must receive the same eligibility assessment result.

**ORN-REQ-015 — Incomplete Evidence Restrictions**  
An application must not receive a final eligibility assessment while mandatory supporting information remains outstanding or invalid.

**ORN-REQ-016 — Automatic Rejection Criteria**  
An application must be rejected where the requested equipment is explicitly excluded by Orion's fictional leasing policy or the synthetic Orion Risk Score is below 35. These conditions cannot be overridden through ordinary manual review.

**ORN-REQ-017 — Manual Review Referral**  
An application must be referred to an authorised Orion reviewer where the synthetic Orion Risk Score is between 35 and 59 inclusive, the equipment value exceeds £100,000, or supplied information contains material inconsistencies.

**ORN-REQ-018 — Assessment Records**  
Orion must retain a record of the information, evidence, business criteria and assessment outcome used to reach each eligibility decision.

### 5. Manual Review and Decision-Making

**ORN-REQ-019 — Reviewer Assignment**  
Applications requiring manual review must be assigned to an authorised Orion reviewer. Reviewer assignment and supporting evidence verification may proceed independently, provided a final decision is not made before all required verification is complete.

**ORN-REQ-020 — Reviewer Actions**  
An authorised reviewer may approve, reject or request additional information for an application. A reviewer must record a business justification for an approval or rejection.

**ORN-REQ-021 — Review Amendments**  
Where additional information materially changes the application's eligibility, Orion must reconsider the application against the applicable business criteria before the reviewer makes a final decision.

**ORN-REQ-022 — Review Deadline and Escalation**  
Applications requiring manual review should receive a decision within three business days after all required evidence becomes available. Applications exceeding this deadline must be escalated to a senior reviewer without automatically changing their eligibility outcome.

**ORN-REQ-023 — Approval Restrictions**  
An application may be approved only after all required checks are complete and no unresolved mandatory rejection condition remains.

**ORN-REQ-024 — Rejection and Customer Notification**  
When an application is rejected, Orion must record the applicable business reasons and notify the customer. A rejected application cannot subsequently receive an offer without an explicitly authorised reconsideration.

### 6. Offer Preparation and Delivery

**ORN-REQ-025 — Synthetic Offer Generation**  
Following approval, Orion must produce a synthetic lease offer containing the approved equipment description, equipment value, lease duration, monthly payment, total fictional lease cost and offer expiry date.

**ORN-REQ-026 — Offer Consistency**  
An offer must reflect the approved application details. Any material changes to the equipment, value or lease duration after approval require renewed business authorisation before a replacement offer may be issued.

**ORN-REQ-027 — Equipment Reservation**  
Where the approved equipment is held within Orion's fictional inventory, Orion must request a temporary reservation from its equipment allocation service. Offer preparation and equipment reservation may proceed independently, but the offer must not be released to the customer until the required reservation has been confirmed.

**ORN-REQ-028 — Offer Generation and Reservation Failures**  
If the offer-document service or equipment allocation service is temporarily unavailable, Orion must preserve the approved business decision and arrange a further attempt. Repeated attempts must not generate multiple active offers or duplicate equipment reservations.

**ORN-REQ-029 — Offer Delivery**  
Orion must send the approved offer to the customer's registered contact address and record the delivery outcome. A temporary notification-service failure must not invalidate the approved offer, and a further delivery attempt must be possible.

**ORN-REQ-030 — Offer Validity Period**  
An issued offer remains valid for seven calendar days from its recorded issue time. The offer must specify its exact expiry date and time.

### 7. Offer Acceptance, Decline and Expiry

**ORN-REQ-031 — Offer Acceptance**  
A customer may accept an offer only while it remains valid, has not been withdrawn and has not already been declined. Acceptance must apply to the exact terms of the issued offer.

**ORN-REQ-032 — Acceptance Confirmation**  
Following valid acceptance, Orion must record the customer's synthetic acceptance confirmation and arrange the creation of a fictional lease agreement. The customer must receive an acknowledgement of acceptance.

**ORN-REQ-033 — Duplicate Acceptance Handling**  
Repeated acceptance requests for the same offer must not create multiple lease agreements, duplicate customer acknowledgements or additional equipment reservations.

**ORN-REQ-034 — Offer Decline**  
A customer may decline an outstanding offer before accepting it. Orion must record the decline, notify the relevant internal team and arrange the release of any associated equipment reservation.

**ORN-REQ-035 — Offer Expiry**  
An offer that has not been accepted by its expiry time must cease to be available for acceptance. Orion must notify the customer and arrange the release of any associated equipment reservation.

**ORN-REQ-036 — Late Customer Responses**  
Where an acceptance or decline notification arrives after the offer's expiry time, Orion must determine whether the customer's action actually occurred before or after the deadline using the available trusted business records. A genuinely late acceptance must not be treated as valid merely because delivery of the notification was delayed.

**ORN-REQ-037 — Delayed External Confirmations**  
A delayed equipment reservation or document-generation confirmation must not cause Orion to issue an offer that is no longer authorised or restore an offer that has already expired, been declined or been withdrawn.

### 8. Cancellation and Withdrawal

**ORN-REQ-038 — Cancellation Before Submission**  
A customer may cancel an unsubmitted application without staff approval. Orion must retain an appropriate cancellation record and prevent further submission of that application.

**ORN-REQ-039 — Cancellation After Submission**  
A customer may request cancellation of a submitted application before accepting an offer. Orion must stop outstanding business processing, withdraw any issued offer and arrange the release of associated equipment reservations. Cancellation must not erase previously recorded assessment evidence.

**ORN-REQ-040 — Cancellation After Acceptance**  
Once a customer has accepted an offer, cancellation must be handled as a formal withdrawal request requiring authorised Orion review. The request must not automatically cancel an accepted agreement or release reserved equipment.

**ORN-REQ-041 — Cancellation and Outstanding External Activities**  
Where cancellation occurs while notifications, document preparation or reservation activities are still being processed, Orion must ensure that late confirmations do not reinstate the cancelled application. Any outstanding equipment commitments must be released or referred for manual resolution if automated release cannot be completed.

### 9. Operational Reliability and Finality

**ORN-REQ-042 — External Communications and Final Outcomes**  
Orion must retain a record of material customer communications, including document requests, decisions, offers, acceptance acknowledgements and cancellation confirmations. Where communication delivery fails temporarily, further attempts must be supported without recording repeated delivery notifications as separate customer decisions. Applications rejected under ORN-REQ-016 must remain permanently rejected: subsequent documents, duplicate customer requests, delayed confirmations or operational recovery activities must never reopen them automatically. A customer wishing to pursue leasing after such a rejection must create a new application.

---

### 10. Business Context and Assumptions

The following assumptions apply throughout the requirements:

- **Fictional financial environment:** All monetary amounts, scores, customer identities and equipment information are synthetic.
- **Business calendars:** Business-day deadlines follow Orion's fictional operating calendar. Calendar-day deadlines include weekends.
- **Authorised personnel:** Orion maintains internal permissions distinguishing customer representatives, reviewers and senior reviewers.
- **Customer communications:** External messages may experience transmission delays, duplicate delivery or temporary unavailability.
- **External services:** Document generation, notifications, equipment allocation and customer-interaction recording are provided by separate fictional operational services. Their availability is not guaranteed.
- **Business records:** Orion must preserve sufficient information to explain significant application decisions and actions, including unsuccessful attempts to complete external activities.
- **Application finality:** Completion of an application does not imply that every outstanding external communication or operational activity has necessarily finished.
- **Scope limitation:** The subsequent servicing of an active lease, collection of payments, equipment maintenance and end-of-lease processing are outside the scope of this document.

**End of requirements document — 42 numbered requirements.**