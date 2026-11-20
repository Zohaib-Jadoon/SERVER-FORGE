<#
.SYNOPSIS
    Phase 12B Preflight Inventory & Safety Validator for Disposable Windows IIS E2E Tests.
.DESCRIPTION
    Verifies Administrator privileges, Windows IIS + AspNetCoreModuleV2 availability,
    inventories pre-existing IIS sites/AppPools/ports, and aborts if production ServerForge
    services or conflicting unmanaged resources are detected. NEVER installs Windows roles
    or alters host-wide IIS settings.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunId,
    [string]$WorkspaceRoot = '',
    [int]$AgentPort = 19443,
    [int]$AppPort = 15199,
    [switch]$AllowProductionHostOverride
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

$safeRunId = ($RunId -replace '[^A-Za-z0-9_-]', '')
if ([string]::IsNullOrWhiteSpace($safeRunId)) {
    throw 'RunId must contain at least one alphanumeric character.'
}

$namespace = "ServerForge_E2E_$safeRunId"
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path 'C:\Temp' $namespace
}
$WorkspaceRoot = Resolve-E2EAbsolutePath $WorkspaceRoot
$diagDir = Join-Path $WorkspaceRoot 'diagnostics'
New-Item -ItemType Directory -Path $diagDir -Force | Out-Null
$logFile = Join-Path $diagDir 'preflight.log'

Write-E2ELog "=== Phase 12 Preflight Check ($namespace) ===" 'INFO' $logFile

$failures = New-Object System.Collections.Generic.List[string]

# 1. OS & Administrator check
if (-not [Environment]::Is64BitOperatingSystem) {
    $failures.Add('64-bit Windows operating system is required.')
}
if (-not (Test-IsAdministrator)) {
    $failures.Add('Administrator privileges are required to manage disposable IIS sites and Windows Services.')
}

# 2. Production ServerForge protection guard
$prodAgentSvc = Get-Service -Name 'ServerForgeAgent' -ErrorAction SilentlyContinue
$prodIdentity = 'C:\ProgramData\ServerForge\Agent\agent-identity.json'
if (-not $AllowProductionHostOverride) {
    if ($null -ne $prodAgentSvc) {
        $failures.Add("SAFETY ABORT: Production Windows Service 'ServerForgeAgent' (Status=$($prodAgentSvc.Status)) already exists on this host. Refusing E2E execution on a production host.")
    }
    if (Test-Path $prodIdentity) {
        $failures.Add("SAFETY ABORT: Existing production Agent identity detected at '$prodIdentity'. Refusing E2E execution on a production host.")
    }
}

# 3. IIS & AspNetCoreModuleV2 presence check (read-only; NEVER auto-installs Windows roles)
$w3wp = Join-Path $env:SystemRoot 'System32\inetsrv\w3wp.exe'
$appCmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
$ancmV2 = Join-Path $env:SystemRoot 'System32\inetsrv\aspnetcorev2.dll'
$ancmV2Alt = Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
$w3svc = Get-Service -Name 'W3SVC' -ErrorAction SilentlyContinue

$iisInstalled = (Test-Path $w3wp) -and (Test-Path $appCmd)
$w3svcRunning = ($null -ne $w3svc) -and ($w3svc.Status -eq 'Running')
$ancmInstalled = (Test-Path $ancmV2) -or (Test-Path $ancmV2Alt)

if (-not $iisInstalled) {
    $failures.Add("Microsoft IIS is NOT installed on this host (missing '$w3wp' or '$appCmd'). Preflight will NEVER auto-install Windows roles. Provision IIS on a disposable Windows VM first.")
}
if ($iisInstalled -and -not $w3svcRunning) {
    $failures.Add("IIS World Wide Web Publishing Service (W3SVC) is not running (Status=$(if ($w3svc) { $w3svc.Status } else { 'Missing' })).")
}
if ($iisInstalled -and -not $ancmInstalled) {
    $failures.Add('IIS AspNetCoreModuleV2 (.NET Hosting Bundle) is NOT installed. Install the .NET 10 Windows Hosting Bundle on the disposable IIS VM before running IIS E2E tests.')
}

# 4. .NET 10 SDK check (for building ServerForge.SampleApp)
$dotnetCmd = Get-Command 'dotnet' -ErrorAction SilentlyContinue
$dotnetVersion = if ($null -ne $dotnetCmd) { (& dotnet --version 2>$null) } else { '' }
if ([string]::IsNullOrWhiteSpace($dotnetVersion)) {
    $failures.Add('.NET SDK (dotnet) not found in PATH.')
}

# 5. Pre-existing IIS inventory & conflict detection
$existingSites = @(Get-IisSitesSnapshot)
$existingPools = @(Get-IisAppPoolsSnapshot)

$proposedSiteName = $namespace
$proposedPoolName = "${namespace}_Pool"
$proposedSvcName = "${namespace}_Agent"

foreach ($s in $existingSites) {
    if ($s.Name -eq $proposedSiteName) {
        $failures.Add("CONFLICT: IIS site '$proposedSiteName' already exists.")
    }
}
foreach ($p in $existingPools) {
    if ($p.Name -eq $proposedPoolName) {
        $failures.Add("CONFLICT: IIS AppPool '$proposedPoolName' already exists.")
    }
}
if ($null -ne (Get-Service -Name $proposedSvcName -ErrorAction SilentlyContinue)) {
    $failures.Add("CONFLICT: Windows Service '$proposedSvcName' already exists.")
}
if (Test-TcpPortInUse -Port $AgentPort) {
    $failures.Add("CONFLICT: Proposed disposable Agent loopback port $AgentPort is already in use.")
}
if (Test-TcpPortInUse -Port $AppPort) {
    $failures.Add("CONFLICT: Proposed disposable IIS app port $AppPort is already in use.")
}

$inventory = [ordered]@{
    timestampUtc            = (Get-Date).ToUniversalTime().ToString('O')
    machineName             = $env:COMPUTERNAME
    runId                   = $safeRunId
    namespace               = $namespace
    workspaceRoot           = $WorkspaceRoot
    isAdministrator         = (Test-IsAdministrator)
    iisInstalled            = $iisInstalled
    w3svcRunning            = $w3svcRunning
    aspNetCoreModuleV2Found = $ancmInstalled
    dotnetSdkVersion        = $dotnetVersion
    proposedResources       = [ordered]@{
        iisSiteName      = $proposedSiteName
        iisAppPoolName   = $proposedPoolName
        agentServiceName = $proposedSvcName
        agentBindAddress = '127.0.0.1'
        agentPort        = $AgentPort
        appBindAddress   = '127.0.0.1'
        appPort          = $AppPort
    }
    preExistingIisSites     = $existingSites
    preExistingAppPools     = $existingPools
    failures                = @($failures)
    passed                  = ($failures.Count -eq 0)
}

$inventoryPath = Join-Path $WorkspaceRoot 'preflight-inventory.json'
$inventory | ConvertTo-Json -Depth 6 | Set-Content -Path $inventoryPath -Encoding UTF8

Write-E2ELog "Preflight inventory saved: $inventoryPath (PreExistingSites=$($existingSites.Count), PreExistingPools=$($existingPools.Count))" 'INFO' $logFile

if ($failures.Count -gt 0) {
    foreach ($f in $failures) {
        Write-E2ELog $f 'FAIL' $logFile
    }
    Write-E2ELog "Preflight FAILED with $($failures.Count) blocking issue(s)." 'FAIL' $logFile
    exit 1
}

Write-E2ELog "Preflight PASSED with zero conflicts. Ready for setup-disposable-iis.ps1." 'PASS' $logFile
exit 0
