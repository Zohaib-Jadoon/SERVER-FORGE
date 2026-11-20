# ServerForge — Disposable Windows IIS E2E Test Guide (Phase 12.1)

This guide provides the exact, reproducible steps to run **TEST A** (locally built, hash-verified sample artifact deployed to real IIS) and **TEST B** (genuine autonomous GitHub Actions polling, update, HTTP 503 rollback, anti-retry-loop, and recovery) on a **dedicated disposable Windows Server IIS VM**.

> **CRITICAL SAFETY RULE:** NEVER run these scripts or `e2e-disposable-iis.yml` against a production Windows Server or a machine running a production `ServerForgeAgent` Windows Service.

---

## 1. Disposable Windows IIS VM Prerequisites

Provision a fresh, disposable Windows Server 2022/2025 (or Windows 11 Pro) VM with:

1. **Microsoft IIS (`Web-Server`)** installed and `W3SVC` running (`appcmd.exe` and `w3wp.exe` present under `C:\Windows\System32\inetsrv\`).
2. **.NET 10 Windows Hosting Bundle (`AspNetCoreModuleV2`)** installed (`aspnetcorev2.dll` present) and `iisreset` completed.
3. **.NET 10 LTS SDK** installed (for building `ServerForge.SampleApp` and installers if building locally).
4. **Inno Setup 6 (`ISCC.exe`)** installed (only needed if building `ServerForge-Agent-Setup.exe` directly on the VM; otherwise download the verified installer artifact from GitHub Actions Run #9/#10 and verify `SHA256SUMS.txt`).
5. **No production ServerForge installation**: `Get-Service ServerForgeAgent` must not exist and `C:\ProgramData\ServerForge\Agent\agent-identity.json` must not exist.
6. **Positive Disposable Host Identity Marker** (MANDATORY safety gate enforced by `preflight.ps1` and `e2e-disposable-iis.yml`):
   ```powershell
   New-Item -ItemType Directory -Path 'C:\ProgramData\ServerForgeE2E' -Force | Out-Null
   Set-Content -Path 'C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker' -Value 'SERVERFORGE_DISPOSABLE_E2E_HOST=true' -Encoding UTF8
   ```
7. **PowerShell 5.1 or PowerShell 7+**: All scripts in `ServerForge/scripts/e2e/` are tested for PowerShell 5.1 (.NET Framework 4.8 `WebRequestHandler` TLS thumbprint pinning) and PowerShell 7+ (`HttpClientHandler.ServerCertificateCustomValidationCallback`).

---

## 2. Running TEST A — Real IIS with Locally Built Sample Artifacts (`ManualSignedPackage`)

TEST A verifies:
- Fail-closed mTLS pairing, wrong-thumbprint rejection, pairing replay rejection, anonymous rejection, Agent restart identity persistence.
- Live negative provenance check (`TESTA.SEC.1`) confirming `GitHubActionsArtifact` rejects caller-supplied local ZIP paths (`HTTP 400`).
- Real IIS site/AppPool creation on isolated loopback port `127.0.0.1:15199`, on-disk `web.config` env var injection, v1 → v2 updates with Desktop closed, persistent `/data` survival, HTTP 503 automatic rollback, Agent service restart resilience, and unmanaged IIS site preservation.

Run from an **elevated Administrator PowerShell prompt** on the disposable IIS VM:

```powershell
$RunId = "e2e$(Get-Date -Format 'yyyyMMddHHmm')"
$Workspace = "C:\Temp\ServerForge_E2E_$RunId"
$AgentInstaller = ".\ServerForge\artifacts\installers\ServerForge-Agent-Setup.exe"

# 1. Setup isolated Agent Windows Service + run preflight
.\ServerForge\scripts\e2e\setup-disposable-iis.ps1 `
    -RunId $RunId `
    -AgentInstallerPath $AgentInstaller `
    -WorkspaceRoot $Workspace `
    -AgentPort 19443 `
    -AppPort 15199 `
    -GitHubAppPort 15200

$Manifest = Join-Path $Workspace 'e2e-manifest.json'

# 2. Execute TEST A (v1 -> v2 LOCAL TEST ARTIFACT + provenance negative check)
.\ServerForge\scripts\e2e\run-deployment-tests.ps1 -ManifestPath $Manifest

# 3. Execute TEST A Rollback & Agent Restart Resilience (v3 HTTP 503 -> auto-rollback to v2)
.\ServerForge\scripts\e2e\verify-rollback.ps1 -ManifestPath $Manifest
```

---

## 3. Running TEST B — Genuine Autonomous GitHub Actions Deployment & Rollback

TEST B makes **ZERO calls to `POST /api/v1/deployments/trigger`** and proves the real `GitHubAutoDeployWorker` autonomously polls GitHub, verifies workflow runs/artifacts/digests, deploys with Desktop closed, rolls back unhealthy releases, prevents infinite retry loops across Agent restarts, and recovers on a new healthy commit.

### Disposable Sample GitHub Repository Setup
1. Create a **dedicated disposable GitHub repository** (e.g., `<your-user>/serverforge-e2e-sample-disposable`) — NEVER use a production repository.
2. Copy the contents of `ServerForge/samples/disposable-test-app/` into the **root** of that disposable repository so that:
   - `.github/workflows/serverforge-deploy.yml` is at the repository root.
   - `ServerForge.SampleApp.csproj`, `Program.cs`, and `release-config.json` are at the repository root.
3. Create a **test-only Fine-Grained Personal Access Token** scoped **ONLY** to that single disposable repository with:
   - `Contents`: Read and write (so `run-github-autonomous-test.ps1` can push `v1`, `v2`, `v3-unhealthy`, and `v4-recovery` commits via GitHub Contents API without storing credentials in `.git/config`).
   - `Actions`: Read-only (so `ServerForge.Agent` can verify completed workflow runs and download signed artifacts with server-reported SHA-256 digests).
   - Never place production credentials or production certificates in the disposable repo or VM.

### Execute TEST B on the Disposable IIS VM
Run immediately after TEST A (reusing the same paired disposable Agent and `e2e-manifest.json`):

```powershell
# Keep token strictly in process memory (never logged, never saved to disk)
$env:E2E_DISPOSABLE_GITHUB_PAT = Read-Host -Prompt 'Enter disposable repo Fine-Grained PAT'

.\ServerForge\scripts\e2e\run-github-autonomous-test.ps1 `
    -ManifestPath $Manifest `
    -GitHubRepo '<your-user>/serverforge-e2e-sample-disposable' `
    -GitHubBranch 'main'

Remove-Item Env:\E2E_DISPOSABLE_GITHUB_PAT -ErrorAction SilentlyContinue
```

---

## 4. Collect Sanitized Diagnostics & Clean Up Owned Resources

Always run cleanup afterward (removes ONLY `ServerForge_E2E_<RunId>*` IIS sites, AppPools, Windows Service, and workspace while preserving pre-existing IIS sites and copying redacted evidence to `$EvidenceOut`):

```powershell
$EvidenceOut = "C:\Temp\ServerForge_E2E_Evidence_$RunId"
.\ServerForge\scripts\e2e\cleanup-disposable-iis.ps1 `
    -WorkspaceRoot $Workspace `
    -PreserveDiagnosticsDir $EvidenceOut
```

Inspect `$EvidenceOut`:
- `deployment-evidence.json` (TEST A evidence)
- `rollback-evidence.json` (TEST A rollback & restart evidence)
- `github-autonomous-evidence.json` (TEST B real GitHub run IDs, artifact IDs, commit SHAs, deployment IDs)
- `preflight-inventory.json`, `iis-snapshot.json`, redacted `agent-*.log`, and Windows Event Log excerpts.

---

## 5. Optional Manual GitHub Actions Workflow (`e2e-disposable-iis.yml`)

To run via GitHub Actions instead of RDP:
1. Register your disposable Windows IIS VM as a **self-hosted GitHub Actions runner** with labels: `self-hosted`, `windows`, `disposable-windows-iis-e2e`.
2. Create GitHub Environment `disposable-windows-iis-e2e` with **Required Reviewers** enabled, and add secret `E2E_DISPOSABLE_GITHUB_PAT` (only if running TEST B).
3. Manually dispatch **Optional Disposable Windows IIS E2E Acceptance (Manual Only)** with:
   - `confirm_disposable_environment = true`
   - `runner_label = disposable-windows-iis-e2e` (note: `windows-latest` and any `prod`/`live` label are strictly rejected)
   - `test_mode = test-a-local-iis-only` or `test-a-and-test-b-github-autonomous`
   - `disposable_github_repo = <owner>/<disposable-repo>` (for TEST B)
4. Approve the Environment deployment gate and download the uploaded `serverforge-e2e-iis-evidence-<run_id>` artifact for `E2E_ACCEPTANCE_REPORT.md`.
