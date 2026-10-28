# Gate 14 local synthetic demonstration

The repeatable bootstrap runs the real State Machine, Generic Harness, and
Decision API interfaces with independently hosted local synthetic provider
services. Verification and deployment outcomes are deterministic demonstration
fixtures. This is not a real production verification, human approval, or
deployment, and must not be reported as production acceptance.

## One-command run

From the `StateMachine` repository root, with the Decision API available at
`http://localhost:5267/`, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Start-AsteriaGate14Demo.ps1
```

The existing Standard scenario remains the default. To run a separate
high-risk Emergency scenario without replacing its evidence, use a new
temporary output directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Start-AsteriaGate14Demo.ps1 -ReleaseCategory Emergency
```

The Emergency run requires all four applicability obligations to be
`Required`, then verifies the archived `Completed` projection and the two
separately authenticated synthetic approval records (`ReleaseReviewer` and
`EmergencyReleaseAuthoriser`) before reporting success. Each run uses a
timestamped directory by default; retain the Standard and Emergency exports
separately.

The script builds the solution, verifies the Decision API and each provider's
health/OpenAPI endpoints, and then starts an isolated State Machine and Generic
Harness. It registers Asteria V5, imports the actual functional, security,
deployment, and health descriptions, verifies each returned provider and
API-description version ID and supported operation key, then runs the full
assessment-to-completion lifecycle and exports structured evidence.

The State Machine and Harness use ports `5001` and `5248`, not the user's
existing services on `5000` and `5247`. Synthetic providers use `5271`
(deployment), `5272` (health), `5273` (approval gateway), `5274` (functional),
and `5275` (security). The script refuses to stop or overwrite existing
processes and creates unique SQLite databases under a new temporary directory
for every run. The user's Decision API is used as an independent service at
port `5267`.

The trusted-ingestion key and approval bearer tokens are generated in process
memory, passed only to their intended child processes, and never printed or
written to disk. The default script completes all stages in the same process
so the key and actual Harness registration values remain available to the
client. `-SkipFullLifecycle` is only for checking service startup and
registration; it does not preserve those ephemeral credentials for a later
CLI invocation.

On success, the script prints:

- The release ID and returned registration results.
- The temporary log/database directory.
- The evidence export path (`gate14-evidence.json`).
- The exact child process IDs it started.

Inspect the JSON export for required applicability, passed functional and
security evidence, separate authenticated approval records, readiness,
terminal deployment status, passed post-deployment health, and State Machine
completion. A successful Harness HTTP execution by itself is not a business
result. Deployment admission is followed by bounded, read-only status
reconciliation; the non-idempotent deployment request is never resent.

## Stop a completed demonstration

Use only the exact child process IDs printed by that invocation. Do not stop
processes by name or terminate the user's services. For example, replace the
sample list with the IDs from the current run:

```powershell
$demoPids = @(1234, 5678)
foreach ($id in $demoPids) {
    Stop-Process -Id $id
}
```

The script does not delete the temporary databases or logs. Retain the
evidence export and logs if they are needed for review; remove only the
specific temporary directory after the evidence is no longer needed.

## Runtime requirements and boundaries

- The independent Decision API must be running and expose the supported
  Asteria policy-version and evaluation operations. The script fails clearly
  if it is unavailable.
- Ports `5001`, `5248`, and `5271`-`5275` must be free. Existing ports `5000`,
  `5247`, and `5267` are not modified.
- A new random trusted-ingestion key is generated for each isolated State
  Machine. It is not a substitute for provider authentication or a production
  secret-management system.
- Functional/security and deployment/health providers are synthetic local
  hosts. Their results demonstrate binding, durable capture, and coordinator
  behavior only.
- The approval gateway authenticates the synthetic local principals using
  ephemeral bearer tokens. It does not establish a real human identity.
- The deployment and health YAML contracts remain authoritative. JSON
  import-equivalent descriptions are also checked in because this Generic
  Harness build's OpenAPI importer accepts JSON, not YAML. The script imports
  those descriptions through the real Harness API and verifies returned
  supported operations.
- Decision API, State Machine, Harness, and provider logs/databases are
  independent. The demo does not modify existing State Machine or Harness
  databases.
- Deadline processing is an explicit CLI step. The synthetic lifecycle
  exports durable deadline state; it does not claim that a continuously
  running scheduler processed future deadlines.

The user reports that a separate Emergency/high-risk lifecycle also passed,
with all four obligations Required, distinct authenticated synthetic approval
principals, deployment and health evidence, terminal Completed, and consistent
historical replay. Retain its own evidence/export separately from the Standard
run; this documentation update does not replace or claim to independently
rerun either live scenario.

The local synthetic runs establish the successful V5 business lifecycle, not
production provider authentication, real-world approval authority, or real
deployment. The Asteria coordinator crash windows and concurrency races have
genuine process/API-client acceptance tests. Most deterministic fault
boundaries use the test-process-hosted controlled Harness; targeted independent
Generic Harness and synthetic-provider process tests separately verify
deployment idempotency and restart durability. The exact coverage boundary
and evidence are recorded in [gate14-final-acceptance.md](./gate14-final-acceptance.md);
see also [gate14-completion-report.md](./gate14-completion-report.md).
