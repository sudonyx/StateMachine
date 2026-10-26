# Durable State Machine

A standalone, domain-independent state-machine runtime that executes versioned workflow definitions through a durable HTTP service. Gate 5 distinguishes commands from external observations and accepted workflow events, adding definition-declared input mappings, controlled observation authority, expected-operation correlation, durable duplicate/stale outcomes and evidence inspection. Gate 7 routes synthetic Orion effects through the independently running Generic Harness and retains durable correlation between harness execution evidence and Gate 5 workflow transitions. Gate 8 adds versioned declarative timers, persisted UTC deadlines, atomic timer lifecycle updates, explicit due processing and restart recovery. Gate 9 strengthens input identity and revision arbitration across independent API hosts sharing SQLite, and adds a reproducible adversarial corpus and runner. Gate 10 adds read-only historical reconstruction, decision verification from stored evidence, projection-integrity reporting, recovery assessment, durable pre-evaluation intake, verified external-result staging and explicit safe continuation. Gate 11 adds immutable Orion 5.0 alongside 4.0, explicit bounded migration with administrative authorization, and replay across definition epochs. Gate 12 adds language 1.3's generic durable concern dimensions, explicit parent/concern input routing, declarative coordination guards and atomic cancellation; Orion 6.0 demonstrates document and asset concerns without changing older definitions. No automatic version selection or migration to Orion 6.0 is provided. Gate 6's direct provider path remains available only for development/test comparison. Gate 4's immutable evaluation facts, decision evidence and independent Meridian HTTP integration remain supported.

## Requirements, build and tests

Requires the .NET 10 SDK.

```powershell
dotnet build DurableStateMachine.slnx
dotnet test DurableStateMachine.slnx
```

## Solution structure

- `src/DurableStateMachine.Core/` — definition loading, validation, typed facts and pure deterministic evaluation.
- `src/DurableStateMachine.Persistence/` — EF Core SQLite records and transactional runtime/input/effect-intent/timer/migration operations.
- `src/DurableStateMachine.Api/` — HTTP endpoints, Gate 7 harness adapter, Gate 8 timer processor, development-only Gate 6 direct coordinator, synthetic provider stores, DTOs, error mapping and generated OpenAPI document.
- `src/DurableStateMachine.ProviderHost/` — independently runnable HTTP host for each synthetic provider capability and its generated OpenAPI description.
- `clients/DurableStateMachine.Cli/` — generic command-line HTTP client.
- `clients/DurableStateMachine.OrionClient/` — Orion scenario using the HTTP contract only.
- `clients/DurableStateMachine.MeridianClient/` — independent live HTTP demonstration of a Meridian decision bound to a generic workflow event.
- `clients/DurableStateMachine.AsteriaClient/` — Asteria-only release registry and coordinator for initial assessments through the published Decision API contract.
- `tests/DurableStateMachine.Tests/` — Gate 2 regression tests and SQLite-backed HTTP integration tests.
- `tools/OrionGate9Corpus/` — deterministic Gate 9 corpus generation, validation and API runner.
- `test-data/` — fixed 5,000-delivery Orion adversarial corpus.
- `definitions/` — Orion and document-review examples.
- `docs/` — supported language, API and design documentation.
- `reference/` — qualification brief and fictional Orion requirements source.

## Start the API

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:DURABLE_STATE_MACHINE_DB = ".\durable-state-machine.db"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "<set-a-local-random-value>"
dotnet run --project src\DurableStateMachine.Api
```

The default database file is `durable-state-machine.db` in the API's working directory. `DURABLE_STATE_MACHINE_DB` selects another SQLite path. The service creates its schema on startup. OpenAPI is served at `http://localhost:5000/openapi/v1.json`.

Synthetic providers use independent SQLite files in `synthetic-provider-data` by default. Set `DURABLE_STATE_MACHINE_PROVIDER_DB_DIR` to choose another directory. Each provider file is outside the workflow transaction and remains inspectable after API restart.

## Register and operate a definition

