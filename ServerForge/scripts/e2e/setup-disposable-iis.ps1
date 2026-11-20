<#
.SYNOPSIS
    Phase 12.1 Disposable IIS & Agent Environment Setup (ServerForge_E2E_<RunId>).
.DESCRIPTION
    Creates ONLY test-owned disposable resources inside an isolated workspace:
    - Installs isolated ServerForge Agent Windows Service (ServerForge_E2E_<RunId>_Agent)
      bound exclusively to 127.0.0.1:<AgentPort> with isolated DataRoot & AppsRoot.
    - Writes ownership manifest e2e-manifest.json for both TEST A (local IIS artifact)
      and TEST B (genuine autonomous GitHub Actions polling).
    - Never modifies pre-existing IIS sites, AppPools, or production ServerForge directories.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunId,
    [Parameter(Mandatory = $true)]
    [string]$AgentInstallerPath,
    [string]$WorkspaceRoot = '',
    [int]$AgentPort = 19443,
    [int]$AppPort = 15199,
    [int]$GitHubAppPort = 15200,
    [switch]$AllowProductionHostOverride
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

$safeRunId = ($RunId -replace '[^A-Za-z0-9_-]', '')
$namespace = "ServerForge_E2E_$safeRunId"
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path 'C:\Temp' $namespace
}
$WorkspaceRoot = Resolve-E2EAbsolutePath $WorkspaceRoot
$AgentInstallerPath = Resolve-E2EAbsolutePath $AgentInstallerPath

if (-not (Test-Path $AgentInstallerPath)) {
    throw "Agent installer not found: $AgentInstallerPath"
}

# 1. Run preflight first
$preflightArgs = @{
    RunId         = $safeRunId
    WorkspaceRoot = $WorkspaceRoot
    AgentPort     = $AgentPort
    AppPort       = $AppPort
    GitHubAppPort = $GitHubAppPort
}
if ($AllowProductionHostOverride) { $preflightArgs['AllowProductionHostOverride'] = $true }
& (Join-Path $PSScriptRoot 'preflight.ps1') @preflightArgs
if ($LASTEXITCODE -ne 0) {
    throw "Preflight failed with exit code $LASTEXITCODE; aborting setup."
}

$diagDir = Join-Path $WorkspaceRoot 'diagnostics'
$agentInstallDir = Join-Path $WorkspaceRoot 'AgentApp'
$agentDataRoot = Join-Path $WorkspaceRoot 'AgentData'
$agentAppsRoot = Join-Path $WorkspaceRoot 'AppsRoot'
$desktopSimDir = Join-Path $WorkspaceRoot 'DesktopIdentity'
$artifactsDir = Join-Path $WorkspaceRoot 'Artifacts'
$logFile = Join-Path $diagDir 'setup.log'

foreach ($d in @($diagDir, $agentInstallDir, $agentDataRoot, $agentAppsRoot, $desktopSimDir, $artifactsDir)) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}

$agentSvcName = "${namespace}_Agent"
$iisSiteName = $namespace
$iisPoolName = "${namespace}_Pool"
$ghIisSiteName = "${namespace}_GH"
$ghIisPoolName = "${namespace}_GH_Pool"

Assert-OwnedE2EName -ResourceName $agentSvcName -ExpectedPrefix $namespace
Assert-OwnedE2EName -ResourceName $iisSiteName -ExpectedPrefix $namespace
Assert-OwnedE2EName -ResourceName $iisPoolName -ExpectedPrefix $namespace
Assert-OwnedE2EName -ResourceName $ghIisSiteName -ExpectedPrefix $namespace
Assert-OwnedE2EName -ResourceName $ghIisPoolName -ExpectedPrefix $namespace

# 2. Pre-seed isolated appsettings.json BEFORE silent install so ServerForge-Agent-Setup.exe preserves it
$appSettingsPath = Join-Path $agentInstallDir 'appsettings.json'
$agentConfig = [ordered]@{
    Agent = [ordered]@{
        ServiceName                = $agentSvcName
        BindAddress                = '127.0.0.1'
        Port                       = $AgentPort
        DataRoot                   = $agentDataRoot
        AppsRoot                   = $agentAppsRoot
        GitHubPollIntervalSeconds  = 15
        RequireVpnOrPrivateNetwork = $true
    }
    Serilog = [ordered]@{
        MinimumLevel = [ordered]@{
            Default = 'Information'
        }
    }
}
$agentConfig | ConvertTo-Json -Depth 6 | Set-Content -Path $appSettingsPath -Encoding UTF8
Write-E2ELog "Wrote isolated Agent appsettings.json at $appSettingsPath (127.0.0.1:$AgentPort, Poll=15s)" 'INFO' $logFile

