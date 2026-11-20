<#
.SYNOPSIS
    Phase 12D, 12E & 12F Real Windows IIS Deployment & Autonomous Update Acceptance Runner.
.DESCRIPTION
    Executes against the live disposable ServerForge Agent Windows Service and real Windows IIS:
    - Phase 12D: Fail-closed mTLS pairing, wrong-thumbprint rejection, pairing replay rejection,
                 unauthorized request rejection, and Agent service restart identity persistence.
    - Phase 12E: Real IIS site & AppPool provisioning for ServerForge.SampleApp v1 (1.0.0-e2e),
                 verified SHA-256 artifact extraction, live HTTP /, /health, /data verification,
                 SQLite deployment history verification, and unmanaged IIS site preservation.
    - Phase 12F: Second release update v2 (1.0.1-e2e) with Desktop closed, verifying updated HTTP
                 output, preserved /data persistent marker, and preserved v1 release on disk.
    Never logs pairing codes, PATs, or private keys. Exits non-zero on any failed assertion.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [string]$SampleAppDir = '',
    [string]$GitHubRepo = '',
    [string]$GitHubBranch = 'main'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

$ManifestPath = Resolve-E2EAbsolutePath $ManifestPath
if (-not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

$logFile = Join-Path $manifest.diagnosticsDir 'deployment-tests.log'
$preflight = Get-Content $manifest.preflightInventory -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($SampleAppDir)) {
    $SampleAppDir = Resolve-E2EAbsolutePath (Join-Path $PSScriptRoot '..\..\samples\disposable-test-app')
} else {
    $SampleAppDir = Resolve-E2EAbsolutePath $SampleAppDir
}

$checks = New-Object System.Collections.Generic.List[object]
function Assert-E2E {
    param([bool]$Condition, [string]$Name, [string]$Detail = '')
    $safeDetail = Protect-SanitizedText -Text $Detail
    $checks.Add([ordered]@{ name = $Name; passed = $Condition; detail = $safeDetail; timestampUtc = (Get-Date).ToUniversalTime().ToString('O') })
    if ($Condition) {
        Write-E2ELog "PASS: $Name ($safeDetail)" 'PASS' $logFile
    } else {
        Write-E2ELog "FAIL: $Name ($safeDetail)" 'FAIL' $logFile
        throw "E2E check failed: $Name ($safeDetail)"
    }
}

Write-E2ELog "=== Phase 12D/12E/12F Real IIS Deployment Tests ($($manifest.namespace)) ===" 'INFO' $logFile

# --- PHASE 12D: Secure Pairing & Management ---
$pairingFile = Join-Path $manifest.agentDataRoot 'pairing-code.txt'
$pairingText = Get-Content $pairingFile -Raw
if ($pairingText -notmatch 'ServerForge Agent Pairing Code:\s*(\S+)') { throw 'Could not parse pairing code.' }
$pairingCode = $matches[1].Trim()
if ($pairingText -notmatch 'Server Cert Thumbprint:\s*([A-Fa-f0-9]+)') { throw 'Could not parse server cert thumbprint.' }
$serverThumbprint = $matches[1].Trim().ToUpperInvariant()

$clientPfxPath = Join-Path $manifest.desktopIdentityDir 'e2e-desktop-client.pfx'
$clientCert = New-E2EDesktopClientCertificate -PfxPath $clientPfxPath -CommonName "ServerForgeE2E-$($manifest.runId)"
$clientPem = ConvertTo-CertPem -Certificate $clientCert
$desktopInstanceId = "e2e-desktop-$($manifest.runId)"

