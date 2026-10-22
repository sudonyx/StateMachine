param(
    [string]$StateMachineUrl = "http://localhost:5001",
    [string]$DecisionApiUrl = "http://localhost:5268",
    [string]$HarnessUrl = "http://localhost:5247",
    [Parameter(Mandatory = $true)]
    [string]$TrustedKey,
    [string]$ApplicationId = "ORION-G13-$([guid]::NewGuid().ToString('N'))",
    [string]$InstanceId = "orion-g13-$([guid]::NewGuid().ToString('N'))",
    [string]$AssetId = "orion-synthetic-asset-$([guid]::NewGuid().ToString('N'))",
    [int]$ApplicantTradingMonths = 36,
    [decimal]$EquipmentValueGbp = 85000,
    [int]$LeaseDurationMonths = 36,
    [int]$RiskScore = 68,
    [string]$EquipmentCategory = "synthetic-industrial-generator",
    [bool]$IsApprovedEquipmentCategory = $true,
    [bool]$IsExcludedEquipmentCategory = $false,
    [bool]$MaterialInconsistency = $false,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$stateMachineBase = $StateMachineUrl.TrimEnd("/")
$decisionApiBase = $DecisionApiUrl.TrimEnd("/")
$harnessBase = $HarnessUrl.TrimEnd("/")
$trustedHeaders = @{ "X-State-Machine-Trusted-Ingestion-Key" = $TrustedKey }

function Invoke-JsonApi {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body,
        [hashtable]$Headers = @{}
    )

    $request = @{
        Method = $Method
        Uri = $Uri
        TimeoutSec = 45
        Headers = $Headers
    }
    if ($null -ne $Body) {
        $request.ContentType = "application/json"
        $request.Body = ConvertTo-Json -InputObject $Body -Depth 100 -Compress
    }
    Invoke-RestMethod @request
}

function Get-ResponseItems {
    param([object]$Value)

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return ,@($Value)
    }
    if ($null -ne $Value.PSObject.Properties["value"]) {
        return ,@($Value.value)
    }
    return ,@($Value)
}

function Register-ExpectedOperation {
    param(
        [string]$OperationId,
        [string]$ObservationType,
        [string]$SourceIdentity,
        [long]$ExpectedRevision,
        [string[]]$RequiredEvidence
    )

    Invoke-JsonApi -Method Post `
        -Uri "$stateMachineBase/api/instances/$InstanceId/expected-operations" `
        -Headers $trustedHeaders `
        -Body @{
            operationId = $OperationId
            observationType = $ObservationType
            sourceIdentity = $SourceIdentity
            expectedRevision = $ExpectedRevision
            requiredEvidence = $RequiredEvidence
        }
}

function Invoke-StateMachineEffect {
    param([hashtable]$EffectRequest)
    Invoke-JsonApi -Method Post `
        -Uri "$stateMachineBase/api/instances/$InstanceId/effects" `
        -Headers $trustedHeaders `
        -Body $EffectRequest
}

$stateMachineOpenApi = Invoke-JsonApi -Method Get -Uri "$stateMachineBase/openapi/v1.json"
$decisionSources = Invoke-JsonApi -Method Get -Uri "$decisionApiBase/orion-equipment-leases/policy-sources"
$harnessOpenApi = Invoke-JsonApi -Method Get -Uri "$harnessBase/openapi/v1.json"

if (-not ($decisionSources | Where-Object { $_.sourceId -eq "ORION_EQUIPMENT_LEASE_ELIGIBILITY" })) {
    throw "The Decision API does not expose the versioned Orion equipment-lease policy source."
}
if ($stateMachineOpenApi.info.title -notmatch "DurableStateMachine|State Machine" -or
    $harnessOpenApi.info.title -notmatch "Harness") {
    throw "A required endpoint did not return the expected OpenAPI service identity."
}

$definitionPath = Join-Path $PSScriptRoot "..\definitions\orion-v6.1.json"
$definitionJson = [System.IO.File]::ReadAllText([System.IO.Path]::GetFullPath($definitionPath))
$definitionRegistration = Invoke-RestMethod `
    -Method Post `
    -Uri "$stateMachineBase/api/definitions" `
    -Headers $trustedHeaders `
    -ContentType "application/json" `
    -TimeoutSec 45 `
    -Body $definitionJson

$businessEffectiveAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
$knownAt = [DateTimeOffset]::UtcNow.ToString("o")
$instance = Invoke-JsonApi -Method Post `
    -Uri "$stateMachineBase/api/instances" `
    -Body @{
        id = $InstanceId
        definitionId = "orion-equipment-lease"
        definitionVersion = "6.1"
        facts = @{ applicationId = $ApplicationId }
    }

$submitCommand = @{
    messageId = "submit-$InstanceId"
    type = "SubmitApplication"
    expectedRevision = 0
}
$submission = Invoke-JsonApi -Method Post `
    -Uri "$stateMachineBase/api/instances/$InstanceId/commands" `
    -Body $submitCommand
if ($submission.attempt.status -ne "Accepted") {
    throw "The State Machine refused SubmitApplication: $($submission.message.reasonCode) $($submission.message.explanation)"
}

$documentOperationId = "documents-$([guid]::NewGuid().ToString('N'))"
Register-ExpectedOperation `
    -OperationId $documentOperationId `
    -ObservationType "DocumentsVerified" `
    -SourceIdentity "gate13-synthetic-document-service" `
    -ExpectedRevision 1 `
    -RequiredEvidence @("documentSetDigest") | Out-Null
$documentObservation = Invoke-JsonApi -Method Post `
    -Uri "$stateMachineBase/api/instances/$InstanceId/observations" `
    -Headers $trustedHeaders `
    -Body @{
        messageId = "documents-$ApplicationId"
        operationId = $documentOperationId
        observationId = $documentOperationId
        type = "DocumentsVerified"
        expectedRevision = 1
        correlationId = $documentOperationId
        source = "gate13-synthetic-document-service"
        evidence = @{ documentSetDigest = "sha256:synthetic-document-set-$ApplicationId" }
    }
if ($documentObservation.message.status -ne "Accepted") {
    throw "The State Machine refused the synthetic document-verification observation."
}

