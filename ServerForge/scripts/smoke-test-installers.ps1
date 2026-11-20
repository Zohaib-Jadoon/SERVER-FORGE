<#
.SYNOPSIS
    Automated Windows Installer Smoke Test Suite for ServerForge Desktop & ServerForge Agent.
.DESCRIPTION
    Verifies both generated Inno Setup 6 installers on a disposable Windows host/runner:
      - Desktop Installer (8 checks): SHA-256 verification, unrelated process isolation,
        silent install to disposable path, self-contained runtime payload, uninstall registry
        registration, upgrade preservation of %LOCALAPPDATA%\ServerForge, diagnostic log,
        and clean uninstall.
      - Agent Installer (10 checks): SHA-256 verification, silent install with disposable
        service name, Windows Service registration/running/auto-start/failure-recovery,
        ProgramData artifacts (cert/identity/sqlite/pairing-code/logs), loopback-only binding
        (127.0.0.1:9443 — never 0.0.0.0), identity persistence across Restart-Service,
        upgrade preservation of appsettings.json, and clean uninstall preserving
        C:\ServerForgeApps hosted content.
    HONEST SCOPE BOUNDARY:
      This script validates installer execution, self-contained files, Windows Service lifecycle,
      and loopback network posture in unattended CI. It does NOT validate interactive WPF GUI
      rendering or live IIS deployment (see INSTALLATION.md manual Windows 11 & VM checklists).
#>
[CmdletBinding()]
param(
    [string]$InstallerDir = (Join-Path $PSScriptRoot '..\artifacts\installers'),
    [string]$LogOutputDir = (Join-Path $PSScriptRoot '..\artifacts\smoke-logs'),
    [string]$DisposableRoot = 'C:\Temp\ServerForgeSmoke',
    [string]$DisposableAgentServiceName = 'ServerForgeAgentSmoke'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

New-Item -ItemType Directory -Path $LogOutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $DisposableRoot -Force | Out-Null

$summaryLog = Join-Path $LogOutputDir 'smoke-test-summary.log'
$script:PassedChecks = 0
$script:FailedChecks = 0

function Write-CheckLog {
    param([string]$Message, [string]$Level = 'INFO')
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] [$Level] $Message"
    Write-Host $line
    Add-Content -Path $summaryLog -Value $line
}

function Assert-Condition {
    param([bool]$Condition, [string]$CheckName, [string]$Detail = '')
    if ($Condition) {
        $script:PassedChecks++
        Write-CheckLog "PASS: $CheckName $(if ($Detail) { "($Detail)" })" 'PASS'
    } else {
        $script:FailedChecks++
        Write-CheckLog "FAIL: $CheckName $(if ($Detail) { "($Detail)" })" 'FAIL'
        throw "Smoke test check failed: $CheckName $Detail"
    }
}

Write-CheckLog '=================================================================='
Write-CheckLog 'ServerForge Phase 11 — Automated Windows Installer Smoke Test Suite'
Write-CheckLog 'Scope: Installer execution, self-contained runtime, service & loopback verification.'
Write-CheckLog 'Note: Interactive WPF GUI rendering is NOT claimed by unattended Windows CI.'
Write-CheckLog '=================================================================='

$desktopSetup = Join-Path $InstallerDir 'ServerForge-Desktop-Setup.exe'
$agentSetup = Join-Path $InstallerDir 'ServerForge-Agent-Setup.exe'
$checksumsFile = Join-Path $InstallerDir 'SHA256SUMS.txt'

Assert-Condition (Test-Path $desktopSetup) 'Artifacts.DesktopInstallerExists' $desktopSetup
Assert-Condition (Test-Path $agentSetup) 'Artifacts.AgentInstallerExists' $agentSetup
Assert-Condition (Test-Path $checksumsFile) 'Artifacts.ChecksumsFileExists' $checksumsFile

# Verify SHA-256 checksums match actual binaries
$checksumLines = Get-Content $checksumsFile | Where-Object { $_ -match '\S' }
foreach ($line in $checksumLines) {
    if ($line -match '^([0-9A-Fa-f]{64})\s+\*?(.+)$') {
        $expectedHash = $matches[1].ToUpperInvariant()
        $fileName = $matches[2].Trim()
        $targetPath = Join-Path $InstallerDir $fileName
        Assert-Condition (Test-Path $targetPath) "Checksum.FilePresent.$fileName" $targetPath
        $actualHash = (Get-FileHash -Path $targetPath -Algorithm SHA256).Hash.ToUpperInvariant()
        Assert-Condition ($actualHash -eq $expectedHash) "Checksum.Match.$fileName" $actualHash
    }
}

