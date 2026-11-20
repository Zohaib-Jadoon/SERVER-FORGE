<#
.SYNOPSIS
    TEST B — Genuine Autonomous GitHub Actions Deployment, Update, Rollback & Recovery on Real IIS.
.DESCRIPTION
    Independently proves ALL 18 requirements of genuine autonomous GitHub Actions deployment:
    1. Dedicated disposable sample GitHub repository with root .github/workflows/serverforge-deploy.yml.
    2. Test-only GitHub PAT passed via E2E_DISPOSABLE_GITHUB_PAT environment variable (NEVER logged or stored in git).
    3. Explicitly authorizes repo and syncs PAT to disposable ServerForge Agent (DPAPI-encrypted).
    4. Registers disposable IIS app in GitHubActionsArtifact mode (SourceMode=0, AutoDeployEnabled=true).
    5. Ensures ServerForge.Desktop is NOT running and makes ZERO calls to POST /api/v1/deployments/trigger.
    6. Waits for real GitHubAutoDeployWorker to autonomously detect, download, verify, and deploy healthy v1.
    7. Pushes real test-only healthy commit v2 via GitHub Contents API -> waits for real GitHub Actions run ->
       confirms Agent autonomously deploys v2 to live IIS with Desktop closed -> records run ID, artifact ID, SHA, deploy ID.
    8. Pushes real deliberately unhealthy commit v3 (simulateUnhealthy=true in release-config.json) ->
       waits for real GitHub Actions run -> confirms Agent detects HTTP 503 and automatically rolls back IIS to v2.
    9. Restarts disposable Agent Windows Service, waits >2 poll intervals, confirms failed v3 commit does NOT retry endlessly.
    10. Pushes real healthy recovery commit v4 -> confirms Agent autonomously deploys v4 to live IIS.
    Writes sanitized github-autonomous-evidence.json (ZERO secrets).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [Parameter(Mandatory = $true)]
    [string]$GitHubRepo,
    [string]$GitHubBranch = 'main',
    [int]$WorkflowTimeoutSeconds = 420,
    [int]$AgentPollTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

Assert-ValidGitHubRepo -Repo $GitHubRepo
if ([string]::IsNullOrWhiteSpace($GitHubBranch) -or $GitHubBranch.Contains('..') -or $GitHubBranch -notmatch '^[A-Za-z0-9._/-]{1,80}$') {
    throw "SAFETY ABORT: Invalid GitHub branch '$GitHubBranch'."
}

$pat = $env:E2E_DISPOSABLE_GITHUB_PAT
if ([string]::IsNullOrWhiteSpace($pat)) {
    throw 'SAFETY ABORT: Environment variable E2E_DISPOSABLE_GITHUB_PAT (scoped ONLY to the disposable sample repository) must be set for TEST B.'
}

$ManifestPath = Resolve-E2EAbsolutePath $ManifestPath
if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

$depEvidencePath = Join-Path $manifest.diagnosticsDir 'deployment-evidence.json'
if (-not (Test-Path $depEvidencePath)) {
    throw "Expected pairing state at '$depEvidencePath'. Run run-deployment-tests.ps1 (TEST A) first so the Agent is paired."
}
$depState = Get-Content $depEvidencePath -Raw | ConvertFrom-Json
$preflight = Get-Content $manifest.preflightInventory -Raw | ConvertFrom-Json
$logFile = Join-Path $manifest.diagnosticsDir 'test-b-github-autonomous.log'

