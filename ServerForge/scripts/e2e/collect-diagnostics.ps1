<#
.SYNOPSIS
    Phase 12B Sanitized Windows Event Log, IIS & Agent Diagnostics Collector.
.DESCRIPTION
    Collects actionable diagnostics into the specified output directory while strictly
    scrubbing any GitHub PATs, pairing codes, passwords, or secrets before saving.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Continue'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

$WorkspaceRoot = Resolve-E2EAbsolutePath $WorkspaceRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $WorkspaceRoot 'diagnostics-bundle'
} else {
    $OutputDir = Resolve-E2EAbsolutePath $OutputDir
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$manifestPath = Join-Path $WorkspaceRoot 'e2e-manifest.json'
$manifest = if (Test-Path $manifestPath) { Get-Content $manifestPath -Raw | ConvertFrom-Json } else { $null }

# 1. Copy preflight inventory, manifest, evidence, and harness logs (redacted)
foreach ($f in @('preflight-inventory.json', 'e2e-manifest.json')) {
    $src = Join-Path $WorkspaceRoot $f
    if (Test-Path $src) {
        $safe = Protect-SanitizedText -Text (Get-Content $src -Raw)
        Set-Content -Path (Join-Path $OutputDir $f) -Value $safe -Encoding UTF8
    }
}

$diagDir = Join-Path $WorkspaceRoot 'diagnostics'
if (Test-Path $diagDir) {
    foreach ($item in @(Get-ChildItem -Path $diagDir -File -ErrorAction SilentlyContinue)) {
        $safe = Protect-SanitizedText -Text (Get-Content $item.FullName -Raw)
        Set-Content -Path (Join-Path $OutputDir $item.Name) -Value $safe -Encoding UTF8
    }
}

# 2. Copy Agent logs (strictly redacted; NEVER copy pairing-code.txt or .pfx private keys)
if ($null -ne $manifest -and (Test-Path $manifest.agentDataRoot)) {
    $agentLogs = Join-Path $manifest.agentDataRoot 'logs'
    if (Test-Path $agentLogs) {
        foreach ($lf in @(Get-ChildItem -Path $agentLogs -Filter 'agent-*.log' -File -ErrorAction SilentlyContinue)) {
            $safe = Protect-SanitizedText -Text (Get-Content $lf.FullName -Raw)
            Set-Content -Path (Join-Path $OutputDir $lf.Name) -Value $safe -Encoding UTF8
        }
    }
}

# 3. Capture IIS sites & AppPools snapshot
$iisSnapshot = [ordered]@{
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
    sites         = @(Get-IisSitesSnapshot)
    appPools      = @(Get-IisAppPoolsSnapshot)
}
$iisSnapshot | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutputDir 'iis-snapshot.json') -Encoding UTF8

# 4. Capture Windows Event Logs (Application, System SCM, IIS/AspNetCore)
try {
    $appEvents = @(Get-WinEvent -LogName Application -MaxEvents 40 -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'ServerForge|IIS|AspNetCore|\.NET Runtime|Application Error' })
    if ($appEvents.Count -gt 0) {
        $txt = ($appEvents | Format-List TimeCreated, ProviderName, LevelDisplayName, Message | Out-String)
        Set-Content -Path (Join-Path $OutputDir 'windows-application-events.txt') -Value (Protect-SanitizedText -Text $txt) -Encoding UTF8
    }

    $scmEvents = @(Get-WinEvent -LogName System -MaxEvents 40 -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -match 'Service Control Manager|WAS|W3SVC' -and $_.Message -match 'ServerForge|W3SVC' })
    if ($scmEvents.Count -gt 0) {
        $txt = ($scmEvents | Format-List TimeCreated, ProviderName, LevelDisplayName, Message | Out-String)
        Set-Content -Path (Join-Path $OutputDir 'windows-system-events.txt') -Value (Protect-SanitizedText -Text $txt) -Encoding UTF8
    }
} catch { }

Write-Host "Sanitized diagnostics bundle collected at: $OutputDir"
exit 0
