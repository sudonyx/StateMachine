# Advanced State Machine Engineering

## Durable Workflow and Transition Engineering — Developer Qualification Programme for Kyo Engineering

**Version:** 0.1  
**Status:** Draft for internal use  
**Qualification model:** Competency-gated  
**Nominal duration:** 10–15 working days  
**Recommended prerequisites:** Advanced Harness Programming and Advanced Layered Decision Systems  
**Primary outcome:** An engineer who can explain, design, build, test and defend durable state-machine execution before working with Kyo state and workflow mechanisms.

---

## 1. Purpose

The first two programmes establish two foundations:

- **Advanced Harness Programming** — control execution around capabilities.
- **Advanced Layered Decision Systems** — control deterministic decisions under explicit facts and rules.

This programme adds the third foundation:

> **Control how work is allowed to move from one durable state to another when events, decisions, time and external effects can all fail or arrive unexpectedly.**

This is deliberately **not a Kyo training course**.

Participants do not begin with Kyo state machines, Kyo runtime structures, Kyo lifecycle specifications or Kyo terminology. They begin with a general engineering problem:

> **Given a current state, an event or requested change, and a set of transition conditions, how do we determine the next valid state, coordinate required external work, survive crashes and duplicates, and preserve enough history to explain exactly how the system got where it is?**

The programme has one central objective:

> **Build a standalone, non-Kyo state-machine runtime that can execute previously unseen workflow definitions, preserve durable instance state and transition history, reject invalid transitions, handle guards, deadlines, duplicates, concurrency and external effects, recover truthfully after failure, and replay historical behaviour without silently repeating side effects.**

To reach that objective, engineers will:

1. study finite state machines, statecharts, durable workflows and event history;
2. implement a workflow naively with status fields and ordinary application code;
3. experience illegal-state and lifecycle problems;
4. separate workflow definition from workflow instance;
5. build a reusable state-machine runtime and API;
6. model events, commands, guards and transition evidence;
7. encounter the side-effect/crash problem directly;
8. integrate external execution through the harness from Course 1;
9. integrate deterministic decisions through the rule engine from Course 2;
10. implement timers, deadlines, duplicates, late events and concurrency handling;
11. support recovery, replay and historical inspection;
12. deal with versioned workflow definitions and in-flight instances;
13. confront state explosion and workflow decomposition; and
14. complete an unseen workflow capstone and oral defence.

The point is not to build the most feature-rich workflow platform. The point is to understand **what a durable transition means and what evidence justifies declaring that it occurred**.

---

## 2. The end state engineers should understand before writing code

The programme begins with conceptual reading and discussion.

A developer should enter the practical work understanding that:

- state is more than a mutable `status` string;
- an event is not the same thing as a request to make something happen;
- a transition being defined is not the same thing as that transition being valid for a particular instance;
- a transition being valid is not the same thing as all required external effects having completed;
- an external effect returning an error is not necessarily proof that the remote effect did not occur;
- retry ownership matters;
- duplicate and late events are normal distributed-system conditions, not exotic edge cases;
- timers must survive process restarts;
- current state and transition history answer different questions;
- state derived from history can be replayed only if replay does not accidentally repeat external effects;
- a workflow definition can change while old instances remain in flight;
- adding more Boolean flags is often a sign that state boundaries have become unclear;
- statecharts, hierarchy and parallel regions are techniques for controlling state explosion, not requirements to use a particular library;
- durable workflow systems such as AWS Step Functions and Temporal make explicit engineering choices about retries, history and recovery; those choices are useful comparisons, not answers to copy.

### Working definition for the opening discussion

Participants start with:

> **A state machine is an explicit model of which states a system may occupy and which events or conditions permit movement between them. A durable state-machine runtime preserves that model and its execution truth across failure.**

This is deliberately incomplete.

At the end of the programme each engineer must replace it with a definition they can defend from experience.

---

## 3. Programme rules

### 3.1 No Kyo source during the qualification

Until the programme has been passed, participants must not use:

- Kyo state-machine source;
- Kyo workflow/lifecycle specifications;
- Kyo runtime schemas;
- Kyo event or transition fixtures;
- Kyo naming as a design template.

### 3.2 Do not submit an off-the-shelf workflow engine

State-machine and workflow libraries may be studied.

The submitted runtime must be the engineer's own implementation of the course's supported subset. It must not simply wrap:

- AWS Step Functions;
- Temporal;
- XState as the runtime engine;
- Camunda;
- Durable Functions;
- another mature workflow/orchestration product.

Libraries for ordinary persistence, HTTP, serialization and testing are allowed.

### 3.3 AI tools may assist, but execution semantics remain owned by the engineer

AI coding tools may be used for productivity unless the assessor declares a task unaided.

The supplied prompts may be used to generate fictional workflow requirements and synthetic case/event data.

Generated material is **test/source material**, not an authoritative state model or expected result.

### 3.4 The runtime is deterministic and non-LLM

An LLM must not decide at runtime which transition occurs.

Given the same workflow definition, current bound state, event and deterministic guard inputs, transition selection must be reproducible.

### 3.5 Competency, not attendance

The nominal duration is 10–15 working days, but progression is gate-based.

### 3.6 Illegal and unresolved are valid outcomes

The runtime must be able to refuse an event that has no valid transition.

