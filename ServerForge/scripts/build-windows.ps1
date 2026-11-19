#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tests, and publishes ServerForge Desktop and ServerForge Agent as self-contained Windows x64 releases.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$ArtifactsDir = Join-Path $Root "artifacts"
$DesktopOut = Join-Path $ArtifactsDir "publish\desktop-win-x64"
$AgentOut = Join-Path $ArtifactsDir "publish\agent-win-x64"

Write-Host "==> Restoring ServerForge.sln (.NET 10 LTS)..." -ForegroundColor Cyan
dotnet restore (Join-Path $Root "ServerForge.sln")

if (-not $SkipTests) {
    Write-Host "==> Running Unit, Agent, and Integration Tests..." -ForegroundColor Cyan
    dotnet test (Join-Path $Root "ServerForge.sln") --configuration $Configuration --no-restore --verbosity normal
}

Write-Host "==> Publishing ServerForge Desktop (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $Root "src\ServerForge.Desktop\ServerForge.Desktop.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    --output $DesktopOut

Write-Host "==> Publishing ServerForge Agent (self-contained win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $Root "src\ServerForge.Agent\ServerForge.Agent.csproj") `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishReadyToRun=true `
    --output $AgentOut

Write-Host "==> Publish completed successfully." -ForegroundColor Green
Write-Host "Desktop output: $DesktopOut"
Write-Host "Agent output:   $AgentOut"
