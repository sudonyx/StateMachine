# Gate 14 controlled/live demonstration

This runbook uses the checked-in Asteria client and the published service
contracts. It does not claim that a controlled test handler is a live service.
The State Machine API is in this repository; the Decision API, Generic Harness,
functional/security verification providers, deployment provider, and health
provider are independent services and are not present in this checkout. Start
those services from their own current source checkouts and verify their build
versions before calling the run a live demonstration.

## 1. Start the repository-owned State Machine API

Open a PowerShell terminal at the `StateMachine` repository root:

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5000'
$env:DURABLE_STATE_MACHINE_DB = (Join-Path $PWD 'durable-state-machine-gate14.db')
$stateMachineKeyBytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($stateMachineKeyBytes)
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = [Convert]::ToBase64String($stateMachineKeyBytes)
dotnet run --project .\src\DurableStateMachine.Api
```

Keep the generated key out of source control and reports. The Asteria CLI and
approval gateway need the same key in their own process environments. Do not
expose the trusted-ingestion API outside the isolated demonstration network.

## 2. Start and register independent services

Start the Decision API, Generic Harness, functional verification provider,
security verification provider, deployment provider, and health provider in
separate terminals using their own service-specific startup instructions.
The Decision API and Harness implementations are not part of this repository,
and no startup command or port is inferred for them here.

Register the providers with Generic Harness using their actual OpenAPI
descriptions. Obtain the provider ID, API-description version ID, and operation
key from the Harness registration records. Do not substitute provider labels,
invent IDs, or treat a successful Harness HTTP response as business success.

The deployment contract uses `POST /deployments`, status by deployment ID, and
the read-only idempotency-key lookup. Its illustrative server is
`http://localhost:5271`. The health contract uses read-only
`GET /health-verifications`; its illustrative server is `http://localhost:5272`.
These contract URLs are not proof those services are running. Functional and
security provider schemas/operation keys must come from their registered
descriptions; no such provider descriptions are included in this repository.

In the Asteria CLI terminal, configure the local clients from the actual
service registrations:

```powershell
$env:DURABLE_STATE_MACHINE_API_URL = 'http://localhost:5000/'
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = '<same generated key>'
$env:DURABLE_STATE_MACHINE_DB = (Join-Path $PWD 'durable-state-machine-gate14.db')
$env:ASTERIA_RELEASE_REGISTRY_PATH = (Join-Path $PWD 'asteria-release-gate14.db')
$env:ASTERIA_DECISION_API_URL = '<Decision API base URL>/'
$env:GENERIC_HARNESS_API_URL = '<Generic Harness base URL>/'

$env:ASTERIA_FUNCTIONAL_VERIFICATION_PROVIDER_IDENTITY = '<configured identity label>'
$env:ASTERIA_FUNCTIONAL_VERIFICATION_PROVIDER_ID = '<registered provider ID>'
$env:ASTERIA_FUNCTIONAL_VERIFICATION_API_DESCRIPTION_VERSION_ID = '<registered description version ID>'
$env:ASTERIA_FUNCTIONAL_VERIFICATION_OPERATION_KEY = '<registered operation key>'
$env:ASTERIA_SECURITY_VERIFICATION_PROVIDER_IDENTITY = '<configured identity label>'
$env:ASTERIA_SECURITY_VERIFICATION_PROVIDER_ID = '<registered provider ID>'
$env:ASTERIA_SECURITY_VERIFICATION_API_DESCRIPTION_VERSION_ID = '<registered description version ID>'
$env:ASTERIA_SECURITY_VERIFICATION_OPERATION_KEY = '<registered operation key>'
$env:ASTERIA_DEPLOYMENT_PROVIDER_IDENTITY = '<configured identity label>'
$env:ASTERIA_DEPLOYMENT_PROVIDER_ID = '<registered provider ID>'
$env:ASTERIA_DEPLOYMENT_API_DESCRIPTION_VERSION_ID = '<registered description version ID>'
$env:ASTERIA_DEPLOYMENT_OPERATION_KEY = 'requestDeployment'
$env:ASTERIA_DEPLOYMENT_STATUS_PROVIDER_IDENTITY = '<configured identity label>'
$env:ASTERIA_DEPLOYMENT_STATUS_PROVIDER_ID = '<registered provider ID>'
$env:ASTERIA_DEPLOYMENT_STATUS_API_DESCRIPTION_VERSION_ID = '<registered description version ID>'
$env:ASTERIA_DEPLOYMENT_STATUS_OPERATION_KEY = 'getDeploymentStatus'
$env:ASTERIA_DEPLOYMENT_IDEMPOTENCY_LOOKUP_PROVIDER_IDENTITY = '<configured identity label>'
$env:ASTERIA_DEPLOYMENT_IDEMPOTENCY_LOOKUP_PROVIDER_ID = '<registered provider ID>'
$env:ASTERIA_DEPLOYMENT_IDEMPOTENCY_LOOKUP_API_DESCRIPTION_VERSION_ID = '<registered description version ID>'
$env:ASTERIA_DEPLOYMENT_IDEMPOTENCY_LOOKUP_OPERATION_KEY = 'lookupDeploymentByIdempotencyKey'
$env:ASTERIA_HEALTH_PROVIDER_IDENTITY = '<configured identity label>'
$env:ASTERIA_HEALTH_PROVIDER_ID = '<registered provider ID>'
$env:ASTERIA_HEALTH_API_DESCRIPTION_VERSION_ID = '<registered description version ID>'
$env:ASTERIA_HEALTH_OPERATION_KEY = 'verifyDeployedApplicationHealth'
```

