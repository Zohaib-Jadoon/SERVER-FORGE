#Requires -Version 5.1
<#
.SYNOPSIS
    Compiles ServerForge-Desktop-Setup.exe and ServerForge-Agent-Setup.exe using Inno Setup 6 (ISCC.exe)
    with strict exit-code and output binary verification.
#>
[CmdletBinding()]
param(
    [string]$IsccPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$InstallersDir = Join-Path $Root "artifacts\installers"
$DesktopIss = Join-Path $Root "installers\Desktop\ServerForge-Desktop.iss"
$AgentIss = Join-Path $Root "installers\Agent\ServerForge-Agent.iss"

$candidateIsccPaths = @(
    $IsccPath,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\ProgramData\chocolatey\bin\ISCC.exe"
)

$resolvedIscc = $candidateIsccPaths | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $resolvedIscc) {
    $fromPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        $resolvedIscc = $fromPath.Source
    }
}

if (-not $resolvedIscc) {
    Write-Error "Inno Setup 6 (ISCC.exe) not found. Install via: winget install JRSoftware.InnoSetup or choco install innosetup"
    exit 1
}
$IsccPath = $resolvedIscc
Write-Host "Using Inno Setup compiler: $IsccPath" -ForegroundColor Gray

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build-windows.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Error "build-windows.ps1 failed with exit code $LASTEXITCODE. Aborting installer compilation."
        exit $LASTEXITCODE
    }
}

foreach ($publishCheck in @(
    (Join-Path $Root "artifacts\publish\desktop-win-x64\ServerForge.Desktop.exe"),
    (Join-Path $Root "artifacts\publish\agent-win-x64\ServerForge.Agent.exe")
)) {
    if (-not (Test-Path $publishCheck)) {
        Write-Error "Published binary missing: '$publishCheck'. Run build-windows.ps1 first."
        exit 1
    }
}

New-Item -ItemType Directory -Force -Path $InstallersDir | Out-Null

Write-Host "==> Compiling ServerForge-Desktop-Setup.exe..." -ForegroundColor Cyan
& $IsccPath $DesktopIss
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed for Desktop installer (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host "==> Compiling ServerForge-Agent-Setup.exe..." -ForegroundColor Cyan
& $IsccPath $AgentIss
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed for Agent installer (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$desktopSetup = Join-Path $InstallersDir "ServerForge-Desktop-Setup.exe"
$agentSetup = Join-Path $InstallersDir "ServerForge-Agent-Setup.exe"
$checksumFile = Join-Path $InstallersDir "SHA256SUMS.txt"

$checksumLines = @()
foreach ($installer in @($desktopSetup, $agentSetup)) {
    if (-not (Test-Path $installer) -or ((Get-Item $installer).Length -le 0)) {
        Write-Error "Installer verification failed: '$installer' does not exist or is empty."
        exit 1
    }
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $sizeMb = [Math]::Round((Get-Item $installer).Length / 1MB, 2)
    $name = Split-Path $installer -Leaf
    $checksumLines += "$hash  $name"
    Write-Host "Verified: $name ($sizeMb MB) | SHA-256: $hash" -ForegroundColor Green
}

$checksumLines | Set-Content -Path $checksumFile -Encoding UTF8
Write-Host "Checksums written to: $checksumFile" -ForegroundColor Green
exit 0
