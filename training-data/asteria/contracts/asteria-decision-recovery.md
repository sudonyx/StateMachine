# Asteria assessment recovery and idempotency

## Assessment idempotency

`POST /asteria/releases/assessments` requires exactly one `Idempotency-Key`
header. Keys are case-sensitive, 1-128 ASCII characters, and limited to
letters, digits, `.`, `_`, `~`, and `-`. Missing, multiple, blank, overlong,
or otherwise invalid keys are rejected with HTTP 400.

For an evaluation with an applicable policy version, the implementation
fingerprints the JSON assessment request, then uses the key and fingerprint
with the Asteria historical-decision store under the AST-POL-001 history
directory. JSON object properties are canonicalized for fingerprinting;
arrays retain their order. The key is looked up within that store and is not
returned by the API. This is not a cross-service or distributed idempotency
namespace guarantee.

- Repeating a successfully persisted request with the same key and same
  request returns the stored evidence and evaluation ID.
- Reusing that key with a different request returns HTTP 409 with
  `type: idempotency_key_reused`.
- If another request with that key holds the store's key lock for more than
  three seconds, the request returns HTTP 409 with
  `type: idempotency_operation_in_progress` and `Retry-After: 1`.
- The store writes the completed evaluation atomically after evaluation and
  evidence construction. An identical retry can recover a lost response once
  that record has been saved. No API guarantee is made for a failure before
  persistence, or for history shared across independently deployed stores.

The no-applicable-version path is an important exception: the service validates
the key but returns an unresolved assessment before fingerprinted
idempotency/persistence. Such a response has `evaluationId: null`, no
`evidence`, and an empty selected-version list. It is not recoverable by replay,
and the key is not reserved; a later request may use the same key without the
changed-request conflict that applies to persisted evaluations.

An unresolved assessment with an applicable policy version is different: it
is a completed policy evaluation, receives an evaluation ID, stores its
evidence, and participates in normal idempotency. Missing, Unknown, or
Unavailable facts can cause that unresolved outcome while still producing a
persisted record.

## Historical replay

`POST /asteria/releases/assessments/{evaluationId}/replay` addresses a saved
assessment by its GUID evaluation ID. It takes no body and requires no
idempotency header. An unknown ID returns 404.

Replay reads the stored fact snapshot and saved evidence, checks stored fact
and engine metadata consistency, resolves the exact registered AST-POL-001
version and verifies its registered source/rule digests and evidence metadata,
then reevaluates with that version and snapshot. It does not write a new
assessment or update the stored record. The response includes the reproduced
assessment, `matchesStoredEvidence`, and differences.

`matchesStoredEvidence` is specifically determined by comparisons of the
generic final decision, composition state, fact-set digest, and selected
policy-version identities. A true result is not a byte-for-byte comparison of
every evidence member. Missing/corrupt history or policy-store/integrity
failures other than a missing evaluation are not translated by this route into
a structured 4xx response; they can result in an unhandled server error.

## Policy selection and failures

Policy selection uses explicit `businessEffectiveAt` (date) and `knownAt`
(timestamp) from the assessment request. The selected policy-version metadata
is returned and preserved in evidence.

- If no policy version is applicable at the supplied temporal context, the
  API returns HTTP 200 with `outcome: Unresolved`, all obligation statuses
  `Unresolved`, `riskClassification: Unresolved`, no selected versions, and
  the no-version reason in `unresolvedFindings`. It is not a persisted
  evaluation, as described above.
- If a policy version is selected but the facts or policy rules leave a
  requirement unresolved, an unresolved result is returned and persisted with
  the evaluation evidence.
- A policy-store, policy-validation, evaluation, or persistence failure is not
  converted into an unresolved success response by this endpoint. The route
  handles request argument and idempotency exceptions as HTTP 400/409; other
  exceptions are unhandled server failures.

Assessment results are requirement/evidence outputs only. The API does not
record performed checks or approvals and never authorizes deployment.
