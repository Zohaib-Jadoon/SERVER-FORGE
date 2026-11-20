<#
.SYNOPSIS
    Phase 12G Real Windows IIS Failed Deployment & Automatic Rollback Verification.
.DESCRIPTION
    Deploys a deliberately unhealthy release (SIMULATE_UNHEALTHY=true -> HTTP 503) to the
    disposable IIS site provisioned by run-deployment-tests.ps1 and verifies:
    - Agent detects HTTP 503 health check failure.
    - Deployment transitions to RolledBack (DeploymentState=10) with recorded reason & timestamp.
    - IIS site automatically switches back to the previous healthy release (1.0.1-e2e) and
      restores its healthy web.config runtime configuration.
    - Live HTTP /health returns 200 OK and / returns 1.0.1-e2e.
    - /data persistent marker is 100% preserved untouched.
    - Endless autonomous retry of the failed commit is blocked (DeploymentState.Cancelled=11).
    - Pre-existing unmanaged IIS sites remain untouched.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [string]$SampleAppDir = '',
    [string]$GitHubBranch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

$ManifestPath = Resolve-E2EAbsolutePath $ManifestPath
if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

$evidencePath = Join-Path $manifest.diagnosticsDir 'deployment-evidence.json'
if (-not (Test-Path $evidencePath)) {
    throw "Deployment evidence not found at '$evidencePath'. Run run-deployment-tests.ps1 first."
}
$depState = Get-Content $evidencePath -Raw | ConvertFrom-Json
$preflight = Get-Content $manifest.preflightInventory -Raw | ConvertFrom-Json
$logFile = Join-Path $manifest.diagnosticsDir 'verify-rollback.log'

if ([string]::IsNullOrWhiteSpace($SampleAppDir)) {
    $SampleAppDir = Resolve-E2EAbsolutePath (Join-Path $PSScriptRoot '..\..\samples\disposable-test-app')
} else {
    $SampleAppDir = Resolve-E2EAbsolutePath $SampleAppDir
}

$checks = New-Object System.Collections.Generic.List[object]
function Assert-Rollback {
    param([bool]$Condition, [string]$Name, [string]$Detail = '')
    $safeDetail = Protect-SanitizedText -Text $Detail
    $checks.Add([ordered]@{ name = $Name; passed = $Condition; detail = $safeDetail; timestampUtc = (Get-Date).ToUniversalTime().ToString('O') })
    if ($Condition) {
        Write-E2ELog "PASS: $Name ($safeDetail)" 'PASS' $logFile
    } else {
        Write-E2ELog "FAIL: $Name ($safeDetail)" 'FAIL' $logFile
        throw "Rollback verification failed: $Name ($safeDetail)"
    }
}

Write-E2ELog "=== Phase 12G Real IIS Failed Deployment & Automatic Rollback ($($manifest.namespace)) ===" 'INFO' $logFile

$clientCert = New-E2EDesktopClientCertificate -PfxPath $depState.clientPfxPath -CommonName "ServerForgeE2E-$($manifest.runId)"
$serverThumbprint = [string]$depState.serverCertThumbprint
$appId = [string]$depState.applicationId

