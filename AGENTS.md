# Durable State Machine — Agent Instructions

## Project Purpose

This repository implements a standalone, reusable state-machine runtime as part of the **Advanced State Machine Engineering** qualification programme.

The objective is to build a generic runtime capable of executing previously unseen workflow definitions without requiring domain-specific changes to its execution logic.

The runtime will progressively support deterministic transitions, guards, durable state, transition evidence, external effects, deadlines, concurrency, recovery, replay and machine-definition versioning.

The fictional Orion Equipment Leasing workflow is one test domain, not an architectural template. The runtime must also support unrelated workflow definitions.

## Authoritative Sources

- `reference/ADVANCED_STATE_MACHINE_ENGINEERING_V0.1.md` — Qualification objectives, gate requirements, constraints and progression.
- `reference/orion-requirements-pack.md` — Fictional Orion business requirements used to exercise the runtime.
- `docs/` — Implemented runtime semantics, supported machine-definition language and architectural decisions.

Use the course document to understand the overall programme, but implement only the scope of the currently requested gate.

Do not infer that a capability is implemented merely because it appears in a future gate.

## Core Engineering Principles

### Domain Independence

- Keep the generic runtime independent of Orion and other business domains.
- Do not introduce workflow-specific branches, hardcoded states or event names into runtime execution logic.
- New supported workflows must be introduced through definitions and configuration rather than changes to generic control-flow code.
- Keep domain-specific adapters and test fixtures separate from the runtime.

### Determinism and Correctness

- Transition selection must be deterministic for identical definitions, states, events and bound guard inputs.
- Reject undeclared, invalid, ambiguous or unsupported transitions explicitly.
- Do not use an LLM to make runtime transition decisions.
- Guards must not perform arbitrary external calls or mutate state.
- Do not interpret missing information as a successful condition without explicit semantics.
- Prefer correctness and explainability over performance optimisations.

### Durable Execution and Evidence

As the corresponding gates introduce these capabilities:

- Preserve authoritative execution state and transition history across process failure.
- Distinguish requested actions, observed outcomes and accepted state transitions.
- Preserve uncertainty when external outcomes cannot be established.
- Do not interpret timeouts as proof that external effects failed.
- Prevent duplicate inputs from applying the same logical transition repeatedly.
- Ensure replay does not automatically repeat historical external side effects.
- Keep machine-definition identities and versions explicit.
- Do not silently reinterpret existing instances when definitions change.

These are progressive architectural goals, not requirements to implement every capability immediately.

### Separation of Responsibilities

Maintain clear boundaries between:

- **Machine definitions:** permitted states, events, transitions and guards.
- **Machine instances:** current execution state and associated bound data.
- **Transition evaluation:** deterministic selection and refusal of transitions.
- **Business decisions:** domain-specific conclusions obtained from bound facts or external decision systems.
- **External execution:** capability execution and provider-specific behaviour.
- **Persistence and evidence:** durable records supporting inspection and recovery.

Do not duplicate business-rule evaluation or provider-specific execution logic inside the generic state-machine runtime.

## Implementation Guidelines

- Use C# and .NET 10 unless instructed otherwise.
- Prefer straightforward, maintainable implementations and focused interfaces.
- Design explicit contracts between components.
- Avoid unnecessary dependencies, frameworks and abstraction layers.
- Do not use an off-the-shelf workflow engine as the submitted runtime.
- Use ordinary libraries for persistence, HTTP, JSON and testing where appropriate.
- Do not introduce future-gate infrastructure prematurely.
- Document assumptions and unsupported constructs rather than inventing implicit behaviour.

## Testing Requirements

- Add automated tests for newly implemented behaviour.
- Include positive, negative and boundary cases.
- Verify deterministic transition selection and explicit refusal of invalid operations.
- Test failure and recovery semantics when introduced by the current gate.
- Keep existing tests passing unless a documented change to supported semantics requires updating them.
- Prefer reproducible tests using controlled inputs, clocks and fault injection.
- Do not claim tests passed unless they were actually executed.

## Documentation

Keep the repository documentation aligned with the implemented system.

- Maintain an accurate `README.md` with build, run and test instructions.
- Document the supported machine-definition format and execution semantics.
- Record significant architectural choices and their trade-offs.
- Distinguish implemented functionality from planned capabilities.
- Document assumptions, unsupported constructs and known limitations.
- Preserve enough information to explain material transition outcomes.

## Gate Progression

The qualification programme is competency-gated.

- Implement only the gate explicitly requested in the current task.
- Future requirements may inform architectural extensibility, but must not cause premature implementation.
- Prefer extending established generic abstractions over introducing workflow-specific behaviour.
- Do not weaken existing execution semantics merely to pass a new test.
- Keep the runtime general enough to support a previously unseen workflow within its declared capabilities.

At the end of each gate:

1. Build the complete solution.
2. Run the relevant automated tests.
3. Summarise implemented functionality and major design decisions.
4. Report test results and any failures.
5. Document limitations and unsupported behaviour.
6. Identify any significant architectural trade-offs introduced by the implementation.

## Guiding Principle

**A durable state-machine runtime must establish what changes are permitted, what has actually happened, and what remains known or unresolved when execution is interrupted.**

Every implementation decision should support that objective without introducing complexity beyond the current gate.