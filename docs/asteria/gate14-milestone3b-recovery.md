# Gate 14 — Milestone 3B recovery

## Durable identities and ordering

The registry creates and commits a unique release/work pair before contacting
the State Machine. Its stable workflow instance ID, submit message ID,
expected-operation ID, Decision API idempotency key, canonical request and
fingerprint are reused during recovery. A processing lease serializes active
coordinators sharing the SQLite registry; the coordinator renews the lease
while work is active and releases it on ordinary completion. After a crash,
another process can continue after the lease expires.

There is no cross-service transaction. Each retry first inspects registry and
remote state and only repeats an action with its stable identity and exact
stored payload. A refusal, changed payload, stale revision or conflicting
identity is surfaced rather than converted into a success-shaped fallback.

## Crash and lost-response matrix

| Window | Durable authority and recovery | Resulting guarantee / limit |
|---|---|---|
| Registry commit before workflow-instance creation | Load the registry row and GET its stable instance ID. Create only if absent; if creation races or its response is lost, GET and validate the same ID, definition/version and creation facts. | Registry identity prevents coordinator retries from allocating a second instance. No atomic transaction spans registry and State Machine. |
| Workflow instance created, before submit | GET the stable instance ID, validate its release facts and exact definition identity, then submit using the deterministic message ID. | State Machine stores/deduplicates the command identity. The coordinator uses inspection after uncertain HTTP outcomes. |
| Submit committed, before registry parent revision is stored | Inspect the instance and inbound receipt for the stable submit message. If accepted/duplicate, derive the committed revision from the instance and persist it once; otherwise retry only with the same submit identity and exact command. | Does not infer that a lost response means no commit. A mismatched state/revision stops recovery. |
| Submission recorded, before expected-operation registration | Reconcile the current committed revision, register the stable operation against that revision, then inspect the operation record and verify ID, instance, type, source, revision and evidence requirements. | Operation registration is deliberately after parent revision discovery. A changed parent revision is refused rather than rebound. |
| Operation registered, before Decision API invocation | Inspect/reconcile the stable expected operation and persist the canonical request/fingerprint before invoking the API. Reuse the same idempotency key and exact request. | The coordinator does not create a second logical assessment work item during recovery. |
| Applicable-policy evaluation persisted, response lost | Retry the exact request with the same key; the API's documented behavior returns the stored result for an identical persisted request. Validate it against the registry and persist the original response locally. | Depends on API history being available in the same Decision API history store. Independent stores are not a shared idempotency namespace. |
| API response received, before local response commit | Repeat the exact API request and key. For a persisted applicable-policy evaluation, recover the same result; for no applicable policy, the API does not persist/reserve the key, so the next response may differ. | The coordinator records the first response it durably receives. If the process loses an unpersisted no-policy response, the API cannot prove the old result; uncertainty cannot be eliminated by this API contract. |
| No-applicable-policy response durably recorded | Reuse the stored response; do not call the API again. Preserve `Unresolved`, null evaluation ID/evidence, empty selected versions, and the policy-selection context even if a later policy could apply. | The registry makes the historical unresolved work immutable although the API itself did not reserve its key. No synthetic ID/version is created. |
| Local response persisted, before observation submission | Validate the stored original response again, ensure the expected operation at its recorded parent revision, then submit the same observation identity and payload. | A changed response or failed evidence/binding comparison stops before workflow mutation. |
| Observation accepted, coordinator acknowledgement lost | On resume, inspect the stable inbound receipt and expected-operation status. Accept only the same payload digest and a resolved operation; then advance registry status to `Accepted`. | Duplicate accepted delivery is idempotent. Conflicting or refused receipts are not treated as accepted. |
| Coordinator process stops during any active phase | A second process cannot acquire the live lease. The original process renews it while active; after termination and lease expiry the next process reconciles records before retrying. | Lease scope is coordinators sharing the same SQLite database. It is not a distributed lock across separate database files or unsupported network filesystems. |

## Response binding and uncertainty

The API contract has no fields for workflow instance, definition, parent
revision, or expected operation. Those values remain coordinator-owned
correlation data and are checked against the State Machine when registering
and submitting the observation. The Decision API's release/generation/digest
echoes are compared with the registry, but are not independent proof of
workflow authority or digest-to-material correspondence.

The no-applicable-policy branch is not an evaluation record: there may be no
evaluation ID, evidence, selected version, or API idempotency reservation.
The coordinator preserves and stores that result as unresolved and does not
retry it after the response is locally durable. Before that local commit, a
process failure leaves an irreducible external uncertainty. The integration
does not invent a synthetic evaluation ID or claim the API can replay that
branch.

## State Machine binding

The State Machine remains authoritative for committed workflow state,
revision, expected-operation registration and resolution, accepted history,
and observation receipts. The coordinator verifies the operation and
submits the trusted observation using its stable operation/message identities.
Only an inspected accepted/duplicate receipt with matching payload and a
resolved operation permits the registry to record `Accepted`. The trusted
ingestion key is not an independent identity proof.

An accepted policy outcome is not evidence that a verification was run, an
approval was granted, deployment occurred, or deployment health was checked.
The V2 definition contains no path to deployment or completion.
