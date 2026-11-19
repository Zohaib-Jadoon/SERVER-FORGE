#Requires -Version 5.1
<#
.SYNOPSIS
    Compiles ServerForge-Desktop-Setup.exe and ServerForge-Agent-Setup.exe using Inno Setup 6 (ISCC.exe).
#>
[CmdletBinding()]
param(
    [string]$IsccPath = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")

if (-not (Test-Path $IsccPath)) {
    $fromPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        $IsccPath = $fromPath.Source
    } else {
        throw "Inno Setup 6 (ISCC.exe) not found. Install via: winget install JRSoftware.InnoSetup"
    }
}

& (Join-Path $PSScriptRoot "build-windows.ps1")

Write-Host "==> Compiling ServerForge-Desktop-Setup.exe..." -ForegroundColor Cyan
& $IsccPath (Join-Path $Root "installers\Desktop\ServerForge-Desktop.iss")

Write-Host "==> Compiling ServerForge-Agent-Setup.exe..." -ForegroundColor Cyan
& $IsccPath (Join-Path $Root "installers\Agent\ServerForge-Agent.iss")

$InstallersDir = Join-Path $Root "artifacts\installers"
Get-ChildItem $InstallersDir -Filter "*.exe" | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    Write-Host "Built: $($_.Name) ($([Math]::Round($_.Length / 1MB, 2)) MB) | SHA-256: $hash" -ForegroundColor Green
}