$checks = New-Object System.Collections.Generic.List[object]
function Assert-TestB {
    param([bool]$Condition, [string]$Name, [string]$Detail = '')
    $safeDetail = Protect-SanitizedText -Text $Detail -ExtraSecrets @($pat)
    $checks.Add([ordered]@{ name = $Name; passed = $Condition; detail = $safeDetail; timestampUtc = (Get-Date).ToUniversalTime().ToString('O') })
    if ($Condition) {
        Write-E2ELog "PASS: $Name ($safeDetail)" 'PASS' $logFile -ExtraSecrets @($pat)
    } else {
        Write-E2ELog "FAIL: $Name ($safeDetail)" 'FAIL' $logFile -ExtraSecrets @($pat)
        throw "TEST B failed: $Name ($safeDetail)"
    }
}

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$Method = 'GET',
        [object]$Body = $null
    )
    $headers = @{
        Authorization          = "Bearer $pat"
        Accept                 = 'application/vnd.github+json'
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent'           = 'ServerForge-E2E-TestB/1.0'
    }
    $uri = "https://api.github.com/$($Path.TrimStart('/'))"
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 8 -Compress
        return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -Body $json -ContentType 'application/json' -TimeoutSec 30
    }
    return Invoke-RestMethod -Uri $uri -Method $Method -Headers $headers -TimeoutSec 30
}

function Push-DisposableConfigCommit {
    param(
        [Parameter(Mandatory = $true)][string]$VersionLabel,
        [Parameter(Mandatory = $true)][bool]$SimulateUnhealthy,
        [Parameter(Mandatory = $true)][string]$CommitMessage
    )
    $existingSha = $null
    try {
        $existing = Invoke-GitHubApi -Path "repos/$GitHubRepo/contents/release-config.json?ref=$GitHubBranch" -Method 'GET'
        $existingSha = [string]$existing.sha
    } catch {
        $existingSha = $null
    }

    $cfgJson = ([ordered]@{
        version           = $VersionLabel
        simulateUnhealthy = $SimulateUnhealthy
        runId             = $manifest.runId
        updatedAtUtc      = (Get-Date).ToUniversalTime().ToString('O')
    } | ConvertTo-Json -Depth 4)
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($cfgJson))

    $payload = [ordered]@{
        message = $CommitMessage
        content = $b64
        branch  = $GitHubBranch
    }
    if (-not [string]::IsNullOrWhiteSpace($existingSha)) {
        $payload['sha'] = $existingSha
    }

    $res = Invoke-GitHubApi -Path "repos/$GitHubRepo/contents/release-config.json" -Method 'PUT' -Body $payload
    return [string]$res.commit.sha
}

function Wait-ForGitHubWorkflowRunAndArtifact {
    param([Parameter(Mandatory = $true)][string]$CommitSha)
    $deadline = (Get-Date).AddSeconds($WorkflowTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $runsResp = Invoke-GitHubApi -Path "repos/$GitHubRepo/actions/runs?head_sha=$CommitSha&branch=$GitHubBranch&per_page=10" -Method 'GET'
        foreach ($run in @($runsResp.workflow_runs)) {
            if ($null -eq $run) { continue }
            if ([string]$run.path -notlike '*serverforge-deploy.yml') { continue }
            if ([string]$run.status -eq 'completed') {
                if ([string]$run.conclusion -ne 'success') {
                    throw "GitHub Actions workflow run $($run.id) for $CommitSha finished with conclusion '$($run.conclusion)'."
                }
                $artResp = Invoke-GitHubApi -Path "repos/$GitHubRepo/actions/runs/$($run.id)/artifacts" -Method 'GET'
                $art = @($artResp.artifacts) | Where-Object { -not $_.expired -and $_.size_in_bytes -gt 0 } | Select-Object -First 1
                if ($null -ne $art) {
                    return [ordered]@{
                        workflowRunId = [long]$run.id
                        artifactId    = [long]$art.id
                        artifactName  = [string]$art.name
                        digest        = [string]$art.digest
                    }
                }
            }
        }
        Start-Sleep -Seconds 10
    }
    throw "Timed out after ${WorkflowTimeoutSeconds}s waiting for real GitHub Actions artifact for $GitHubRepo@$CommitSha."
}