It must also be able to preserve unresolved external work rather than inventing a successful or failed transition when the evidence does not justify one.

### 3.7 Do not optimise away the history before you understand it

Correctness, durability and truthfulness come before compactness or throughput.

Snapshots, projections, caches and incremental execution may be introduced later, but they must remain derived from preserved execution truth.

---

# Part I — Conceptual Foundation

## 4. Gate 0 — Understand the end game

### Objective

Understand state machines as a way to make permitted behaviour explicit, and durable workflows as the additional problem of preserving that behaviour across time, failure and external interaction.

### Required reading

Read these in order.

#### 1. Stately — What are state machines and statecharts?

https://stately.ai/docs/state-machines-and-statecharts

Focus on:

- states;
- events;
- transitions;
- determinism;
- impossible states;
- how statecharts extend ordinary finite state machines with hierarchy, concurrency and communication.

#### 2. David Harel — Statecharts: A Visual Formalism for Complex Systems

https://www.sciencedirect.com/science/article/pii/0167642387900359

The full paper is optional on first pass. Read the abstract and enough introductory material to understand why hierarchy and concurrency were introduced to control complexity in large state-transition models.

#### 3. W3C — State Chart XML (SCXML) Recommendation

https://www.w3.org/TR/scxml/

Focus initially on:

- state;
- initial state;
- final state;
- transition;
- event;
- legal state configuration;
- guards/conditions;
- parallel states;
- separation between the state model and executable content.

Do not implement SCXML unless you independently choose to support a subset later.

#### 4. XState — Machines, guards and actions

https://stately.ai/docs/machines

https://stately.ai/docs/guards

https://stately.ai/docs/actions

Focus on the conceptual distinctions:

- machine logic versus a running machine instance;
- guards as conditions on transitions;
- actions/effects as behaviour associated with transitions;
- why pure transition logic is easier to reason about than hidden mutation.

XState is an example, not the required implementation.

#### 5. Martin Fowler — Domain Event

https://martinfowler.com/eaaDev/DomainEvent.html

Focus on:

- events as durable representations of something significant that occurred;
- preserving why state changed;
- events arriving through different input mechanisms.

#### 6. Martin Fowler — Event Sourcing

https://martinfowler.com/eaaDev/EventSourcing.html

Focus on:

- state derived from event history;
- rebuilding state;
- temporal queries;
- replay;
- the complication created by external interactions.

Event sourcing is **not mandatory** for the submitted runtime. Read it as one way to reason about durable history.

#### 7. AWS Step Functions — Error handling

https://docs.aws.amazon.com/step-functions/latest/dg/concepts-error-handling.html

Focus on:

- task failures;
- retry;
- catch/fallback behaviour;
- explicit workflow-level failure handling.

Ask which responsibilities belong to a workflow definition and which belong to the capability executor.

#### 8. Temporal — Platform documentation overview

https://docs.temporal.io/

Focus on the broad durable-execution promise: workflow execution should survive crashes, network failures and infrastructure outages and continue from durable history.

Do not treat Temporal's implementation as the design you must reproduce.

### Opening discussion

Before coding, submit short answers to:

1. What problem does an explicit state machine solve that a `status` column does not necessarily solve?
2. What is the difference between a machine definition and one running instance?
3. What is the difference between a command and an event?
4. Can the same event be valid in one state and invalid in another?
5. What makes a transition deterministic?
6. What is a guard?
7. Should a guard send an email or call an external API?
8. If an external action is required by a transition and the process crashes halfway through, what state is the workflow in?
9. Why are timers hard in a durable workflow?
10. How should duplicate events be handled?
11. What is the difference between current state and transition history?
12. Can replay safely mean "perform every historical side effect again"?
13. What happens to running instances when the workflow definition changes?
14. Write your current one-paragraph definition of a durable state-machine runtime.

### Gate 0 pass condition

The engineer can explain that this course is about **permitted transitions plus durable execution truth**, not drawing boxes and arrows around ordinary procedural code.

---

# Part II — Build the naive workflow first

## 5. Gate 1 — Implement a business workflow using ordinary application code

### Objective

Experience the problems created by mutable status fields, scattered transition checks and side effects before building a generic state-machine runtime.

### Scenario: Orion Equipment Lease Applications

Orion is a fictional company offering synthetic commercial equipment leases for this training exercise.

No real financial decision or real customer data is involved.

The business wants an application process that broadly supports:

- creating and editing a draft application;
- submitting it;
- checking whether required information is complete;
- requesting additional documents;
- receiving documents;
- carrying out a deterministic eligibility/risk decision;
- escalating some cases for manual review;
- approving or rejecting an application;
- generating a synthetic offer when appropriate;
- accepting or declining an offer;
- expiring an unanswered offer after a deadline;
- cancelling where permitted;
- preserving a complete history of what happened.

### Data-generation prompt — workflow requirements v1

Use this to generate a fresh human-readable requirements pack for a cohort.

