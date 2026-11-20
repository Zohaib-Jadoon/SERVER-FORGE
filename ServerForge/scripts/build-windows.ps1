#Requires -Version 5.1
<#
.SYNOPSIS
    Restores, builds, tests, and publishes ServerForge Desktop and ServerForge Agent
    as self-contained Windows x64 releases with strict native exit-code verification.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$CollectTestResults
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$StepName
    )
    Write-Host "==> $StepName..." -ForegroundColor Cyan
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Error "FAILED: $StepName exited with code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
}

$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Solution = Join-Path $Root "ServerForge.sln"
$ArtifactsDir = Join-Path $Root "artifacts"
$DesktopOut = Join-Path $ArtifactsDir "publish\desktop-win-x64"
$AgentOut = Join-Path $ArtifactsDir "publish\agent-win-x64"
$TestResultsDir = Join-Path $ArtifactsDir "test-results"

if (-not (Test-Path $Solution)) {
    Write-Error "Solution file not found at '$Solution'."
    exit 1
}

Invoke-CheckedCommand -FilePath "dotnet" -Arguments @("restore", $Solution) -StepName "Restoring ServerForge.sln (.NET 10 LTS)"

Invoke-CheckedCommand -FilePath "dotnet" -Arguments @(
    "build", $Solution,
    "--configuration", $Configuration,
    "--no-restore"
) -StepName "Building ServerForge.sln ($Configuration)"

$SampleCsproj = Join-Path $Root "samples\disposable-test-app\ServerForge.SampleApp.csproj"
if (Test-Path $SampleCsproj) {
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments @(
        "build", $SampleCsproj,
        "--configuration", $Configuration
    ) -StepName "Building Disposable E2E Sample App (ServerForge.SampleApp.csproj)"
}

if (-not $SkipTests) {
    $testArgs = @(
        "test", $Solution,
        "--configuration", $Configuration,
        "--no-build",
        "--verbosity", "normal"
    )
    if ($CollectTestResults) {
        New-Item -ItemType Directory -Force -Path $TestResultsDir | Out-Null
        $testArgs += @("--logger", "trx", "--results-directory", $TestResultsDir)
    }
    Invoke-CheckedCommand -FilePath "dotnet" -Arguments $testArgs -StepName "Running ServerForge xUnit Test Suites"
}

if (Test-Path $DesktopOut) { Remove-Item -Recurse -Force $DesktopOut }
if (Test-Path $AgentOut) { Remove-Item -Recurse -Force $AgentOut }

Invoke-CheckedCommand -FilePath "dotnet" -Arguments @(
    "publish", (Join-Path $Root "src\ServerForge.Desktop\ServerForge.Desktop.csproj"),
    "--configuration", $Configuration,
    "--runtime", "win-x64",
    "--self-contained", "true",
    "-p:PublishReadyToRun=true",
    "--output", $DesktopOut
) -StepName "Publishing ServerForge Desktop (self-contained win-x64)"

Invoke-CheckedCommand -FilePath "dotnet" -Arguments @(
    "publish", (Join-Path $Root "src\ServerForge.Agent\ServerForge.Agent.csproj"),
    "--configuration", $Configuration,
    "--runtime", "win-x64",
    "--self-contained", "true",
    "-p:PublishReadyToRun=true",
    "--output", $AgentOut
) -StepName "Publishing ServerForge Agent (self-contained win-x64)"

$desktopExe = Join-Path $DesktopOut "ServerForge.Desktop.exe"
$agentExe = Join-Path $AgentOut "ServerForge.Agent.exe"

foreach ($expectedExe in @($desktopExe, $agentExe)) {
    if (-not (Test-Path $expectedExe) -or ((Get-Item $expectedExe).Length -le 0)) {
        Write-Error "Publish verification failed: expected binary '$expectedExe' is missing or empty."
        exit 1
    }
}

Write-Host "==> Build, test, and publish verified successfully." -ForegroundColor Green
Write-Host "Desktop binary: $desktopExe"
Write-Host "Agent binary:   $agentExe"
exit 0
