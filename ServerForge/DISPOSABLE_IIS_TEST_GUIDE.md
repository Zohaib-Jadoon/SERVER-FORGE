# ServerForge — Disposable Windows IIS E2E Test Guide (Phase 12.2)

This guide provides the exact, reproducible commands to run **TEST A** (locally built, hash-verified sample artifact deployed to real IIS) and **TEST B** (genuine autonomous GitHub Actions polling, update, HTTP 503 rollback, anti-retry-loop, and recovery) on a **dedicated disposable Windows Server IIS VM**.

> **CRITICAL SAFETY RULE:** NEVER run these scripts or `e2e-disposable-iis.yml` against a production Windows Server or a machine running a production `ServerForgeAgent` Windows Service.

---

## 1. Disposable Windows IIS VM Prerequisites

Provision a fresh, disposable Windows Server 2022/2025 (or Windows 11 Pro) VM with:

1. **Microsoft IIS (`Web-Server`)** installed and `W3SVC` running (`appcmd.exe` and `w3wp.exe` present under `C:\Windows\System32\inetsrv\`).
2. **.NET 10 Windows Hosting Bundle (`AspNetCoreModuleV2`)** installed (`aspnetcorev2.dll` present) and `iisreset` completed.
3. **.NET 10 LTS SDK** installed (for publishing `ServerForge.SampleApp` in TEST A or building installers locally).
4. **Verified `ServerForge-Agent-Setup.exe`** downloaded from the latest passing GitHub Actions run (`build-windows-installers.yml`) with SHA-256 verified against `SHA256SUMS.txt` (or built locally via `build-windows.ps1` + `build-installers.ps1` with Inno Setup 6).
5. **No production ServerForge installation**: `Get-Service ServerForgeAgent` must not exist and `C:\ProgramData\ServerForge\Agent\agent-identity.json` must not exist.
6. **Positive Disposable Host Identity Marker (EXACT REQUIRED CONTENT)**:
   Both `E2ECommon.psm1` (`preflight.ps1` / `run-disposable-iis-acceptance.ps1`) and `.github/workflows/e2e-disposable-iis.yml` enforce the exact string `SERVERFORGE_DISPOSABLE_E2E_HOST=true`:
   ```powershell
   New-Item -ItemType Directory -Path 'C:\ProgramData\ServerForgeE2E' -Force | Out-Null
   Set-Content -Path 'C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker' -Value 'SERVERFORGE_DISPOSABLE_E2E_HOST=true' -Encoding UTF8
   ```
7. **PowerShell 5.1 or PowerShell 7+**: All scripts under `ServerForge/scripts/e2e/` support both Windows PowerShell 5.1 (`WebRequestHandler` TLS thumbprint pinning) and PowerShell 7+ (`HttpClientHandler.ServerCertificateCustomValidationCallback`).

---

## 2. Recommended: Single Safe Orchestration Entry Point (`run-disposable-iis-acceptance.ps1`)

Run from an **elevated Administrator PowerShell prompt** at the repository root on the disposable IIS VM.

### Option A — Run TEST A Only (Real IIS with Locally Built Sample Artifacts)
```powershell
.\ServerForge\scripts\e2e\run-disposable-iis-acceptance.ps1 `
    -AgentInstallerPath '.\ServerForge\artifacts\installers\ServerForge-Agent-Setup.exe' `
    -ConfirmDisposableHost `
    -RunId "e2e$(Get-Date -Format 'yyyyMMddHHmm')" `
    -AgentPort 19443 `
    -AppPort 15199 `
    -GitHubAppPort 15200
```

### Option B — Run TEST A + TEST B (Includes Genuine Autonomous GitHub Actions Deploy & Rollback)
First prepare your **dedicated disposable GitHub repository** (see Section 4 below), then run:
```powershell
# Keep disposable test-only PAT strictly in process memory (never written to disk or .git/config)
$env:E2E_DISPOSABLE_GITHUB_PAT = Read-Host -Prompt 'Enter disposable repo Fine-Grained PAT'

.\ServerForge\scripts\e2e\run-disposable-iis-acceptance.ps1 `
    -AgentInstallerPath '.\ServerForge\artifacts\installers\ServerForge-Agent-Setup.exe' `
    -ConfirmDisposableHost `
    -RunId "e2e$(Get-Date -Format 'yyyyMMddHHmm')" `
    -AgentPort 19443 `
    -AppPort 15199 `
    -GitHubAppPort 15200 `
    -IncludeTestB `
    -GitHubRepo '<your-user>/serverforge-e2e-sample-disposable' `
    -GitHubBranch 'main'

Remove-Item Env:\E2E_DISPOSABLE_GITHUB_PAT -ErrorAction SilentlyContinue
```

---

## 3. Complete Step-by-Step Manual Commands (All Required Parameters Verified)

Use these exact commands if running each stage individually on a disposable Windows Server IIS VM (elevated Administrator PowerShell at repo root):

```powershell
$RunId          = "e2e$(Get-Date -Format 'yyyyMMddHHmm')"
$Workspace      = "C:\Temp\ServerForge_E2E_$RunId"
$EvidenceOut    = "C:\Temp\ServerForge_E2E_Evidence_$RunId"
$AgentInstaller = ".\ServerForge\artifacts\installers\ServerForge-Agent-Setup.exe"
$Manifest       = Join-Path $Workspace 'e2e-manifest.json'

# 1. Preflight safety & IIS inventory (-RunId is mandatory)
.\ServerForge\scripts\e2e\preflight.ps1 `
    -RunId $RunId `
    -WorkspaceRoot $Workspace `
    -AgentPort 19443 `
    -AppPort 15199 `
    -GitHubAppPort 15200

# 2. Setup isolated disposable Agent Windows Service (-RunId and -AgentInstallerPath are mandatory)
.\ServerForge\scripts\e2e\setup-disposable-iis.ps1 `
    -RunId $RunId `
    -AgentInstallerPath $AgentInstaller `
    -WorkspaceRoot $Workspace `
    -AgentPort 19443 `
    -AppPort 15199 `
    -GitHubAppPort 15200

# 3. TEST A — Real IIS local verified sample deploy v1 -> v2 + mTLS + negative provenance probe (-ManifestPath is mandatory)
.\ServerForge\scripts\e2e\run-deployment-tests.ps1 `
    -ManifestPath $Manifest `
    -GitHubBranch 'main'

# 4. TEST A Rollback — Unhealthy v3 (HTTP 503) -> auto-rollback to v2 + Agent restart resilience (-ManifestPath is mandatory)
.\ServerForge\scripts\e2e\verify-rollback.ps1 `
    -ManifestPath $Manifest `
    -GitHubBranch 'main'

# 5. Optional TEST B — Genuine autonomous GitHub Actions deploy, update, rollback, anti-retry & recovery
#    (-ManifestPath and -GitHubRepo are mandatory; E2E_DISPOSABLE_GITHUB_PAT env var required)
$env:E2E_DISPOSABLE_GITHUB_PAT = Read-Host -Prompt 'Enter disposable repo Fine-Grained PAT'
.\ServerForge\scripts\e2e\run-github-autonomous-test.ps1 `
    -ManifestPath $Manifest `
    -GitHubRepo '<your-user>/serverforge-e2e-sample-disposable' `
    -GitHubBranch 'main'
Remove-Item Env:\E2E_DISPOSABLE_GITHUB_PAT -ErrorAction SilentlyContinue

# 6. Collect sanitized diagnostics explicitly (-WorkspaceRoot is mandatory)
.\ServerForge\scripts\e2e\collect-diagnostics.ps1 `
    -WorkspaceRoot $Workspace `
    -OutputDir $EvidenceOut

# 7. Clean up ONLY owned ServerForge_E2E_<RunId>* IIS sites, AppPools & Agent service (-WorkspaceRoot is mandatory)
.\ServerForge\scripts\e2e\cleanup-disposable-iis.ps1 `
    -WorkspaceRoot $Workspace `
    -PreserveDiagnosticsDir $EvidenceOut
```

### Exact Script Parameter Reference (Verified Against Script `param(...)` Definitions)

| Script | Mandatory Parameters | Optional Parameters (Defaults) |
|---|---|---|
| `run-disposable-iis-acceptance.ps1` | `-AgentInstallerPath`, `-ConfirmDisposableHost` | `-RunId` (timestamp), `-WorkspaceRoot`, `-EvidenceDir`, `-AgentPort` (19443), `-AppPort` (15199), `-GitHubAppPort` (15200), `-IncludeTestB`, `-GitHubRepo`, `-GitHubBranch` (`main`), `-SkipCleanupOnFailure` |
| `preflight.ps1` | `-RunId` | `-WorkspaceRoot`, `-AgentPort` (19443), `-AppPort` (15199), `-GitHubAppPort` (15200), `-AllowProductionHostOverride` |
| `setup-disposable-iis.ps1` | `-RunId`, `-AgentInstallerPath` | `-WorkspaceRoot`, `-AgentPort` (19443), `-AppPort` (15199), `-GitHubAppPort` (15200), `-AllowProductionHostOverride` |
| `run-deployment-tests.ps1` | `-ManifestPath` | `-SampleAppDir`, `-GitHubBranch` (`main`) |
| `verify-rollback.ps1` | `-ManifestPath` | `-SampleAppDir`, `-GitHubBranch` (`main`) |
| `run-github-autonomous-test.ps1` | `-ManifestPath`, `-GitHubRepo` | `-GitHubBranch` (`main`), `-WorkflowTimeoutSeconds` (420), `-AgentPollTimeoutSeconds` (180) |
| `collect-diagnostics.ps1` | `-WorkspaceRoot` | `-OutputDir` |
| `cleanup-disposable-iis.ps1` | `-WorkspaceRoot` | `-PreserveDiagnosticsDir` |

---

## 4. Disposable Sample GitHub Repository Setup (Required for TEST B)

1. Create a **dedicated disposable GitHub repository** (e.g., `<your-user>/serverforge-e2e-sample-disposable`) — NEVER use a production repository.
2. Copy the contents of `ServerForge/samples/disposable-test-app/` into the **root** of that disposable repository so that:
   - `.github/workflows/serverforge-deploy.yml` is at the repository root.
   - `ServerForge.SampleApp.csproj`, `Program.cs`, and `release-config.json` are at the repository root.
3. Confirm the workflow produces a single flat deployment artifact (`serverforge-sample-app-win-x64`) with a GitHub-reported SHA-256 digest (`digest: sha256:...`).
4. Create a **test-only Fine-Grained Personal Access Token** scoped **ONLY** to that single disposable repository with:
   - `Contents`: Read and write (so `run-github-autonomous-test.ps1` pushes `v1`, `v2`, `v3-unhealthy`, and `v4-recovery` commits via the GitHub Contents API without ever writing credentials to `.git/config`).
   - `Actions`: Read-only (so `ServerForge.Agent` verifies workflow runs, resolves exact workflow identity, and downloads signed artifacts with server-reported SHA-256 digests).
5. TEST B makes **ZERO calls to `POST /api/v1/deployments/trigger`** and confirms `ServerForge.Desktop` is not running while the real `GitHubAutoDeployWorker` autonomously polls (including across HTTP 304 branch-head responses while artifacts build), deploys, rolls back unhealthy `v3`, survives Agent restart without retrying `v3`, and recovers on `v4`.

---

## 5. Evidence Files Produced in `$EvidenceOut`

Inspect `$EvidenceOut` after cleanup:
- `deployment-evidence.json` — TEST A real IIS deployment evidence (`ManualSignedPackage` / `LOCAL TEST ARTIFACT`, live HTTP `/`, `/health`, `/data`, on-disk `web.config`, `TESTA.SEC.1` provenance probe).
- `rollback-evidence.json` — TEST A HTTP 503 automatic rollback & Agent restart resilience evidence.
- `github-autonomous-evidence.json` — TEST B genuine autonomous GitHub Actions commit SHAs, workflow run IDs, artifact IDs, deployment IDs, rollback proof, restart non-retry proof, and recovery proof.
- `preflight-inventory.json`, `iis-snapshot.json`, redacted `agent-*.log`, and Windows Event Log excerpts (zero PATs or pairing codes).

---

## 6. Optional Manual GitHub Actions Workflow (`/.github/workflows/e2e-disposable-iis.yml`)

To dispatch from GitHub Actions onto your self-hosted disposable Windows IIS VM:
1. Create the required disposable marker file on the VM (`C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker` containing `SERVERFORGE_DISPOSABLE_E2E_HOST=true`).
2. Register the VM as a self-hosted GitHub Actions runner with labels: `self-hosted`, `windows`, `disposable-windows-iis-e2e`.
3. Create GitHub Environment `disposable-windows-iis-e2e` with **Required Reviewers** enabled, and add secret `E2E_DISPOSABLE_GITHUB_PAT` (only if running TEST B).
4. Manually dispatch **Optional Disposable Windows IIS E2E Acceptance (Manual Only)** (`workflow_dispatch` only; `windows-latest` and production-like runner labels are strictly blocked).
5. Approve the Environment gate and download the uploaded `serverforge-e2e-iis-evidence-<run_id>` artifact to record evidence in `E2E_ACCEPTANCE_REPORT.md`.
