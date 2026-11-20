<#
.SYNOPSIS
    TEST A — Real Windows IIS Deployment with Locally Built, Hash-Verified Sample Artifacts.
.DESCRIPTION
    Executes TEST A against the live disposable ServerForge Agent Windows Service and real Windows IIS:
    - mTLS Security: Fail-closed wrong-thumbprint rejection, one-time pairing, replay rejection,
      anonymous rejection, and Agent Windows Service restart identity persistence.
    - Provenance Boundary Check: Verifies that a GitHubActionsArtifact app rejects caller-supplied
      local file paths via POST /api/v1/deployments/trigger (HTTP 400).
    - TEST A Real IIS Deploy (v1 -> v2): Uses explicit ManualSignedPackage mode labeled
      "LOCAL TEST ARTIFACT (ManualSignedPackage)" with TriggeredAutomatically = $false.
      Verifies real IIS site/AppPool creation, web.config environment variable injection,
      live HTTP /, /health, /data responses, persistent /data survival, immutable release retention,
      SQLite history persistence, and unmanaged IIS site preservation.
    NOTE: TEST A does NOT claim to prove autonomous GitHub polling (gitHubLivePollTested = false).
    Genuine autonomous GitHub Actions polling is independently tested by run-github-autonomous-test.ps1 (TEST B).
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

$logFile = Join-Path $manifest.diagnosticsDir 'test-a-local-iis.log'
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
        throw "TEST A failed: $Name ($safeDetail)"
    }
}

Write-E2ELog "=== TEST A: Real IIS Deployment with LOCAL TEST ARTIFACT ($($manifest.namespace)) ===" 'INFO' $logFile

