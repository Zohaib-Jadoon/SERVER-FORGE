<#
.SYNOPSIS
    Automated Windows Installer Smoke Test Suite for ServerForge Desktop & ServerForge Agent.
.DESCRIPTION
    Verifies both generated Inno Setup 6 installers on a clean disposable Windows host/runner:
      - Normalizes InstallerDir, LogOutputDir, and DisposableRoot to absolute paths before
        launching any installer or uninstaller (Inno Setup's uninstaller clones itself into
        %TEMP% with CWD=%TEMP%, so relative /LOG paths abort with ExitCode=1).
      - Resolves the authoritative registered uninstaller path from Windows Uninstall registry
        metadata after in-place upgrades rather than hardcoding unins000.exe.
      - Pre-flight production safety guards: aborts immediately if an existing ServerForge Desktop
        installation, production ServerForgeAgent service, or paired Agent identity is already present.
      - Desktop Installer (targeted install -> upgrade -> registered uninstall reproduction):
        SHA-256 verification, unrelated process isolation, silent install, self-contained .NET 10
        WPF runtime, registry registration, in-place upgrade preserving %LOCALAPPDATA%\ServerForge,
        registered uninstaller execution with absolute log path, executable & registry removal,
        user data preservation, and unrelated process survival.
      - Agent Installer (full lifecycle verification):
        SHA-256 verification, silent install with unique disposable service name, self-contained
        runtime, Windows Service Running + Auto + Failure Recovery, first-boot artifacts
        (cert/identity/sqlite/pairing-code/logs), loopback-only 127.0.0.1:9443 binding,
        identity/cert persistence across Restart-Service, appsettings.json preservation across
        upgrade, clean registered uninstall removing service/binaries/registry while preserving
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
    [string]$DisposableRoot = '',
    [string]$DisposableAgentServiceName = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$InputPath)
    if ([System.IO.Path]::IsPathRooted($InputPath)) {
        return [System.IO.Path]::GetFullPath($InputPath)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $InputPath))
}

$runToken = [Guid]::NewGuid().ToString('N').Substring(0, 8)
if ([string]::IsNullOrWhiteSpace($DisposableRoot)) {
    $DisposableRoot = Join-Path 'C:\Temp' "ServerForgeSmoke-$runToken"
}
if ([string]::IsNullOrWhiteSpace($DisposableAgentServiceName)) {
    $DisposableAgentServiceName = "ServerForgeAgentSmoke_$runToken"
}

# Normalize ALL paths to strict absolute paths before launching any Inno Setup process
$InstallerDir = Resolve-AbsolutePath $InstallerDir
$LogOutputDir = Resolve-AbsolutePath $LogOutputDir
$DisposableRoot = Resolve-AbsolutePath $DisposableRoot

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

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 20,
        [int]$PollMilliseconds = 500
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    return [bool](& $Condition)
}

function Find-UninstallRegistryKey {
    param([string[]]$CandidatePaths)
    foreach ($rp in $CandidatePaths) {
        if (Test-Path $rp) { return $rp }
    }
    return $null
}

