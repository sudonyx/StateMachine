# Durable State Machine

A standalone, domain-independent state-machine runtime that executes versioned workflow definitions through a durable HTTP service. Gate 4 adds immutable per-evaluation facts, recorded decision evidence and an independent Meridian HTTP integration while preserving the earlier evaluator and persistence semantics.

## Requirements, build and tests

Requires the .NET 10 SDK.

```powershell
dotnet build DurableStateMachine.slnx
dotnet test DurableStateMachine.slnx
```

## Solution structure

- `src/DurableStateMachine.Core/` — definition loading, validation, typed facts and pure deterministic evaluation.
- `src/DurableStateMachine.Persistence/` — EF Core SQLite records and transactional runtime operations.
- `src/DurableStateMachine.Api/` — HTTP endpoints, DTOs, error mapping and OpenAPI document.
- `clients/DurableStateMachine.Cli/` — generic command-line HTTP client.
- `clients/DurableStateMachine.OrionClient/` — Orion scenario using the HTTP contract only.
- `clients/DurableStateMachine.MeridianClient/` — independent live HTTP demonstration of a Meridian decision bound to a generic workflow event.
- `tests/DurableStateMachine.Tests/` — Gate 2 regression tests and SQLite-backed HTTP integration tests.
- `definitions/` — Orion and document-review examples.
- `docs/` — supported language, API and design documentation.
- `references/` — qualification brief and fictional Orion requirements source.

## Start the API

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:DURABLE_STATE_MACHINE_DB = ".\durable-state-machine.db"
dotnet run --project src\DurableStateMachine.Api
```

The default database file is `durable-state-machine.db` in the API's working directory. `DURABLE_STATE_MACHINE_DB` selects another SQLite path. The service creates its schema on startup. OpenAPI is served at `http://localhost:5000/openapi/v1.json`.

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

The CLI supports `register`, `definitions`, `create`, `inspect`, `send`, `attempts` and `history`. `create` accepts optional inline scalar facts or `@path-to-json-file` (the latter avoids shell quoting problems). Configure the base URL with `DURABLE_STATE_MACHINE_API_URL` or pass `--url http://host:port` before the command. Workflow rejections are returned as readable `200 OK` result envelopes with reason codes; malformed requests and unknown resources use HTTP error statuses.

The REST routes and request/response examples are in [gate-3-api.md](./docs/gate-3-api.md). The underlying machine format is documented in [state-machine-language-v1.md](./docs/state-machine-language-v1.md).

## Orion client demonstration

With the API running:

```powershell
dotnet run --project clients\DurableStateMachine.OrionClient -- definitions\orion-v1.json
```

The client registers or reuses Orion definition version 1.0, creates an instance with synthetic facts, submits guarded document/eligibility and offer events, then submits `Cancel` from a terminal state to show a recorded rejection. It retrieves current state, attempts and accepted history through HTTP.

## Gate 4 fact-binding and Meridian demonstration

Event submissions may provide additional typed `facts` for that evaluation only. They are never merged into durable instance facts, and a per-attempt fact name that collides with an instance fact is rejected. Optional `factsProvenance` labels and generic `decisionEvidence` are preserved with the attempt; caller-supplied provenance is not proof of authenticity. The exact context, findings, decision evidence, result and a local evidence digest can be inspected at the attempt endpoints.

The new [Meridian supplier definition](./definitions/meridian-supplier-qualification-v1.json) exercises the same generic evaluator as Orion and Document Review. The standalone client calls the actual Meridian HTTP endpoint, explicitly supplies its temporal context, submits the result to the state-machine API, inspects persisted evidence, and explicitly invokes historical decision replay:

```powershell
$env:MERIDIAN_DECISION_API_URL = "http://localhost:5267"
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5000"
dotnet run --project clients\DurableStateMachine.MeridianClient -- --assessment-file .\meridian-assessment.json
```

Start both services first. The client persists the keyed request before sending and reuses it on later invocations; preserve the envelope file to retry an uncertain outcome. Use a new file and operation/key for an intentional reassessment. Add `--verify-conflict --decision-only` to test the service's same-key/changed-request conflict without submitting a workflow event. See [gate-4-meridian-integration.md](./docs/gate-4-meridian-integration.md) for the supported key semantics, live verification and limits, and [gate-4-design.md](./docs/gate-4-design.md) for evidence and binding semantics. Controlled HTTP contract tests do not count as live service verification.

## Semantics and limitations

Definitions are immutable by `(id, version)`; new versions coexist. Instances remain bound to the exact version used at creation, with their instance facts fixed for their lifetime. Explicit per-attempt facts are immutable evaluation-only inputs and never mutate those facts. Each event attempt (including rejected, ambiguous and unresolved evaluations) is stored separately from accepted transition history, together with a complete evaluation-evidence snapshot. Accepted state, revision, attempt, evidence and transition history commit in one SQLite transaction. The HTTP service serializes same-instance submissions within one process and checks the persisted revision during updates.

Gate 4's Meridian client is a demonstration adapter, not a durable external-activity subsystem. Meridian's idempotency key plus a saved local request envelope support deliberate same-key/same-request retries, but there is no atomic transaction spanning Meridian and local SQLite and the key does not deduplicate state-machine events. Generic durable external effects, automatic retries, exactly-once delivery, input deduplication, timers, full replay, migrations, distributed locking, authentication, and hierarchical/parallel statecharts are not implemented. The runtime has no pending time-based or external work. See [gate-3-design.md](./docs/gate-3-design.md) for the existing transaction/concurrency boundaries and [gate-4-design.md](./docs/gate-4-design.md) for Gate 4's additional limits.
