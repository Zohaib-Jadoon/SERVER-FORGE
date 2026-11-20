<#
.SYNOPSIS
    Phase 12.2 Single Safe Entry Point for Disposable Windows IIS E2E Acceptance (TEST A + Optional TEST B).
.DESCRIPTION
    Safely validates disposable host ownership, orchestrates preflight, isolated Agent setup,
    TEST A (locally built hash-verified sample artifact on real IIS + rollback + restart resilience),
    optional TEST B (genuine autonomous GitHub Actions polling, update, HTTP 503 rollback, anti-retry-loop,
    and recovery with Desktop closed and ZERO calls to POST /api/v1/deployments/trigger),
    sanitized diagnostics collection, and owned-only cleanup in a guaranteed finally block.

    NEVER runs against production IIS hosts or machines with a production ServerForgeAgent service.
    Requires BOTH -ConfirmDisposableHost AND positive host marker:
        C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker
        containing: SERVERFORGE_DISPOSABLE_E2E_HOST=true
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AgentInstallerPath,
    [Parameter(Mandatory = $true)]
    [switch]$ConfirmDisposableHost,
    [string]$RunId = '',
    [string]$WorkspaceRoot = '',
    [string]$EvidenceDir = '',
    [int]$AgentPort = 19443,
    [int]$AppPort = 15199,
    [int]$GitHubAppPort = 15200,
    [switch]$IncludeTestB,
    [string]$GitHubRepo = '',
    [string]$GitHubBranch = 'main',
    [switch]$SkipCleanupOnFailure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'E2ECommon.psm1') -Force

# 1. Fail-closed safety & parameter validation before touching anything on the machine
if (-not $ConfirmDisposableHost) {
    throw 'SAFETY ABORT: Pass -ConfirmDisposableHost to confirm this machine is a dedicated disposable Windows IIS test VM (never production).'
}

if (-not (Test-IsAdministrator)) {
    throw 'SAFETY ABORT: Elevated Administrator PowerShell session is required to run disposable IIS E2E acceptance.'
}

if (-not (Test-DisposableHostIdentity)) {
    throw "SAFETY ABORT: Positive disposable host marker missing. Create 'C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker' containing 'SERVERFORGE_DISPOSABLE_E2E_HOST=true' on a dedicated non-production VM."
}

if ($null -ne (Get-Service -Name 'ServerForgeAgent' -ErrorAction SilentlyContinue)) {
    throw "SAFETY ABORT: Production Windows Service 'ServerForgeAgent' detected on this host. Refusing to run E2E acceptance."
}

if (Test-Path 'C:\ProgramData\ServerForge\Agent\agent-identity.json') {
    throw 'SAFETY ABORT: Existing production ServerForge Agent identity detected on this host. Refusing to run E2E acceptance.'
}

Assert-ValidE2EPort -Name 'AgentPort' -Port $AgentPort
Assert-ValidE2EPort -Name 'AppPort' -Port $AppPort
Assert-ValidE2EPort -Name 'GitHubAppPort' -Port $GitHubAppPort
if ($AgentPort -eq $AppPort -or $AgentPort -eq $GitHubAppPort -or $AppPort -eq $GitHubAppPort) {
    throw "SAFETY ABORT: AgentPort ($AgentPort), AppPort ($AppPort), and GitHubAppPort ($GitHubAppPort) must all be distinct."
}

$AgentInstallerPath = Resolve-E2EAbsolutePath $AgentInstallerPath
if (-not (Test-Path $AgentInstallerPath)) {
    throw "Agent installer not found at '$AgentInstallerPath'."
}

if ($IncludeTestB) {
    Assert-ValidGitHubRepo -Repo $GitHubRepo
    if ([string]::IsNullOrWhiteSpace($GitHubBranch) -or $GitHubBranch.Contains('..') -or $GitHubBranch -notmatch '^[A-Za-z0-9._/-]{1,80}$') {
        throw "SAFETY ABORT: Invalid GitHubBranch '$GitHubBranch'."
    }
    if ([string]::IsNullOrWhiteSpace($env:E2E_DISPOSABLE_GITHUB_PAT)) {
        throw 'SAFETY ABORT: -IncludeTestB requires environment variable E2E_DISPOSABLE_GITHUB_PAT scoped strictly to the disposable sample repository.'
    }
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = "e2e$(Get-Date -Format 'yyyyMMddHHmmss')"
}
$safeRunId = ($RunId -replace '[^A-Za-z0-9_-]', '')
if ([string]::IsNullOrWhiteSpace($safeRunId)) {
    throw 'RunId must contain at least one alphanumeric character.'
}

$namespace = "ServerForge_E2E_$safeRunId"
if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Join-Path 'C:\Temp' $namespace
}
$WorkspaceRoot = Resolve-E2EAbsolutePath $WorkspaceRoot

if ([string]::IsNullOrWhiteSpace($EvidenceDir)) {
    $EvidenceDir = Join-Path 'C:\Temp' "ServerForge_E2E_Evidence_$safeRunId"
}
$EvidenceDir = Resolve-E2EAbsolutePath $EvidenceDir
New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null

$manifestPath = Join-Path $WorkspaceRoot 'e2e-manifest.json'
$testAStatus = 'NOT_STARTED'
$testBStatus = if ($IncludeTestB) { 'NOT_STARTED' } else { 'SKIPPED' }
$setupSucceeded = $false
$overallFailure = $null