function Resolve-RegisteredUninstaller {
    param(
        [string[]]$RegistryPaths,
        [string]$FallbackInstallDir
    )
    $regPath = Find-UninstallRegistryKey -CandidatePaths $RegistryPaths
    $result = [ordered]@{
        RegistryPath         = $regPath
        DisplayName          = $null
        DisplayVersion       = $null
        InstallLocation      = $null
        UninstallString      = $null
        QuietUninstallString = $null
        ExecutablePath       = $null
        Source               = 'None'
    }

    if ($null -ne $regPath) {
        $props = Get-ItemProperty -Path $regPath -ErrorAction SilentlyContinue
        if ($null -ne $props) {
            foreach ($propName in @('DisplayName', 'DisplayVersion', 'InstallLocation', 'UninstallString', 'QuietUninstallString')) {
                $propObj = $props.PSObject.Properties[$propName]
                if ($null -ne $propObj) {
                    $result[$propName] = $propObj.Value
                }
            }

            foreach ($rawCmd in @($result.UninstallString, $result.QuietUninstallString)) {
                if ([string]::IsNullOrWhiteSpace($rawCmd)) { continue }
                $candidateExe = $null
                if ($rawCmd -match '^\s*"([^"]+)"') {
                    $candidateExe = $matches[1]
                } elseif ($rawCmd -match '^\s*(\S+)') {
                    $candidateExe = $matches[1]
                }
                if (-not [string]::IsNullOrWhiteSpace($candidateExe) -and (Test-Path $candidateExe)) {
                    $result.ExecutablePath = Resolve-AbsolutePath $candidateExe
                    $result.Source = 'UninstallRegistry'
                    break
                }
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($result.ExecutablePath) -and (Test-Path $FallbackInstallDir)) {
        $fallbackExe = Get-ChildItem -Path $FallbackInstallDir -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $fallbackExe) {
            $result.ExecutablePath = $fallbackExe.FullName
            $result.Source = 'InstallDirFallback'
        }
    }

    return [PSCustomObject]$result
}

function Invoke-InnoExecutable {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogFilePath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )
    $absExe = Resolve-AbsolutePath $ExecutablePath
    $absLog = Resolve-AbsolutePath $LogFilePath
    $absWorkDir = Resolve-AbsolutePath $WorkingDirectory

    $logParent = Split-Path -Path $absLog -Parent
    New-Item -ItemType Directory -Path $logParent -Force | Out-Null

    $fullArgs = @($Arguments) + @("/LOG=`"$absLog`"")
    $argDisplay = ($fullArgs -join ' ')
    Write-CheckLog "Launching [$Label]: Exe=`"$absExe`" WorkDir=`"$absWorkDir`" Args=[$argDisplay]"

    $proc = Start-Process -FilePath $absExe -ArgumentList $fullArgs -WorkingDirectory $absWorkDir -Wait -PassThru

    # Allow brief flush for Inno Setup's cloned child uninstaller log handle
    [void](Wait-Until -Condition { Test-Path $absLog } -TimeoutSeconds 5 -PollMilliseconds 250)
    $logExists = Test-Path $absLog
    $logSize = if ($logExists) { (Get-Item $absLog).Length } else { 0 }
    Write-CheckLog "Completed [$Label]: ExitCode=$($proc.ExitCode) LogExists=$logExists LogBytes=$logSize LogPath=`"$absLog`""

    if ($proc.ExitCode -ne 0 -and $logExists) {
        Write-CheckLog "--- Tail of $absLog (last 40 lines) ---" 'WARN'
        foreach ($logLine in @(Get-Content -Path $absLog -Tail 40 -ErrorAction SilentlyContinue)) {
            Write-CheckLog "  $logLine" 'WARN'
        }
    }

    return [PSCustomObject]@{
        ExitCode  = $proc.ExitCode
        LogPath   = $absLog
        LogExists = $logExists
        LogBytes  = $logSize
        Args      = $argDisplay
    }
}

Write-CheckLog '=================================================================='
Write-CheckLog 'ServerForge Phase 11 — Automated Windows Installer Smoke Test Suite'
Write-CheckLog "RunToken=$runToken"
Write-CheckLog "InstallerDir=$InstallerDir"
Write-CheckLog "LogOutputDir=$LogOutputDir"
Write-CheckLog "DisposableRoot=$DisposableRoot"
Write-CheckLog "DisposableService=$DisposableAgentServiceName"
Write-CheckLog 'Scope: Installer execution, self-contained runtime, service & loopback verification.'
Write-CheckLog 'Note: Interactive WPF GUI rendering is NOT claimed by unattended Windows CI.'
Write-CheckLog '=================================================================='

# =========================================================================
# PRE-FLIGHT PRODUCTION PROTECTION GUARDS
# =========================================================================
$existingProdSvc = Get-Service -Name 'ServerForgeAgent' -ErrorAction SilentlyContinue
Assert-Condition ($null -eq $existingProdSvc) 'Safety.NoPreExistingProductionAgentService' `
    'Refusing to run disposable smoke test on a host with an active production ServerForgeAgent service.'

$existingProdAgentIdentity = 'C:\ProgramData\ServerForge\Agent\agent-identity.json'
Assert-Condition (-not (Test-Path $existingProdAgentIdentity)) 'Safety.NoPreExistingAgentIdentity' `
    "Refusing to run disposable smoke test when $existingProdAgentIdentity already exists."

$desktopRegPaths = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0001}_is1',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0001}_is1',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0001}_is1'
)
$agentRegPaths = @(
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0002}_is1',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0002}_is1',
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9B4E2D18-7F3A-4C61-A82E-1F4A9C7E0002}_is1'
)

$existingDesktopReg = Find-UninstallRegistryKey -CandidatePaths $desktopRegPaths
Assert-Condition ($null -eq $existingDesktopReg) 'Safety.NoPreExistingDesktopInstall' `
    "Refusing to overwrite pre-existing ServerForge Desktop install ($existingDesktopReg)."

$existingAgentReg = Find-UninstallRegistryKey -CandidatePaths $agentRegPaths
Assert-Condition ($null -eq $existingAgentReg) 'Safety.NoPreExistingAgentInstall' `
    "Refusing to overwrite pre-existing ServerForge Agent install ($existingAgentReg)."

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

# Pre-declare cleanup targets before try so StrictMode never fails in finally
$hostedAppMarkerDir = "C:\ServerForgeApps\smoke-hosted-app-$runToken"
$desktopLocalAppData = Join-Path $env:LOCALAPPDATA 'ServerForge'
$desktopMarkerFile = Join-Path $desktopLocalAppData "smoke-upgrade-marker-$runToken.txt"
$agentProgramData = 'C:\ProgramData\ServerForge\Agent'
$createdAgentProgramData = -not (Test-Path $agentProgramData)

try {
    # =========================================================================
    # PART 1 — DESKTOP INSTALLER SMOKE TESTS (TARGETED INSTALL -> UPGRADE -> UNINSTALL)
    # =========================================================================
    $desktopInstallDir = Join-Path $DisposableRoot 'DesktopApp'
    $desktopInstallLog = Join-Path $LogOutputDir 'desktop-install.log'
    $desktopUpgradeLog = Join-Path $LogOutputDir 'desktop-upgrade.log'
    $desktopUninstallLog = Join-Path $LogOutputDir 'desktop-uninstall.log'

    Write-CheckLog '--- PART 1: ServerForge Desktop Installer ---'

    # 1-2. Clean silent install to unique disposable directory with absolute log path
    $dInst = Invoke-InnoExecutable -Label 'Desktop.Install' -ExecutablePath $desktopSetup `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$desktopInstallDir`"") `
        -LogFilePath $desktopInstallLog -WorkingDirectory $InstallerDir
    Assert-Condition ($dInst.ExitCode -eq 0) 'Desktop.1_SilentInstallSucceeded' "ExitCode=$($dInst.ExitCode)"
    Assert-Condition ($dInst.LogExists -and $dInst.LogBytes -gt 0) 'Desktop.2_InstallerDiagnosticLogWritten' "$($dInst.LogPath) ($($dInst.LogBytes) bytes)"

    # 3-6. Verify native WPF binary, self-contained .NET 10 runtime, and authoritative uninstall registry registration
    $desktopExe = Join-Path $desktopInstallDir 'ServerForge.Desktop.exe'
    $coreClrDll = Join-Path $desktopInstallDir 'coreclr.dll'
    $wpfDll = Join-Path $desktopInstallDir 'PresentationFramework.dll'
    Assert-Condition (Test-Path $desktopExe) 'Desktop.3_MainExecutablePresent' $desktopExe
    Assert-Condition ((Test-Path $coreClrDll) -and (Test-Path $wpfDll)) 'Desktop.4_SelfContainedNet10WpfRuntimePresent' 'coreclr.dll & PresentationFramework.dll'

    $desktopUninsPreUpgrade = Resolve-RegisteredUninstaller -RegistryPaths $desktopRegPaths -FallbackInstallDir $desktopInstallDir
    Write-CheckLog "Desktop Uninstall Registry (pre-upgrade): Key=$($desktopUninsPreUpgrade.RegistryPath) Ver=$($desktopUninsPreUpgrade.DisplayVersion) Exe=$($desktopUninsPreUpgrade.ExecutablePath) Source=$($desktopUninsPreUpgrade.Source)"
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($desktopUninsPreUpgrade.ExecutablePath) -and (Test-Path $desktopUninsPreUpgrade.ExecutablePath)) 'Desktop.5_UninstallerPresent' "$($desktopUninsPreUpgrade.ExecutablePath)"
    Assert-Condition ($null -ne $desktopUninsPreUpgrade.RegistryPath) 'Desktop.6_UninstallRegistryEntryCreated' "$($desktopUninsPreUpgrade.RegistryPath)"

    # 7. Verify in-place upgrade preserves %LOCALAPPDATA%\ServerForge user data and does NOT terminate unrelated canary process
    New-Item -ItemType Directory -Path $desktopLocalAppData -Force | Out-Null
    Set-Content -Path $desktopMarkerFile -Value "preserve-me-$runToken"
    $dUp = Invoke-InnoExecutable -Label 'Desktop.Upgrade' -ExecutablePath $desktopSetup `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$desktopInstallDir`"") `
        -LogFilePath $desktopUpgradeLog -WorkingDirectory $InstallerDir
    Assert-Condition ($dUp.ExitCode -eq 0) 'Desktop.7a_InPlaceUpgradeSucceeded' "ExitCode=$($dUp.ExitCode)"
    Assert-Condition ((Test-Path $desktopMarkerFile) -and ((Get-Content $desktopMarkerFile -Raw).Trim() -eq "preserve-me-$runToken")) 'Desktop.7b_UpgradePreservedLocalAppData' $desktopMarkerFile
    Assert-Condition (-not $canaryProc.HasExited) 'Desktop.7c_UnrelatedProcessUntouched' "Canary PID=$($canaryProc.Id) still running"

    # 8. Resolve authoritative post-upgrade registered uninstaller & run with absolute log path and WorkingDirectory=%TEMP%
    $desktopUnins = Resolve-RegisteredUninstaller -RegistryPaths $desktopRegPaths -FallbackInstallDir $desktopInstallDir
    Write-CheckLog "Desktop Uninstall Registry (post-upgrade): Key=$($desktopUnins.RegistryPath) InstallLocation=$($desktopUnins.InstallLocation) UninstallString=$($desktopUnins.UninstallString) QuietUninstallString=$($desktopUnins.QuietUninstallString) ResolvedExe=$($desktopUnins.ExecutablePath) Source=$($desktopUnins.Source)"
    Assert-Condition ($desktopUnins.Source -eq 'UninstallRegistry' -and (Test-Path $desktopUnins.ExecutablePath)) 'Desktop.8_RegisteredUninstallerResolved' "$($desktopUnins.ExecutablePath)"

    $dUn = Invoke-InnoExecutable -Label 'Desktop.Uninstall' -ExecutablePath $desktopUnins.ExecutablePath `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
        -LogFilePath $desktopUninstallLog -WorkingDirectory $env:TEMP
    Assert-Condition ($dUn.ExitCode -eq 0) 'Desktop.8a_CleanUninstallSucceeded' "ExitCode=$($dUn.ExitCode) LogExists=$($dUn.LogExists) LogBytes=$($dUn.LogBytes)"
    Assert-Condition ($dUn.LogExists -and $dUn.LogBytes -gt 0) 'Desktop.8b_UninstallDiagnosticLogWritten' "$($dUn.LogPath) ($($dUn.LogBytes) bytes)"

    # Wait for Inno Setup's cloned child uninstaller (_iu*.tmp) to finish removing binary & registry entry
    $desktopRemoved = Wait-Until -Condition { -not (Test-Path $desktopExe) } -TimeoutSeconds 25
    $desktopRegRemoved = Wait-Until -Condition { $null -eq (Find-UninstallRegistryKey -CandidatePaths $desktopRegPaths) } -TimeoutSeconds 25
    Assert-Condition $desktopRemoved 'Desktop.8c_DesktopBinaryRemoved' $desktopExe
    Assert-Condition $desktopRegRemoved 'Desktop.8d_UninstallRegistryRemoved' 'Uninstall registry entry deleted cleanly'
    Assert-Condition (Test-Path $desktopMarkerFile) 'Desktop.8e_UninstallPreservedUserData' $desktopMarkerFile
    Assert-Condition (-not $canaryProc.HasExited) 'Desktop.8f_UnrelatedProcessUntouchedAfterUninstall' "Canary PID=$($canaryProc.Id) still running"

    # =========================================================================
    # PART 2 — AGENT INSTALLER SMOKE TESTS (10 CHECKS)
    # =========================================================================
    $agentInstallDir = Join-Path $DisposableRoot 'AgentApp'
    $agentInstallLog = Join-Path $LogOutputDir 'agent-install.log'
    $agentUpgradeLog = Join-Path $LogOutputDir 'agent-upgrade.log'
    $agentUninstallLog = Join-Path $LogOutputDir 'agent-uninstall.log'
    $hostedAppMarkerFile = Join-Path $hostedAppMarkerDir 'index.html'

    Write-CheckLog '--- PART 2: ServerForge Agent Installer ---'

    # Pre-create a simulated hosted site under C:\ServerForgeApps to verify uninstall NEVER deletes hosted apps
    New-Item -ItemType Directory -Path $hostedAppMarkerDir -Force | Out-Null
    Set-Content -Path $hostedAppMarkerFile -Value "<h1>ServerForge Hosted App Preservation Check ($runToken)</h1>"

    # 1-2. Silent install with isolated disposable service name and absolute log path
    $aInst = Invoke-InnoExecutable -Label 'Agent.Install' -ExecutablePath $agentSetup `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$agentInstallDir`"", "/SERVICENAME=$DisposableAgentServiceName") `
        -LogFilePath $agentInstallLog -WorkingDirectory $InstallerDir
    Assert-Condition ($aInst.ExitCode -eq 0) 'Agent.1_SilentInstallSucceeded' "ExitCode=$($aInst.ExitCode)"
    Assert-Condition ($aInst.LogExists -and $aInst.LogBytes -gt 0) 'Agent.2_InstallerDiagnosticLogWritten' "$($aInst.LogPath) ($($aInst.LogBytes) bytes)"

    # 3-4. Verify self-contained Agent binary & Windows Service registration + Running status + Recovery policy
    $agentExe = Join-Path $agentInstallDir 'ServerForge.Agent.exe'
    Assert-Condition ((Test-Path $agentExe) -and (Test-Path (Join-Path $agentInstallDir 'coreclr.dll'))) 'Agent.3_SelfContainedAgentBinaryPresent' $agentExe

    $svcRunning = Wait-Until -Condition {
        $s = Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue
        $null -ne $s -and $s.Status -eq 'Running'
    } -TimeoutSeconds 25
    $svc = Get-Service -Name $DisposableAgentServiceName -ErrorAction Stop
    Assert-Condition $svcRunning 'Agent.4a_WindowsServiceRunning' "Service=$($svc.Name) Status=$($svc.Status)"

    $svcWmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$DisposableAgentServiceName'"
    Assert-Condition ($svcWmi.StartMode -eq 'Auto') 'Agent.4b_WindowsServiceAutoStart' "StartMode=$($svcWmi.StartMode)"
    $qfailure = (& sc.exe qfailure $DisposableAgentServiceName | Out-String)
    Assert-Condition ($qfailure -match 'RESTART') 'Agent.4c_WindowsServiceRecoveryConfigured' 'sc qfailure includes RESTART actions'

    # 5. Wait up to 25s for Agent first-boot artifacts (cert, identity, sqlite, pairing-code)
    $pairingFile = Join-Path $agentProgramData 'pairing-code.txt'
    $identityFile = Join-Path $agentProgramData 'agent-identity.json'
    $certFile = Join-Path $agentProgramData 'agent-server.pfx'
    $dbFile = Join-Path $agentProgramData 'agent-state.db'
    $artifactsReady = Wait-Until -Condition {
        (Test-Path $pairingFile) -and (Test-Path $identityFile) -and (Test-Path $certFile) -and (Test-Path $dbFile)
    } -TimeoutSeconds 25
    Assert-Condition $artifactsReady 'Agent.5_FirstBootArtifactsGenerated' 'pairing-code.txt, agent-identity.json, agent-server.pfx, agent-state.db'

    # 6. Verify Agent runtime logs created
    $agentLogs = @(Get-ChildItem -Path (Join-Path $agentProgramData 'logs') -Filter 'agent-*.log' -ErrorAction SilentlyContinue)
    Assert-Condition ($agentLogs.Count -gt 0) 'Agent.6_AgentRuntimeLogsCreated' "LogFiles=$($agentLogs.Count)"

    # 7. Verify Agent listens ONLY on loopback 127.0.0.1:9443 by default (NEVER public 0.0.0.0 or ::)
    $portBound = Wait-Until -Condition {
        @(Get-NetTCPConnection -LocalPort 9443 -State Listen -ErrorAction SilentlyContinue).Count -gt 0
    } -TimeoutSeconds 20
    $listeners = @(Get-NetTCPConnection -LocalPort 9443 -State Listen -ErrorAction SilentlyContinue)
    Assert-Condition ($portBound -and $listeners.Count -gt 0) 'Agent.7a_ListeningOnPort9443' "Listeners=$($listeners.Count)"
    $nonLoopback = @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') })
    Assert-Condition ($nonLoopback.Count -eq 0) 'Agent.7b_LoopbackOnlySecurityPosture' "BoundAddresses=$(($listeners.LocalAddress) -join ',')"

    # 8. Verify Restart-Service preserves Agent identity & certificate thumbprint
    $identityBefore = (Get-Content $identityFile -Raw | ConvertFrom-Json).AgentId
    $certHashBefore = (Get-FileHash -Path $certFile -Algorithm SHA256).Hash
    Restart-Service -Name $DisposableAgentServiceName -Force
    $restartedOk = Wait-Until -Condition {
        $s = Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue
        $null -ne $s -and $s.Status -eq 'Running'
    } -TimeoutSeconds 25
    $svcAfterRestart = Get-Service -Name $DisposableAgentServiceName
    Assert-Condition $restartedOk 'Agent.8a_ServiceSurvivedRestart' "Status=$($svcAfterRestart.Status)"
    $identityAfter = (Get-Content $identityFile -Raw | ConvertFrom-Json).AgentId
    $certHashAfter = (Get-FileHash -Path $certFile -Algorithm SHA256).Hash
    Assert-Condition (($identityBefore -eq $identityAfter) -and ($certHashBefore -eq $certHashAfter)) `
        'Agent.8b_IdentityAndCertPreservedAcrossRestart' "AgentId=$identityAfter"

    # 9. Verify upgrade preserves appsettings.json customizations and keeps service running
    $appSettingsPath = Join-Path $agentInstallDir 'appsettings.json'
    $settingsObj = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    $settingsObj | Add-Member -NotePropertyName 'SmokeUpgradeMarker' -NotePropertyValue "preserved-$runToken" -Force
    $settingsObj | ConvertTo-Json -Depth 10 | Set-Content -Path $appSettingsPath -Encoding UTF8

    $aUp = Invoke-InnoExecutable -Label 'Agent.Upgrade' -ExecutablePath $agentSetup `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$agentInstallDir`"", "/SERVICENAME=$DisposableAgentServiceName") `
        -LogFilePath $agentUpgradeLog -WorkingDirectory $InstallerDir
    Assert-Condition ($aUp.ExitCode -eq 0) 'Agent.9a_InPlaceUpgradeSucceeded' "ExitCode=$($aUp.ExitCode)"
    $settingsAfterUpgrade = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
    Assert-Condition ($settingsAfterUpgrade.SmokeUpgradeMarker -eq "preserved-$runToken") 'Agent.9b_UpgradePreservedAppSettings' $appSettingsPath
    $runningAfterUpgrade = Wait-Until -Condition {
        $s = Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue
        $null -ne $s -and $s.Status -eq 'Running'
    } -TimeoutSeconds 25
    $svcAfterUpgrade = Get-Service -Name $DisposableAgentServiceName
    Assert-Condition $runningAfterUpgrade 'Agent.9c_ServiceRunningAfterUpgrade' "Status=$($svcAfterUpgrade.Status)"
    Assert-Condition (-not $canaryProc.HasExited) 'Agent.9d_UnrelatedProcessUntouched' "Canary PID=$($canaryProc.Id) still running"

    # 10. Resolve authoritative registered Agent uninstaller and verify clean uninstall preserving C:\ServerForgeApps hosted files
    $agentUnins = Resolve-RegisteredUninstaller -RegistryPaths $agentRegPaths -FallbackInstallDir $agentInstallDir
    Write-CheckLog "Agent Uninstall Registry (post-upgrade): Key=$($agentUnins.RegistryPath) UninstallString=$($agentUnins.UninstallString) ResolvedExe=$($agentUnins.ExecutablePath) Source=$($agentUnins.Source)"
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($agentUnins.ExecutablePath) -and (Test-Path $agentUnins.ExecutablePath)) 'Agent.10_RegisteredUninstallerResolved' "$($agentUnins.ExecutablePath)"

    $aUn = Invoke-InnoExecutable -Label 'Agent.Uninstall' -ExecutablePath $agentUnins.ExecutablePath `
        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
        -LogFilePath $agentUninstallLog -WorkingDirectory $env:TEMP
    Assert-Condition ($aUn.ExitCode -eq 0) 'Agent.10a_CleanUninstallSucceeded' "ExitCode=$($aUn.ExitCode) LogExists=$($aUn.LogExists) LogBytes=$($aUn.LogBytes)"
    Assert-Condition ($aUn.LogExists -and $aUn.LogBytes -gt 0) 'Agent.10b_UninstallDiagnosticLogWritten' "$($aUn.LogPath) ($($aUn.LogBytes) bytes)"

    $svcRemoved = Wait-Until -Condition {
        $null -eq (Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue)
    } -TimeoutSeconds 25
    $agentExeRemoved = Wait-Until -Condition { -not (Test-Path $agentExe) } -TimeoutSeconds 25
    $agentRegRemoved = Wait-Until -Condition { $null -eq (Find-UninstallRegistryKey -CandidatePaths $agentRegPaths) } -TimeoutSeconds 25
    Assert-Condition $svcRemoved 'Agent.10c_WindowsServiceRemoved' $DisposableAgentServiceName
    Assert-Condition $agentExeRemoved 'Agent.10d_AgentBinaryRemoved' $agentExe
    Assert-Condition $agentRegRemoved 'Agent.10e_UninstallRegistryRemoved' 'Agent uninstall registry entry deleted cleanly'
    Assert-Condition (Test-Path $hostedAppMarkerFile) 'Agent.10f_HostedAppsPreservedOnUninstall' $hostedAppMarkerFile

    Write-CheckLog '=================================================================='
    Write-CheckLog "ALL INSTALLER SMOKE TESTS PASSED: $($script:PassedChecks) passed, $($script:FailedChecks) failed."
    Write-CheckLog '=================================================================='
}
finally {
    # Always capture Agent logs and Windows Event Log diagnostics BEFORE cleanup
    $agentLogDir = Join-Path $agentProgramData 'logs'
    if (Test-Path $agentLogDir) {
        foreach ($lf in @(Get-ChildItem -Path $agentLogDir -Filter 'agent-*.log' -ErrorAction SilentlyContinue)) {
            Copy-Item -Path $lf.FullName -Destination (Join-Path $LogOutputDir $lf.Name) -Force -ErrorAction SilentlyContinue
        }
    }
    try {
        $appEvents = @(Get-WinEvent -LogName Application -MaxEvents 25 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match 'ServerForge|\.NET Runtime|Application Error' })
        if ($appEvents.Count -gt 0) {
            $appEvents | Format-List TimeCreated, ProviderName, LevelDisplayName, Message |
                Out-File -FilePath (Join-Path $LogOutputDir 'windows-application-events.txt') -Encoding UTF8
        }
        $sysEvents = @(Get-WinEvent -LogName System -MaxEvents 25 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -match 'Service Control Manager' -and $_.Message -match 'ServerForge' })
        if ($sysEvents.Count -gt 0) {
            $sysEvents | Format-List TimeCreated, ProviderName, LevelDisplayName, Message |
                Out-File -FilePath (Join-Path $LogOutputDir 'windows-scm-events.txt') -Encoding UTF8
        }
    } catch { }

    # Ensure disposable Agent service is stopped & uninstalled even on mid-run failure
    $leftoverSvc = Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue
    if ($null -ne $leftoverSvc) {
        Write-CheckLog "Cleanup: stopping and removing leftover disposable service '$DisposableAgentServiceName'." 'WARN'
        Stop-Service -Name $DisposableAgentServiceName -Force -ErrorAction SilentlyContinue
        $fallbackAgentUnins = Resolve-RegisteredUninstaller -RegistryPaths $agentRegPaths -FallbackInstallDir (Join-Path $DisposableRoot 'AgentApp')
        if (-not [string]::IsNullOrWhiteSpace($fallbackAgentUnins.ExecutablePath) -and (Test-Path $fallbackAgentUnins.ExecutablePath)) {
            [void](Invoke-InnoExecutable -Label 'Agent.FallbackUninstall' -ExecutablePath $fallbackAgentUnins.ExecutablePath `
                -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
                -LogFilePath (Join-Path $LogOutputDir 'agent-fallback-uninstall.log') -WorkingDirectory $env:TEMP)
            [void](Wait-Until -Condition { $null -eq (Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue) } -TimeoutSeconds 20)
        }
        if ($null -ne (Get-Service -Name $DisposableAgentServiceName -ErrorAction SilentlyContinue)) {
            & sc.exe delete $DisposableAgentServiceName | Out-Null
        }
    }

    # Ensure disposable Desktop install is uninstalled even on mid-run failure
    $fallbackDesktopUnins = Resolve-RegisteredUninstaller -RegistryPaths $desktopRegPaths -FallbackInstallDir (Join-Path $DisposableRoot 'DesktopApp')
    if (-not [string]::IsNullOrWhiteSpace($fallbackDesktopUnins.ExecutablePath) -and (Test-Path $fallbackDesktopUnins.ExecutablePath)) {
        Write-CheckLog 'Cleanup: uninstalling leftover disposable Desktop installation.' 'WARN'
        [void](Invoke-InnoExecutable -Label 'Desktop.FallbackUninstall' -ExecutablePath $fallbackDesktopUnins.ExecutablePath `
            -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
            -LogFilePath (Join-Path $LogOutputDir 'desktop-fallback-uninstall.log') -WorkingDirectory $env:TEMP)
        [void](Wait-Until -Condition { -not (Test-Path (Join-Path (Join-Path $DisposableRoot 'DesktopApp') 'ServerForge.Desktop.exe')) } -TimeoutSeconds 20)
    }

    if ($canaryProc -and -not $canaryProc.HasExited) {
        Stop-Process -Id $canaryProc.Id -Force -ErrorAction SilentlyContinue
    }

    # Remove ONLY test-created markers & disposable directories (NEVER delete C:\ServerForgeApps root or pre-existing user data)
    Remove-Item -Path $desktopMarkerFile -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $hostedAppMarkerDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($createdAgentProgramData -and (Test-Path $agentProgramData)) {
        Remove-Item -Path $agentProgramData -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -Path $DisposableRoot -Recurse -Force -ErrorAction SilentlyContinue
}