In another PowerShell terminal, the CLI can register the document-review definition and create an instance:

```powershell
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5000"
@'
{ "reviewer": "reviewer-1", "qualityScore": 85 }
'@ | Set-Content -Encoding utf8 review-facts.json
$id = "review-$([guid]::NewGuid().ToString('N'))"
dotnet run --project clients\DurableStateMachine.Cli -- register definitions\document-review-v1.json
dotnet run --project clients\DurableStateMachine.Cli -- create $id internal-document-review 1.0 '@review-facts.json'
dotnet run --project clients\DurableStateMachine.Cli -- inspect $id
dotnet run --project clients\DurableStateMachine.Cli -- send $id Submit
dotnet run --project clients\DurableStateMachine.Cli -- send $id Approve
dotnet run --project clients\DurableStateMachine.Cli -- attempts $id
dotnet run --project clients\DurableStateMachine.Cli -- history $id
```

The CLI supports `register`, `definitions`, `create`, `inspect`, `send`, `attempts`, `history`, `receipts`, `expected-operations`, `timers`, `effects`, `replay`, `verify-decisions`, `recovery`, `migrate-preview`, `migrate` and `migrations`. `create` accepts optional inline scalar facts or `@path-to-json-file` (the latter avoids shell quoting problems). Configure the base URL with `DURABLE_STATE_MACHINE_API_URL` or pass `--url http://host:port` before the command. Workflow rejections are returned as readable `200 OK` result envelopes with reason codes; malformed requests and unknown resources use HTTP error statuses.

The REST routes and request/response examples are in [gate-3-api.md](./docs/gate-3-api.md). The underlying machine format is documented in [state-machine-language-v1.md](./docs/state-machine-language-v1.md).

## Gate 11 definition evolution

Register [Orion 5.0](./definitions/orion-v5.json) without replacing [Orion 4.0](./definitions/orion-v4.json). Existing instances remain pinned to their creation version. The training API exposes a read-only migration preview and a controlled, durable migration endpoint. Execution is restricted to Development and requires `DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY`; the shared key is not production IAM.

The only successful migration contract is Orion 4.0 `Draft` to Orion 5.0 `Draft`, with no outstanding timer or external/local work. A refused attempt is durably inspectable and does not change workflow state or accepted history. Replay validates the exact definition digest for each epoch. For example, after registering both definitions:

```powershell
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "<local-development-key>"
dotnet run --project clients\DurableStateMachine.Cli -- migrate-preview <instance-id> `
  orion-equipment-lease 4.0 orion-equipment-lease 5.0 0 <migration-id> "approved training migration"
dotnet run --project clients\DurableStateMachine.Cli -- migrate <instance-id> `
  orion-equipment-lease 4.0 orion-equipment-lease 5.0 0 <migration-id> "approved training migration"
dotnet run --project clients\DurableStateMachine.Cli -- migrations <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- replay <instance-id>
```

V5 implements a bounded subset of the fictional amendment pack, including preliminary offer review, a five-day new-offer expiry, seven-day follow-up for newly created v5 requests, Specialist Assessment terminology and an explicit cancellation-review fact. The runtime does not enforce effective dates or establish that caller-supplied per-attempt facts are true. See [gate-11-design.md](./docs/gate-11-design.md), [gate-11-amendment-traceability.md](./docs/gate-11-amendment-traceability.md), [gate-11-test-results.md](./docs/gate-11-test-results.md), and [orion-definition-v5.md](./docs/orion-definition-v5.md) for details and unsupported requirements.

## Gate 12 durable concerns

Register [Orion 6.0](./definitions/orion-v6.json) alongside Orion 4.0 and 5.0.
Its parent lifecycle, document completeness and asset availability progress as
separate durable dimensions in one instance. Parent coordination is expressed
in the definition using `concernState` guards; observations continue through the
existing trusted Gate 5 input pipeline. Parent lifecycle revisions and each
concern's revision/history are tracked separately. Cancellation atomically
terminates the parent and all active concerns, without implying remote effect
reversal.