# 3. Install disposable Agent Windows Service via Inno Setup with absolute /LOG path
$installLog = Join-Path $diagDir 'agent-e2e-install.log'
$installArgs = @(
    '/VERYSILENT',
    '/SUPPRESSMSGBOXES',
    '/NORESTART',
    "/DIR=`"$agentInstallDir`"",
    "/SERVICENAME=$agentSvcName",
    "/LOG=`"$installLog`""
)
Write-E2ELog "Installing disposable Agent service '$agentSvcName'..." 'INFO' $logFile
$proc = Start-Process -FilePath $AgentInstallerPath -ArgumentList $installArgs -WorkingDirectory (Split-Path $AgentInstallerPath -Parent) -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    throw "Disposable Agent installer failed with exit code $($proc.ExitCode). Inspect $installLog."
}

# 4. Wait for Windows Service running + loopback port listening + pairing-code.txt generated
$svcReady = Wait-E2ECondition -Condition {
    $s = Get-Service -Name $agentSvcName -ErrorAction SilentlyContinue
    $null -ne $s -and $s.Status -eq 'Running'
} -TimeoutSeconds 30
if (-not $svcReady) {
    throw "Disposable Agent service '$agentSvcName' did not reach Running state within 30s."
}

$portReady = Wait-E2ECondition -Condition { Test-TcpPortInUse -Port $AgentPort } -TimeoutSeconds 30
if (-not $portReady) {
    throw "Disposable Agent service '$agentSvcName' did not bind loopback port 127.0.0.1:$AgentPort within 30s."
}

$pairingFile = Join-Path $agentDataRoot 'pairing-code.txt'
$pairingReady = Wait-E2ECondition -Condition { Test-Path $pairingFile } -TimeoutSeconds 30
if (-not $pairingReady) {
    throw "Disposable Agent did not generate pairing-code.txt at '$pairingFile' within 30s."
}

# Verify Agent is bound ONLY to 127.0.0.1 (never 0.0.0.0)
$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
    Where-Object { $_.Port -eq $AgentPort }
foreach ($ep in $listeners) {
    if ($ep.Address.ToString() -ne '127.0.0.1') {
        throw "SECURITY VIOLATION: Disposable Agent bound to non-loopback address '$($ep.Address):$($ep.Port)'."
    }
}

# 5. Write ownership manifest (NO secrets, NO pairing codes)
$manifest = [ordered]@{
    schemaVersion           = 2
    createdAtUtc            = (Get-Date).ToUniversalTime().ToString('O')
    runId                   = $safeRunId
    namespace               = $namespace
    workspaceRoot           = $WorkspaceRoot
    diagnosticsDir          = $diagDir
    agentInstallDir         = $agentInstallDir
    agentDataRoot           = $agentDataRoot
    agentAppsRoot           = $agentAppsRoot
    desktopIdentityDir      = $desktopSimDir
    artifactsDir            = $artifactsDir
    agentServiceName        = $agentSvcName
    agentBaseUrl            = "https://127.0.0.1:$AgentPort"
    agentPort               = $AgentPort
    agentPollIntervalSec    = 15
    iisSiteName             = $iisSiteName
    iisAppPoolName          = $iisPoolName
    appBaseUrl              = "http://127.0.0.1:$AppPort"
    appPort                 = $AppPort
    ghIisSiteName           = $ghIisSiteName
    ghIisAppPoolName        = $ghIisPoolName
    ghAppBaseUrl            = "http://127.0.0.1:$GitHubAppPort"
    ghAppPort               = $GitHubAppPort
    preflightInventory      = (Join-Path $WorkspaceRoot 'preflight-inventory.json')
}
$manifestPath = Join-Path $WorkspaceRoot 'e2e-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8

Write-E2ELog "Disposable IIS + Agent environment ready. Manifest: $manifestPath" 'PASS' $logFile
exit 0
