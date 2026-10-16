# Gate 7 local integration runbook

This runbook connects the workflow API to a running Generic Harness, registers the three sample provider descriptions, and runs Orion through the harness-backed `/effects` API. The Generic Harness is an external project and is not launched by this solution.

## Prerequisites

- .NET 10 SDK and the repository restored/built.
- Generic Harness running at a URL reachable by the workflow API (default `http://localhost:5247`).
- Three network-reachable provider URLs. The defaults below use ports 5261–5263 on the same machine. If the harness runs in a container or another host, set each advertised provider URL to an address reachable from that harness.
- A local-only trusted-ingestion value shared by the API and Orion client.

The provider hosts expose `/health` and `/openapi.json`. Each description uses the configured absolute provider URL as its OpenAPI server. Do not register a document whose server URL cannot be reached by the Generic Harness.

## Start the provider hosts

Open three PowerShell terminals in the repository root. Set a common local provider-data directory if desired; each capability writes a separate SQLite file.

Offer provider (port 5261):

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5261"
$env:DURABLE_STATE_MACHINE_PROVIDER_CAPABILITY = "offer-document"
$env:DURABLE_STATE_MACHINE_PROVIDER_PUBLIC_URL = "http://localhost:5261"
$env:DURABLE_STATE_MACHINE_PROVIDER_DB_DIR = ".\synthetic-provider-data"
dotnet run --project src\DurableStateMachine.ProviderHost
```

Reservation provider (port 5262):

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5262"
$env:DURABLE_STATE_MACHINE_PROVIDER_CAPABILITY = "equipment-reservation"
$env:DURABLE_STATE_MACHINE_PROVIDER_PUBLIC_URL = "http://localhost:5262"
$env:DURABLE_STATE_MACHINE_PROVIDER_DB_DIR = ".\synthetic-provider-data"
dotnet run --project src\DurableStateMachine.ProviderHost
```

Notification provider (port 5263):

```powershell
$env:ASPNETCORE_URLS = "http://localhost:5263"
$env:DURABLE_STATE_MACHINE_PROVIDER_CAPABILITY = "customer-notification"
$env:DURABLE_STATE_MACHINE_PROVIDER_PUBLIC_URL = "http://localhost:5263"
$env:DURABLE_STATE_MACHINE_PROVIDER_DB_DIR = ".\synthetic-provider-data"
dotnet run --project src\DurableStateMachine.ProviderHost
```

In another terminal, verify each host before registering its description:

```powershell
Invoke-RestMethod http://localhost:5261/health
Invoke-RestMethod http://localhost:5262/health
Invoke-RestMethod http://localhost:5263/health
```

For a fresh end-to-end Orion demonstration, use a clean provider-data directory. The sample client requests the fixed fictional asset `fictional-asset-1`; a prior reservation in the reservation provider's durable database will correctly cause a later, new operation to be refused. Keep an existing directory when inspecting or recovering that earlier run.

## Register provider descriptions and start the API

Run the following in one PowerShell terminal at the repository root. The function imports each host's JSON OpenAPI description into the Generic Harness, verifies the expected operation key is available, and retains the returned provider ID and exact API-description version ID for API configuration.

```powershell
$harnessUrl = "http://localhost:5247"

function Register-SyntheticProvider([string]$Capability, [string]$ProviderIdentity, [string]$ProviderUrl, [string]$ExpectedOperationKey) {
    $providerId = [guid]::NewGuid().ToString()
    $description = Invoke-RestMethod -UseBasicParsing "$ProviderUrl/openapi.json"
    $registration = Invoke-RestMethod `
        -UseBasicParsing `
        -Method Post `
        -Uri "$harnessUrl/providers/$providerId/api-descriptions" `
        -ContentType "application/json" `
        -Body ($description | ConvertTo-Json -Depth 100)
    $operation = $registration.operations | Where-Object { $_.key -eq $ExpectedOperationKey }
    if ($null -eq $operation) {
        throw "Harness did not import callable operation '$ExpectedOperationKey' for $Capability."
    }
    if ($operation.supportStatus -ne 0) {
        throw "Harness imported '$ExpectedOperationKey' as unsupported for $Capability (supportStatus=$($operation.supportStatus))."
    }
    [pscustomobject]@{
        Capability = $Capability
        ProviderIdentity = $ProviderIdentity
        ProviderId = $providerId
        ApiDescriptionVersionId = $registration.apiDescription.id
        OperationKey = $operation.key
        ProviderUrl = $ProviderUrl
    }
}

$offer = Register-SyntheticProvider "offer-document" "synthetic-offer-provider" "http://localhost:5261" "PUT:/offers/{operationId}"
$reservation = Register-SyntheticProvider "equipment-reservation" "synthetic-reservation-provider" "http://localhost:5262" "POST:/reservations"
$notification = Register-SyntheticProvider "customer-notification" "synthetic-notification-provider" "http://localhost:5263" "POST:/notifications"