Write-Host "================================================================="
Write-Host " ServerForge Disposable Windows IIS E2E Acceptance (Phase 12.2)"
Write-Host " RunId        : $safeRunId"
Write-Host " Namespace    : $namespace"
Write-Host " Workspace    : $WorkspaceRoot"
Write-Host " EvidenceDir  : $EvidenceDir"
Write-Host " Ports        : Agent=$AgentPort | TEST_A=$AppPort | TEST_B=$GitHubAppPort"
Write-Host " TEST B Mode  : $(if ($IncludeTestB) { "ENABLED ($GitHubRepo @ $GitHubBranch)" } else { 'SKIPPED (TEST A only)' })"
Write-Host "================================================================="

try {
    # Step 1: Isolated Agent Setup (internally runs preflight.ps1 with all validated parameters)
    Write-Host "`n[1/4] Running setup-disposable-iis.ps1 (includes preflight.ps1)..."
    & (Join-Path $PSScriptRoot 'setup-disposable-iis.ps1') `
        -RunId $safeRunId `
        -AgentInstallerPath $AgentInstallerPath `
        -WorkspaceRoot $WorkspaceRoot `
        -AgentPort $AgentPort `
        -AppPort $AppPort `
        -GitHubAppPort $GitHubAppPort
    if ($LASTEXITCODE -ne 0) { throw "setup-disposable-iis.ps1 failed with exit code $LASTEXITCODE." }
    $setupSucceeded = $true

    if (-not (Test-Path $manifestPath)) {
        throw "Expected manifest '$manifestPath' was not created by setup-disposable-iis.ps1."
    }

    # Step 2: TEST A — Local Sample Artifact Deploy (v1 -> v2) + mTLS + Provenance Negative Check
    Write-Host "`n[2/4] Running TEST A deployment (run-deployment-tests.ps1)..."
    $testAStatus = 'FAILED'
    & (Join-Path $PSScriptRoot 'run-deployment-tests.ps1') `
        -ManifestPath $manifestPath `
        -GitHubBranch $GitHubBranch
    if ($LASTEXITCODE -ne 0) { throw "run-deployment-tests.ps1 failed with exit code $LASTEXITCODE." }

    # Step 3: TEST A Rollback — Unhealthy v3 (HTTP 503) -> Automatic Rollback to v2 + Agent Restart Resilience
    Write-Host "`n[3/4] Running TEST A rollback & restart verification (verify-rollback.ps1)..."
    & (Join-Path $PSScriptRoot 'verify-rollback.ps1') `
        -ManifestPath $manifestPath `
        -GitHubBranch $GitHubBranch
    if ($LASTEXITCODE -ne 0) { throw "verify-rollback.ps1 failed with exit code $LASTEXITCODE." }
    $testAStatus = 'PASSED'

    # Step 4: Optional TEST B — Genuine Autonomous GitHub Actions Deployment & Rollback
    if ($IncludeTestB) {
        Write-Host "`n[4/4] Running TEST B genuine autonomous GitHub deployment (run-github-autonomous-test.ps1)..."
        $testBStatus = 'FAILED'
        & (Join-Path $PSScriptRoot 'run-github-autonomous-test.ps1') `
            -ManifestPath $manifestPath `
            -GitHubRepo $GitHubRepo `
            -GitHubBranch $GitHubBranch
        if ($LASTEXITCODE -ne 0) { throw "run-github-autonomous-test.ps1 failed with exit code $LASTEXITCODE." }
        $testBStatus = 'PASSED'
    } else {
        Write-Host "`n[4/4] Skipping TEST B (-IncludeTestB not specified)."
    }
}
catch {
    $overallFailure = $_
    Write-Error (Protect-SanitizedText -Text "E2E Acceptance FAILED: $($_.Exception.Message)" -ExtraSecrets @($env:E2E_DISPOSABLE_GITHUB_PAT))
}
finally {
    if ($setupSucceeded -and (Test-Path $WorkspaceRoot)) {
        if ($null -ne $overallFailure -and $SkipCleanupOnFailure) {
            Write-Warning "Skipping cleanup because -SkipCleanupOnFailure was specified. Collecting sanitized diagnostics only..."
            & (Join-Path $PSScriptRoot 'collect-diagnostics.ps1') `
                -WorkspaceRoot $WorkspaceRoot `
                -OutputDir $EvidenceDir
        } else {
            Write-Host "`n[CLEANUP] Collecting sanitized diagnostics and removing ONLY owned $namespace* resources..."
            try {
                & (Join-Path $PSScriptRoot 'cleanup-disposable-iis.ps1') `
                    -WorkspaceRoot $WorkspaceRoot `
                    -PreserveDiagnosticsDir $EvidenceDir
            } catch {
                Write-Error "Cleanup failed: $($_.Exception.Message)"
                if ($null -eq $overallFailure) { $overallFailure = $_ }
            }
        }
    }

    Write-Host "`n================================================================="
    Write-Host " ServerForge Disposable Windows IIS E2E Summary"
    Write-Host " TEST A (Local Sample Artifact on Real IIS)       : $testAStatus"
    Write-Host " TEST B (Genuine Autonomous GitHub Actions on IIS): $testBStatus"
    Write-Host " Sanitized Evidence Directory                     : $EvidenceDir"
    Write-Host "================================================================="
}

if ($null -ne $overallFailure) {
    exit 1
}
exit 0
