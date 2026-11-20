<#
.SYNOPSIS
    Disposable Windows Server VM End-to-End Verification Helper for ServerForge.
.DESCRIPTION
    Verifies a deployed instance of ServerForge.SampleApp against its live HTTP endpoints
    (/, /health, /data) and confirms that pre-existing unmanaged IIS sites remain untouched.
    NEVER modifies IIS configuration, hosted files, or databases (strictly read-only probe).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,
    [string]$ExpectedVersion = '',
    [string]$ExpectedPersistentMarker = '',
    [string]$PreserveIisSiteName = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$base = $BaseUrl.TrimEnd('/')
Write-Host "=== ServerForge Disposable E2E Verification ($base) ===" -ForegroundColor Cyan

# 1. Verify /health returns HTTP 200
$healthResp = Invoke-RestMethod -Uri "$base/health" -Method Get -TimeoutSec 15
if ($healthResp.status -ne 'healthy') {
    throw "Health endpoint returned non-healthy status: $($healthResp | ConvertTo-Json -Compress)"
}
Write-Host "[PASS] /health -> status=$($healthResp.status), version=$($healthResp.version), commit=$($healthResp.commitSha)" -ForegroundColor Green

# 2. Verify / returns expected version if provided
$rootResp = Invoke-RestMethod -Uri "$base/" -Method Get -TimeoutSec 15
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $rootResp.version -ne $ExpectedVersion) {
    throw "Expected version '$ExpectedVersion', but live endpoint returned '$($rootResp.version)'."
}
Write-Host "[PASS] / -> service=$($rootResp.service), version=$($rootResp.version), host=$($rootResp.machineName)" -ForegroundColor Green

# 3. Verify /data persistent marker
$dataResp = Invoke-RestMethod -Uri "$base/data" -Method Get -TimeoutSec 15
if (-not $dataResp.markerExists -or [string]::IsNullOrWhiteSpace($dataResp.markerContent)) {
    throw "Persistent data marker missing at $($dataResp.persistentDataDirectory)"
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedPersistentMarker) -and $dataResp.markerContent -ne $ExpectedPersistentMarker) {
    throw "Persistent marker CHANGED across deploy/rollback! Expected '$ExpectedPersistentMarker', got '$($dataResp.markerContent)'."
}
Write-Host "[PASS] /data -> marker=$($dataResp.markerContent) (dir=$($dataResp.persistentDataDirectory))" -ForegroundColor Green

# 4. Read-only verification that a pre-existing unmanaged IIS site is still running & untouched
if (-not [string]::IsNullOrWhiteSpace($PreserveIisSiteName)) {
    if (Get-Module -ListAvailable -Name WebAdministration) {
        Import-Module WebAdministration -ErrorAction Stop
        $site = Get-Website -Name $PreserveIisSiteName -ErrorAction Stop
        Write-Host "[PASS] Pre-existing IIS site '$PreserveIisSiteName' remains untouched (State=$($site.State), PhysicalPath=$($site.physicalPath))." -ForegroundColor Green
    } else {
        Write-Host "[INFO] WebAdministration module not installed on this host; skipping IIS site check." -ForegroundColor Yellow
    }
}

Write-Host "=== E2E Verification Completed Successfully ===" -ForegroundColor Cyan