$offer, $reservation, $notification | Format-Table Capability, ProviderId, ApiDescriptionVersionId, OperationKey
```

The harness contract uses `supportStatus: 0` for supported and `1` for unsupported. If a provider description imports with an unsupported operation, fix the advertised URL or OpenAPI description before continuing. The harness's callable-operation list for each imported version is authoritative; do not guess an operation key.

Configure the API in that same terminal, substituting the local database path and a non-empty local trusted key if needed:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = "http://localhost:5000"
$env:DURABLE_STATE_MACHINE_DB = ".\durable-state-machine.db"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "local-demo-key-change-me"
$env:Harness__BaseUrl = $harnessUrl

Set-Item 'Env:Harness__Capabilities__offer-document__ProviderIdentity' $offer.ProviderIdentity
Set-Item 'Env:Harness__Capabilities__offer-document__ProviderId' $offer.ProviderId
Set-Item 'Env:Harness__Capabilities__offer-document__ApiDescriptionVersionId' $offer.ApiDescriptionVersionId
Set-Item 'Env:Harness__Capabilities__offer-document__OperationKey' $offer.OperationKey

Set-Item 'Env:Harness__Capabilities__equipment-reservation__ProviderIdentity' $reservation.ProviderIdentity
Set-Item 'Env:Harness__Capabilities__equipment-reservation__ProviderId' $reservation.ProviderId
Set-Item 'Env:Harness__Capabilities__equipment-reservation__ApiDescriptionVersionId' $reservation.ApiDescriptionVersionId
Set-Item 'Env:Harness__Capabilities__equipment-reservation__OperationKey' $reservation.OperationKey

Set-Item 'Env:Harness__Capabilities__customer-notification__ProviderIdentity' $notification.ProviderIdentity
Set-Item 'Env:Harness__Capabilities__customer-notification__ProviderId' $notification.ProviderId
Set-Item 'Env:Harness__Capabilities__customer-notification__ApiDescriptionVersionId' $notification.ApiDescriptionVersionId
Set-Item 'Env:Harness__Capabilities__customer-notification__OperationKey' $notification.OperationKey

dotnet run --project src\DurableStateMachine.Api
```

Bindings are exact snapshots: changing the environment after the API starts does not change an existing effect's provider/version/operation. Create a new intent to use a newly registered description version. Leave `CredentialBindingId` unset for these local synthetic providers.

## Run the Orion client

In a fourth terminal:

```powershell
$env:DURABLE_STATE_MACHINE_API_URL = "http://localhost:5000"
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = "local-demo-key-change-me"
dotnet run --project clients\DurableStateMachine.OrionClient -- definitions\orion-v1.json
```

The client registers/reuses Orion 3.0, creates two synthetic instances, submits a forged observation without the trusted key, and runs offer generation, reservation and notification through `/api/instances/{id}/effects`. It also injects a pause after the late offer provider result but before Gate 5 observation acceptance, cancels that instance, and explicitly resumes the same operation; Gate 5 should retain a stale receipt rather than reopen the cancelled workflow.

The API returns workflow-side effect evidence from:

```powershell
$instanceId = "<instance id printed by the client>"
$operationId = "<effect operation id printed by the client>"
Invoke-RestMethod "http://localhost:5000/api/instances/$instanceId/effects"
Invoke-RestMethod "http://localhost:5000/api/instances/$instanceId/effects/$operationId"
Invoke-RestMethod "http://localhost:5000/api/instances/$instanceId/effects/$operationId/inspection"
```

The inspection route reads the known harness execution, attempts and evidence, and posts the documented replay-safety evaluation. It reports whether a provider was called during that evaluation; that value must be `false`.

## Controlled interruption cases

The automated `Gate7HarnessTests` use a recording HTTP handler to make harness responses deterministic; they do not call a live Generic Harness. The tests cover:

- a successful provider response correlated through Gate 5, including saved harness attempt/evidence IDs and transition history;
- harness rejection and unsupported imported operation without a workflow observation;
- a response lost on initial execution submission, leaving unknown state and no second execution on resume;
- an intent committed before submission, followed by API test-host restart and explicit resume from its persisted binding;
- an accepted harness response interrupted before local persistence, followed by test-host restart, unresolved local state and no second submission.

These interruption points are deterministic simulated faults. The restart cases dispose one in-process `WebApplicationFactory` and start another against the same SQLite data; they do not terminate an operating-system process. The Orion client additionally simulates the late-result-before-observation window. Do not report these as real process-kill tests or as a live harness execution.

The development-only one-shot control route is:

```text
POST /api/test-controls/effects/{operationId}/{point}
```

The API must run in Development and `DURABLE_STATE_MACHINE_ENABLE_TEST_CONTROLS=true` for this route to exist. Gate 7-specific points are `AfterLocalIntentCommitBeforeHarness` and `AfterHarnessResponseBeforeLocalPersistence`; these return `503` at the injected boundary. These controls are for development/test only and are not mapped in normal production configuration.

## Failure and recovery interpretation

- If the API fails before intent commit, there is no durable workflow operation.
- If intent is committed with no attempt, pending-work can be inspected and the same operation explicitly resumed. The provider call has not yet been submitted.
- If an invocation is sent but its response or execution ID is unavailable, report `OutcomeUnknown`; absence of a local ID is not proof the provider did nothing.
- If the execution ID is known, resume/inspection queries that execution instead of submitting another one.
- If the provider result is known but Gate 5 refuses the observation, preserve both facts. The provider-side effect is not undone and the workflow state is not force-updated.

The Generic Harness intentionally does not persist full provider response bodies and currently has no lookup-by-idempotency-key endpoint. The workflow cannot recover the business result from execution metadata alone when the original response was lost. See [gate-7-design.md](./gate-7-design.md) for the full boundary and [gate-3-api.md](./gate-3-api.md) for route semantics.