Inspect with the CLI:

```powershell
dotnet run --project clients\DurableStateMachine.Cli -- concerns <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- concern-history <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- concern-replay <instance-id>
```

Language 1.3, API routes, coordination semantics, tested behavior and limitations
are documented in [state-machine-language-v1.md](./docs/state-machine-language-v1.md),
[gate-3-api.md](./docs/gate-3-api.md), [gate-12-design.md](./docs/gate-12-design.md),
[gate-12-architecture-decision.md](./docs/gate-12-architecture-decision.md),
[gate-12-test-results.md](./docs/gate-12-test-results.md), and
[orion-definition-v6.md](./docs/orion-definition-v6.md).

## Gate 10 recovery inspection

For an instance ID, use the CLI or API to compare current projection and accepted transition history, inspect retained inputs/effects/timers, and review recovery classification:

```powershell
dotnet run --project clients\DurableStateMachine.Cli -- replay <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- verify-decisions <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- receipts <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- expected-operations <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- timers <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- effects <instance-id>
dotnet run --project clients\DurableStateMachine.Cli -- recovery <instance-id>
```

Replay and recovery inspection are read-only. They do not execute due timers, resume effects or call external systems. See [gate-10-design.md](./docs/gate-10-design.md), [gate-10-crash-matrix.md](./docs/gate-10-crash-matrix.md) and [gate-10-test-results.md](./docs/gate-10-test-results.md) for the implemented guarantees and tested limitations.

Commands and observations first commit their immutable request envelope as an inbound receipt with status `Received`. The evaluator, final receipt, attempt, accepted history, projection and local timer/effect/operation changes are committed in a second SQLite immediate transaction. If processing is interrupted between these commits, inspect `/api/instances/{id}/recovery` and explicitly resume the specific receipt with `POST /api/instances/{id}/inbound-messages/{receiptId}/resume` and an empty body. The continuation uses the persisted envelope and authority classification; it cannot accept a replacement payload. The original expected revision and observation operation/source checks are re-applied, so an input that has become stale remains refused. No schema migration rewrites legacy attempts or invents intake records.

After the trusted Generic Harness adapter receives and verifies a successful result, it first persists the immutable result payload/evidence and correlation to `StagedObservations`. Inspect them at `/api/instances/{id}/staged-observations`; continue a specific stored result with `POST /api/instances/{id}/staged-observations/{resultId}/resume` and an empty body. A crash after the staging commit is recoverable without another Harness execution. A remote response lost before this local commit remains unresolved; the API does not infer or retry it. The result still passes the existing Gate 5 checks, so a result that has become stale or whose instance was cancelled cannot alter workflow state.

## Orion client demonstration

The Orion client defaults to definition version 4.0 using language 1.2; version 3.0/language 1.1 remains available as the unchanged Gate 7 baseline. Configure the same local-only demonstration key on the API and client process. Gate 7 requires the Generic Harness and all three provider hosts to be running, and exact provider/API-description/operation bindings must be configured on the API. Follow [gate-7-integration.md](./docs/gate-7-integration.md) to start and register those providers before running the client. The key is a controlled test boundary, not production authentication:

```powershell
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "<set-a-local-random-value>"
dotnet run --project clients\DurableStateMachine.OrionClient
```

The client registers or reuses the exact Orion definition, creates a synthetic instance, demonstrates that an untrusted forged observation cannot progress it, then runs offer-document generation, asset reservation and customer notification through the Generic Harness. A successful provider result advances Orion only through a Gate 5 accepted observation. It inspects the persisted offer reminder/expiry deadlines before acceptance and their cancellation afterward. A second instance demonstrates cancellation and a late stale observation. The client does not use the direct provider route.

Language 1.1 and 1.2 definitions declare allowed command and observation mappings. Their direct `/events` endpoint is restricted; use `/commands` for requests and `/observations` for external claims. Timed definitions additionally restrict timer events to the durable timer processor. Observation authority requires an expected operation and the configured `X-State-Machine-Trusted-Ingestion-Key`; a claimed source label or `trusted` field is not authority. Configure the key for the API separately and set it in the client environment before running the Orion demo.