```text
You are creating fictional business requirements for an advanced state-machine engineering exercise.

Create a Markdown requirements document for a fictional company called Orion Equipment Leasing.

The document must describe an equipment-lease application workflow in business language, not state-machine terminology.

Requirements:
- Everything is fictional and uses synthetic data. Do not cite real financial law, regulation, credit rules or real companies.
- Provide 35-45 numbered business requirements grouped under sensible headings.
- Cover creating/editing a draft, submission, completeness checking, requesting and receiving documents, deterministic eligibility assessment, manual review, approval, rejection, synthetic offer generation, acceptance, decline, expiry and cancellation.
- Include at least 6 requirements that restrict when an action may occur based on what has already happened.
- Include at least 4 deadlines or time-related behaviours.
- Include at least 4 cases where an external action is needed, such as sending a notification, generating a document, reserving a fictional asset or recording a synthetic customer interaction.
- Include at least 3 situations where the external action might fail temporarily.
- Include duplicate user actions and repeated external notifications as realistic concerns.
- Include at least 2 situations where an event may arrive late.
- Include at least 2 cases where two independent activities could be in progress at the same time.
- Include cancellation rules that differ depending on progress.
- Include one terminal outcome that must never be reopened automatically.
- Give every requirement a stable reference such as ORN-REQ-001.
- Do not name states, draw a state machine, prescribe event names, provide pseudocode, prescribe database tables, suggest a workflow engine or give implementation advice.
- Do not provide expected state sequences.
```

### Your task

Build the first Orion application in ordinary application code.

At this stage it is acceptable to use:

- a `status` field;
- controllers/services with conditional logic;
- direct calls to synthetic external services;
- normal database persistence.

The point is to experience the pressure before abstracting it.

### Required API surface

Expose enough internal HTTP endpoints for another client to:

- create an application;
- inspect it;
- perform every supported business action;
- inspect a history/audit view;
- query the current status;
- trigger internal test failures in the synthetic external services.

You design the endpoints.

### Data-generation prompt — application fact records

```text
Generate synthetic Orion equipment-lease application records for the fictional requirements supplied below.

Return CSV in a fenced code block.

Requirements:
- Exactly 200 applications.
- application_id ORN-APP-0001 through ORN-APP-0200.
- Include only fictional applicant/company names and data.
- Infer a sensible fact schema from the business requirements, but do not invent real credit scores or real regulatory data.
- Include requested amount, equipment category, trading history, document-completeness fields, synthetic eligibility inputs, contact preferences and any other fields needed by the requirements.
- Include boundary values around thresholds mentioned in the requirements.
- Include approximately 10% records with material missing data.
- Include dates and timestamps that exercise deadlines.
- Include combinations likely to take different workflow paths.
- Do not include current_state, expected_state, expected_decision, expected_transition or any answer derived from the workflow.

REQUIREMENTS:
[paste the Orion requirements here]
```

### Assessor fault injections

Once the naive implementation works, the assessor introduces cases such as:

- submit twice;
- approve before required review;
- document arrives after cancellation;
- cancellation races with approval;
- external offer generation succeeds remotely but the application process loses the response;
- notification service times out;
- application process crashes after changing its database state but before making an external call;
- application process crashes after an external call but before changing its database state;
- two users act on the same application simultaneously;
- offer expiry occurs while acceptance is arriving.

### Required reflection

Write no more than two pages answering:

1. Where are valid transitions encoded?
2. Can you list every legal transition without reading many controller methods?
3. Can two API paths move the same application differently?
4. How do you prevent impossible status combinations?
5. What does a crash between state mutation and an external call do?
6. What happened to duplicate requests?
7. Which historical facts are recoverable and which are only in logs?
8. What now looks as though it belongs in an explicit machine definition?

### Gate 1 pass condition

The engineer can explain why a mutable status plus scattered conditionals is not yet a durable state-machine architecture.

---

# Part III — Make the machine explicit

## 6. Gate 2 — Separate workflow definition from workflow instance

### Objective

Build a reusable state-machine runtime capable of executing more than one workflow definition.

### Task

Define a machine-description format and runtime for a supported subset of state-machine behaviour.

The programme does **not** prescribe:

- JSON, YAML, a DSL or code-based definitions;
- state naming;
- event naming;
- persistence model;
- transition-table representation;
- interpreter design;
- whether machine definitions are compiled or interpreted;
- whether current state is stored directly or derived.

### Minimum supported concepts

Your declared subset must support at least:

- a stable machine-definition identity/version;
- initial state;
- finite named states;
- one or more terminal/final states;
- events;
- transitions from a defined source to target;
- guards or conditions;
- transition metadata sufficient for explanation;
- rejection of undeclared transitions.

### Required second workflow

Create a second, much smaller workflow definition that is not Orion-specific, for example:

- document review;
- internal change request;
- simple release approval;
- equipment maintenance ticket.

The runtime must execute it without adding workflow-specific runtime branches.

### Review questions

- What belongs to the definition and what belongs to an instance?
- Can definitions be changed after instances start?
- What identifies the exact definition used by one instance?
- How are invalid events represented?
- Can two transitions from the same state accept the same event?
- If so, how is determinism preserved?

### Gate 2 pass condition

A new workflow can be introduced through a new supported definition rather than new runtime control-flow code.

---

## 7. Gate 3 — Build the state-machine runtime API

### Objective

Make the state-machine engine a reusable service boundary rather than a library hidden inside the Orion application.

### Task

Expose the state-machine runtime through an HTTP API of your own design.

The assessor must be able to build a separate client without linking against your runtime source.

### Required capabilities

The API must provide a way to:

