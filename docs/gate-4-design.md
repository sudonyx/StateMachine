# Gate 4 design — explicit facts and decision evidence

## Why state and event are insufficient

A definition, current state and event identify candidate transitions, but guarded transitions also need the exact facts against which their guards were evaluated. Gate 4 makes those inputs an explicit immutable `TransitionEvaluationContext`:

1. **Instance facts** are the immutable facts captured when the instance was created.
2. **Attempt facts** are explicitly submitted for one event evaluation only.
3. **Resolved facts** are the deterministic union used by the existing guard evaluator.
4. **Decision evidence** records a generic externally produced decision and its temporal, policy and provenance metadata.
5. **Fact provenance** records caller-supplied labels for per-attempt values where provided.
6. **Guard findings and the evaluation result** record what the evaluator concluded.

The context is created for one evaluation and is never written back to the instance. Identical definitions, states, events and resolved inputs therefore continue to produce identical selection results.

## Binding, precedence and uncertainty

Instance facts and attempt facts are separate namespaces with a deliberate collision policy: **any same-name binding is rejected**, even if the values happen to be equal. Attempt values never override instance values. This avoids an implicit precedence rule and prevents callers from silently shadowing durable input.

An absent fact remains absent; a bound JSON `null` remains explicitly null. Decision evidence separately classifies facts as missing, unknown or unavailable. Comparisons against any absent value remain unresolved; unknown or unavailable values are reported with their own guard-finding code. Presence guards treat a known missing fact as absent, but do not turn unknown or unavailable information into a false result.

Malformed scalar inputs, empty names, invalid provenance associations, evidence inconsistencies and binding collisions are rejected as invalid HTTP requests. Rejected, ambiguous and unresolved workflow evaluations are still durable attempts and do not change state or revision.

## Guard purity and lifecycle decisions

The existing `TransitionEvaluator` remains the only transition-selection mechanism. It receives the resolved context and has no API client, persistence dependency, clock, mutable business-data access, or callback. Existing three-valued logic and selection rules remain in force: multiple enabled transitions are ambiguous, and any unresolved competing guard prevents an unjustified selection.

A decision outcome is data, not a lifecycle transition. Core has no Meridian vocabulary or outcome-specific branch. A domain definition may bind an outcome under its own fact name and declare guards over it. The supplier example binds `decisionOutcome` and uses ordinary `eq` guards for its declared workflow states.

## Evidence and persistence

Every new attempt stores a complete immutable evaluation-evidence snapshot containing:

- exact definition ID/version, instance ID/revision, event and evaluation timestamp;
- instance, attempt and resolved facts;
- per-attempt provenance labels;
- fact knowledge states;
- generic external-decision metadata and the complete received response JSON;
- individual guard findings, evaluation result, selected transition or refusal reason;
- previous/resulting state and local commit status.

The attempt row also stores an SHA-256 evidence digest over the UTF-8 bytes of the serialized evidence snapshot. Fact dictionaries are ordinally sorted before serialization; property/array order in the evidence contract is stable. The external-response digest is computed separately after recursively sorting JSON object properties while retaining array order and JSON number tokens. Neither digest is Meridian's `factSet.digest`; that upstream digest is preserved verbatim and is not recomputed or asserted to use the same canonicalisation.

Accepted state/revision, attempt evidence and transition history commit in the existing SQLite transaction. Rejected, ambiguous and unresolved attempts store their evidence without updating the instance. Existing databases receive the two evidence columns additively at startup; old attempts without Gate 4 evidence remain identifiable by their empty evidence digest.

Caller-provided provenance is recorded as a claim, not verified authority. The HTTP API does not authenticate the decision source or prove that evidence corresponds to the submitted outcome. That authority boundary is outside Gate 4.

## Meridian adapter and historical decisions

`clients/DurableStateMachine.MeridianClient` is a standalone HTTP client. It submits an explicit business-effective date and knowledge cutoff to Meridian, parses the response fields used by the supplied OpenAPI contract, and posts the mapped result to the state-machine HTTP API. It references no Meridian implementation assembly and has no runtime Core/Persistence dependency. The adapter preserves the complete response JSON in the state-machine attempt.

The Meridian HTTP contract supports an optional `Idempotency-Key`. For keyed requests, the client requires explicit `businessEffectiveAt` and `knownAt` values and saves the operation ID, key and exact JSON request in a local envelope before sending. It verifies a same-key/same-request repeat returns the original evaluation and reports `409` with `Retry-After` as in progress and a different-payload `409` as a conflict. An uncertain transport, server or unusable-response outcome is surfaced with the same key so a caller can retry only from the retained envelope. This is idempotency for one Meridian API operation, not a durable external-activity subsystem or a transaction spanning Meridian and SQLite. In particular, it does not deduplicate workflow events, coordinate separate client filesystems/replicas, or make retries automatic.

Historical decision replay is a separate explicit HTTP call made only by the integration demonstration. It uses Meridian's historical replay endpoint and exact stored policy versions; it is not part of transition evaluation or state-machine replay. The demo does not modify the policy store. A future policy version and historical replay can be tested live only when the configured Meridian API retains that evaluation and has the later policy versions available.

## Orion and generic examples

The Orion definition remains configuration, not runtime code. It demonstrates guards for document completeness, eligibility outcome, completed manual review, cancellation permission and offer validity at a supplied evaluation time. In particular, offer validity is an explicit bound Boolean accompanied by a caller-supplied timestamp; Core does not read a clock or infer validity.

The Meridian supplier definition and existing Document Review definition use the same generic evaluator and API.

## Deliberate limits and trade-offs

Gate 4 does not implement authenticated evidence authority, commands/observations, generic external activity scheduling/retries, state-machine event deduplication, timers, distributed locks, full event replay, rule execution, or machine-definition migration. Meridian's request idempotency and local retry envelope do not remove the distributed uncertainty boundary: an evaluation may be stored remotely while the client is unsure whether it received the response. Retrying is safe only when the caller preserves and reuses the same key and same parsed request; a new key represents a new assessment.

The evidence snapshot duplicates fact and response data to make historical inspection independent of mutable current data. This costs storage but avoids lossy references and keeps the evaluator and persistence history explainable.