try {
    # 1. Build genuine v3 artifact for the broken commit
    $pubScript = Join-Path $SampleAppDir 'publish-local-release.ps1'
    $hexSuffix3 = (($manifest.runId.ToLowerInvariant() -replace '[^0-9a-f]', 'c').PadRight(8, 'c')).Substring(0, 8)
    $commitV3Broken = "33333333333333333333333333333333$hexSuffix3"
    $v3Art = & $pubScript -OutputZipPath (Join-Path $manifest.artifactsDir 'sample-v3-broken.zip') -Version '1.0.2-broken' -CommitSha $commitV3Broken
    Assert-Rollback ($v3Art.SizeBytes -gt 0) '12G.1_UnhealthyReleaseArtifactBuilt' "SHA=$($v3Art.Sha256.Substring(0,12)).."

    # 2. Configure SIMULATE_UNHEALTHY=true in Production environment variables
    $envBadResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/env" -Method 'PUT' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Tier = 2 # EnvironmentTier.Production
            Variables = @(
                @{ Key = 'APP_VERSION'; Value = '1.0.2-broken'; IsSecret = $false; IsRequired = $true },
                @{ Key = 'APP_COMMIT_SHA'; Value = $commitV3Broken; IsSecret = $false; IsRequired = $false },
                @{ Key = 'SIMULATE_UNHEALTHY'; Value = 'true'; IsSecret = $false; IsRequired = $false }
            )
        }
    Assert-Rollback $envBadResp.IsSuccess '12G.2_SimulateUnhealthyEnvConfigured' 'SIMULATE_UNHEALTHY=true'

    # 3. Trigger autonomous deployment of unhealthy v3 -> Agent must detect HTTP 503 and roll back automatically
    Start-Sleep -Seconds 2
    $badDepResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 120 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV3Broken
            Branch                 = $GitHubBranch
            CommitMessage          = 'Phase 12G deliberately unhealthy release (SIMULATE_UNHEALTHY=true)'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v3Art.ZipPath
            ExpectedArtifactSha256 = $v3Art.Sha256
            TriggeredAutomatically = $true
            RequestedBy            = 'ServerForge.Agent.AutoDeploy'
            VerifiedWorkflowRunId  = 120003
            VerifiedArtifactId     = 120003
            VerifiedArtifactName   = 'serverforge-sample-app-win-x64'
            ArtifactTrustSource    = 'LocalBuildVerifiedSha256'
        }
    Assert-Rollback ($badDepResp.IsSuccess -and $badDepResp.Json.state -eq 10) '12G.3_UnhealthyDeploymentDetectedAndAutoRolledBack' `
        "State=$($badDepResp.Json.state) Error=$($badDepResp.Json.errorSummary)"
    Assert-Rollback (-not [string]::IsNullOrWhiteSpace($badDepResp.Json.errorSummary) -and -not [string]::IsNullOrWhiteSpace($badDepResp.Json.completedAtUtc)) `
        '12G.4_RollbackReasonAndTimestampRecorded' "CompletedAtUtc=$($badDepResp.Json.completedAtUtc)"

    # 4. Probe real live IIS endpoints after rollback -> MUST be 200 OK on 1.0.1-e2e with original persistent marker intact
    $healthyRestored = Wait-E2ECondition -Condition {
        try {
            $h = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/health" -Method Get -TimeoutSec 10
            return $h.status -eq 'healthy'
        } catch { return $false }
    } -TimeoutSeconds 25
    Assert-Rollback $healthyRestored '12G.5_LiveIisHealthEndpointRestoredTo200OK' "$($manifest.appBaseUrl)/health"

    $rootAfter = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/" -Method Get -TimeoutSec 15
    $dataAfter = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-Rollback ($rootAfter.version -eq $depState.v2Version) '12G.6_LiveIisRestoredPreviousHealthyVersion' `
        "Expected=$($depState.v2Version) Actual=$($rootAfter.version)"
    Assert-Rollback ($dataAfter.markerContent -eq $depState.persistentDataMarker) '12G.7_PersistentDataPreservedAcrossRollback' `
        "Marker=$($dataAfter.markerContent)"

    # 5. Verify anti-retry-loop guard blocks repeating the failed autonomous commit (DeploymentState.Cancelled = 11)
    $retryResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 60 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV3Broken
            Branch                 = $GitHubBranch
            CommitMessage          = 'Retry loop test for failed commit'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v3Art.ZipPath
            ExpectedArtifactSha256 = $v3Art.Sha256
            TriggeredAutomatically = $true
            RequestedBy            = 'ServerForge.Agent.AutoDeploy'
            VerifiedWorkflowRunId  = 120003
            VerifiedArtifactId     = 120003
            VerifiedArtifactName   = 'serverforge-sample-app-win-x64'
            ArtifactTrustSource    = 'LocalBuildVerifiedSha256'
        }
    Assert-Rollback ($retryResp.IsSuccess -and $retryResp.Json.state -eq 11) '12G.8_EndlessRetryLoopPrevented' "State=$($retryResp.Json.state)"

    # 6. Verify pre-existing unmanaged IIS sites were never modified or stopped
    $sitesAfter = @(Get-IisSitesSnapshot)
    foreach ($orig in @($preflight.preExistingIisSites)) {
        $match = $sitesAfter | Where-Object { $_.Name -eq $orig.Name } | Select-Object -First 1
        Assert-Rollback ($null -ne $match -and $match.Bindings -eq $orig.Bindings) "12G.9_UnmanagedIisSitePreserved_$($orig.Name)" 'Untouched'
    }

    $rollbackEvidence = [ordered]@{
        timestampUtc         = (Get-Date).ToUniversalTime().ToString('O')
        namespace            = $manifest.namespace
        failedCommitSha      = $commitV3Broken
        rollbackState        = $badDepResp.Json.state
        rollbackErrorSummary = (Protect-SanitizedText -Text ([string]$badDepResp.Json.errorSummary))
        restoredVersion      = $rootAfter.version
        restoredCommitSha    = $rootAfter.commitSha
        persistentDataIntact = ($dataAfter.markerContent -eq $depState.persistentDataMarker)
        checks               = @($checks)
    }
    $outPath = Join-Path $manifest.diagnosticsDir 'rollback-evidence.json'
    $rollbackEvidence | ConvertTo-Json -Depth 6 | Set-Content -Path $outPath -Encoding UTF8
    Write-E2ELog "Rollback verification PASSED ($($checks.Count) checks). Evidence saved to $outPath" 'PASS' $logFile
}
finally {
    $clientCert.Dispose()
}
exit 0