- register or load a machine definition;
- inspect available machine definitions and versions;
- create a new machine instance;
- inspect an instance's current durable state;
- submit an event or request for an event to be processed;
- inspect whether an event was accepted, rejected or left unresolved;
- inspect prior accepted and rejected inputs;
- inspect the transition history;
- discover enough information to explain why a transition occurred or did not occur;
- inspect pending time-based or external work where your design has such a concept;
- survive a client disconnect and later inspection.

### Not prescribed

The programme does not prescribe:

- REST versus JSON-RPC;
- synchronous versus asynchronous submission;
- event endpoint names;
- poll versus stream;
- identifier formats;
- database layout;
- response object model.

### Required two clients

Build at least two clients of the runtime API, such as:

- the Orion application/service; and
- a CLI or small web UI.

Both must use the same runtime transition semantics.

### Gate 3 pass condition

The workflow runtime is independently operable through its published API and Orion-specific state logic is not duplicated in the clients.

---

# Part IV — Guards and deterministic transition selection

## 8. Gate 4 — State plus event is not always enough

### Objective

Handle transitions whose validity depends on bound data without turning the runtime into arbitrary procedural application code.

### Exercise

Introduce transitions that depend on facts such as:

- required documents being present;
- an offer not being expired;
- an application being cancellable at its current progress point;
- a deterministic eligibility decision having a particular outcome;
- a manual review being completed.

### Questions you must resolve

- Where do guard inputs come from?
- Are guards allowed to mutate data?
- Are guards allowed to call external systems?
- What happens when guard data is missing?
- Can two guarded transitions both be enabled by the same event?
- If the same input is replayed, will the same transition be chosen?

### Required invariant

For this qualification, guard evaluation must be deterministic from data bound to the transition evaluation. A guard must not depend on an arbitrary live external network call.

### Integration with Course 2

At least one non-trivial transition must use a result from the deterministic rule engine built in **Advanced Layered Decision Systems**.

The state-machine runtime should not duplicate the rule engine's layered policy logic.

### Review challenge

The assessor changes a rule-engine source after a transition has already occurred and asks:

> Why did this historical instance take that transition at the time?

Your evidence must distinguish the decision actually used from a fresh decision calculated today.

### Gate 4 pass condition

Transition guards are reproducible and the workflow history identifies the decision/facts that justified material guarded transitions.

---

# Part V — Commands, events and transition evidence

## 9. Gate 5 — A request is not the same thing as an event that happened

### Objective

Distinguish requests to cause change from observations that justify change.

### Exercise

Consider a user asking:

```text
AcceptOffer
```

versus an external system reporting:

```text
OfferDocumentGenerated
```

versus the state machine recording:

```text
OfferAccepted
```

The exact vocabulary and model are yours, but the runtime must avoid treating every incoming message as equally authoritative proof of a world-state change.

### Cases to handle

- user requests an action that is invalid in the current state;
- duplicate user request;
- external result arrives for work the instance does not expect;
- event belongs to an older instance version;
- late result arrives after cancellation;
- same external result is delivered twice;
- malicious or malformed client claims that an external effect succeeded without evidence accepted by your architecture.

### Review questions

- What does an inbound message claim?
- What allows it to change state?
- Is message receipt itself a state transition?
- Which inputs are commands, observations, facts or events in your design?
- How are duplicates identified?

### Gate 5 pass condition

The runtime does not equate "someone asked for this" with "this happened".

---

# Part VI — External effects and the crash window

## 10. Gate 6 — Make the workflow interact with the outside world

### Objective

Encounter the hardest practical problem in durable state-machine execution: coordinating durable state with external effects that cannot participate in one local transaction.

### Required external effects

Orion must perform at least three synthetic external actions, for example:

- send a notification;
- generate an offer document;
- reserve a fictional asset;
- create a synthetic customer-service case.

At least one effect must be consequential enough that accidental duplication matters.

### First implementation

You may initially implement the effects in the way that feels most natural.

Then the assessor will terminate processes at deliberately awkward times.

### Crash matrix

You must reason about and test at least:

1. crash before durable state change and before external call;
2. crash after durable state change but before external call;
3. crash after external request transmission but before response;
4. remote effect occurs but response is lost;
5. response arrives but local persistence fails;
6. effect is attempted twice;
7. effect completes after the workflow has moved somewhere that no longer expects it.

### Required outcome

After recovery, the runtime must not fabricate a clean story.

For each case it should be possible to determine what is known, what is not known, and what may safely happen next.

### Important restriction

Do not solve the exercise by placing the external service database inside the same local transaction as the workflow database.

The purpose is to handle a real distributed boundary.

### Gate 6 pass condition

The engineer can explain why neither "save state then call" nor "call then save state" by itself solves the distributed effect problem.

---

## 11. Gate 7 — Use the execution harness for external effects

### Objective

Compose the state-machine runtime with the execution harness rather than teaching the state machine how every external API behaves.

### Task

Route Orion's external effects through the harness built in **Advanced Harness Programming**.

The state-machine runtime should be concerned with workflow meaning; the harness should remain responsible for the generic external execution lifecycle it already owns.

### Required behaviours

Demonstrate:

- state-machine request for an external capability;
- harness acceptance or refusal;
- one or more harness attempts where appropriate;
- provider result or unresolved external outcome;
- workflow reaction to that result;
- process restart while work is pending;
- duplicate effect request;
- late harness result.

### Questions you must resolve