function Wait-ForAgentAutonomousDeployment {
    param(
        [Parameter(Mandatory = $true)][string]$AppId,
        [Parameter(Mandatory = $true)][string]$ExpectedCommitSha,
        [Parameter(Mandatory = $true)][int]$ExpectedState,
        [Parameter(Mandatory = $true)][string]$ServerThumbprint,
        [Parameter(Mandatory = $true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$ClientCert
    )
    $deadline = (Get-Date).AddSeconds($AgentPollTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $histResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$AppId/deployments" -Method 'GET' `
            -ExpectedServerThumbprint $ServerThumbprint -ClientCertificate $ClientCert
        if ($histResp.IsSuccess -and $null -ne $histResp.Json) {
            foreach ($rec in @($histResp.Json)) {
                if ([string]::Equals([string]$rec.commitSha, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase) -and
                    [bool]$rec.triggeredAutomatically -eq $true -and
                    [int]$rec.state -eq $ExpectedState) {
                    return $rec
                }
            }
        }
        Start-Sleep -Seconds 5
    }
    throw "Timed out after ${AgentPollTimeoutSeconds}s waiting for autonomous Agent deployment of $ExpectedCommitSha to reach state $ExpectedState."
}

Write-E2ELog "=== TEST B: Genuine Autonomous GitHub Actions Deployment & Rollback ($GitHubRepo@$GitHubBranch) ===" 'INFO' $logFile -ExtraSecrets @($pat)

$clientCert = New-E2EDesktopClientCertificate -PfxPath $depState.clientPfxPath -CommonName "ServerForgeE2E-$($manifest.runId)"
$serverThumbprint = [string]$depState.serverCertThumbprint

try {
    # Step 1-2: Verify disposable repo has root .github/workflows/serverforge-deploy.yml
    $wfCheck = Invoke-GitHubApi -Path "repos/$GitHubRepo/contents/.github/workflows/serverforge-deploy.yml?ref=$GitHubBranch" -Method 'GET'
    Assert-TestB ($null -ne $wfCheck -and -not [string]::IsNullOrWhiteSpace([string]$wfCheck.sha)) `
        'TESTB.1_DisposableRepoWorkflowVerifiedAtRoot' "$GitHubRepo/.github/workflows/serverforge-deploy.yml"

    # Step 3-4: Sync test-only PAT to disposable Agent & register app in GitHubActionsArtifact mode
    $syncResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/github/credentials' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ PersonalAccessToken = $pat }
    Assert-TestB $syncResp.IsSuccess 'TESTB.2_TestOnlyGitHubPatSyncedToAgent' "HTTP $($syncResp.StatusCode)"

    # Confirm ServerForge Desktop is NOT running
    $desktopProcs = @(Get-Process -Name 'ServerForge.Desktop' -ErrorAction SilentlyContinue)
    Assert-TestB ($desktopProcs.Count -eq 0) 'TESTB.3_DesktopClosedBeforeAutonomousTest' 'ServerForge.Desktop not running'

    $ghSlug = ('gh-' + ($manifest.runId.ToLowerInvariant() -replace '[^a-z0-9]', '')).Substring(0, [Math]::Min(20, 3 + ($manifest.runId -replace '[^A-Za-z0-9]', '').Length))
    if ($ghSlug.Length -lt 5) { $ghSlug = "${ghSlug}app" }

    # Push fresh v1 healthy commit BEFORE registering app so we have a guaranteed real GitHub Actions artifact for v1
    $v1Ver = "2.0.1-$($manifest.runId)"
    $v1Sha = Push-DisposableConfigCommit -VersionLabel $v1Ver -SimulateUnhealthy $false -CommitMessage "TEST B v1 healthy ($v1Ver)"
    Write-E2ELog "Pushed TEST B v1 healthy commit $v1Sha; waiting for real GitHub Actions workflow..." 'INFO' $logFile -ExtraSecrets @($pat)
    $v1Gh = Wait-ForGitHubWorkflowRunAndArtifact -CommitSha $v1Sha
    Assert-TestB ($v1Gh.workflowRunId -gt 0 -and $v1Gh.artifactId -gt 0) 'TESTB.4_RealGitHubActionsBuiltV1' `
        "RunId=$($v1Gh.workflowRunId) ArtifactId=$($v1Gh.artifactId) Sha=$($v1Sha.Substring(0,8))"

    $ghAppResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/applications' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Id                             = $null
            ProjectGroupId                 = [string]$depState.projectId
            Name                           = "Autonomous GitHub Sample $($manifest.runId)"
            Slug                           = $ghSlug
            RuntimeType                    = 5 # AspNetCore
            RepositoryFullName             = $GitHubRepo
            Branch                         = $GitHubBranch
            WorkingDirectory               = '.'
            AutoDeployEnabled              = $true
            RepositoryExplicitlyAuthorized = $true
            SourceMode                     = 0 # GitHubActionsArtifact (server-side verified ONLY)
            GitHubWorkflowFileName         = 'serverforge-deploy.yml'
            BuildCommand                   = ''
            StartCommand                   = ''
            RuntimeVersion                 = '10.0'
            InternalPort                   = [int]$manifest.ghAppPort
            IisSiteName                    = $manifest.ghIisSiteName
            IisAppPoolName                 = $manifest.ghIisAppPoolName
            WindowsServiceName             = "SF_$ghSlug"
            PrimaryDomain                  = 'localhost'
            HealthCheckPath                = '/health'
            HealthCheckTimeoutSeconds      = 30
            RetainHealthyReleasesCount     = 5
            AutoRollbackOnFailure          = $true
        }
    Assert-TestB ($ghAppResp.IsSuccess -and $null -ne $ghAppResp.Json.id) 'TESTB.5_AutonomousGitHubAppRegistered' `
        "AppId=$($ghAppResp.Json.id) Site=$($manifest.ghIisSiteName) Port=$($manifest.ghAppPort)"
    $ghAppId = [string]$ghAppResp.Json.id

    # Step 5: Wait for Agent GitHubAutoDeployWorker (ZERO /api/v1/deployments/trigger calls!) to deploy v1
    $v1Deploy = Wait-ForAgentAutonomousDeployment -AppId $ghAppId -ExpectedCommitSha $v1Sha -ExpectedState 8 `
        -ServerThumbprint $serverThumbprint -ClientCert $clientCert
    Assert-TestB ($v1Deploy.triggeredAutomatically -eq $true) 'TESTB.6_AgentAutonomouslyDeployedV1WithDesktopClosed' `
        "DeployId=$($v1Deploy.id) Commit=$($v1Sha.Substring(0,8))"

    $liveV1Root = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/" -Method Get -TimeoutSec 15
    $liveV1Data = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-TestB ($liveV1Root.commitSha -eq $v1Sha -and $liveV1Data.markerExists -eq $true) 'TESTB.7_LiveIisServingAutonomousV1' `
        "Version=$($liveV1Root.version) Commit=$($liveV1Root.commitSha)"
    $ghMarker = [string]$liveV1Data.markerContent

    # Steps 6-14: Push v2 healthy commit with Desktop closed -> real GitHub Actions -> autonomous Agent deploy
    $desktopProcs2 = @(Get-Process -Name 'ServerForge.Desktop' -ErrorAction SilentlyContinue)
    Assert-TestB ($desktopProcs2.Count -eq 0) 'TESTB.8_DesktopStillClosedForV2Push' 'Confirmed'

    $v2Ver = "2.0.2-$($manifest.runId)"
    $v2Sha = Push-DisposableConfigCommit -VersionLabel $v2Ver -SimulateUnhealthy $false -CommitMessage "TEST B v2 healthy ($v2Ver)"
    Write-E2ELog "Pushed TEST B v2 healthy commit $v2Sha; waiting for real GitHub Actions workflow..." 'INFO' $logFile -ExtraSecrets @($pat)
    $v2Gh = Wait-ForGitHubWorkflowRunAndArtifact -CommitSha $v2Sha
    Assert-TestB ($v2Gh.workflowRunId -gt 0 -and $v2Gh.artifactId -gt 0) 'TESTB.9_RealGitHubActionsBuiltV2' `
        "RunId=$($v2Gh.workflowRunId) ArtifactId=$($v2Gh.artifactId)"

    $v2Deploy = Wait-ForAgentAutonomousDeployment -AppId $ghAppId -ExpectedCommitSha $v2Sha -ExpectedState 8 `
        -ServerThumbprint $serverThumbprint -ClientCert $clientCert
    $liveV2Root = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/" -Method Get -TimeoutSec 15
    $liveV2Data = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-TestB ($liveV2Root.commitSha -eq $v2Sha -and $liveV2Data.markerContent -eq $ghMarker) 'TESTB.10_AgentAutonomouslyDeployedV2AndPreservedData' `
        "DeployId=$($v2Deploy.id) Version=$($liveV2Root.version)"

    # Steps 15-16: Push deliberately unhealthy v3 commit (simulateUnhealthy=true) -> real GitHub Actions -> Agent detects 503 & auto-rolls back to v2
    $v3Ver = "2.0.3-unhealthy-$($manifest.runId)"
    $v3Sha = Push-DisposableConfigCommit -VersionLabel $v3Ver -SimulateUnhealthy $true -CommitMessage "TEST B v3 deliberately unhealthy ($v3Ver)"
    Write-E2ELog "Pushed TEST B v3 unhealthy commit $v3Sha; waiting for real GitHub Actions workflow..." 'INFO' $logFile -ExtraSecrets @($pat)
    $v3Gh = Wait-ForGitHubWorkflowRunAndArtifact -CommitSha $v3Sha
    Assert-TestB ($v3Gh.workflowRunId -gt 0 -and $v3Gh.artifactId -gt 0) 'TESTB.11_RealGitHubActionsBuiltUnhealthyV3' `
        "RunId=$($v3Gh.workflowRunId) ArtifactId=$($v3Gh.artifactId)"

    $v3Deploy = Wait-ForAgentAutonomousDeployment -AppId $ghAppId -ExpectedCommitSha $v3Sha -ExpectedState 10 `
        -ServerThumbprint $serverThumbprint -ClientCert $clientCert
    $liveAfterRbHealth = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/health" -Method Get -TimeoutSec 15
    $liveAfterRbRoot = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/" -Method Get -TimeoutSec 15
    $liveAfterRbData = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-TestB ($liveAfterRbHealth.status -eq 'healthy' -and $liveAfterRbRoot.commitSha -eq $v2Sha -and $liveAfterRbData.markerContent -eq $ghMarker) `
        'TESTB.12_UnhealthyV3DetectedAndAutoRolledBackToV2' "DeployId=$($v3Deploy.id) RestoredCommit=$($liveAfterRbRoot.commitSha)"

    # Step 17: Restart Agent Windows Service & wait >2 poll intervals -> failed v3 MUST NOT retry endlessly
    $histBeforeRestart = (Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$ghAppId/deployments" -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert).Json
    $v3AttemptsBefore = @($histBeforeRestart | Where-Object { [string]$_.commitSha -eq $v3Sha }).Count

    Restart-Service -Name $manifest.agentServiceName -Force
    $svcUp = Wait-E2ECondition -Condition {
        $s = Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue
        ($null -ne $s -and $s.Status -eq 'Running') -and (Test-TcpPortInUse -Port ([int]$manifest.agentPort))
    } -TimeoutSeconds 30
    Assert-TestB $svcUp 'TESTB.13_AgentRestartedForAntiRetryCheck' $manifest.agentServiceName

    $pollWait = ([int]$manifest.agentPollIntervalSec * 2) + 10
    Start-Sleep -Seconds $pollWait
    $histAfterRestart = (Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$ghAppId/deployments" -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert).Json
    $v3AttemptsAfter = @($histAfterRestart | Where-Object { [string]$_.commitSha -eq $v3Sha }).Count
    Assert-TestB ($v3AttemptsAfter -eq $v3AttemptsBefore) 'TESTB.14_FailedV3CommitDidNotRetryIndefinitely' `
        "AttemptsBefore=$v3AttemptsBefore AttemptsAfter=$v3AttemptsAfter"

    # Step 18: Push healthy recovery commit v4 -> Agent autonomously deploys v4 afterward
    $v4Ver = "2.0.4-recovery-$($manifest.runId)"
    $v4Sha = Push-DisposableConfigCommit -VersionLabel $v4Ver -SimulateUnhealthy $false -CommitMessage "TEST B v4 healthy recovery ($v4Ver)"
    Write-E2ELog "Pushed TEST B v4 healthy recovery commit $v4Sha; waiting for real GitHub Actions workflow..." 'INFO' $logFile -ExtraSecrets @($pat)
    $v4Gh = Wait-ForGitHubWorkflowRunAndArtifact -CommitSha $v4Sha
    $v4Deploy = Wait-ForAgentAutonomousDeployment -AppId $ghAppId -ExpectedCommitSha $v4Sha -ExpectedState 8 `
        -ServerThumbprint $serverThumbprint -ClientCert $clientCert
    $liveV4Root = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/" -Method Get -TimeoutSec 15
    $liveV4Data = Invoke-RestMethod -Uri "$($manifest.ghAppBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-TestB ($liveV4Root.commitSha -eq $v4Sha -and $liveV4Data.markerContent -eq $ghMarker) 'TESTB.15_HealthyRecoveryCommitV4AutonomouslyDeployed' `
        "DeployId=$($v4Deploy.id) Version=$($liveV4Root.version)"

    # Verify pre-existing unmanaged IIS sites remain untouched
    $sitesAfter = @(Get-IisSitesSnapshot)
    foreach ($orig in @($preflight.preExistingIisSites)) {
        $match = $sitesAfter | Where-Object { $_.Name -eq $orig.Name } | Select-Object -First 1
        Assert-TestB ($null -ne $match -and $match.Bindings -eq $orig.Bindings) "TESTB.16_UnmanagedIisSitePreserved_$($orig.Name)" 'Untouched'
    }

    $ghEvidence = [ordered]@{
        testId                        = 'TEST_B_REAL_GITHUB_AUTONOMOUS_IIS'
        timestampUtc                  = (Get-Date).ToUniversalTime().ToString('O')
        namespace                     = $manifest.namespace
        disposableRepository          = $GitHubRepo
        branch                        = $GitHubBranch
        iisSiteName                   = $manifest.ghIisSiteName
        appBaseUrl                    = $manifest.ghAppBaseUrl
        directTriggerApiCallsUsed     = 0
        gitHubLivePollTested          = $true
        provesAutonomousGitHubPolling = $true
        v1 = [ordered]@{ commitSha = $v1Sha; workflowRunId = $v1Gh.workflowRunId; artifactId = $v1Gh.artifactId; deploymentId = [string]$v1Deploy.id; state = [int]$v1Deploy.state }
        v2 = [ordered]@{ commitSha = $v2Sha; workflowRunId = $v2Gh.workflowRunId; artifactId = $v2Gh.artifactId; deploymentId = [string]$v2Deploy.id; state = [int]$v2Deploy.state }
        v3UnhealthyRollback = [ordered]@{ commitSha = $v3Sha; workflowRunId = $v3Gh.workflowRunId; artifactId = $v3Gh.artifactId; deploymentId = [string]$v3Deploy.id; state = [int]$v3Deploy.state; restoredCommitSha = [string]$liveAfterRbRoot.commitSha; retriedAfterRestart = ($v3AttemptsAfter -ne $v3AttemptsBefore) }
        v4Recovery = [ordered]@{ commitSha = $v4Sha; workflowRunId = $v4Gh.workflowRunId; artifactId = $v4Gh.artifactId; deploymentId = [string]$v4Deploy.id; state = [int]$v4Deploy.state }
        persistentDataPreserved       = ($liveV4Data.markerContent -eq $ghMarker)
        checks                        = @($checks)
    }
    $outPath = Join-Path $manifest.diagnosticsDir 'github-autonomous-evidence.json'
    $ghEvidence | ConvertTo-Json -Depth 6 | Set-Content -Path $outPath -Encoding UTF8
    Write-E2ELog "TEST B PASSED ($($checks.Count) checks). Real GitHub autonomous evidence saved to $outPath" 'PASS' $logFile -ExtraSecrets @($pat)
}
finally {
    $clientCert.Dispose()
}
exit 0
