<#
.SYNOPSIS
    Phase 12.1 Owned-Only Disposable IIS & Agent Cleanup Script.
.DESCRIPTION
    Removes ONLY resources listed in e2e-manifest.json whose names begin with ServerForge_E2E_<RunId>
    (covering both TEST A and optional TEST B disposable IIS sites and AppPools).
    Preserves all pre-existing IIS sites, AppPools, and diagnostics bundles (if -PreserveDiagnosticsDir is set).
    Exits non-zero if cleanup fails while retaining diagnostics for inspection.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,
    [string]$PreserveDiagnosticsDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

$WorkspaceRoot = Resolve-E2EAbsolutePath $WorkspaceRoot
$manifestPath = Join-Path $WorkspaceRoot 'e2e-manifest.json'
if (-not (Test-Path $manifestPath)) {
    Write-Host "No e2e-manifest.json found at '$WorkspaceRoot'. Nothing to clean up."
    exit 0
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$namespace = [string]$manifest.namespace
$preflight = if (Test-Path $manifest.preflightInventory) { Get-Content $manifest.preflightInventory -Raw | ConvertFrom-Json } else { $null }

$ownedSites = New-Object System.Collections.Generic.List[string]
$ownedPools = New-Object System.Collections.Generic.List[string]
$ownedSites.Add([string]$manifest.iisSiteName)
$ownedPools.Add([string]$manifest.iisAppPoolName)

if ($null -ne $manifest.PSObject.Properties['ghIisSiteName'] -and -not [string]::IsNullOrWhiteSpace([string]$manifest.ghIisSiteName)) {
    $ownedSites.Add([string]$manifest.ghIisSiteName)
}
if ($null -ne $manifest.PSObject.Properties['ghIisAppPoolName'] -and -not [string]::IsNullOrWhiteSpace([string]$manifest.ghIisAppPoolName)) {
    $ownedPools.Add([string]$manifest.ghIisAppPoolName)
}

# Strict ownership validation before touching ANY resource
foreach ($sn in $ownedSites) { Assert-OwnedE2EName -ResourceName $sn -ExpectedPrefix $namespace }
foreach ($pn in $ownedPools) { Assert-OwnedE2EName -ResourceName $pn -ExpectedPrefix $namespace }
Assert-OwnedE2EName -ResourceName ([string]$manifest.agentServiceName) -ExpectedPrefix $namespace

# Preserve diagnostics bundle outside WorkspaceRoot if requested
if (-not [string]::IsNullOrWhiteSpace($PreserveDiagnosticsDir)) {
    & (Join-Path $PSScriptRoot 'collect-diagnostics.ps1') -WorkspaceRoot $WorkspaceRoot -OutputDir $PreserveDiagnosticsDir
}

$cleanupErrors = New-Object System.Collections.Generic.List[string]
$appCmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'

# 1. Remove ONLY the owned disposable IIS sites & AppPools
if (Test-Path $appCmd) {
    foreach ($sn in $ownedSites) {
        $siteExists = & $appCmd list site $sn 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($siteExists -join ''))) {
            & $appCmd stop site $sn 2>$null | Out-Null
            & $appCmd delete site $sn | Out-Null
            if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add("Failed to delete disposable IIS site '$sn'.") }
        }
    }

    foreach ($pn in $ownedPools) {
        $poolExists = & $appCmd list apppool $pn 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($poolExists -join ''))) {
            & $appCmd stop apppool $pn 2>$null | Out-Null
            Start-Sleep -Milliseconds 500
            & $appCmd delete apppool $pn | Out-Null
            if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add("Failed to delete disposable IIS AppPool '$pn'.") }
        }
    }
}

# 2. Stop & uninstall ONLY the owned disposable Agent Windows Service
$svc = Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue
if ($null -ne $svc) {
    try { Stop-Service -Name $manifest.agentServiceName -Force -ErrorAction SilentlyContinue } catch { }
    $uninsExe = Get-ChildItem -Path $manifest.agentInstallDir -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $uninsExe) {
        $uLog = Join-Path ([System.IO.Path]::GetTempPath()) ("sf-e2e-agent-unins-$($manifest.runId).log")
        [void](Start-Process -FilePath $uninsExe.FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$uLog`"") -WorkingDirectory $env:TEMP -Wait -PassThru)
        [void](Wait-E2ECondition -Condition { $null -eq (Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue) } -TimeoutSeconds 25)
    }
    if ($null -ne (Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue)) {
        & sc.exe delete $manifest.agentServiceName | Out-Null
    }
    $removed = Wait-E2ECondition -Condition { $null -eq (Get-Service -Name $manifest.agentServiceName -ErrorAction SilentlyContinue) } -TimeoutSeconds 20
    if (-not $removed) { $cleanupErrors.Add("Disposable service '$($manifest.agentServiceName)' still present after cleanup.") }
}

# 3. Verify pre-existing IIS sites remain intact
if ($null -ne $preflight) {
    $currentSites = @(Get-IisSitesSnapshot)
    foreach ($orig in @($preflight.preExistingIisSites)) {
        $match = $currentSites | Where-Object { $_.Name -eq $orig.Name } | Select-Object -First 1
        if ($null -eq $match) {
            $cleanupErrors.Add("UNEXPECTED: Pre-existing IIS site '$($orig.Name)' is missing after cleanup!")
        }
    }
}

# 4. Remove ONLY the owned disposable WorkspaceRoot (only if no cleanup errors so diagnostics survive on failure)
if ($cleanupErrors.Count -eq 0) {
    if ($WorkspaceRoot -match 'ServerForge_E2E_' -and (Test-Path $WorkspaceRoot)) {
        try {
            Remove-Item -Path $WorkspaceRoot -Recurse -Force -ErrorAction Stop
        } catch {
            $cleanupErrors.Add("Failed removing workspace directory '$WorkspaceRoot': $($_.Exception.Message)")
        }
    }
}

if ($cleanupErrors.Count -gt 0) {
    foreach ($e in $cleanupErrors) { Write-Host "[FAIL] $e" -ForegroundColor Red }
    Write-Host "Diagnostics retained for investigation at: $WorkspaceRoot $(if ($PreserveDiagnosticsDir) { "and $PreserveDiagnosticsDir" })" -ForegroundColor Yellow
    exit 1
}

Write-Host "[PASS] Disposable IIS E2E resources for '$namespace' cleaned up cleanly; pre-existing sites untouched." -ForegroundColor Green
exit 0