- What identifier binds external work back to the correct workflow instance and point in its lifecycle?
- Can the state machine assume a timeout means the external effect failed?
- Which component owns provider retry semantics?
- What workflow state is appropriate while an external result remains unresolved?
- What happens if the workflow instance is cancelled while the harness is still working?

The programme does not prescribe a specific pending-state model or message topology.

### Gate 7 pass condition

The state-machine runtime no longer contains provider-specific retry/authentication/HTTP logic, and external uncertainty does not get converted into invented workflow certainty.

---

# Part VII — Time is an input to state

## 12. Gate 8 — Timers, deadlines and expiry

### Objective

Implement durable time-based behaviour without relying on an in-memory sleep.

### Required cases

Orion must support at least:

- offer expiry after a deadline;
- reminder before expiry;
- document-request follow-up after a configured period;
- a deadline that is cancelled because the instance moved elsewhere first.

### Failure tests

The assessor will:

- stop the runtime before a deadline and restart it after the deadline;
- restart it multiple times;
- submit the competing business event at almost the same time as expiry;
- change the machine definition's future deadline rule while old instances exist;
- deliberately skew a test clock where your design supports deterministic clock injection.

### Questions you must resolve

- What time was the deadline calculated from?
- Is the deadline part of instance state or recomputed from today's definition?
- What event or condition causes expiry?
- How is a timer cancelled?
- What happens if expiry fires twice?
- Which clock is authoritative for testability and replay?

### Gate 8 pass condition

Time-based transitions survive process restart and do not depend on a particular process remaining alive for the duration.

---

# Part VIII — Duplicates, concurrency and late information

## 13. Gate 9 — Assume messages will be duplicated, reordered and raced

### Objective

Make state transitions correct under realistic distributed-system delivery conditions.

### Data-generation prompt — adversarial event stream

```text
Create synthetic event-delivery test data for the fictional Orion equipment-lease workflow.

Return NDJSON, one delivery record per line.

Requirements:
- Produce 5,000 delivery records affecting 300 fictional application instances.
- Every record must have delivery_id, application_id, event_type, event_id, occurred_at, delivered_at and payload.
- Use only event types present in the supplied Orion workflow requirements or machine definition.
- Include normal ordered delivery for most records.
- Include at least 150 exact duplicate deliveries with the same event_id.
- Include at least 100 semantically duplicate requests with different delivery_id values.
- Include at least 100 late events delivered after a later business event.
- Include at least 75 pairs of events that race for the same instance within 100 milliseconds of delivered_at.
- Include events aimed at terminal/cancelled instances.
- Include events whose occurred_at is before cancellation but delivered_at is after cancellation.
- Include malformed payloads in approximately 1% of records.
- Do not include expected resulting state, accepted/rejected flags or any solution.

WORKFLOW MATERIAL:
[paste the Orion requirements or machine definition here]
```

If a model cannot reliably create the full dataset, ask it to generate a reviewed deterministic script implementing the constraints.

### Required cases

The runtime must have a declared policy for:

- exact duplicate delivery;
- semantically repeated command;
- optimistic/concurrent updates;
- stale event;
- out-of-order observation;
- event for terminal instance;
- event for unknown instance;
- malformed event;
- late external result;
- conflicting simultaneous transition requests.

### Required evidence

For a rejected or ignored duplicate/late event, preserve enough information to explain why it did not change the workflow.

### Gate 9 pass condition

Concurrent and duplicate input cannot silently corrupt the current state or cause the same logical transition to be accepted twice.

---

# Part IX — Recovery and replay

## 14. Gate 10 — Rebuild truth after process failure

### Objective

Recover state and explain history without relying on the memory of the process that previously ran the workflow.

### Required tests

The assessor will terminate the runtime:

- after receiving an input but before evaluating it;
- during guard evaluation;
- while external work is pending;
- after receiving an external result but before accepting the next transition;
- while persisting a transition;
- while updating a current-state projection or cache.

### Required capabilities

After restart, the system must be able to:

- determine the durable current state of each tested instance;
- identify unresolved work;
- inspect the input/transition history;
- distinguish accepted from rejected inputs;
- explain the evidence for the current state;
- resume or refuse work according to declared semantics.

### Replay requirement

Provide a mechanism that can reconstruct or verify machine state from historical state-transition evidence **without automatically performing historical external side effects again**.

Event sourcing is one possible implementation. It is not mandatory.

### Optional optimisation

If histories become long, introduce snapshots or materialised current-state projections.

If you do, demonstrate that:

- the snapshot/projection can be discarded and rebuilt;
- it does not become a second contradictory source of truth;
- snapshot versioning is handled when machine/runtime code changes.

### Gate 10 pass condition

A process crash cannot erase the only truthful account of how an instance reached its current state.

---

# Part X — Workflow definition versions

## 15. Gate 11 — Change the workflow while instances are running

### Objective

Handle the fact that workflow definitions evolve but real work may remain in flight for days, months or longer.

### Data-generation prompt — workflow requirements v2

