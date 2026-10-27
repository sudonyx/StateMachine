[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PWD "gate14-milestone4c-evidence")
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY)) {
    throw "Set DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY to the local State Machine trusted-ingestion key."
}

$stateMachineUrl = if ($env:DURABLE_STATE_MACHINE_API_URL) {
    $env:DURABLE_STATE_MACHINE_API_URL.TrimEnd("/")
} else {
    "http://localhost:5000"
}
$decisionApiUrl = if ($env:ASTERIA_DECISION_API_URL) {
    $env:ASTERIA_DECISION_API_URL.TrimEnd("/")
} else {
    "http://localhost:5267"
}
$registryPath = if ($env:ASTERIA_RELEASE_REGISTRY_PATH) {
    [System.IO.Path]::GetFullPath($env:ASTERIA_RELEASE_REGISTRY_PATH)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $OutputDirectory "asteria-release-registry.db"))
}

if (Test-Path $registryPath) {
    throw "Registry already exists at '$registryPath'. Use a new dedicated registry path; this script never deletes or resets registries."
}

$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$env:DURABLE_STATE_MACHINE_API_URL = $stateMachineUrl
$env:ASTERIA_DECISION_API_URL = $decisionApiUrl
$env:ASTERIA_RELEASE_REGISTRY_PATH = $registryPath

$releaseId = "AST-GATE14-4C-001"
$amendmentId = "AST-GATE14-4C-AMEND-001"
$initialMaterialPath = Join-Path $OutputDirectory "generation-1-material.json"
$amendedMaterialPath = Join-Path $OutputDirectory "generation-2-material.json"
$evidencePath = Join-Path $OutputDirectory "asteria-gate14-milestone4c-evidence.json"
$clientProject = Join-Path $PSScriptRoot "..\clients\DurableStateMachine.AsteriaClient\DurableStateMachine.AsteriaClient.csproj"

$initialMaterial = @{
    product = "AsteriaPortal"
    targetVersion = "4.2.0"
    environment = "production"
    releaseCategory = "Standard"
    changeSize = "Small"
    authenticationChange = $false
    customerDataImpact = $false
    releaseSummary = "Synthetic low-risk Standard release"
    releaseOwner = "release-owner@example.invalid"
    plannedDeploymentAt = "2027-03-15T10:00:00Z"
}
$amendedMaterial = @{
    product = "AsteriaPortal"
    targetVersion = "4.3.0"
    environment = "production"
    releaseCategory = "Emergency"
    changeSize = "Large"
    authenticationChange = $true
    customerDataImpact = $true
    releaseSummary = "Synthetic emergency release with authentication and customer-data impact"
    releaseOwner = "release-owner@example.invalid"
    plannedDeploymentAt = "2027-03-15T10:00:00Z"
}

$initialMaterial | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $initialMaterialPath
$amendedMaterial | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $amendedMaterialPath

try {
    Invoke-RestMethod -Method Get -Uri "$stateMachineUrl/health" | Out-Null
} catch {
    throw "State Machine API is not reachable at '$stateMachineUrl/health': $($_.Exception.Message)"
}

Write-Host "Release: $releaseId"
Write-Host "Amendment: $amendmentId"
Write-Host "Decision API: $decisionApiUrl (all assessments use its HTTP endpoint; no test handler is used)"

dotnet run --project $clientProject --no-launch-profile -- assess $releaseId $initialMaterialPath
if ($LASTEXITCODE -ne 0) { throw "Generation 1 assessment failed with exit code $LASTEXITCODE." }

dotnet run --project $clientProject --no-launch-profile -- amend $releaseId $amendmentId 1 $amendedMaterialPath
if ($LASTEXITCODE -ne 0) { throw "Generation 2 amendment failed with exit code $LASTEXITCODE." }

dotnet run --project $clientProject --no-launch-profile -- resume $releaseId
if ($LASTEXITCODE -ne 0) { throw "Generation 2 assessment failed with exit code $LASTEXITCODE." }

dotnet run --project $clientProject --no-launch-profile -- resume $releaseId
if ($LASTEXITCODE -ne 0) { throw "Repeated Generation 2 assessment failed with exit code $LASTEXITCODE." }

dotnet run --project $clientProject --no-launch-profile -- export-evidence $releaseId $evidencePath
if ($LASTEXITCODE -ne 0) { throw "Persisted evidence export failed with exit code $LASTEXITCODE." }

$evidence = Get-Content -Raw $evidencePath | ConvertFrom-Json
if ($evidence.currentGeneration -ne 2 -or $evidence.generations.Count -ne 2) {
    throw "Expected exactly two persisted generations and current generation 2."
}
if ($evidence.generations[0].currentStateMachineProjection.currentState -ne "Superseded") {
    throw "Generation 1 is not terminally Superseded."
}
if ($evidence.generations[1].assessmentOutcome -ne "AdditionalReviewRequired") {
    throw "Generation 2 did not produce the expected review assessment outcome."
}
foreach ($obligation in @("functionalVerification", "securityVerification", "releaseReviewApproval", "emergencyApproval")) {
    if ($evidence.generations[1].independentObligations.$obligation.status -ne "Required") {
        throw "Generation 2 did not independently require '$obligation'."
    }
}

Write-Host "Verified two generations. Evidence exported to '$evidencePath'."
