# Durable State Machine

A standalone, domain-independent state-machine runtime that executes versioned workflow definitions through a durable HTTP service. Gate 3 adds a SQLite-backed API and two independent HTTP clients while preserving the Gate 2 deterministic evaluator.

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
dotnet run --project clients\DurableStateMachine.Cli -- register definitions\document-review-v1.json
dotnet run --project clients\DurableStateMachine.Cli -- create review-123 internal-document-review 1.0 '@review-facts.json'
dotnet run --project clients\DurableStateMachine.Cli -- inspect review-123
dotnet run --project clients\DurableStateMachine.Cli -- send review-123 Submit
dotnet run --project clients\DurableStateMachine.Cli -- send review-123 Approve
dotnet run --project clients\DurableStateMachine.Cli -- attempts review-123
dotnet run --project clients\DurableStateMachine.Cli -- history review-123
```

The CLI supports `register`, `definitions`, `create`, `inspect`, `send`, `attempts` and `history`. `create` accepts optional inline scalar facts or `@path-to-json-file` (the latter avoids shell quoting problems). Configure the base URL with `DURABLE_STATE_MACHINE_API_URL` or pass `--url http://host:port` before the command. Workflow rejections are returned as readable `200 OK` result envelopes with reason codes; malformed requests and unknown resources use HTTP error statuses.

The REST routes and request/response examples are in [gate-3-api.md](./docs/gate-3-api.md). The underlying machine format is documented in [state-machine-language-v1.md](./docs/state-machine-language-v1.md).

## Orion client demonstration

With the API running:

```powershell
dotnet run --project clients\DurableStateMachine.OrionClient -- definitions\orion-v1.json
```

The client registers or reuses Orion definition version 1.0, creates an instance with synthetic facts, submits `Submit` and `Approve`, then submits `Cancel` from a terminal state to show a recorded rejection. It retrieves current state, attempts and accepted history through HTTP.

## Semantics and limitations

Definitions are immutable by `(id, version)`; new versions coexist. Instances remain bound to the exact version used at creation, with facts fixed for their lifetime. Each event attempt (including rejected, ambiguous and unresolved evaluations) is stored separately from accepted transition history. Accepted state, revision, attempt and transition history commit in one SQLite transaction. The HTTP service serializes same-instance submissions within one process and checks the persisted revision during updates.

This gate does not implement mutable facts, timers, external effects, a scheduler, retries, exactly-once delivery, input deduplication, replay, migrations, distributed locking, authentication, or hierarchical/parallel statecharts. The current supported runtime has no pending time-based or external work. See [gate-3-design.md](./docs/gate-3-design.md) for transaction boundaries, concurrency limits and deferred work.