```text
Create an amendment document for the fictional Orion Equipment Leasing workflow requirements supplied below.

Requirements:
- Keep everything fictional and non-regulatory.
- Produce 12-16 numbered amendments.
- State an effective date for each amendment.
- Include at least:
  - one new intermediate business step;
  - one deadline change;
  - one new cancellation restriction;
  - one renamed business concept without changing its meaning;
  - one genuinely changed transition condition;
  - one new external action;
  - one retired action;
  - one requirement that applies only to new applications created after a stated date;
  - one requirement that must also affect in-flight applications if they have not passed a particular point;
  - one correction recorded today but said to describe the intended behaviour since an earlier date.
- Do not give state-machine definitions, migration scripts, expected states or implementation advice.
- Preserve references to the original requirements being amended.

ORIGINAL REQUIREMENTS:
[paste ORION requirements v1 here]
```

### Questions you must resolve

- Are instances pinned to a machine-definition version?
- Can an in-flight instance migrate?
- Who or what decides whether migration is allowed?
- How are historical transitions interpreted after a definition changes?
- What happens if a state no longer exists in the new version?
- Are new timers calculated for old instances?
- How do you replay an old instance if the current runtime no longer contains its exact definition?

### Required demonstrations

Show at least:

1. an old instance continuing under its original definition;
2. a new instance using the new definition;
3. one explicitly supported migration or a clearly justified refusal to migrate;
4. historical replay using the correct definition version.

### Gate 11 pass condition

Deploying a new workflow definition does not silently reinterpret every historical and in-flight instance.

---

# Part XI — State explosion, hierarchy and decomposition

## 16. Gate 12 — Model concurrent concerns without inventing every Cartesian combination

### Objective

Understand when a flat state machine becomes unwieldy and how hierarchy, parallel regions or decomposition can reduce complexity.

### Scenario expansion

Orion introduces two activities that can progress somewhat independently, for example:

- document completeness; and
- external asset availability.

The business does not want states such as:

```text
UNDER_REVIEW_DOCS_COMPLETE_ASSET_PENDING
UNDER_REVIEW_DOCS_PENDING_ASSET_AVAILABLE
UNDER_REVIEW_DOCS_COMPLETE_ASSET_AVAILABLE
UNDER_REVIEW_DOCS_PENDING_ASSET_PENDING
...
```

merely because two concerns have been multiplied together.

### Exercise

Model the requirements using one or more of:

- hierarchical states;
- parallel/orthogonal regions;
- separate cooperating machines;
- derived projections over independent state;
- another defensible decomposition.

### Required analysis

Compare at least two approaches and explain:

- legal-state complexity;
- transition determinism;
- evidence/replay implications;
- cross-region coordination;
- cancellation behaviour;
- testing complexity;
- failure isolation.

### Important lesson

"One giant state machine" and "many tiny state machines" are both capable of becoming unmanageable.

The engineer must be able to explain the boundary chosen.

### Gate 12 pass condition

The solution controls state explosion without hiding coordination in unmodelled application code.

---

# Part XII — Full knowledge-ladder composition

## 17. Gate 13 — Compose harness, rule engine and state machine

### Objective

Demonstrate the three general engineering techniques as separate cooperating boundaries.

### End-to-end Orion flow

Build a complete flow in which:

1. a client submits a workflow event/request to the state-machine API;
2. the state machine determines whether the event is meaningful for the current state;
3. when a deterministic business decision is needed, the state machine uses a bound result from the layered rule engine rather than reimplementing the policy;
4. when an external capability is needed, execution occurs through the harness rather than provider-specific workflow code;
5. the resulting evidence is returned to the state-machine runtime in a form it can associate with the correct instance and expected work;
6. only justified transitions become durable current state;
7. historical inspection can trace the path across all three systems.

The exact protocol between the systems is yours to design.

### Required separation

The assessor will ask you to point to:

- where API/provider retry semantics live;
- where layered business-rule semantics live;
- where permitted workflow transitions live;
- where current workflow state lives;
- what evidence links a rule decision to a transition;
- what evidence links an external effect to a transition;
- which component can refuse work and why.

### Cross-system failure tests

Test at least:

- rule result unavailable;
- rule result unresolved because required facts are missing;
- harness refuses an unsupported external capability;
- harness reports unknown outcome for a mutating effect;
- state-machine process restarts while waiting for a harness result;
- duplicate harness result;
- stale rule result submitted after the instance has moved elsewhere;
- workflow cancelled while external execution is pending.

### Gate 13 pass condition

The three systems compose without collapsing their responsibilities into one monolithic service.

---

# Part XIII — Unseen workflow capstone

## 18. Gate 14 — Execute a workflow domain you have not previously modelled

### Scenario

You receive:

- a previously unseen fictional business workflow specification;
- synthetic case data;
- one layered rule pack compatible with the rule engine from Course 2;
- API descriptions for two synthetic external capabilities compatible with the harness from Course 1;
- timing/deadline requirements;
- a set of business goals;
- a strict time box chosen by the assessor.

The domain is not disclosed beforehand.

Possible domains include:

- internal procurement;
- synthetic warranty claims;
- software release governance;
- employee equipment provisioning;
- contract review;
- laboratory sample processing;
- fictional insurance-case administration without real insurance decisions.

### Hard rules

- no domain-specific branch may be added to the state-machine runtime;
- no capstone-provider-specific execution branch may be added to the harness;
- no capstone-rule-specific branch may be added to the rule evaluator;
- unsupported workflow constructs must be declared rather than guessed;
- machine definition versions must be identifiable;
- current state and history must survive restart;
- external uncertainty must remain uncertainty until justified evidence resolves it;
- replay must not blindly repeat external effects.

### Assessor fault injection