The provider identity variables are local audit labels, not cryptographic
provider authentication. Only the authenticated service configuration and
validated provider response establish the evidence available to this
demonstration.

## 3. Prepare the material and assess

Create a material file with the exact client fields. The values below are
illustrative training inputs; the independent Decision API must return the
policy outcome and applicability values.

```powershell
@{
    product = 'AsteriaPortal'
    targetVersion = '4.2.0'
    environment = 'staging'
    releaseCategory = 'Standard'
    changeSize = 'Medium'
    authenticationChange = $false
    customerDataImpact = $true
    releaseSummary = 'Gate 14 controlled release demonstration'
    releaseOwner = 'release-owner@example.invalid'
    plannedDeploymentAt = '2027-03-15T10:00:00Z'
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 .\asteria-release-demo.json

dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    assess AST-G14-DEMO-001 .\asteria-release-demo.json
```

Only continue when the accepted Decision API response is bound to the exact
release, generation, and material digest and the policy outcome is eligible.
An unresolved or prohibited assessment is a blocked result, not a failed
command to work around.

## 4. Run verification and approvals

Run only the verification activities the accepted Decision API response marks
`Required`:

```powershell
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    verify AST-G14-DEMO-001 FunctionalVerification
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    verify AST-G14-DEMO-001 SecurityVerification
```

Start the synthetic approval gateway in a separate terminal with the same
registry path and trusted State Machine key. Supply two distinct principals
and credentials out-of-band; each token must have at least 32 characters.
Never commit the credential JSON or include tokens in evidence exports.

```powershell
$env:ASPNETCORE_URLS = 'http://localhost:5273'
$env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = '<same generated key>'
$env:ASTERIA_RELEASE_REGISTRY_PATH = '<same absolute registry path>'
$env:ASTERIA_APPROVAL_AUTHORITY_REGISTRY_VERSION = 'synthetic-authority-v1'
$env:ASTERIA_APPROVAL_PRINCIPALS_JSON = '<out-of-band JSON for distinct ReleaseReviewer and EmergencyReleaseAuthoriser principals>'
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- approval-gateway
```

For every required approval, send a JSON body containing exactly
`releaseId`, `assessmentGeneration`, `materialDigest`, `concernId`,
`submissionId`, `idempotencyKey`, `decision`, and `evidenceReference` to
`POST http://localhost:5273/approvals`, with the corresponding principal's
`Authorization: Bearer <token>` header. Use separate principals for
`ReleaseReviewApproval` and `EmergencyApproval`. Do not submit a `NotRequired`
approval or fabricate an approval result.

## 5. Confirm readiness, deploy, verify health, and process deadlines

```powershell
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    readiness AST-G14-DEMO-001
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    deploy AST-G14-DEMO-001
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    health AST-G14-DEMO-001
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    process-deadlines AST-G14-DEMO-001
dotnet run --project .\clients\DurableStateMachine.AsteriaClient -- `
    export-evidence AST-G14-DEMO-001 .\asteria-gate14-evidence.json
```

The deployment provider response is admission evidence only. The client
reconciles by executing the documented status or idempotency-key GET operation
through Generic Harness and does not resubmit an ambiguous deployment. Health
is a separate Harness execution with the contract's `queryParameters`;
HTTP 200 alone does not establish a pass. Deadline processing is explicit and
durable; no continuously running scheduler is claimed. An exported
`OutcomeUnknown`, refusal, conflict, failed result, or missing instance must
remain visible and must not be described as a successful completion.

## Current demonstration boundary

The controlled integration tests use deterministic HTTP handlers and are
synthetic. They verify the repository-owned State Machine integration and
contract-shaped response processing, not the live Decision API, Generic
Harness, or independent providers. A live Gate 14 demonstration remains
pending until all independently started service versions and registrations
are recorded and the original provider responses are captured in the export.