See [gate-5-design.md](./docs/gate-5-design.md) for input identity, duplicate/stale semantics, expected-operation evidence and the explicit local-demo limitations.

## Gate 7 harness-backed effects

`POST /api/instances/{id}/effects` accepts the three declared synthetic capabilities and submits each through the Generic Harness. Configure exact bindings under `Harness:Capabilities:{capability}`; runtime code contains no harness/provider IDs. `GET /api/instances/{id}/effects/{operationId}/inspection` fetches harness execution, attempts, evidence and a non-invoking replay-safety evaluation. An unresolved `OutcomeUnknown` is not converted into failure, and resume never re-invokes a known harness execution. If the initial harness response is lost before the execution ID is committed locally, the current harness API offers no idempotency-key lookup, so the adapter preserves the unresolved correlation gap instead of re-submitting.

See [gate-7-design.md](./docs/gate-7-design.md) for the trust, persistence and recovery boundaries and [gate-7-integration.md](./docs/gate-7-integration.md) for provider registration, environment configuration, the Orion scenario and fault demonstrations. The Gate 6 direct-provider path is retained at `/api/instances/{id}/direct-effects` only in Development with `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`; it is for regression comparison, not the primary runtime path. Gate 6's historical semantics and test boundaries are in [gate-6-design.md](./docs/gate-6-design.md) and [gate-6-crash-matrix.md](./docs/gate-6-crash-matrix.md).

## Gate 8 durable timers

Orion 4.0 schedules a 6-day offer reminder, a 7-day offer expiry, and a 10-day outstanding-document follow-up. The exact `dueAt` values and their definition version are stored in SQLite. Acceptance, decline, completion of the document request, or cancellation records timer cancellation in the same transaction as the workflow transition. At or after the stored expiry instant, acceptance and decline are refused even when expiry processing has not yet run.

The evaluator remains pure and clock-free. The API uses an injectable `TimeProvider`; restart recovery queries persisted due timers. There is no required in-memory timer and no provider call from the timer processor. Reminder/follow-up processing writes Gate 7 effect intents and expected observations transactionally; provider dispatch remains explicit through the existing effects API.

For deterministic development recovery, enable the controlled test/admin route:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS = "true"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "<set-a-local-random-value>"
dotnet run --project src\DurableStateMachine.Api
```

Then call `POST /api/test-controls/timers/process-due?limit=100` with the trusted ingestion header. The route processes all currently due rows in persisted due-time order; it does not accept caller-supplied timer IDs or events. See [gate-8-design.md](./docs/gate-8-design.md), [gate-8-test-matrix.md](./docs/gate-8-test-matrix.md), and [orion-definition-v4.md](./docs/orion-definition-v4.md) for the policy, limitations and coverage.

## Gate 9 duplicate and concurrency handling

The API records transport `deliveryId` separately from logical `messageId`/event identity, optional command `operationId`, optional `observationId`, expected-operation `correlationId`, instance revision, durable timer ID and Generic Harness execution ID. Exact logical retries return the original outcome; changed content under the same message ID is an identity conflict. A new command identity is evaluated normally and is not automatically called a transport duplicate.

Per-host instance gates are an optimization only. State-changing persistence transactions acquire SQLite's immediate writer reservation before reading the instance; a unique `(instanceId, revisionAfter)` transition-history index and conditional revision update provide the durable checks. This supports independent API hosts that share a SQLite file with working file locking. SQLite remains a single-writer database; network filesystems and database-independent distributed guarantees are not claimed.

Generate/validate the fixed corpus and run it against an API configured with a dedicated database and trusted-ingestion key:

```powershell
dotnet run --project tools\OrionGate9Corpus -- generate --seed 20261019
dotnet run --project tools\OrionGate9Corpus -- validate --seed 20261019
dotnet run --project tools\OrionGate9Corpus -- run --base-url http://localhost:5000 `
  --trusted-key "<local-test-key>" --mode concurrent --concurrency 8 --run-id gate9-run-1