$assessmentOperationId = "assessment-$([guid]::NewGuid().ToString('N'))"
Register-ExpectedOperation `
    -OperationId $assessmentOperationId `
    -ObservationType "EligibilityAssessmentResult" `
    -SourceIdentity "Orion Layered Decision API" `
    -ExpectedRevision 1 `
    -RequiredEvidence @("decisionEvaluationId", "inputFactSetDigest", "policyVersionIdentities", "outcomeState") |
    Out-Null

$decisionRequest = @{
    applicationId = $ApplicationId
    workflowInstanceId = $InstanceId
    definitionId = "orion-equipment-lease"
    definitionVersion = "6.1"
    workflowRevision = 1
    assessmentGeneration = 1
    expectedOperationId = $assessmentOperationId
    businessEffectiveAt = $businessEffectiveAt
    knownAt = $knownAt
    applicantTradingMonths = $ApplicantTradingMonths
    equipmentValueGbp = $EquipmentValueGbp
    leaseDurationMonths = $LeaseDurationMonths
    riskScore = $RiskScore
    equipmentCategory = $EquipmentCategory
    isApprovedEquipmentCategory = $IsApprovedEquipmentCategory
    isExcludedEquipmentCategory = $IsExcludedEquipmentCategory
    materialInconsistency = $MaterialInconsistency
}

try {
    $decision = Invoke-JsonApi -Method Post `
        -Uri "$decisionApiBase/orion-equipment-leases/layered" `
        -Headers @{ "Idempotency-Key" = $assessmentOperationId } `
        -Body $decisionRequest
}
catch {
    $expectedOperations = Invoke-JsonApi -Method Get `
        -Uri "$stateMachineBase/api/instances/$InstanceId/expected-operations"
    $pendingAssessment = $expectedOperations | Where-Object { $_.operationId -eq $assessmentOperationId }
    if ($null -ne $pendingAssessment -and $pendingAssessment.status -eq "Expected") {
        throw "Decision API unavailable; no result was invented or submitted. Expected assessment '$assessmentOperationId' remains $($pendingAssessment.status). Instance '$InstanceId' is available for a later correlated result. Cause: $($_.Exception.Message)"
    }
    throw
}

$binding = $decision.workflowBinding
if ($binding.applicationId -ne $ApplicationId -or
    $binding.workflowInstanceId -ne $InstanceId -or
    $binding.definitionId -ne "orion-equipment-lease" -or
    $binding.definitionVersion -ne "6.1" -or
    $binding.workflowRevision -ne 1 -or
    $binding.expectedOperationId -ne $assessmentOperationId) {
    throw "The Decision API response is not bound to the expected workflow assessment."
}
if ($decision.outcomeState -eq "Present" -and
    $decision.outcome -notin @("Eligible", "Referral", "MandatoryRejection")) {
    throw "The Decision API returned an unsupported present outcome '$($decision.outcome)'."
}

$workflowOutcome = if ($null -eq $decision.outcome) { "Unresolved" } else { $decision.outcome }
$decisionResponseJson = ConvertTo-Json -InputObject $decision -Depth 100 -Compress
$policyVersionIdentities = @($decision.selectedPolicySourceVersions | ForEach-Object { [string]$_.versionId })
$decisionEvidence = @{
    decisionSystemIdentity = $decision.decisionSystemIdentity
    externalEvaluationId = [string]$decision.evaluationId
    outcomeState = $decision.outcomeState
    outcome = $decision.outcome
    evaluatedAt = $decision.evaluatedAt
    businessEffectiveAt = $businessEffectiveAt
    knowledgeCutoff = $knownAt
    inputFactSetDigest = $decision.inputFactSetDigest
    policyVersionIdentities = $policyVersionIdentities
    provenance = "HTTP response from the independently hosted Orion Layered Decision API."
    originalResponseJson = $decisionResponseJson
    missingFacts = @($decision.missingFacts)
    unknownFacts = @($decision.unknownFacts)
    unavailableFacts = @($decision.unavailableFacts)
}
$decisionObservation = Invoke-JsonApi -Method Post `
    -Uri "$stateMachineBase/api/instances/$InstanceId/observations" `
    -Headers $trustedHeaders `
    -Body @{
        messageId = "decision-$($decision.evaluationId)"
        observationId = [string]$decision.evaluationId
        operationId = $assessmentOperationId
        type = "EligibilityAssessmentResult"
        expectedRevision = 1
        correlationId = $assessmentOperationId
        source = "Orion Layered Decision API"
        facts = @{
            checksComplete = ($null -ne $decision.outcome)
            eligibilityDecision = $workflowOutcome
        }
        evidence = @{
            decisionEvaluationId = [string]$decision.evaluationId
            inputFactSetDigest = $decision.inputFactSetDigest
            policyVersionIdentities = $policyVersionIdentities
            outcomeState = $decision.outcomeState
        }
        decisionEvidence = $decisionEvidence
    }
if ($decisionObservation.message.status -ne "Accepted") {
    throw "The State Machine refused the correlated decision result: $($decisionObservation.message.explanation)"
}

$effectResults = @()
if ($decision.outcome -eq "Eligible") {
    $reservationRequest = Invoke-JsonApi -Method Post `
        -Uri "$stateMachineBase/api/instances/$InstanceId/commands" `
        -Body @{
            messageId = "reservation-request-$ApplicationId"
            type = "RequestAssetReservation"
            expectedRevision = 2
        }
    if ($reservationRequest.attempt.status -ne "Accepted") {
        throw "The State Machine refused the equipment-reservation command."
    }

    $reservationOperationId = "reservation-$([guid]::NewGuid().ToString('N'))"
    $reservation = Invoke-StateMachineEffect @{
        operationId = $reservationOperationId
        capability = "equipment-reservation"
        assetId = $AssetId
    }
    if ($reservation.status -ne "ObservationAccepted") {
        throw "Equipment reservation did not produce an accepted workflow observation; status: $($reservation.status)."
    }
    $reservationInspection = Invoke-JsonApi -Method Get `
        -Uri "$stateMachineBase/api/instances/$InstanceId/effects/$reservationOperationId/inspection"
    $effectResults += [pscustomobject]@{
        operationId = $reservationOperationId
        capability = "equipment-reservation"
        operation = $reservation
        remoteInspection = $reservationInspection
    }

    $offerOperationId = "offer-$([guid]::NewGuid().ToString('N'))"
    $offer = Invoke-StateMachineEffect @{
        operationId = $offerOperationId
        capability = "offer-document"
        customerReference = $ApplicationId
        offerReference = "offer-$ApplicationId"
    }
    if ($offer.status -ne "ObservationAccepted") {
        throw "Offer-document generation did not produce an accepted workflow observation; status: $($offer.status)."
    }
    $offerInspection = Invoke-JsonApi -Method Get `
        -Uri "$stateMachineBase/api/instances/$InstanceId/effects/$offerOperationId/inspection"
    $effectResults += [pscustomobject]@{
        operationId = $offerOperationId
        capability = "offer-document"
        operation = $offer
        remoteInspection = $offerInspection
    }

    $release = Invoke-JsonApi -Method Post `
        -Uri "$stateMachineBase/api/instances/$InstanceId/commands" `
        -Body @{
            messageId = "release-offer-$ApplicationId"
            type = "ReleaseOffer"
            expectedRevision = 3
        }
    if ($release.attempt.status -ne "Accepted") {
        throw "The State Machine refused to release the coordinated offer."
    }

    $notificationOperationId = "notification-$([guid]::NewGuid().ToString('N'))"
    $notification = Invoke-StateMachineEffect @{
        operationId = $notificationOperationId
        capability = "customer-notification"
        customerReference = $ApplicationId
        messageTemplate = "Synthetic offer $ApplicationId is ready for customer review."
    }
    if ($notification.status -ne "ObservationAccepted") {
        throw "Customer notification did not produce an accepted workflow observation; status: $($notification.status)."
    }
    $notificationInspection = Invoke-JsonApi -Method Get `
        -Uri "$stateMachineBase/api/instances/$InstanceId/effects/$notificationOperationId/inspection"
    $effectResults += [pscustomobject]@{
        operationId = $notificationOperationId
        capability = "customer-notification"
        operation = $notification
        remoteInspection = $notificationInspection
    }
}

$currentInstance = Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId"
$history = Get-ResponseItems (Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId/history")
$attempts = Get-ResponseItems (Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId/attempts")
$inboundMessages = Get-ResponseItems (Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId/inbound-messages")
$expectedOperations = Get-ResponseItems (Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId/expected-operations")
$concerns = Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId/concerns"
$stateMachineReplay = Invoke-JsonApi -Method Get -Uri "$stateMachineBase/api/instances/$InstanceId/replay"
$decisionReplay = Invoke-JsonApi -Method Post `
    -Uri "$decisionApiBase/orion-equipment-leases/historical-decisions/$($decision.evaluationId)/replay"

$expectedFinalState = switch ($workflowOutcome) {
    "Eligible" { "AwaitingOfferAcceptance" }
    "Referral" { "UnderAssessment" }
    "MandatoryRejection" { "Rejected" }
    default { "UnderAssessment" }
}
if ($currentInstance.currentState -ne $expectedFinalState) {
    throw "The final workflow state '$($currentInstance.currentState)' does not match the bound '$workflowOutcome' outcome."
}
if ($stateMachineReplay.outcome -ne "Consistent") {
    throw "State Machine replay did not establish a consistent history: $($stateMachineReplay.outcome)."
}
if ($decisionReplay.evaluationId -ne $decision.evaluationId -or
    -not $decisionReplay.matchesStoredOutcome) {
    throw "Decision API historical replay did not reproduce the original evaluation."
}

$evidenceChain = [ordered]@{
    topology = [ordered]@{
        stateMachine = $stateMachineBase
        decisionApi = $decisionApiBase
        genericHarness = $harnessBase
        providerCallsRouteThroughStateMachineHarnessAdapter = $true
    }
    originalClientCommand = $submitCommand
    application = [ordered]@{
        applicationId = $ApplicationId
        instance = $currentInstance
        definitionRegistration = $definitionRegistration.definition
        documentVerification = $documentObservation
    }
    decision = [ordered]@{
        request = $decisionRequest
        response = $decision
        acceptedWorkflowObservation = $decisionObservation
        replay = $decisionReplay
    }
    workflow = [ordered]@{
        expectedOperations = $expectedOperations
        inboundMessages = $inboundMessages
        transitionAttempts = @($attempts)
        acceptedTransitions = @($history)
        concerns = $concerns
        replay = $stateMachineReplay
    }
    harnessEffects = $effectResults
    finalState = $currentInstance.currentState
}

$json = ConvertTo-Json -InputObject $evidenceChain -Depth 100
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $destination = [System.IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [System.IO.File]::WriteAllText($destination, $json)
    Write-Output "Gate 13 evidence chain written to $destination"
}
else {
    Write-Output $json
}