The assessor may introduce:

- invalid transition request;
- duplicate event;
- late event;
- out-of-order event;
- conflicting concurrent transition requests;
- missing guard fact;
- unresolved rule decision;
- external 429/5xx/timeout;
- external mutation whose response is lost;
- process crash at any transition/effect boundary;
- deadline passing during downtime;
- machine-definition update while instances are in flight;
- stale external result after cancellation;
- duplicate external result;
- terminal-state reopening attempt;
- projection/current-state cache corruption where a rebuild should reveal the correct durable history.

### Source-diff requirement

At the end of the capstone, show source diffs for:

- state-machine runtime;
- harness runtime;
- rule-engine runtime.

The capstone should add definitions/configuration/data, not hidden domain-specific execution paths.

### Gate 14 pass condition

The engineer can model and execute the unseen workflow within the declared support envelope and explain every refusal, uncertainty and transition from preserved evidence.

---

# Part XIV — Synthetic load and long-running history

## 19. Gate 15 — Operate many durable instances

### Objective

Test the architecture beyond a handful of hand-driven examples.

### Data-generation prompt — workflow load corpus

```text
Generate a synthetic load-test corpus for the fictional workflow definition supplied below.

Preferred output: a deterministic script that writes NDJSON files rather than trying to print every record directly.

The script must generate:
- 10,000 workflow instances;
- 100,000 total event deliveries across those instances;
- plausible business timestamps covering 180 days;
- a mix of short and long-running instances;
- terminal, cancelled, pending, timed and externally-waiting cases;
- at least 2% exact duplicate event deliveries;
- at least 1% late deliveries;
- at least 0.5% competing/concurrent event pairs;
- deadlines both before and after simulated process downtime;
- repeated event patterns suitable for deterministic replay testing;
- no expected_state or answer columns.

The generator must accept a fixed seed argument and produce byte-for-byte identical output for the same seed.

Do not implement the workflow or calculate expected transitions. Generate only synthetic inputs consistent with the supplied event schemas and business requirements.

WORKFLOW MATERIAL:
[paste the workflow definition and event schemas here]
```

### Required measurements

Measure and report:

- event admission throughput;
- transition evaluation latency;
- restart/recovery time;
- replay/rebuild time;
- timer recovery after downtime;
- current-state query latency;
- storage growth per accepted/rejected event;
- effect of snapshots/projections if implemented.

Performance is not allowed to justify dropping history or weakening correctness.

### Gate 15 pass condition

The engineer can explain where the runtime spends time and storage, and can identify optimisations without changing the declared transition semantics.

---

# Part XV — Engineering defence

## 20. Gate 16 — Oral defence

### Format

45–60 minutes at a whiteboard or shared diagramming surface.

No AI assistant is used during the defence.

### Core questions

The assessor may ask:

1. What is the difference between a status field and an explicit state machine?
2. What is the difference between a machine definition and an instance?
3. What is the difference between a command, event, observation and transition?
4. What makes a transition valid?
5. What makes transition selection deterministic?
6. What does a guard know, and what is it forbidden to do in your design?
7. Why is a request for an action not proof the action occurred?
8. If an external mutation times out, what state is the workflow in?
9. Who owns retry of the external capability?
10. How does a late external result get associated with the correct expected work?
11. How do duplicate events fail safely?
12. What happens when cancellation races with approval?
13. How do timers survive restart?
14. Can replay safely call all external systems again?
15. What makes current state trustworthy after a crash?
16. What happens when a workflow definition changes while instances are running?
17. When would you use hierarchy or parallel regions rather than more flat states?
18. When would you split one large workflow into cooperating machines?
19. Where does the rule engine end and the state machine begin?
20. Where does the state machine end and the harness begin?
21. What is the most dangerous assumption in your current runtime?
22. Give your final definition of a durable state-machine runtime.

### Defence standard

The engineer must distinguish:

- requested work from observed outcome;
- current state from history;
- transition validity from external-effect success;
- state-machine retry from capability retry;
- guard evaluation from business-rule authoring;
- deterministic machine logic from nondeterministic external systems;
- durable evidence from logs;
- definition version from instance state;
- state decomposition from hidden application control flow.

---

# Part XVI — Assessment model

## 21. What is being assessed

### A. State modelling

Can the engineer make legal and illegal transitions explicit?

### B. Definition/instance separation

Can multiple instances and multiple workflows use the same runtime correctly?

### C. Determinism

Does the same bound state/event/guard context produce the same transition result?

### D. Input semantics

Can the runtime distinguish requests, events, duplicates and stale information?

### E. External-effect correctness

Can the workflow survive the gap between local durable state and remote side effects?

### F. Harness composition

Are provider-specific execution semantics delegated to the harness rather than duplicated?

### G. Rule-engine composition

Are layered decisions reused without embedding rule logic in state-machine branches?

### H. Time

Are deadlines durable and reproducible?

### I. Concurrency

Can simultaneous and repeated inputs be processed without corrupting state?

### J. Recovery and replay

Can state be rebuilt or verified without repeating historical side effects?

### K. Versioning

Can old and new workflow definitions coexist truthfully?

### L. Complexity control

Can the engineer recognise state explosion and choose a defensible decomposition?

### M. Evidence

Can every material transition be explained from durable history?

---

## 22. Automatic failure conditions

