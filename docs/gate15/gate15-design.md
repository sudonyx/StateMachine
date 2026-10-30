# Gate 15 workload and methodology

## Scope and workload selection

Gate 15 uses the published State Machine API and its SQLite-backed persistence.
The benchmark definition is the deliberately small, domain-neutral
`gate15-generic-load` v1.0 fixture at
`tools/Gate15Load/definitions/gate15-generic-load-v1.json`. It exercises the
same definition loader, admission, evaluator, arbitration, receipts, history,
timer creation, current-state reads, and replay endpoints as other definitions;
it adds no runtime branch or provider integration.

Asteria V5 was not selected for the primary throughput profile: its meaningful
transitions require separately bound decision, approval, provider, or health
evidence. Fabricating those outcomes would invalidate the workload. Orion was
also not used as the primary definition: its cancellation and progression
guards require business facts that a load generator must not invent. The
generic fixture keeps those authority concerns out of the core-runtime
measurement. Its `WaitingExternal` state is an unresolved wait only; it does
not assert that a provider ran or succeeded.

The fixture produces pending `Active` work, externally waiting work with a
durable seven-day deadline, `Completed` and `Cancelled` terminal instances,
and revision-competing `BeginWork`/`CancelWork` pairs from the same `Draft`
revision. Exact duplicate messages preserve the original message identity and
payload. Late records have a business occurrence time before an earlier event
and a delivery time after it. Repeated `BeginWork` messages are used for
deterministic historical replay and refusal/staleness paths.

## Corpus

The default seed is `20261029`; all business timestamps are anchored to
2026-01-01 UTC and do not read the wall clock. Default output is
`artifacts\gate15\corpus`. The manifest hashes each NDJSON data file and binds
the corpus to the exact generic definition digest. JSON property order is
fixed by record shape, UTF-8 is emitted without a BOM, and NDJSON uses LF.

The generated timer schedule is an input plan, not evidence that the runtime
processed those timers. The load runner submits the delivery records to the
runtime. It processes concurrent groups using a barrier and has bounded
parallelism. Retries reuse the same `delivery_id` and `message_id`; checkpoints
are bound to the run ID, manifest digest, and absolute database path.
Scenario profiles are mutually exclusive, while the long-history, duplicate,
late-delivery, and concurrency dimensions can overlap. The corpus validation
report documents their counts.

## Reproduction (PowerShell)

Run these commands from the StateMachine repository directory:

```powershell
dotnet run --project tools\Gate15Load\Gate15Load.csproj -- generate --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --output artifacts\gate15\corpus
dotnet run --project tools\Gate15Load\Gate15Load.csproj -- validate --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --output artifacts\gate15\corpus
dotnet run --project tools\Gate15Load\Gate15Load.csproj -- determinism --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --output artifacts\gate15\corpus
```

For an isolated runtime, build first and start the API with a database path
that is not used by development:

```powershell
dotnet build DurableStateMachine.slnx
$run = Join-Path $PWD 'artifacts\gate15\runtime'
New-Item -ItemType Directory -Force $run | Out-Null
$env:DURABLE_STATE_MACHINE_DB = Join-Path $run 'gate15.db'
$api = Start-Process dotnet -ArgumentList @('src\DurableStateMachine.Api\bin\Debug\net10.0\DurableStateMachine.Api.dll', '--urls', 'http://127.0.0.1:5188') -PassThru
Invoke-RestMethod http://127.0.0.1:5188/health
dotnet run --project tools\Gate15Load\Gate15Load.csproj -- run --base-url http://127.0.0.1:5188 --output artifacts\gate15\corpus --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --database $env:DURABLE_STATE_MACHINE_DB --run-id gate15-full-20261029 --concurrency 4 --report artifacts\gate15\runtime\performance-results.json --checkpoint artifacts\gate15\runtime\checkpoint.json
Stop-Process -Id $api.Id
```

The API is a single process; no external providers are started or contacted.
The full run command used in the measured execution is:

```powershell
dotnet run --project tools\Gate15Load\Gate15Load.csproj -- run --base-url http://127.0.0.1:5188 --output artifacts\gate15\corpus --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --database $env:DURABLE_STATE_MACHINE_DB --run-id gate15-full-20261029 --concurrency 4 --report artifacts\gate15\runtime\performance-results.json --checkpoint artifacts\gate15\runtime\checkpoint.json
```

For post-run restart and replay verification, stop the original API process,
start a new API process with the same database path, and run:

```powershell
dotnet run --project tools\Gate15Load\Gate15Load.csproj -- verify --base-url http://127.0.0.1:5188 --output artifacts\gate15\corpus --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --run-id gate15-full-20261029 --concurrency 4 --sample-size 1000 --report artifacts\gate15\runtime\correctness-results.json
```

The canonical timed run created runtime timers from accepted transition time
plus the declared `P7D` duration and used `TimeProvider.System`. The API has
no mutable clock-advance endpoint. A supplemental process-start fixed clock
is available only for isolated development experiments. It requires enabled
test controls, a trusted-ingestion key, an explicit absolute DB path matching
`DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE`, and a sidecar marker protecting
an existing database. Use a fresh database and key; never point the test
clock at the canonical workload database.

The development-only due-processing endpoint requires
`DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true` and a trusted-ingestion key
configured locally. Use a fresh local key; never put it in the corpus or
reports. It is an explicit operation, not an always-on scheduler:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS = 'true'
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = [guid]::NewGuid().ToString('N')
$api = Start-Process dotnet -ArgumentList @('src\DurableStateMachine.Api\bin\Debug\net10.0\DurableStateMachine.Api.dll', '--urls', 'http://127.0.0.1:5188') -PassThru
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5188/api/test-controls/timers/process-due?limit=500' -Headers @{ 'X-State-Machine-Trusted-Ingestion-Key' = $env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY }
Stop-Process -Id $api.Id
```

In the canonical run, this endpoint returned no due timers because each timer
was scheduled seven days after its accepted transition. The fixed historical
business timestamps do not advance the runtime clock. A later experiment
used a separate marked database and fixed process-start times across
restarts to demonstrate an overdue timer firing and idempotent subsequent
processing; see `gate15-recovery-results.md` and
`gate15-supplemental-results.md`.

To configure a fixed clock for a new, isolated database, set the exact same
absolute path in both DB variables before starting the API:

```powershell
$run = Join-Path $PWD 'artifacts\gate15\supplemental\timer-recovery'
New-Item -ItemType Directory -Force $run | Out-Null
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS = 'true'
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = [guid]::NewGuid().ToString('N')
$env:DURABLE_STATE_MACHINE_DB = Join-Path $run 'isolated-timers.db'
$env:DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE = $env:DURABLE_STATE_MACHINE_DB
$env:DURABLE_STATE_MACHINE_TEST_CLOCK_UTC = '2026-01-01T00:00:00Z'
$api = Start-Process dotnet -ArgumentList @('src\DurableStateMachine.Api\bin\Debug\net10.0\DurableStateMachine.Api.dll', '--urls', 'http://127.0.0.1:5188') -PassThru
```

The test clock is fixed for that process. To simulate elapsed time, stop the
API and restart it against the same marked test database after setting a new
`DURABLE_STATE_MACHINE_TEST_CLOCK_UTC`. Do not modify timer rows or point this
configuration at the canonical workload DB.

## Separate profile and evaluator measurements

The runner supports `--scenario ordinary|adversarial|long-history|all`.
Scenario filters are mutually exclusive. Use a unique absolute database path,
run ID, checkpoint, and report for each profile; the checkpoint is bound to
the corpus, scenario, run, and database. For each profile, set its fresh DB
path and instrumentation environment before starting the API, then run:

```powershell
$scenario = 'ordinary'
$run = Join-Path $PWD "artifacts\gate15\supplemental\profiles\$scenario"
New-Item -ItemType Directory -Force $run | Out-Null
$env:DURABLE_STATE_MACHINE_DB = Join-Path $run 'profile.db'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS = 'true'
$env:DURABLE_STATE_MACHINE_ENABLE_EVALUATION_METRICS = 'true'
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = [guid]::NewGuid().ToString('N')
Remove-Item Env:DURABLE_STATE_MACHINE_TEST_CLOCK_UTC -ErrorAction SilentlyContinue
Remove-Item Env:DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE -ErrorAction SilentlyContinue
$api = Start-Process dotnet -ArgumentList @('src\DurableStateMachine.Api\bin\Debug\net10.0\DurableStateMachine.Api.dll', '--urls', 'http://127.0.0.1:5188') -PassThru
try {
    Invoke-RestMethod http://127.0.0.1:5188/health
    dotnet run --project tools\Gate15Load\Gate15Load.csproj -- run --base-url http://127.0.0.1:5188 --output artifacts\gate15\corpus --seed 20261029 --instances 10000 --deliveries 100000 --history-days 180 --profile generic-durable-command-mix --database $env:DURABLE_STATE_MACHINE_DB --run-id "gate15-profile-$scenario" --scenario $scenario --concurrency 4 --trusted-ingestion-key $env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY --report (Join-Path $run 'performance.json') --checkpoint (Join-Path $run 'checkpoint.json')
}
finally {
    Stop-Process -Id $api.Id
}
```

Repeat with `$scenario = 'adversarial'` and `$scenario = 'long-history'`,
restarting the API against a different database path each time.

For evaluator metrics, set `ASPNETCORE_ENVIRONMENT=Development`,
`DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true`, and
`DURABLE_STATE_MACHINE_ENABLE_EVALUATION_METRICS=true` before starting the
API. The runner retrieves aggregates from the protected
`/api/test-controls/evaluation-metrics` route when given the trusted key.
Timing surrounds evaluator calls and uses a bounded histogram; percentile
values are approximate bucket upper bounds. The measured delivery interval
excludes definition registration and instance creation, which is timed
separately. Capture the starting DB and WAL sizes if an exact combined
storage delta is needed; the profile reports include DB-file baselines and
post-run DB/WAL sizes, but not a baseline WAL size.

The three scenario partitions, the separate fresh mixed run, their
post-restart replay results, and their storage observations are summarized in
`gate15-supplemental-results.md`. The original canonical run and its
machine-readable results remain unchanged.

## Outcome-specific storage attribution

The `storage-attribution` command measures one outcome in one fresh database.
Use a unique database and run ID for each outcome and repetition. It registers
the generic definition, creates 512 identical `Active` instances through
accepted `BeginWork` setup inputs, checkpoints the database, and captures the
baseline. It then submits 512 measured commands and verifies their durable
receipt statuses, input/history row deltas, current projections, and
historical replay. A second `PRAGMA wal_checkpoint(TRUNCATE)` precedes the
final DB/WAL and row-count snapshot.

The same setup is used for all categories. `accepted` sends `CompleteWork`;
`rejected` sends `BeginWork` in `Active`, where the declared event has no
applicable transition; `duplicate` exactly resubmits each baseline
`BeginWork` message identity and envelope; `stale` sends `CompleteWork` with
revision 0 against the prepared revision-1 instance. The fixed, small payload
and process-start test clock are shared across runs. Instance IDs are fixed
across outcome categories, while run IDs remain unique report identifiers.
The tool refuses a database that already contains instance/receipt/history
evidence in any instance namespace.

Example (repeat with a fresh path and run ID for each category/repetition):

```powershell
$outcome = 'accepted'
$repeat = 1
$run = Join-Path $PWD "artifacts\gate15\supplemental\storage-attribution\$outcome-r$repeat"
New-Item -ItemType Directory -Force $run | Out-Null
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS = 'true'
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = [guid]::NewGuid().ToString('N')
$env:DURABLE_STATE_MACHINE_DB = Join-Path $run 'storage.db'
$env:DURABLE_STATE_MACHINE_TEST_CLOCK_DATABASE = $env:DURABLE_STATE_MACHINE_DB
$env:DURABLE_STATE_MACHINE_TEST_CLOCK_UTC = '2026-01-01T00:00:00Z'
$api = Start-Process dotnet -ArgumentList @('src\DurableStateMachine.Api\bin\Debug\net10.0\DurableStateMachine.Api.dll', '--urls', 'http://127.0.0.1:5188') -PassThru
try {
    Invoke-RestMethod http://127.0.0.1:5188/health
    dotnet run --project tools\Gate15Load\Gate15Load.csproj -- storage-attribution --base-url http://127.0.0.1:5188 --database $env:DURABLE_STATE_MACHINE_DB --outcome $outcome --run-id "gate15-store-$outcome-r$repeat" --sample-count 512 --report (Join-Path $run 'result.json')
}
finally {
    Stop-Process -Id $api.Id
}
```

The reported quotient is an experiment-specific estimate, not a universal
per-row cost. It includes page allocation and index changes caused by the
measured deliveries, but excludes all 512 setup instances and their
prerequisite receipts, attempts, and histories by using the post-setup
checkpoint as the baseline. WAL is checkpointed at both boundaries. Compare
the two independent runs per category and report variability rather than
attributing setup data to the measured outcome.