try {
    # 1. Wrong server thumbprint MUST fail closed during TLS handshake
    $wrongThumbRejected = $false
    try {
        [void](Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/pairing/complete' -Method 'POST' `
            -ExpectedServerThumbprint 'DEADBEEF00001111222233334444555566667777' -ClientCertificate $clientCert `
            -Body @{ DesktopInstanceId = $desktopInstanceId; DesktopDisplayName = 'E2E'; DesktopClientCertPem = $clientPem; OneTimePairingCode = $pairingCode; RequestedAtUtc = (Get-Date).ToUniversalTime().ToString('O') })
    } catch {
        $wrongThumbRejected = $true
    }
    Assert-E2E $wrongThumbRejected '12D.1_WrongServerThumbprintFailsClosed' 'TLS handshake rejected mismatched thumbprint'

    # 2. Valid one-time pairing code + pinned thumbprint succeeds
    $pairResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/pairing/complete' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ DesktopInstanceId = $desktopInstanceId; DesktopDisplayName = 'E2E'; DesktopClientCertPem = $clientPem; OneTimePairingCode = $pairingCode; RequestedAtUtc = (Get-Date).ToUniversalTime().ToString('O') }
    Assert-E2E ($pairResp.IsSuccess -and $pairResp.Json.accepted -eq $true) '12D.2_InitialMtlsPairingSucceeded' "AgentId=$($pairResp.Json.agentId)"

    # 3. Reusing the one-time pairing code MUST fail
    $replayResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/pairing/complete' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ DesktopInstanceId = $desktopInstanceId; DesktopDisplayName = 'E2E'; DesktopClientCertPem = $clientPem; OneTimePairingCode = $pairingCode; RequestedAtUtc = (Get-Date).ToUniversalTime().ToString('O') }
    Assert-E2E (-not $replayResp.IsSuccess) '12D.3_PairingCodeReplayRejected' "HTTP $($replayResp.StatusCode)"

    # 4. Unauthorized (anonymous) request MUST be rejected
    $anonResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/diagnostics' -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $null
    Assert-E2E ($anonResp.StatusCode -in @(401, 403)) '12D.4_UnauthorizedRequestRejected' "HTTP $($anonResp.StatusCode)"

    # 5. Restart Agent Windows Service & verify identity + mTLS persistence
    Restart-Service -Name $manifest.agentServiceName -Force
    $restarted = Wait-E2ECondition -Condition {
        $s = Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue
        ($null -ne $s -and $s.Status -eq 'Running') -and (Test-TcpPortInUse -Port ([int]$manifest.agentPort))
    } -TimeoutSeconds 30
    Assert-E2E $restarted '12D.5_AgentRestartedCleanly' $manifest.agentServiceName

    $diagResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/diagnostics' -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert
    Assert-E2E ($diagResp.IsSuccess) '12D.6_AuthorizedMtlsPersistedAfterAgentRestart' "HTTP $($diagResp.StatusCode)"

    # --- PHASE 12E: Real IIS Deployment (v1) ---
    $pubScript = Join-Path $SampleAppDir 'publish-local-release.ps1'
    $hexSuffix1 = (($manifest.runId.ToLowerInvariant() -replace '[^0-9a-f]', 'a').PadRight(8, 'a')).Substring(0, 8)
    $hexSuffix2 = (($manifest.runId.ToLowerInvariant() -replace '[^0-9a-f]', 'b').PadRight(8, 'b')).Substring(0, 8)
    $commitV1 = "11111111111111111111111111111111$hexSuffix1"
    $commitV2 = "22222222222222222222222222222222$hexSuffix2"

    $v1Art = & $pubScript -OutputZipPath (Join-Path $manifest.artifactsDir 'sample-v1.zip') -Version '1.0.0-e2e' -CommitSha $commitV1
    $v2Art = & $pubScript -OutputZipPath (Join-Path $manifest.artifactsDir 'sample-v2.zip') -Version '1.0.1-e2e' -CommitSha $commitV2
    Assert-E2E ($v1Art.SizeBytes -gt 0 -and $v2Art.SizeBytes -gt 0) '12C.1_RealSampleAppArtifactsBuiltAndHashed' "v1=$($v1Art.Sha256.Substring(0,12)).. v2=$($v2Art.Sha256.Substring(0,12)).."

    $repoName = if ([string]::IsNullOrWhiteSpace($GitHubRepo)) { 'disposable-e2e/serverforge-sample-app' } else { $GitHubRepo }
    $projId = [Guid]::NewGuid().ToString()
    $projResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/projects' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ Id = $projId; Name = "E2E $($manifest.runId)"; Slug = "e2e-$($manifest.runId.ToLowerInvariant() -replace '[^a-z0-9]', '')"; Description = 'Disposable IIS acceptance project' }
    Assert-E2E $projResp.IsSuccess '12E.1_ProjectCreatedOverMtls' "HTTP $($projResp.StatusCode)"

    $slug = ('e2e-' + ($manifest.runId.ToLowerInvariant() -replace '[^a-z0-9]', '')).Substring(0, [Math]::Min(20, 4 + ($manifest.runId -replace '[^A-Za-z0-9]', '').Length))
    if ($slug.Length -lt 5) { $slug = "${slug}app" }

    $appPayload = @{
        Id                             = $null
        ProjectGroupId                 = $projId
        Name                           = "Disposable Sample $($manifest.runId)"
        Slug                           = $slug
        RuntimeType                    = 5 # ApplicationRuntimeType.AspNetCore
        RepositoryFullName             = $repoName
        Branch                         = $GitHubBranch
        WorkingDirectory               = '.'
        AutoDeployEnabled              = $true
        RepositoryExplicitlyAuthorized = $true
        SourceMode                     = 0 # GitHubActionsArtifact
        GitHubWorkflowFileName         = 'serverforge-deploy.yml'
        BuildCommand                   = ''
        StartCommand                   = ''
        RuntimeVersion                 = '10.0'
        InternalPort                   = [int]$manifest.appPort
        IisSiteName                    = $manifest.iisSiteName
        IisAppPoolName                 = $manifest.iisAppPoolName
        WindowsServiceName             = "SF_$slug"
        PrimaryDomain                  = 'localhost'
        HealthCheckPath                = '/health'
        HealthCheckTimeoutSeconds      = 30
        RetainHealthyReleasesCount     = 5
        AutoRollbackOnFailure          = $true
    }
    $appResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/applications' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -Body $appPayload
    Assert-E2E ($appResp.IsSuccess -and $null -ne $appResp.Json.id) '12E.2_ApplicationRegisteredOverMtls' "AppId=$($appResp.Json.id) Site=$($manifest.iisSiteName)"
    $appId = [string]$appResp.Json.id

    # Configure v1 environment variables
    $envV1Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/env" -Method 'PUT' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Tier = 2 # Production
            Variables = @(
                @{ Key = 'APP_VERSION'; Value = '1.0.0-e2e'; IsSecret = $false; IsRequired = $true },
                @{ Key = 'APP_COMMIT_SHA'; Value = $commitV1; IsSecret = $false; IsRequired = $false },
                @{ Key = 'SIMULATE_UNHEALTHY'; Value = 'false'; IsSecret = $false; IsRequired = $false }
            )
        }
    Assert-E2E $envV1Resp.IsSuccess '12E.3_EnvironmentVariablesConfigured' "HTTP $($envV1Resp.StatusCode)"

    # Trigger real IIS v1 deployment
    $dep1Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 120 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV1
            Branch                 = $GitHubBranch
            CommitMessage          = 'Phase 12E v1 healthy release'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v1Art.ZipPath
            ExpectedArtifactSha256 = $v1Art.Sha256
            TriggeredAutomatically = $false
            RequestedBy            = 'Phase12E-Harness'
            VerifiedWorkflowRunId  = 120001
            VerifiedArtifactId     = 120001
            VerifiedArtifactName   = 'serverforge-sample-app-win-x64'
            ArtifactTrustSource    = 'LocalBuildVerifiedSha256'
        }
    Assert-E2E ($dep1Resp.IsSuccess -and $dep1Resp.Json.state -eq 8) '12E.4_RealIisDeploymentV1Succeeded' "State=$($dep1Resp.Json.state) Release=$($dep1Resp.Json.releaseDirectory)"

    # Probe real live IIS HTTP endpoints (/, /health, /data)
    $liveHealth1 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/health" -Method Get -TimeoutSec 15
    $liveRoot1 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/" -Method Get -TimeoutSec 15
    $liveData1 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-E2E ($liveHealth1.status -eq 'healthy' -and $liveRoot1.version -eq '1.0.0-e2e' -and $liveData1.markerExists -eq $true) `
        '12E.5_LiveIisHttpEndpointsVerifiedV1' "version=$($liveRoot1.version) commit=$($liveRoot1.commitSha) marker=$($liveData1.markerContent)"
    $originalMarker = [string]$liveData1.markerContent

    # Verify real IIS site exists and pre-existing sites remain untouched
    $sitesAfterV1 = @(Get-IisSitesSnapshot)
    $createdSite = $sitesAfterV1 | Where-Object { $_.Name -eq $manifest.iisSiteName } | Select-Object -First 1
    Assert-E2E ($null -ne $createdSite -and $createdSite.State -eq 'Started') '12E.6_RealIisSiteCreatedAndStarted' "SiteId=$($createdSite.Id) Bindings=$($createdSite.Bindings)"
    foreach ($orig in @($preflight.preExistingIisSites)) {
        $match = $sitesAfterV1 | Where-Object { $_.Name -eq $orig.Name } | Select-Object -First 1
        Assert-E2E ($null -ne $match -and $match.Bindings -eq $orig.Bindings) "12E.7_UnmanagedIisSitePreserved_$($orig.Name)" "Bindings unchanged"
    }

    # --- PHASE 12F: Second Release Update (Desktop Closed) ---
    $desktopProcs = @(Get-Process -Name 'ServerForge.Desktop' -ErrorAction SilentlyContinue)
    Assert-E2E ($desktopProcs.Count -eq 0) '12F.1_DesktopProcessNotRunning' 'Confirmed headless Agent-only execution'

    Start-Sleep -Seconds 2
    $envV2Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/env" -Method 'PUT' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Tier = 2
            Variables = @(
                @{ Key = 'APP_VERSION'; Value = '1.0.1-e2e'; IsSecret = $false; IsRequired = $true },
                @{ Key = 'APP_COMMIT_SHA'; Value = $commitV2; IsSecret = $false; IsRequired = $false },
                @{ Key = 'SIMULATE_UNHEALTHY'; Value = 'false'; IsSecret = $false; IsRequired = $false }
            )
        }
    Assert-E2E $envV2Resp.IsSuccess '12F.2_EnvironmentUpdatedForV2' "HTTP $($envV2Resp.StatusCode)"

    $dep2Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 120 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV2
            Branch                 = $GitHubBranch
            CommitMessage          = 'Phase 12F v2 update while Desktop closed'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v2Art.ZipPath
            ExpectedArtifactSha256 = $v2Art.Sha256
            TriggeredAutomatically = $true
            RequestedBy            = 'ServerForge.Agent.AutoDeploy'
            VerifiedWorkflowRunId  = 120002
            VerifiedArtifactId     = 120002
            VerifiedArtifactName   = 'serverforge-sample-app-win-x64'
            ArtifactTrustSource    = 'LocalBuildVerifiedSha256'
        }
    Assert-E2E ($dep2Resp.IsSuccess -and $dep2Resp.Json.state -eq 8) '12F.3_RealIisUpdateV2Succeeded' "Release=$($dep2Resp.Json.releaseDirectory)"

    $liveRoot2 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/" -Method Get -TimeoutSec 15
    $liveData2 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-E2E ($liveRoot2.version -eq '1.0.1-e2e' -and $liveData2.markerContent -eq $originalMarker) `
        '12F.4_LiveIisUpdatedToV2AndPreservedPersistentData' "version=$($liveRoot2.version) markerPreserved=true"

    # Duplicate autonomous commit MUST be skipped (DeploymentState.Cancelled = 11)
    $dupResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 60 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV2
            Branch                 = $GitHubBranch
            CommitMessage          = 'Duplicate commit test'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v2Art.ZipPath
            ExpectedArtifactSha256 = $v2Art.Sha256
            TriggeredAutomatically = $true
            RequestedBy            = 'ServerForge.Agent.AutoDeploy'
            VerifiedWorkflowRunId  = 120002
            VerifiedArtifactId     = 120002
            VerifiedArtifactName   = 'serverforge-sample-app-win-x64'
            ArtifactTrustSource    = 'LocalBuildVerifiedSha256'
        }
    Assert-E2E ($dupResp.IsSuccess -and $dupResp.Json.state -eq 11) '12F.5_DuplicateCommitPrevented' "State=$($dupResp.Json.state)"

    # Verify releases preserved on disk & history persisted in SQLite
    $relResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/releases" -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert
    $histResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/deployments" -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert
    Assert-E2E ($relResp.IsSuccess -and @($relResp.Json).Count -ge 2) '12F.6_PreviousHealthyReleasePreservedOnDisk' "ReleaseCount=$(@($relResp.Json).Count)"
    Assert-E2E ($histResp.IsSuccess -and @($histResp.Json).Count -ge 2) '12F.7_DeploymentHistoryPersistedInSqlite' "HistoryCount=$(@($histResp.Json).Count)"

    # Save sanitized state for verify-rollback.ps1 (ZERO secrets)
    $stateOut = [ordered]@{
        timestampUtc          = (Get-Date).ToUniversalTime().ToString('O')
        namespace             = $manifest.namespace
        serverCertThumbprint  = $serverThumbprint
        clientPfxPath         = $clientPfxPath
        applicationId         = $appId
        iisSiteId             = $createdSite.Id
        iisSiteName           = $createdSite.Name
        iisBindings           = $createdSite.Bindings
        v1CommitSha           = $commitV1
        v2CommitSha           = $commitV2
        v2Version             = $liveRoot2.version
        persistentDataMarker  = $originalMarker
        gitHubLivePollTested  = $false
        artifactMode          = 'LocalVerifiedSampleBuild'
        checks                = @($checks)
    }
    $evidencePath = Join-Path $manifest.diagnosticsDir 'deployment-evidence.json'
    $stateOut | ConvertTo-Json -Depth 6 | Set-Content -Path $evidencePath -Encoding UTF8
    Write-E2ELog "Deployment tests PASSED ($($checks.Count) checks). Evidence saved to $evidencePath" 'PASS' $logFile
}
finally {
    $clientCert.Dispose()
}
exit 0