A participant does not pass if any of the following remain in the submitted system:

- capstone workflow requires runtime source-code branches;
- clients can directly set arbitrary workflow state;
- an incoming command is automatically treated as proof that the requested real-world action occurred;
- guards perform arbitrary live external network calls;
- external provider retry/authentication logic is reimplemented in the state-machine runtime;
- an external timeout is automatically converted to "effect failed" despite ambiguous evidence;
- duplicate events can apply the same logical transition repeatedly;
- timers are only in-memory sleeps and disappear on restart;
- process restart loses the only authoritative transition history;
- replay blindly repeats historical external side effects;
- deploying a new machine definition silently changes historical instances;
- current-state caches/projections cannot be reconciled with durable history;
- invalid transitions silently mutate state;
- the runtime asks an LLM which transition to take;
- the engineer cannot explain why the current state is justified.

---

# Part XVII — Reading programme

## 23. Reading sequence

### Before any coding — state-machine concepts

1. **Stately — What are state machines and statecharts?**  
   https://stately.ai/docs/state-machines-and-statecharts

2. **David Harel — Statecharts: A Visual Formalism for Complex Systems**  
   https://www.sciencedirect.com/science/article/pii/0167642387900359

3. **W3C — SCXML 1.0 Recommendation**  
   https://www.w3.org/TR/scxml/

4. **XState — Machines**  
   https://stately.ai/docs/machines

5. **XState — Guards**  
   https://stately.ai/docs/guards

6. **XState — Actions**  
   https://stately.ai/docs/actions

### Before durable history/replay work

7. **Martin Fowler — Domain Event**  
   https://martinfowler.com/eaaDev/DomainEvent.html

8. **Martin Fowler — Event Sourcing**  
   https://martinfowler.com/eaaDev/EventSourcing.html

9. **Martin Fowler — What do you mean by Event-Driven?**  
   https://martinfowler.com/articles/201701-event-driven.html

Read this specifically to avoid collapsing event notification, event-carried state transfer, event sourcing and other distinct patterns into one phrase.

### Before distributed failure work

10. **AWS Step Functions — Handling errors in workflows**  
    https://docs.aws.amazon.com/step-functions/latest/dg/concepts-error-handling.html

11. Revisit the retry/idempotency reading from **Advanced Harness Programming**, especially provider-safe retry semantics.

### Before durable execution/recovery work

12. **Temporal documentation overview**  
    https://docs.temporal.io/

Use Temporal as a mature comparison point for durable execution and replay after you have already encountered the recovery problem.

### After the capstone — mature-engine comparison

Only after the capstone, inspect implementation details of one or more mature state/workflow systems such as:

- XState;
- Temporal;
- AWS Step Functions state-language examples;
- SCXML implementations;
- a production workflow/orchestration engine.

Compare their decisions with your own rather than replacing your reasoning with theirs.

---

## 24. Reading reflection template

For each assigned source, answer:

1. What is the unit of durable state in this model?
2. What causes transitions?
3. What is the role of guards?
4. Where do side effects occur?
5. How are errors represented?
6. What survives process failure?
7. How is history or replay handled?
8. Which semantics are inherent to state machines and which are product-specific choices?
9. What idea would you borrow?
10. What would you deliberately avoid copying, and why?

---

# Part XVIII — Suggested repository structure

## 25. Repository expectations

The programme does not mandate a language or directory layout, but the repository should make it easy to find:

- state-machine runtime source;
- machine-definition schema/language documentation;
- Orion source requirements;
- Orion machine definitions;
- second non-Orion workflow definition;
- instance/event schemas;
- persistence/recovery code;
- timer/deadline code;
- harness integration;
- rule-engine integration;
- synthetic datasets or deterministic generators;
- replay/rebuild tooling;
- tests and fault-injection tests;
- ADRs;
- README with build/run/recovery instructions.

Machine definitions and generated datasets used for qualification should be immutably identified or digested so results can be reproduced.

---

# Part XIX — Completion record

## 26. Qualification evidence

A completed submission should include:

- source commit identifier;
- state-machine language/runtime version;
- machine-definition digests or equivalent immutable IDs;
- synthetic data/event corpus digests;
- automated test result;
- crash/fault-injection result;
- capstone result;
- source diffs showing no capstone-specific runtime paths across the state engine, harness and rule engine;
- one historical replay/rebuild demonstration;
- one machine-definition versioning demonstration;
- one deadline recovery demonstration;
- one duplicate/concurrency demonstration;
- one cross-system end-to-end evidence trace;
- load/performance report;
- oral defence outcome;
- known limitations and unsupported constructs.

---

## 27. What completion means

After all three programmes, an engineer should have independently experienced three different engineering pressures:

```text
1. Advanced Harness Programming

   A capability exists.
   How do we execute it safely and know what happened?


2. Advanced Layered Decision Systems

   Facts and rule sources exist.
   What conclusion is justified, under which rules, and why?


3. Advanced State Machine Engineering

   A process is in a durable state.
   What change is permitted next, what must happen around that change,
   and how do we remain truthful when execution is interrupted?
```

Only then should Kyo be introduced as a system in which these concerns meet under a common governance and evidence architecture.

The objective of the training ladder is not to teach engineers to imitate Kyo.

It is to ensure that, when they finally encounter Kyo's architecture, they recognise the engineering problems that forced many of its boundaries to exist.
