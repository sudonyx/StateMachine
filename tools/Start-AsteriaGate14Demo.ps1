param(
    [string]$DecisionApiBaseUrl = 'http://localhost:5267/',
    [string]$DemoRoot = (Join-Path $env:TEMP ("asteria-g14-" + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))),
    [ValidateSet('Standard', 'Emergency')]
    [string]$ReleaseCategory = 'Standard',
    [switch]$SkipFullLifecycle
)

$ErrorActionPreference = 'Stop'
$stateMachinePort = 5001
$harnessPort = 5248
$approvalPort = 5273
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$harnessRoot = (Resolve-Path (Join-Path $repositoryRoot '..\GenericHarness')).Path
$stateMachineUrl = "http://127.0.0.1:$stateMachinePort/"
$harnessUrl = "http://127.0.0.1:$harnessPort"
$providerProject = Join-Path $repositoryRoot 'clients\DurableStateMachine.AsteriaSyntheticProvider\DurableStateMachine.AsteriaSyntheticProvider.csproj'
$clientDll = Join-Path $repositoryRoot 'clients\DurableStateMachine.AsteriaClient\bin\Debug\net10.0\DurableStateMachine.AsteriaClient.dll'
$definitionPath = Join-Path $repositoryRoot 'definitions\asteria-release-governance-v5.json'
$contractsPath = Join-Path $repositoryRoot 'training-data\asteria\contracts'
$decisionUri = [Uri]$DecisionApiBaseUrl
$decisionBase = $decisionUri.AbsoluteUri.TrimEnd('/') + '/'
$startedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Assert-PortFree([int]$Port) {
    $listeners = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    if ($listeners) {
        throw "Required isolated demo port $Port is already in use; no existing process will be stopped."
    }
}