```

Use `--mode baseline` for serial application lanes. Use `--mode multi-host` with repeated `--host-url` values for hosts sharing one SQLite database. The runner saves structured reports under the system temporary directory by default. See [gate-9-design.md](./docs/gate-9-design.md) and [gate-9-corpus.md](./docs/gate-9-corpus.md).

## Gate 4 fact-binding and Meridian demonstration

Event submissions may provide additional typed `facts` for that evaluation only. They are never merged into durable instance facts, and a per-attempt fact name that collides with an instance fact is rejected. Optional `factsProvenance` labels and generic `decisionEvidence` are preserved with the attempt; caller-supplied provenance is not proof of authenticity. The exact context, findings, decision evidence, result and a local evidence digest can be inspected at the attempt endpoints.

The new [Meridian supplier definition](./definitions/meridian-supplier-qualification-v1.json) exercises the same generic evaluator as Orion and Document Review. The standalone client calls the actual Meridian HTTP endpoint, explicitly supplies its temporal context, submits the result to the state-machine API, inspects persisted evidence, and explicitly invokes historical decision replay:

```powershell
$env:MERIDIAN_DECISION_API_URL = "http://localhost:5267"
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5000"
dotnet run --project clients\DurableStateMachine.MeridianClient -- --assessment-file .\meridian-assessment.json
```

Start both services first. The client persists the keyed request before sending and reuses it on later invocations; preserve the envelope file to retry an uncertain outcome. Use a new file and operation/key for an intentional reassessment. Add `--verify-conflict --decision-only` to test the service's same-key/changed-request conflict without submitting a workflow event. See [gate-4-meridian-integration.md](./docs/gate-4-meridian-integration.md) for the supported key semantics, live verification and limits, and [gate-4-design.md](./docs/gate-4-design.md) for evidence and binding semantics. Controlled HTTP contract tests do not count as live service verification.

## Gate 14 Asteria initial assessment

Milestone 3B adds an Asteria-specific SQLite release registry/coordinator and
the separately versioned
[Asteria release-governance V2 definition](./definitions/asteria-release-governance-v2.json).
It derives the canonical release-material digest locally, submits only the
published Decision API request fields, records the API response/evidence, and
correlates it through a genuine State Machine expected operation. The workflow
remains unable to reach deployment or `Completed`; assessment obligations
are not completed checks or approvals.

Start the real Asteria Decision API with AST-POL-001 V1 available on port
5267. In a separate PowerShell terminal, start the State Machine API with an
isolated database:

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5270"
$env:DURABLE_STATE_MACHINE_DB = "$env:TEMP\asteria-state-machine.db"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "asteria-local-demo-key"
dotnet run --project src\DurableStateMachine.Api
```

In another terminal at the repository root, point the coordinator at both
services and use a separate registry database. `prepare` commits/inspects the
workflow submission and parent revision; running `resume` as another process
then exercises recovery while it registers the expected operation, calls the
Decision API and submits the observation. The trusted-ingestion key must be
exactly the value the State Machine API was started with (any local value
works, but it must match); a mismatch fails expected-operation registration
with HTTP 403. The coordinator can safely be re-run with `resume` after
correcting it:

```powershell
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5270"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "asteria-local-demo-key"
$env:ASTERIA_DECISION_API_URL = "http://localhost:5267"
$env:ASTERIA_RELEASE_REGISTRY_PATH = "$env:TEMP\asteria-release-registry.db"
$releaseId = "AST-REL-G14-M3B-$([guid]::NewGuid().ToString('N'))"
dotnet run --project clients\DurableStateMachine.AsteriaClient -- prepare $releaseId `
  .\training-data\asteria\cases\asteria-initial-assessment-demo.json