# --- Secure Pairing & mTLS Verification ---
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
    Assert-E2E $wrongThumbRejected 'TESTA.1_WrongServerThumbprintFailsClosed' 'TLS handshake rejected mismatched thumbprint'

    # 2. Valid one-time pairing code + pinned thumbprint succeeds
    $pairResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/pairing/complete' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ DesktopInstanceId = $desktopInstanceId; DesktopDisplayName = 'E2E'; DesktopClientCertPem = $clientPem; OneTimePairingCode = $pairingCode; RequestedAtUtc = (Get-Date).ToUniversalTime().ToString('O') }
    Assert-E2E ($pairResp.IsSuccess -and $pairResp.Json.accepted -eq $true) 'TESTA.2_InitialMtlsPairingSucceeded' "AgentId=$($pairResp.Json.agentId)"

    # 3. Reusing the one-time pairing code MUST fail
    $replayResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/pairing/complete' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ DesktopInstanceId = $desktopInstanceId; DesktopDisplayName = 'E2E'; DesktopClientCertPem = $clientPem; OneTimePairingCode = $pairingCode; RequestedAtUtc = (Get-Date).ToUniversalTime().ToString('O') }
    Assert-E2E (-not $replayResp.IsSuccess) 'TESTA.3_PairingCodeReplayRejected' "HTTP $($replayResp.StatusCode)"

    # 4. Unauthorized (anonymous) request MUST be rejected
    $anonResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/diagnostics' -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $null
    Assert-E2E ($anonResp.StatusCode -in @(401, 403)) 'TESTA.4_UnauthorizedRequestRejected' "HTTP $($anonResp.StatusCode)"

    # 5. Restart Agent Windows Service & verify identity + mTLS persistence
    Restart-Service -Name $manifest.agentServiceName -Force
    $restarted = Wait-E2ECondition -Condition {
        $s = Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue
        ($null -ne $s -and $s.Status -eq 'Running') -and (Test-TcpPortInUse -Port ([int]$manifest.agentPort))
    } -TimeoutSeconds 30
    Assert-E2E $restarted 'TESTA.5_AgentRestartedCleanly' $manifest.agentServiceName

    $diagResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/diagnostics' -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert
    Assert-E2E ($diagResp.IsSuccess) 'TESTA.6_AuthorizedMtlsPersistedAfterAgentRestart' "HTTP $($diagResp.StatusCode)"

    # --- Build Locally Hash-Verified Sample Artifacts (Explicitly Labeled LOCAL TEST ARTIFACT) ---
    $pubScript = Join-Path $SampleAppDir 'publish-local-release.ps1'
    $hexSuffix1 = (($manifest.runId.ToLowerInvariant() -replace '[^0-9a-f]', 'a').PadRight(8, 'a')).Substring(0, 8)
    $hexSuffix2 = (($manifest.runId.ToLowerInvariant() -replace '[^0-9a-f]', 'b').PadRight(8, 'b')).Substring(0, 8)
    $commitV1 = "11111111111111111111111111111111$hexSuffix1"
    $commitV2 = "22222222222222222222222222222222$hexSuffix2"

    $v1Art = & $pubScript -OutputZipPath (Join-Path $manifest.artifactsDir 'sample-v1.zip') -Version '1.0.0-local-e2e' -CommitSha $commitV1
    $v2Art = & $pubScript -OutputZipPath (Join-Path $manifest.artifactsDir 'sample-v2.zip') -Version '1.0.1-local-e2e' -CommitSha $commitV2
    Assert-E2E ($v1Art.SizeBytes -gt 0 -and $v2Art.SizeBytes -gt 0) 'TESTA.7_LocalSampleArtifactsBuiltAndHashed' "v1=$($v1Art.Sha256.Substring(0,12)).. v2=$($v2Art.Sha256.Substring(0,12)).."

    $projId = [Guid]::NewGuid().ToString()
    $projSlug = ('e2e-' + ($manifest.runId.ToLowerInvariant() -replace '[^a-z0-9]', '')).Substring(0, [Math]::Min(20, 4 + ($manifest.runId -replace '[^A-Za-z0-9]', '').Length))
    if ($projSlug.Length -lt 5) { $projSlug = "${projSlug}app" }

    $projResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/projects' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{ Id = $projId; Name = "E2E $($manifest.runId)"; Slug = $projSlug; Description = 'Disposable IIS acceptance project' }
    Assert-E2E $projResp.IsSuccess 'TESTA.8_ProjectCreatedOverMtls' "HTTP $($projResp.StatusCode)"

    # --- Provenance Security Negative Check: GitHubActionsArtifact MUST reject local ZIP paths ---
    $probeGhSlug = "${projSlug}-ghsec"
    $probeGhResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/applications' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Id = $null; ProjectGroupId = $projId; Name = "Provenance Guard Probe"; Slug = $probeGhSlug;
            RuntimeType = 5; RepositoryFullName = 'disposable-e2e/provenance-guard'; Branch = $GitHubBranch;
            WorkingDirectory = '.'; AutoDeployEnabled = $false; RepositoryExplicitlyAuthorized = $true;
            SourceMode = 0; GitHubWorkflowFileName = 'serverforge-deploy.yml'; BuildCommand = ''; StartCommand = '';
            RuntimeVersion = '10.0'; InternalPort = [int]$manifest.appPort; IisSiteName = "$($manifest.iisSiteName)_Guard";
            IisAppPoolName = "$($manifest.iisAppPoolName)_Guard"; WindowsServiceName = "SF_guard"; PrimaryDomain = 'localhost';
            HealthCheckPath = '/health'; HealthCheckTimeoutSeconds = 15; RetainHealthyReleasesCount = 2; AutoRollbackOnFailure = $true
        }
    Assert-E2E $probeGhResp.IsSuccess 'TESTA.SEC.0_ProvenanceProbeAppRegistered' "AppId=$($probeGhResp.Json.id)"
    $spoofResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            ApplicationId = [string]$probeGhResp.Json.id; CommitSha = $commitV1; Branch = $GitHubBranch;
            CommitMessage = 'Attempted local path spoof in GitHubActionsArtifact mode'; CommitAuthor = 'e2e';
            ArtifactDownloadUrl = $v1Art.ZipPath; ExpectedArtifactSha256 = $v1Art.Sha256;
            TriggeredAutomatically = $true; RequestedBy = 'spoof-test';
            VerifiedWorkflowRunId = 999001; VerifiedArtifactId = 999001
        }
    Assert-E2E ($spoofResp.StatusCode -eq 400) 'TESTA.SEC.1_GitHubActionsModeRejectsCallerSuppliedLocalArtifact' "HTTP $($spoofResp.StatusCode)"
    [void](Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$($probeGhResp.Json.id)" -Method 'DELETE' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert)

    # --- Register TEST A Application Strictly in ManualSignedPackage Mode (LOCAL TEST ARTIFACT) ---
    $appPayload = @{
        Id                             = $null
        ProjectGroupId                 = $projId
        Name                           = "Disposable Local Artifact Sample $($manifest.runId)"
        Slug                           = $projSlug
        RuntimeType                    = 5 # ApplicationRuntimeType.AspNetCore
        RepositoryFullName             = 'disposable-e2e/local-test-artifact'
        Branch                         = $GitHubBranch
        WorkingDirectory               = '.'
        AutoDeployEnabled              = $false
        RepositoryExplicitlyAuthorized = $true
        SourceMode                     = 2 # DeploymentSourceMode.ManualSignedPackage (LOCAL TEST ARTIFACT)
        GitHubWorkflowFileName         = ''
        BuildCommand                   = ''
        StartCommand                   = ''
        RuntimeVersion                 = '10.0'
        InternalPort                   = [int]$manifest.appPort
        IisSiteName                    = $manifest.iisSiteName
        IisAppPoolName                 = $manifest.iisAppPoolName
        WindowsServiceName             = "SF_$projSlug"
        PrimaryDomain                  = 'localhost'
        HealthCheckPath                = '/health'
        HealthCheckTimeoutSeconds      = 30
        RetainHealthyReleasesCount     = 5
        AutoRollbackOnFailure          = $true
    }
    $appResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/applications' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -Body $appPayload
    Assert-E2E ($appResp.IsSuccess -and $null -ne $appResp.Json.id) 'TESTA.9_LocalArtifactAppRegisteredOverMtls' "AppId=$($appResp.Json.id) Site=$($manifest.iisSiteName)"
    $appId = [string]$appResp.Json.id

    # Configure v1 environment variables
    $envV1Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/env" -Method 'PUT' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Tier = 2 # Production
            Variables = @(
                @{ Key = 'APP_VERSION'; Value = '1.0.0-local-e2e'; IsSecret = $false; IsRequired = $true },
                @{ Key = 'APP_COMMIT_SHA'; Value = $commitV1; IsSecret = $false; IsRequired = $false },
                @{ Key = 'SIMULATE_UNHEALTHY'; Value = 'false'; IsSecret = $false; IsRequired = $false }
            )
        }
    Assert-E2E $envV1Resp.IsSuccess 'TESTA.10_EnvironmentVariablesConfiguredV1' "HTTP $($envV1Resp.StatusCode)"

    # Trigger real IIS v1 deployment (explicitly manual + LOCAL TEST ARTIFACT)
    $dep1Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 120 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV1
            Branch                 = $GitHubBranch
            CommitMessage          = 'TEST A v1 healthy local test artifact'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v1Art.ZipPath
            ExpectedArtifactSha256 = $v1Art.Sha256
            TriggeredAutomatically = $false
            RequestedBy            = 'TEST_A_LocalHarness'
            ArtifactTrustSource    = 'LOCAL TEST ARTIFACT (ManualSignedPackage)'
        }
    Assert-E2E ($dep1Resp.IsSuccess -and $dep1Resp.Json.state -eq 8 -and $dep1Resp.Json.triggeredAutomatically -eq $false) `
        'TESTA.11_RealIisDeploymentV1Succeeded' "State=$($dep1Resp.Json.state) Release=$($dep1Resp.Json.releaseDirectory)"

    # Verify on-disk web.config environment variable injection
    $v1WebConfig = Join-Path ([string]$dep1Resp.Json.releaseDirectory) 'web.config'
    $v1WebCfgText = if (Test-Path $v1WebConfig) { Get-Content $v1WebConfig -Raw } else { '' }
    Assert-E2E ($v1WebCfgText -match 'APP_VERSION' -and $v1WebCfgText -match 'PERSISTENT_DATA_DIR' -and $v1WebCfgText -match 'AspNetCoreModuleV2') `
        'TESTA.12_WebConfigEnvVarsInjectedOnDisk' $v1WebConfig

    # Probe real live IIS HTTP endpoints (/, /health, /data)
    $liveHealth1 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/health" -Method Get -TimeoutSec 15
    $liveRoot1 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/" -Method Get -TimeoutSec 15
    $liveData1 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-E2E ($liveHealth1.status -eq 'healthy' -and $liveRoot1.version -eq '1.0.0-local-e2e' -and $liveData1.markerExists -eq $true) `
        'TESTA.13_LiveIisHttpEndpointsVerifiedV1' "version=$($liveRoot1.version) commit=$($liveRoot1.commitSha)"
    $originalMarker = [string]$liveData1.markerContent

    # Verify real IIS site exists and pre-existing sites remain untouched
    $sitesAfterV1 = @(Get-IisSitesSnapshot)
    $createdSite = $sitesAfterV1 | Where-Object { $_.Name -eq $manifest.iisSiteName } | Select-Object -First 1
    Assert-E2E ($null -ne $createdSite -and $createdSite.State -eq 'Started') 'TESTA.14_RealIisSiteCreatedAndStarted' "SiteId=$($createdSite.Id) Bindings=$($createdSite.Bindings)"
    foreach ($orig in @($preflight.preExistingIisSites)) {
        $match = $sitesAfterV1 | Where-Object { $_.Name -eq $orig.Name } | Select-Object -First 1
        Assert-E2E ($null -ne $match -and $match.Bindings -eq $orig.Bindings) "TESTA.15_UnmanagedIisSitePreserved_$($orig.Name)" 'Bindings unchanged'
    }

    # --- Second Release Update v2 (Manual Local Package, Desktop Closed) ---
    $desktopProcs = @(Get-Process -Name 'ServerForge.Desktop' -ErrorAction SilentlyContinue)
    Assert-E2E ($desktopProcs.Count -eq 0) 'TESTA.16_DesktopProcessNotRunning' 'Confirmed headless Agent-only execution'

    Start-Sleep -Seconds 2
    $envV2Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/env" -Method 'PUT' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert `
        -Body @{
            Tier = 2
            Variables = @(
                @{ Key = 'APP_VERSION'; Value = '1.0.1-local-e2e'; IsSecret = $false; IsRequired = $true },
                @{ Key = 'APP_COMMIT_SHA'; Value = $commitV2; IsSecret = $false; IsRequired = $false },
                @{ Key = 'SIMULATE_UNHEALTHY'; Value = 'false'; IsSecret = $false; IsRequired = $false }
            )
        }
    Assert-E2E $envV2Resp.IsSuccess 'TESTA.17_EnvironmentUpdatedForV2' "HTTP $($envV2Resp.StatusCode)"

    $dep2Resp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path '/api/v1/deployments/trigger' -Method 'POST' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert -TimeoutSeconds 120 `
        -Body @{
            ApplicationId          = $appId
            CommitSha              = $commitV2
            Branch                 = $GitHubBranch
            CommitMessage          = 'TEST A v2 local test artifact update'
            CommitAuthor           = 'e2e-harness'
            ArtifactDownloadUrl    = $v2Art.ZipPath
            ExpectedArtifactSha256 = $v2Art.Sha256
            TriggeredAutomatically = $false
            RequestedBy            = 'TEST_A_LocalHarness'
            ArtifactTrustSource    = 'LOCAL TEST ARTIFACT (ManualSignedPackage)'
        }
    Assert-E2E ($dep2Resp.IsSuccess -and $dep2Resp.Json.state -eq 8 -and $dep2Resp.Json.triggeredAutomatically -eq $false) `
        'TESTA.18_RealIisUpdateV2Succeeded' "Release=$($dep2Resp.Json.releaseDirectory)"

    $liveRoot2 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/" -Method Get -TimeoutSec 15
    $liveData2 = Invoke-RestMethod -Uri "$($manifest.appBaseUrl)/data" -Method Get -TimeoutSec 15
    Assert-E2E ($liveRoot2.version -eq '1.0.1-local-e2e' -and $liveData2.markerContent -eq $originalMarker) `
        'TESTA.19_LiveIisUpdatedToV2AndPreservedPersistentData' "version=$($liveRoot2.version) markerPreserved=true"

    # Verify releases preserved on disk & history persisted in SQLite
    $relResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/releases" -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert
    $histResp = Invoke-PinnedAgentRequest -BaseUrl $manifest.agentBaseUrl -Path "/api/v1/applications/$appId/deployments" -Method 'GET' `
        -ExpectedServerThumbprint $serverThumbprint -ClientCertificate $clientCert
    Assert-E2E ($relResp.IsSuccess -and @($relResp.Json).Count -ge 2) 'TESTA.20_PreviousHealthyReleasePreservedOnDisk' "ReleaseCount=$(@($relResp.Json).Count)"
    Assert-E2E ($histResp.IsSuccess -and @($histResp.Json).Count -ge 2) 'TESTA.21_DeploymentHistoryPersistedInSqlite' "HistoryCount=$(@($histResp.Json).Count)"

    # Save sanitized state for verify-rollback.ps1 and run-github-autonomous-test.ps1 (ZERO secrets)
    $stateOut = [ordered]@{
        testId                        = 'TEST_A_LOCAL_IIS_ARTIFACT'
        timestampUtc                  = (Get-Date).ToUniversalTime().ToString('O')
        namespace                     = $manifest.namespace
        projectId                     = $projId
        serverCertThumbprint          = $serverThumbprint
        clientPfxPath                 = $clientPfxPath
        applicationId                 = $appId
        iisSiteId                     = $createdSite.Id
        iisSiteName                   = $createdSite.Name
        iisBindings                   = $createdSite.Bindings
        v1CommitSha                   = $commitV1
        v2CommitSha                   = $commitV2
        v2Version                     = $liveRoot2.version
        persistentDataMarker          = $originalMarker
        artifactMode                  = 'LOCAL TEST ARTIFACT (ManualSignedPackage)'
        gitHubLivePollTested          = $false
        provesAutonomousGitHubPolling = $false
        checks                        = @($checks)
    }
    $evidencePath = Join-Path $manifest.diagnosticsDir 'deployment-evidence.json'
    $stateOut | ConvertTo-Json -Depth 6 | Set-Content -Path $evidencePath -Encoding UTF8
    Write-E2ELog "TEST A PASSED ($($checks.Count) checks). Evidence saved to $evidencePath" 'PASS' $logFile
}
finally {
    $clientCert.Dispose()
}
exit 0