function Start-DemoProcess(
    [string]$Name,
    [string]$WorkingDirectory,
    [string]$Project,
    [int]$Port,
    [hashtable]$Environment
) {
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    foreach ($entry in $Environment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
    }
    $stdout = Join-Path $DemoRoot "$Name.stdout.log"
    $stderr = Join-Path $DemoRoot "$Name.stderr.log"
    $arguments = @('run', '--no-build', '--no-launch-profile', '--project', $Project)
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments `
        -WorkingDirectory $WorkingDirectory -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $startedProcesses.Add($process)
    Write-Output "Started $Name on port $Port (PID $($process.Id)); logs: $DemoRoot"
}

function Wait-ForHttp([string]$Name, [string]$Url, [int]$TimeoutSeconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = $null
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 2 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                return $response
            }
            $lastError = "HTTP status $($response.StatusCode)"
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$Name failed its HTTP health/OpenAPI check at $Url. Last result: $lastError"
}

function Get-OpenApiDocument([string]$BaseUrl) {
    return Invoke-RestMethod -Uri ($BaseUrl.TrimEnd('/') + '/openapi/v1.json') -TimeoutSec 5
}

function New-RandomBase64([int]$ByteCount) {
    $bytes = New-Object byte[] $ByteCount
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $generator.GetBytes($bytes)
    }
    finally {
        $generator.Dispose()
    }
    return [Convert]::ToBase64String($bytes)
}

function Register-HarnessDescription(
    [string]$Name,
    [string]$ContractPath,
    [string]$ProviderBaseUrl,
    [string[]]$ExpectedOperationIds
) {
    $providerId = [Guid]::NewGuid().ToString()
    $providerOpenApiResponse = Invoke-WebRequest `
        -Uri ($ProviderBaseUrl.TrimEnd('/') + '/openapi/v1.json') `
        -Method Get -TimeoutSec 5 -UseBasicParsing
    $providerDescription = $providerOpenApiResponse.Content | ConvertFrom-Json
    if (-not $providerDescription.servers -or
        $providerDescription.servers[0].url.TrimEnd('/') -ne $ProviderBaseUrl.TrimEnd('/')) {
        throw "$Name provider OpenAPI server URL does not match its verified service URL."
    }
    foreach ($operationId in $ExpectedOperationIds) {
        $found = $false
        foreach ($path in $providerDescription.paths.PSObject.Properties) {
            foreach ($method in $path.Value.PSObject.Properties) {
                if ($method.Value.operationId -eq $operationId) {
                    $found = $true
                    break
                }
            }
            if ($found) { break }
        }
        if (-not $found) {
            throw "$Name served OpenAPI document lacks runtime operation ID '$operationId'."
        }
    }

    $contract = if ([string]::IsNullOrWhiteSpace($ContractPath)) {
        $providerOpenApiResponse.Content
    } else {
        Get-Content -LiteralPath $ContractPath -Raw
    }
    try {
        $registered = Invoke-RestMethod `
            -Uri "$harnessUrl/providers/$providerId/api-descriptions" `
            -Method Post -ContentType 'application/json' -Body $contract -TimeoutSec 20
    } catch {
        $responseBody = $null
        $response = $_.Exception.Response
        if ($response -and $response.GetResponseStream()) {
            $reader = [IO.StreamReader]::new($response.GetResponseStream())
            try { $responseBody = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        throw "$Name Harness registration failed: $($_.Exception.Message) $responseBody"
    }
    $apiDescriptionId = [string]$registered.apiDescription.id
    $parsedDescriptionId = [Guid]::Empty
    if (-not [Guid]::TryParse($apiDescriptionId, [ref]$parsedDescriptionId)) {
        throw "$Name registration did not return an actual API-description version ID."
    }
    $operationsResponse = Invoke-RestMethod `
        -Uri "$harnessUrl/api-descriptions/$apiDescriptionId/operations" `
        -Method Get -TimeoutSec 10
    $operations = if ($operationsResponse.PSObject.Properties['value']) {
        @($operationsResponse.value)
    } else {
        @($operationsResponse)
    }
    $result = @{}
    foreach ($expected in $ExpectedOperationIds) {
        $operation = $operations | Where-Object { $_.operationId -eq $expected } | Select-Object -First 1
        if ($null -eq $operation) {
            throw "$Name Harness registration lacks expected operation ID '$expected'."
        }
        $supportStatus = @($operation.supportStatus)
        if ($supportStatus.Count -ne 1 -or
            [string]$supportStatus[0] -notin @('0', 'Supported')) {
            $operationJson = ConvertTo-Json -InputObject $operation -Compress -Depth 8
            throw "$Name operation '$expected' is not supported by this Generic Harness build. Registration returned: $operationJson"
        }
        $result[$expected] = [pscustomobject]@{
            ProviderIdentity = $Name
            ProviderId = $providerId
            ApiDescriptionVersionId = $apiDescriptionId
            OperationKey = [string]$operation.key
        }
    }
    Write-Host "Registered $Name using returned provider/version IDs and operation keys."
    return $result
}

function Invoke-AsteriaCli([string[]]$Arguments) {
    $output = & dotnet $clientDll @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Asteria CLI '$($Arguments[0])' failed with exit code $LASTEXITCODE. Output: $($output -join [Environment]::NewLine)"
    }
    return ($output -join [Environment]::NewLine)
}

function Start-SyntheticApprovalGateway(
    [string]$RegistryPath,
    [string]$TrustedKey
) {
    $reviewToken = New-RandomBase64 48
    $emergencyToken = New-RandomBase64 48
    $credentials = @(
        @{ principalId = 'synthetic-release-reviewer'; role = 'ReleaseReviewer'; bearerToken = $reviewToken },
        @{ principalId = 'synthetic-emergency-authoriser'; role = 'EmergencyReleaseAuthoriser'; bearerToken = $emergencyToken }
    ) | ConvertTo-Json -Compress
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$approvalPort"
    $env:DURABLE_STATE_MACHINE_API_URL = $stateMachineUrl
    $env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = $TrustedKey
    $env:ASTERIA_RELEASE_REGISTRY_PATH = $RegistryPath
    $env:ASTERIA_APPROVAL_AUTHORITY_REGISTRY_VERSION = 'asteria-g14-local-synthetic-v1'
    $env:ASTERIA_APPROVAL_PRINCIPALS_JSON = $credentials
    $process = Start-Process -FilePath 'dotnet' `
        -ArgumentList @($clientDll, 'approval-gateway') `
        -WorkingDirectory $repositoryRoot -PassThru `
        -RedirectStandardOutput (Join-Path $DemoRoot 'approval.stdout.log') `
        -RedirectStandardError (Join-Path $DemoRoot 'approval.stderr.log')
    $startedProcesses.Add($process)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        $listener = Get-NetTCPConnection -State Listen -LocalPort $approvalPort -ErrorAction SilentlyContinue
        if ($listener) {
            return @{ Reviewer = $reviewToken; Emergency = $emergencyToken }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Synthetic approval gateway did not start on the isolated demo port.'
}

try {
    New-Item -ItemType Directory -Path $DemoRoot -Force | Out-Null
    foreach ($port in @($stateMachinePort, $harnessPort, 5271, 5272, 5274, 5275, $approvalPort)) {
        Assert-PortFree $port
    }
    $policyVersions = @(Invoke-RestMethod -Uri ($decisionBase + 'asteria/releases/policy-versions') -TimeoutSec 10)
    if ($policyVersions.Count -eq 0) {
        throw "Decision API at $decisionBase exposes no registered AST-POL-001 policy version."
    }

    Set-Location $repositoryRoot
    & dotnet build .\DurableStateMachine.slnx --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) {
        throw 'Full solution build failed; refusing to start demo services.'
    }

    $trustedKey = New-RandomBase64 32
    $stateDatabase = Join-Path $DemoRoot 'state-machine.db'
    $registryDatabase = Join-Path $DemoRoot 'asteria-registry.db'
    $harnessDatabase = Join-Path $DemoRoot 'harness.db'
    $providerDatabase = Join-Path $DemoRoot 'synthetic-deployments.db'
    Start-DemoProcess 'state-machine' $repositoryRoot '.\src\DurableStateMachine.Api' $stateMachinePort @{
        DURABLE_STATE_MACHINE_DB = $stateDatabase
        DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = $trustedKey
    }
    Start-DemoProcess 'generic-harness' $harnessRoot '.\Harness.Api' $harnessPort @{
        ConnectionStrings__Harness = "Data Source=$harnessDatabase"
        ASPNETCORE_ENVIRONMENT = 'Development'
    }

    $providerDefinitions = @(
        @{ Name = 'Asteria synthetic functional verification'; Mode = 'functional'; Port = 5274 },
        @{ Name = 'Asteria synthetic security verification'; Mode = 'security'; Port = 5275 },
        @{ Name = 'Asteria synthetic deployment'; Mode = 'deployment'; Port = 5271 },
        @{ Name = 'Asteria synthetic health'; Mode = 'health'; Port = 5272 }
    )
    foreach ($provider in $providerDefinitions) {
        Start-DemoProcess $provider.Mode $repositoryRoot '.\clients\DurableStateMachine.AsteriaSyntheticProvider' $provider.Port @{
            ASTERIA_SYNTHETIC_PROVIDER_MODE = $provider.Mode
            ASTERIA_SYNTHETIC_DEPLOYMENT_DB = $providerDatabase
            ASTERIA_SYNTHETIC_DEPLOYMENT_DELAY_MS = '25'
        }
    }

    Wait-ForHttp 'State Machine' ($stateMachineUrl + 'health') | Out-Null
    Wait-ForHttp 'Generic Harness' ($harnessUrl + '/openapi/v1.json') | Out-Null
    foreach ($provider in $providerDefinitions) {
        $base = "http://127.0.0.1:$($provider.Port)"
        Wait-ForHttp "$($provider.Mode) provider health" ($base + '/health') | Out-Null
        $openApi = Get-OpenApiDocument $base
        if (-not $openApi.paths) {
            throw "$($provider.Mode) provider returned an empty OpenAPI document."
        }
    }

    $stateMachineDocument = Get-Content -LiteralPath $definitionPath -Raw
    $definitionRegistration = Invoke-RestMethod `
        -Uri ($stateMachineUrl + 'api/definitions/') `
        -Method Post -ContentType 'application/json' -Body $stateMachineDocument -TimeoutSec 15
    $registeredDefinitions = @(Invoke-RestMethod -Uri ($stateMachineUrl + 'api/definitions/') -TimeoutSec 10)
    if (-not ($registeredDefinitions | Where-Object {
        $_.id -eq 'asteria-release-governance' -and $_.version -eq '5.0'
    })) {
        throw 'State Machine registration did not make immutable Asteria V5 discoverable.'
    }

    $functional = Register-HarnessDescription 'Asteria functional synthetic provider' `
        (Join-Path $contractsPath 'asteria-functional-verification.openapi.json') `
        'http://127.0.0.1:5274' @('verifyFunctionalRelease')
    $security = Register-HarnessDescription 'Asteria security synthetic provider' `
        (Join-Path $contractsPath 'asteria-security-verification.openapi.json') `
        'http://127.0.0.1:5275' @('verifySecurityRelease')
    $deployment = Register-HarnessDescription 'Asteria synthetic deployment provider' `
        (Join-Path $contractsPath 'asteria-deployment-platform.openapi.json') `
        'http://127.0.0.1:5271' @('requestDeployment', 'getDeploymentStatus', 'lookupDeploymentByIdempotencyKey')
    $health = Register-HarnessDescription 'Asteria synthetic health provider' `
        (Join-Path $contractsPath 'asteria-health-verification.openapi.json') `
        'http://127.0.0.1:5272' @('verifyDeployedApplicationHealth')

    $env:DURABLE_STATE_MACHINE_API_URL = $stateMachineUrl
    $env:DURABLE_STATE_MACHINE_TRUSTED_INGESTION_KEY = $trustedKey
    $env:DURABLE_STATE_MACHINE_DB = $stateDatabase
    $env:ASTERIA_RELEASE_REGISTRY_PATH = $registryDatabase
    $env:ASTERIA_DECISION_API_URL = $decisionBase
    $env:GENERIC_HARNESS_API_URL = $harnessUrl
    $env:ASTERIA_FUNCTIONAL_VERIFICATION_PROVIDER_IDENTITY = $functional.verifyFunctionalRelease.ProviderIdentity
    $env:ASTERIA_FUNCTIONAL_VERIFICATION_PROVIDER_ID = $functional.verifyFunctionalRelease.ProviderId
    $env:ASTERIA_FUNCTIONAL_VERIFICATION_API_DESCRIPTION_VERSION_ID = $functional.verifyFunctionalRelease.ApiDescriptionVersionId
    $env:ASTERIA_FUNCTIONAL_VERIFICATION_OPERATION_KEY = $functional.verifyFunctionalRelease.OperationKey
    $env:ASTERIA_SECURITY_VERIFICATION_PROVIDER_IDENTITY = $security.verifySecurityRelease.ProviderIdentity
    $env:ASTERIA_SECURITY_VERIFICATION_PROVIDER_ID = $security.verifySecurityRelease.ProviderId
    $env:ASTERIA_SECURITY_VERIFICATION_API_DESCRIPTION_VERSION_ID = $security.verifySecurityRelease.ApiDescriptionVersionId
    $env:ASTERIA_SECURITY_VERIFICATION_OPERATION_KEY = $security.verifySecurityRelease.OperationKey
    foreach ($binding in @(
        @{ Prefix = 'ASTERIA_DEPLOYMENT'; Binding = $deployment.requestDeployment },
        @{ Prefix = 'ASTERIA_DEPLOYMENT_STATUS'; Binding = $deployment.getDeploymentStatus },
        @{ Prefix = 'ASTERIA_DEPLOYMENT_IDEMPOTENCY_LOOKUP'; Binding = $deployment.lookupDeploymentByIdempotencyKey },
        @{ Prefix = 'ASTERIA_HEALTH'; Binding = $health.verifyDeployedApplicationHealth }
    )) {
        $envName = $binding.Prefix
        [Environment]::SetEnvironmentVariable("${envName}_PROVIDER_IDENTITY", $binding.Binding.ProviderIdentity, 'Process')
        [Environment]::SetEnvironmentVariable("${envName}_PROVIDER_ID", $binding.Binding.ProviderId, 'Process')
        [Environment]::SetEnvironmentVariable("${envName}_API_DESCRIPTION_VERSION_ID", $binding.Binding.ApiDescriptionVersionId, 'Process')
        [Environment]::SetEnvironmentVariable("${envName}_OPERATION_KEY", $binding.Binding.OperationKey, 'Process')
    }

    $releaseId = 'AST-G14-LIVE-' + (Get-Date -Format 'yyyyMMddHHmmss')
    $materialPath = Join-Path $DemoRoot 'release-material.json'
    $isEmergency = $ReleaseCategory -eq 'Emergency'
    $material = @{
        product = 'AsteriaPortal'
        targetVersion = '4.2.0'
        environment = 'staging'
        releaseCategory = $ReleaseCategory
        changeSize = if ($isEmergency) { 'Large' } else { 'Medium' }
        authenticationChange = $isEmergency
        customerDataImpact = $true
        releaseSummary = if ($isEmergency) {
            'Gate 14 local synthetic emergency high-risk demonstration'
        } else {
            'Gate 14 local synthetic cross-service demonstration'
        }
        releaseOwner = 'gate14-demo@example.invalid'
        plannedDeploymentAt = '2027-03-15T10:00:00Z'
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath $materialPath -Value $material -Encoding utf8

    Write-Output "Starting isolated Gate 14 demo for $releaseId."
    Write-Output "The trusted-ingestion key is generated in-process and will not be printed or persisted."
    Write-Output "Decision API: $decisionBase; isolated State Machine: $stateMachineUrl; isolated Harness: $harnessUrl"
    if ($SkipFullLifecycle) {
        Write-Output 'Services are running and registration/bootstrap completed. Full lifecycle was skipped by request.'
    }
    else {
        $assessmentOutput = Invoke-AsteriaCli @('assess', $releaseId, $materialPath)
        Write-Output $assessmentOutput
        $evidencePath = Join-Path $DemoRoot 'assessment-evidence.json'
        $null = Invoke-AsteriaCli @('export-evidence', $releaseId, $evidencePath)
        $evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        $generation = $evidence.generations | Where-Object { $_.generation -eq $evidence.currentGeneration } | Select-Object -First 1
        $obligations = $generation.independentObligations
        if ($null -eq $obligations) {
            throw 'The assessment did not produce persisted applicability evidence; no verification or approval will be fabricated.'
        }
        if ($isEmergency) {
            foreach ($obligation in @(
                'functionalVerification',
                'securityVerification',
                'releaseReviewApproval',
                'emergencyApproval'
            )) {
                if ($obligations.$obligation.status -ne 'Required') {
                    throw "Emergency high-risk assessment did not require '$obligation'; no approval or verification will be fabricated."
                }
            }
        }
        foreach ($concern in @(
            @{ Id = 'FunctionalVerification'; Applicability = $obligations.functionalVerification.status },
            @{ Id = 'SecurityVerification'; Applicability = $obligations.securityVerification.status }
        )) {
            if ($concern.Applicability -eq 'Required') {
                Write-Output (Invoke-AsteriaCli @('verify', $releaseId, $concern.Id))
            }
            elseif ($concern.Applicability -eq 'Unresolved') {
                throw "$($concern.Id) applicability is unresolved; readiness cannot be inferred."
            }
        }

        $approvalTokens = Start-SyntheticApprovalGateway $registryDatabase $trustedKey
        foreach ($approval in @(
            @{ Concern = 'ReleaseReviewApproval'; Applicability = $obligations.releaseReviewApproval.status; Token = $approvalTokens.Reviewer },
            @{ Concern = 'EmergencyApproval'; Applicability = $obligations.emergencyApproval.status; Token = $approvalTokens.Emergency }
        )) {
            if ($approval.Applicability -eq 'Unresolved') {
                throw "$($approval.Concern) applicability is unresolved; readiness cannot be inferred."
            }
            if ($approval.Applicability -ne 'Required') {
                continue
            }
            $submission = @{
                releaseId = $releaseId
                assessmentGeneration = [int]$generation.generation
                materialDigest = [string]$generation.materialDigest
                concernId = $approval.Concern
                submissionId = [Guid]::NewGuid().ToString()
                idempotencyKey = [Guid]::NewGuid().ToString()
                decision = 'Approved'
                evidenceReference = 'synthetic-local-gate14-demo'
            }
            $null = Invoke-RestMethod -Uri "http://127.0.0.1:$approvalPort/approvals" `
                -Method Post -Headers @{ Authorization = "Bearer $($approval.Token)" } `
                -ContentType 'application/json' -Body ($submission | ConvertTo-Json -Compress)
        }

        Write-Output (Invoke-AsteriaCli @('readiness', $releaseId))
        Write-Output (Invoke-AsteriaCli @('deploy', $releaseId))
        Write-Output (Invoke-AsteriaCli @('health', $releaseId))
        Write-Output (Invoke-AsteriaCli @('process-deadlines', $releaseId))
        $finalEvidencePath = Join-Path $DemoRoot 'gate14-evidence.json'
        Write-Output (Invoke-AsteriaCli @('export-evidence', $releaseId, $finalEvidencePath))
        Write-Output "Synthetic cross-service evidence: $finalEvidencePath"
        if ($isEmergency) {
            $finalEvidence = Get-Content -LiteralPath $finalEvidencePath -Raw | ConvertFrom-Json
            $finalGeneration = $finalEvidence.generations |
                Where-Object { $_.generation -eq $finalEvidence.currentGeneration } |
                Select-Object -First 1
            if ($finalGeneration.currentStateMachineProjection.currentState -ne 'Completed') {
                throw 'Emergency lifecycle did not reach persisted State Machine Completed.'
            }
            foreach ($approval in @(
                @{
                    Concern = 'ReleaseReviewApproval'
                    Principal = 'synthetic-release-reviewer'
                    Role = 'ReleaseReviewer'
                },
                @{
                    Concern = 'EmergencyApproval'
                    Principal = 'synthetic-emergency-authoriser'
                    Role = 'EmergencyReleaseAuthoriser'
                }
            )) {
                $activity = $finalGeneration.activities |
                    ForEach-Object { $_.activity } |
                    Where-Object { $_.concernId -eq $approval.Concern } |
                    Select-Object -Last 1
                $projection = $finalGeneration.concernProjection.PSObject.Properties[$approval.Concern].Value
                if ($null -eq $activity -or
                    $activity.status -ne 'Accepted' -or
                    $activity.validatedOutcome -ne 'Approved' -or
                    $activity.authorityPrincipalId -ne $approval.Principal -or
                    $activity.authorityRole -ne $approval.Role -or
                    $projection.currentState -ne 'Approved') {
                    throw "Emergency approval '$($approval.Concern)' lacks its separately authenticated, accepted synthetic principal evidence."
                }
            }
            Write-Output 'Verified all four Emergency obligations were Required and both separately authenticated synthetic approvals were accepted.'
        }
        Write-Output 'This is a live local synthetic demonstration, not production verification or a real deployment.'
    }

    Write-Output "Demo processes remain running. Stop only the listed PIDs after use: $($startedProcesses.Id -join ', ')"
}
catch {
    foreach ($process in $startedProcesses) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -ErrorAction Stop
            }
        } catch {
            Write-Warning "Could not stop demo process PID $($process.Id): $($_.Exception.Message)"
        }
    }
    Write-Error $_
    throw
}