dotnet run --project clients\DurableStateMachine.AsteriaClient -- resume $releaseId
```

The demo fixture is synthetic. Inspect the resulting workflow through the
generic CLI or State Machine API; its accepted state depends on the registered
policy result. `prepare` and `resume` must use the same registry database and
exact release ID. A persisted applicable-policy result is recovered using
the same Decision API idempotency key and request. A no-applicable-policy
response is stored locally as `Unresolved` with no fabricated evaluation ID
or selected policy version, because the Decision API does not persist or
reserve its idempotency key for that branch. The coordinator cannot provide a
cross-service transaction; see the
[Milestone 3B design](./docs/asteria/gate14-milestone3b-design.md),
[recovery analysis](./docs/asteria/gate14-milestone3b-recovery.md), and
[test results](./docs/asteria/gate14-milestone3b-test-results.md) for system
ownership, tested failure windows and limitations.

## Semantics and limitations

Definitions are immutable by `(id, version)`; new versions coexist. Instances remain bound to the exact version used at creation, with their instance facts fixed for their lifetime. Explicit per-attempt facts are immutable evaluation-only inputs and never mutate those facts. Each event attempt (including rejected, ambiguous and unresolved evaluations) is stored separately from accepted transition history, together with a complete evaluation-evidence snapshot. Accepted state, revision, attempt, evidence and transition history commit in one SQLite transaction. Per-host instance gates reduce local contention; Gate 9 SQLite write transactions and revision constraints arbitrate independent hosts sharing a database.

The Meridian client remains a demonstration adapter, not a durable external-activity subsystem. Meridian's idempotency key plus a saved local request envelope support deliberate same-key/same-request retries, but there is no atomic transaction spanning Meridian and local SQLite and the key does not deduplicate state-machine events. Gate 7 supports only the three independently hosted synthetic providers and the documented Generic Harness API; production provider credentials/secret management, background scheduling, automatic workflow retry orchestration, full replay, production authentication, a general distributed lock service and hierarchical/parallel statecharts remain unsupported. SQLite concurrency is limited to hosts sharing a database file with valid locking; no network-filesystem or database-independent distributed guarantee is claimed. The trusted-ingestion credential is a controlled training boundary; it does not prove provider identity or evidence authenticity. See [gate-3-design.md](./docs/gate-3-design.md), [gate-4-design.md](./docs/gate-4-design.md), [gate-5-design.md](./docs/gate-5-design.md), [gate-7-design.md](./docs/gate-7-design.md) and [gate-9-design.md](./docs/gate-9-design.md) for transaction, evidence and authority limits.

## Gate 13 Orion composition

Gate 13 composes this API, the independently hosted Orion Decision API, and the
existing Generic Harness without moving decision rules or provider execution
into the state-machine runtime. Start and configure the Generic Harness and
synthetic providers as described in [gate-7-integration.md](./docs/gate-7-integration.md).
Run the State Machine on port 5001 with its own SQLite database and exact
Harness capability bindings, and run the Decision API on port 5268 with a
separate Orion decision-history directory. The Orion policy pack is isolated
under the Decision API's `training-data/orion-policy/`; it is not part of
Meridian's shared policy catalog.

Run the live HTTP scenario from the repository root after all services are
ready:

```powershell
.\scripts\gate13-demo.ps1 -TrustedKey "<local-training-key>" `
  -OutputPath "$env:TEMP\gate13-evidence.json"
```

The runner registers immutable Orion 6.1, submits a workflow request, obtains a
bound layered decision over HTTP, and sends reservation, offer-document and
notification effects through the State Machine's Generic Harness adapter. It
then checks both systems' historical replay and State Machine history before
exporting the evidence chain. Use unique local database paths and keep the
trusted key out of source control. The runner does not replace controlled
failure-injection tests with live service failures. The protocol, responsibility
matrix, configuration, limitations and failure-test mapping are documented in
[gate-13-design.md](./docs/gate-13-design.md), [gate-13-integration-audit.md](./docs/gate-13-integration-audit.md),
and [gate-13-test-results.md](./docs/gate-13-test-results.md).
