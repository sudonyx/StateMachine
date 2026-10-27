# Gate 14 — Milestone 4B recovery

## Stable identities and ordering

The immediate reservation transaction stores the amendment fingerprint,
expected base generation, allocated generation, canonical material/digest,
stable replacement workflow/work IDs, and `AmendmentReserved` before the
coordinator contacts the State Machine. Repeating an amendment ID with an
identical request returns that reservation; a different payload or expected
base conflicts. A competing amendment cannot reserve while the release is
pending or after its base pointer has changed.

Supersession attempts are persisted by `(release, amendment, expected old
revision)`. Operation and message IDs are deterministic and stored before
registration/submission. The same attempt is reconciled after a lost response.
If another old-generation transition wins the revision race, its attempt and
receipt remain historical; after inspecting the new revision, recovery may
allocate a distinct deterministic attempt for that revision.

## Recovery matrix

| Interruption or race | Durable authority and recovery action | Safety result |
|---|---|---|
| Reservation committed before replacement creation | Read the amendment and reserved generation; register V3 if needed; GET the persisted instance ID and create only if absent. | No generation or workflow identity is reallocated. |
| Replacement created but response lost | Re-run the same amendment. GET the stable instance ID, verify definition/version/facts, and advance to `NewInstanceProvisioned`. | No duplicate replacement instance; no assessment work is enabled. |
| Process stops after provisioning but before phase commit | Reconcile the same instance and commit the phase with the current amendment lease. | Old instance remains current and unsealed until the phase is durable. |
| Supersession operation registration response lost | List expected operations and validate the same operation ID, source, revision, and required evidence; retry only with its persisted ID. | Registration is not inferred from a missing response. |
| Old observation changes revision concurrently | Inspect current old state/revision. The stale attempt remains inspectable; recovery does not reuse it at a different revision and instead records a deterministic revision-specific attempt. | A racing observation can win only on the old instance; no old evidence is copied to the replacement. |
| Supersession accepted but acknowledgement lost | Inspect the old instance, stable inbound receipts, expected-operation resolution, accepted attempt, and transition history. Record `OldInstanceSuperseded` only when the V3 terminal transition is proved. | The old instance stays sealed; no duplicate or new-random-ID supersession is submitted. |
| Supersession refused or uncertain | Leave phase at `NewInstanceProvisioned`, keep admission suspended, surface the refusal, and inspect/retry with persisted attempt identities. | The replacement cannot become operational. No timeout is treated as proof of success. |
| Old generation had another expected operation pending | Preserve the operation on the old instance. A late observation is subject to generic terminal handling and cannot reactivate it or resolve new-generation work. | Old expected operations/evidence remain historical; they are not rebound. |
| Old sealed; process stops before pointer activation | Registry is `ActivationIncomplete` and still points at the old generation. On restart, re-prove remote supersession/history, then activate. | Fail-closed pointer; no reassessment work is enabled until the terminal barrier is confirmed. |
| Crash during pointer activation | Pointer, new work status, normal admission, and `NewGenerationActivated` phase are in one immediate SQLite transaction. Reload after restart to determine whether it committed; if not, repeat the transaction. | No partially switched pointer/work state is visible. |
| Ordinary coordinator lease expires during a call | A replacement coordinator can acquire only after expiry. The old coordinator checks current generation and lease before each business request; a request already in flight is reconciled by stable identity. | Lease is not treated as a cross-service fence. The old instance's terminal State Machine barrier rejects late direct input after sealing. |
| Concurrent amendments | The immediate transaction serializes reservations and verifies current generation plus pending state. | At most one amendment for a base is reserved; a stale base refuses. |

## Inspecting unresolved work

While `RegistryState` is `AmendmentPending` or `ActivationIncomplete`, ordinary
coordinator work is refused. Inspect the amendment row, its phase, the current
pointer, generation snapshots, per-revision supersession attempts, State
Machine inbound messages, expected operations, attempts, and accepted history
before taking action. Do not delete records, edit material snapshots, change
the current pointer manually, or reuse an amendment ID with a changed payload.

Retry the `amend` CLI operation with the same release ID, amendment ID,
expected base generation, and exact material file. The CLI has no manual
"force" or identity-regeneration path. An unresolved V2 base is not migrated
or reopened; the registry rejects amendment because that immutable definition
has no supersession transition.

The system has no cross-service transaction. A request accepted by the State
Machine is established through durable receipt/attempt/history inspection.
External requests already dispatched before a supersession are not cancelled
or reversed by this protocol.