# Launch a benign unrelated background process to verify CloseApplicationsFilter NEVER terminates unrelated apps
$canaryProc = Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 300' -PassThru -WindowStyle Hidden
Write-CheckLog "Started unrelated canary process PID=$($canaryProc.Id) to verify CloseApplicationsFilter safety."

try {
    # =========================================================================
    # PART 1 — DESKTOP INSTALLER SMOKE TESTS (8 CHECKS)
    # =========================================================================
    $desktopInstallDir = Join-Path $DisposableRoot 'DesktopApp'
    $desktopInstallLog = Join-Path $LogOutputDir 'desktop-install.log'
    $desktopUpgradeLog = Join-Path $LogOutputDir 'desktop-upgrade.log'
    $desktopUninstallLog = Join-Path $LogOutputDir 'desktop-uninstall.log'
    $desktopLocalAppData = Join-Path $env:LOCALAPPDATA 'ServerForge'
    $desktopMarkerFile = Join-Path $desktopLocalAppData 'smoke-upgrade-marker.txt'

    Write-CheckLog '--- PART 1: ServerForge Desktop Installer ---'

    # 1. Clean silent install to disposable directory
    $p = Start-Process -FilePath $desktopSetup -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=`"$desktopInstallDir`"",
        "/LOG=`"$desktopInstallLog`""
    ) -Wait -PassThru
    Assert-Condition ($p.ExitCode -eq 0) 'Desktop.1_SilentInstallSucceeded' "ExitCode=$($p.ExitCode)"
    Assert-Condition (Test-Path $desktopInstallLog) 'Desktop.2_InstallerDiagnosticLogWritten' $desktopInstallLog

    # 3. Verify self-contained native WPF binary and runtime files exist
    $desktopExe = Join-Path $desktopInstallDir 'ServerForge.Desktop.exe'
    $coreClrDll = Join-Path $desktopInstallDir 'coreclr.dll'
    $wpfDll = Join-Path $desktopInstallDir 'PresentationFramework.dll'
    $uninsDesktop = Join-Path $desktopInstallDir 'unins000.exe'
    Assert-Condition (Test-Path $desktopExe) 'Desktop.3_MainExecutablePresent' $desktopExe
    Assert-Condition ((Test-Path $coreClrDll) -and (Test-Path $wpfDll)) 'Desktop.4_SelfContainedNet10WpfRuntimePresent' "coreclr.dll & PresentationFramework.dll"
    Assert-Condition (Test-Path $uninsDesktop) 'Desktop.5_UninstallerPresent' $uninsDesktop

    # 6. Verify uninstall registry registration exists
    $desktopRegPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0001}_is1',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0001}_is1'
    )
    $desktopRegFound = $desktopRegPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    Assert-Condition ($null -ne $desktopRegFound) 'Desktop.6_UninstallRegistryEntryCreated' "$desktopRegFound"

    # 7. Verify upgrade preserves %LOCALAPPDATA%\ServerForge user data and does NOT terminate unrelated canary process
    New-Item -ItemType Directory -Path $desktopLocalAppData -Force | Out-Null
    Set-Content -Path $desktopMarkerFile -Value 'preserve-me-across-upgrade'
    $pUp = Start-Process -FilePath $desktopSetup -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=`"$desktopInstallDir`"",
        "/LOG=`"$desktopUpgradeLog`""
    ) -Wait -PassThru
    Assert-Condition ($pUp.ExitCode -eq 0) 'Desktop.7a_InPlaceUpgradeSucceeded' "ExitCode=$($pUp.ExitCode)"
    Assert-Condition ((Test-Path $desktopMarkerFile) -and ((Get-Content $desktopMarkerFile -Raw).Trim() -eq 'preserve-me-across-upgrade')) 'Desktop.7b_UpgradePreservedLocalAppData' $desktopMarkerFile
    Assert-Condition (-not $canaryProc.HasExited) 'Desktop.7c_UnrelatedProcessUntouched' "Canary PID=$($canaryProc.Id) still running"

    # 8. Verify clean silent uninstall removes binaries while preserving user data
    $pUn = Start-Process -FilePath $uninsDesktop -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/LOG=`"$desktopUninstallLog`""
    ) -Wait -PassThru
    Assert-Condition ($pUn.ExitCode -eq 0) 'Desktop.8a_CleanUninstallSucceeded' "ExitCode=$($pUn.ExitCode)"
    Assert-Condition (-not (Test-Path $desktopExe)) 'Desktop.8b_DesktopBinaryRemoved' $desktopExe
    Assert-Condition (Test-Path $desktopMarkerFile) 'Desktop.8c_UninstallPreservedUserData' $desktopMarkerFile
    Remove-Item -Path $desktopMarkerFile -Force -ErrorAction SilentlyContinue

    # =========================================================================
    # PART 2 — AGENT INSTALLER SMOKE TESTS (10 CHECKS)
    # =========================================================================
    $agentInstallDir = Join-Path $DisposableRoot 'AgentApp'
    $agentInstallLog = Join-Path $LogOutputDir 'agent-install.log'
    $agentUpgradeLog = Join-Path $LogOutputDir 'agent-upgrade.log'
    $agentUninstallLog = Join-Path $LogOutputDir 'agent-uninstall.log'
    $agentProgramData = 'C:\ProgramData\ServerForge\Agent'
    $hostedAppMarkerDir = 'C:\ServerForgeApps\smoke-hosted-app'
    $hostedAppMarkerFile = Join-Path $hostedAppMarkerDir 'index.html'

    Write-CheckLog '--- PART 2: ServerForge Agent Installer ---'

    # Pre-create a simulated hosted site under C:\ServerForgeApps to verify uninstall NEVER deletes hosted apps
    New-Item -ItemType Directory -Path $hostedAppMarkerDir -Force | Out-Null
    Set-Content -Path $hostedAppMarkerFile -Value '<h1>ServerForge Hosted App Preservation Check</h1>'

    # 1. Silent install with disposable service name
    $aInst = Start-Process -FilePath $agentSetup -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=`"$agentInstallDir`"",
        "/SERVICENAME=$DisposableAgentServiceName",
        "/LOG=`"$agentInstallLog`""
    ) -Wait -PassThru
    Assert-Condition ($aInst.ExitCode -eq 0) 'Agent.1_SilentInstallSucceeded' "ExitCode=$($aInst.ExitCode)"
    Assert-Condition (Test-Path $agentInstallLog) 'Agent.2_InstallerDiagnosticLogWritten' $agentInstallLog

    # 3. Verify self-contained Agent binary & Windows Service registration + Running status + Recovery policy
    $agentExe = Join-Path $agentInstallDir 'ServerForge.Agent.exe'
    $uninsAgent = Join-Path $agentInstallDir 'unins000.exe'
    Assert-Condition ((Test-Path $agentExe) -and (Test-Path (Join-Path $agentInstallDir 'coreclr.dll'))) 'Agent.3_SelfContainedAgentBinaryPresent' $agentExe

    $svc = Get-Service -Name $DisposableAgentServiceName -ErrorAction Stop
    Assert-Condition ($svc.Status -eq 'Running') 'Agent.4a_WindowsServiceRunning' "Service=$($svc.Name) Status=$($svc.Status)"
    $svcWmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$DisposableAgentServiceName'"
    Assert-Condition ($svcWmi.StartMode -eq 'Auto') 'Agent.4b_WindowsServiceAutoStart' "StartMode=$($svcWmi.StartMode)"
    $qfailure = (& sc.exe qfailure $DisposableAgentServiceName | Out-String)
    Assert-Condition ($qfailure -match 'RESTART') 'Agent.4c_WindowsServiceRecoveryConfigured' 'sc qfailure includes RESTART actions'

    # 5. Wait up to 20s for Agent first-boot artifacts (cert, identity, sqlite, pairing-code, logs)
    $deadline = (Get-Date).AddSeconds(20)
    $pairingFile = Join-Path $agentProgramData 'pairing-code.txt'
    $identityFile = Join-Path $agentProgramData 'agent-identity.json'
    $certFile = Join-Path $agentProgramData 'agent-server.pfx'
    $dbFile = Join-Path $agentProgramData 'agent-state.db'
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $pairingFile) -and (Test-Path $identityFile) -and (Test-Path $certFile) -and (Test-Path $dbFile)) { break }
        Start-Sleep -Milliseconds 500
    }
    Assert-Condition ((Test-Path $pairingFile) -and (Test-Path $identityFile) -and (Test-Path $certFile) -and (Test-Path $dbFile)) `
        'Agent.5_FirstBootArtifactsGenerated' 'pairing-code.txt, agent-identity.json, agent-server.pfx, agent-state.db'

    # Copy agent log snippet to smoke-logs for CI visibility
    $agentLogs = @(Get-ChildItem -Path (Join-Path $agentProgramData 'logs') -Filter 'agent-*.log' -ErrorAction SilentlyContinue)
    Assert-Condition ($agentLogs.Count -gt 0) 'Agent.6_AgentRuntimeLogsCreated' "LogFiles=$($agentLogs.Count)"

    # 7. Verify Agent listens ONLY on loopback 127.0.0.1:9443 by default (NEVER public 0.0.0.0 or ::)
    $listeners = @(Get-NetTCPConnection -LocalPort 9443 -State Listen -ErrorAction SilentlyContinue)
    Assert-Condition ($listeners.Count -gt 0) 'Agent.7a_ListeningOnPort9443' "Listeners=$($listeners.Count)"
    $nonLoopback = @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') })
    Assert-Condition ($nonLoopback.Count -eq 0) 'Agent.7b_LoopbackOnlySecurityPosture' "BoundAddresses=$(($listeners.LocalAddress) -join ',')"

    # 8. Verify Restart-Service preserves Agent identity & certificate thumbprint
    $identityBefore = (Get-Content $identityFile -Raw | ConvertFrom-Json).AgentId
    $certHashBefore = (Get-FileHash -Path $certFile -Algorithm SHA256).Hash
    Restart-Service -Name $DisposableAgentServiceName -Force
    Start-Sleep -Seconds 3
    $svcAfterRestart = Get-Service -Name $DisposableAgentServiceName
    Assert-Condition ($svcAfterRestart.Status -eq 'Running') 'Agent.8a_ServiceSurvivedRestart' "Status=$($svcAfterRestart.Status)"
    $identityAfter = (Get-Content $identityFile -Raw | ConvertFrom-Json).AgentId
    $certHashAfter = (Get-FileHash -Path $certFile -Algorithm SHA256).Hash
    Assert-Condition (($identityBefore -eq $identityAfter) -and ($certHashBefore -eq $certHashAfter)) `
        'Agent.8b_IdentityAndCertPreservedAcrossRestart' "AgentId=$identityAfter"

    # 9. Verify upgrade preserves appsettings.json customizations and keeps service running
    $appSettingsPath = Join-Path $agentInstallDir 'appsettings.json'
    $settingsObj = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $settingsObj | Add-Member -NotePropertyName 'SmokeUpgradeMarker' -NotePropertyValue 'preserved-on-upgrade' -Force
    $settingsObj | ConvertTo-Json -Depth 10 | Set-Content -Path $appSettingsPath -Encoding UTF8

    $aUp = Start-Process -FilePath $agentSetup -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/DIR=`"$agentInstallDir`"",
        "/SERVICENAME=$DisposableAgentServiceName",
        "/LOG=`"$agentUpgradeLog`""
    ) -Wait -PassThru
    Assert-Condition ($aUp.ExitCode -eq 0) 'Agent.9a_InPlaceUpgradeSucceeded' "ExitCode=$($aUp.ExitCode)"
    $settingsAfterUpgrade = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    Assert-Condition ($settingsAfterUpgrade.SmokeUpgradeMarker -eq 'preserved-on-upgrade') 'Agent.9b_UpgradePreservedAppSettings' $appSettingsPath
    $svcAfterUpgrade = Get-Service -Name $DisposableAgentServiceName
    Assert-Condition ($svcAfterUpgrade.Status -eq 'Running') 'Agent.9c_ServiceRunningAfterUpgrade' "Status=$($svcAfterUpgrade.Status)"
    Assert-Condition (-not $canaryProc.HasExited) 'Agent.9d_UnrelatedProcessUntouched' "Canary PID=$($canaryProc.Id) still running"

    # 10. Clean uninstall removes Agent service & binaries while preserving C:\ServerForgeApps hosted files
    $aUn = Start-Process -FilePath $uninsAgent -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        "/LOG=`"$agentUninstallLog`""
    ) -Wait -PassThru
    Assert-Condition ($aUn.ExitCode -eq 0) 'Agent.10a_CleanUninstallSucceeded' "ExitCode=$($aUn.ExitCode)"
    Start-Sleep -Seconds 2
    $svcGone = Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue
    Assert-Condition ($null -eq $svcGone) 'Agent.10b_WindowsServiceRemoved' $DisposableAgentServiceName
    Assert-Condition (-not (Test-Path $agentExe)) 'Agent.10c_AgentBinaryRemoved' $agentExe
    Assert-Condition (Test-Path $hostedAppMarkerFile) 'Agent.10d_HostedAppsPreservedOnUninstall' $hostedAppMarkerFile

    # Copy agent runtime log before cleanup for CI artifact inspection
    foreach ($lf in $agentLogs) {
        Copy-Item -Path $lf.FullName -Destination (Join-Path $LogOutputDir $lf.Name) -Force -ErrorAction SilentlyContinue
    }

    Write-CheckLog "=================================================================="
    Write-CheckLog "ALL INSTALLER SMOKE TESTS PASSED: $($script:PassedChecks) passed, $($script:FailedChecks) failed."
    Write-CheckLog "=================================================================="
}
finally {
    if ($canaryProc -and -not $canaryProc.HasExited) {
        Stop-Process -Id $canaryProc.Id -Force -ErrorAction SilentlyContinue
    }
    # Clean up disposable smoke marker directory only
    Remove-Item -Path $hostedAppMarkerDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $DisposableRoot -Recurse -Force -ErrorAction SilentlyContinue
}
