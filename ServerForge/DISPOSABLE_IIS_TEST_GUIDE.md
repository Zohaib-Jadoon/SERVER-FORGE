# ServerForge — Disposable Windows IIS End-to-End Test Guide

This guide describes how to execute **real Windows IIS end-to-end deployment, update, and automatic rollback verification** using the reproducible Phase 12 harness (`ServerForge/scripts/e2e/*.ps1`) without ever risking a production server, production IIS site, or existing database.

## 1. Strict Safety Rules
- **NEVER run on a production Windows Server.** `preflight.ps1` automatically aborts if a `ServerForgeAgent` Windows Service or `C:\ProgramData\ServerForge\Agent\agent-identity.json` already exists.
- **No automatic role mutation.** The harness never installs Windows Server roles or alters host-wide IIS settings.
- **Strict ownership namespace.** Every test resource uses the prefix `ServerForge_E2E_<RunId>`:
  - IIS Site: `ServerForge_E2E_<RunId>` (bound strictly to `http/127.0.0.1:<AppPort>:`)
  - IIS AppPool: `ServerForge_E2E_<RunId>_Pool` (`No Managed Code`, `ApplicationPoolIdentity`)
  - Agent Service: `ServerForge_E2E_<RunId>_Agent` (bound strictly to `https://127.0.0.1:<AgentPort>`)
  - Workspace: `C:\Temp\ServerForge_E2E_<RunId>`
- **Zero secret exposure.** Pairing codes, PATs, passwords, and private keys are never written to console output, JSON evidence, or diagnostic bundles.

## 2. Disposable Windows Server VM Prerequisites
Provision a **fresh disposable Windows Server 2022/2025 VM** (or isolated Windows 11 lab VM) with:
1. **Microsoft IIS (`Web-Server` role)** installed and `W3SVC` service running.
2. **.NET 10 Windows Hosting Bundle** installed (`AspNetCoreModuleV2` / `aspnetcorev2.dll` present in IIS).
3. **.NET 10 SDK** installed (to compile `ServerForge/samples/disposable-test-app/ServerForge.SampleApp.csproj`).
4. Downloaded `ServerForge-Agent-Setup.exe` and `SHA256SUMS.txt` from a green CI build (verify SHA-256 first).

## 3. Option A — Run the 6-Script Harness Directly on the Disposable IIS VM

Open an **Administrator PowerShell 7 (`pwsh`) or Windows PowerShell 5.1** console at the repository root:

```powershell
$RunId          = "vm$(Get-Date -Format 'yyyyMMddHHmm')"
$WorkspaceRoot  = "C:\Temp\ServerForge_E2E_$RunId"
$AgentInstaller = (Resolve-Path ".\ServerForge\artifacts\installers\ServerForge-Agent-Setup.exe").Path
$EvidenceOut    = "C:\Temp\ServerForge_E2E_Evidence_$RunId"

try {
    # Step 1: Preflight inventory & conflict check (read-only)
    & .\ServerForge\scripts\e2e\preflight.ps1 `
        -RunId $RunId -WorkspaceRoot $WorkspaceRoot -AgentPort 19443 -AppPort 15199

    # Step 2: Provision isolated ServerForge_E2E_<RunId> Agent Windows Service & manifest
    & .\ServerForge\scripts\e2e\setup-disposable-iis.ps1 `
        -RunId $RunId -AgentInstallerPath $AgentInstaller -WorkspaceRoot $WorkspaceRoot `
        -AgentPort 19443 -AppPort 15199

    $ManifestPath = Join-Path $WorkspaceRoot 'e2e-manifest.json'

    # Step 3: Execute Phase 12D (mTLS pairing/restart), 12E (real IIS v1 deploy), & 12F (v2 update + duplicate prevention)
    & .\ServerForge\scripts\e2e\run-deployment-tests.ps1 `
        -ManifestPath $ManifestPath

    # Step 4: Execute Phase 12G (SIMULATE_UNHEALTHY=true -> HTTP 503 -> automatic rollback -> data preservation -> anti-retry-loop)
    & .\ServerForge\scripts\e2e\verify-rollback.ps1 `
        -ManifestPath $ManifestPath
}
finally {
    # Step 5 & 6: Collect sanitized diagnostics/Event Logs & remove ONLY ServerForge_E2E_<RunId> resources
    & .\ServerForge\scripts\e2e\cleanup-disposable-iis.ps1 `
        -WorkspaceRoot $WorkspaceRoot -PreserveDiagnosticsDir $EvidenceOut
}
```

Inspect `$EvidenceOut` for:
- `preflight-inventory.json` (pre-existing IIS baseline)
- `deployment-evidence.json` (mTLS pairing, IIS Site ID, v1/v2 HTTP status, commit SHAs, `/data` marker)
- `rollback-evidence.json` (`SIMULATE_UNHEALTHY=true` detection, `RolledBack` state, restored v2 version & `/data` marker)
- `iis-snapshot.json`, redacted `agent-*.log`, `windows-application-events.txt`, and `windows-system-events.txt`

## 4. Option B — Full Live GitHub Repository Autonomous Push-to-IIS Verification
To verify autonomous GitHub Actions polling end-to-end with Desktop closed:
1. Create a **disposable** GitHub repository (e.g., `<your-user>/serverforge-e2e-sample`) and push the contents of `/ServerForge/samples/disposable-test-app/` so `.github/workflows/serverforge-deploy.yml` sits at the repository root.
2. Confirm GitHub Actions builds and uploads artifact `serverforge-sample-app-win-x64` (`app-release.zip` + `app-release.zip.sha256` with embedded `release-metadata.json`).
3. Create a least-privilege Fine-Grained PAT scoped **only** to that disposable repository (`Contents: Read`, `Actions: Read`, `Metadata: Read`).
4. Pair ServerForge Desktop (on Windows 11) with your disposable Windows Server VM Agent over private VPN (WireGuard/Tailscale), import the disposable repo, enable Auto-Deploy with explicit authorization, deploy v1, **exit ServerForge Desktop completely**, push commit v2 to `main`, and verify the Agent autonomously polls, verifies the artifact SHA-256 digest from the GitHub API, and updates IIS without Desktop running.
5. Push a commit or set `SIMULATE_UNHEALTHY=true` and confirm automatic rollback restores v2 while preserving `/data`.

## 5. Option C — Optional Manual GitHub Actions Workflow (`e2e-disposable-iis.yml`)
Register a self-hosted GitHub Actions runner on your disposable Windows Server IIS VM with label `windows-iis-e2e` (never on a production host), then trigger **Actions -> Optional Disposable Windows IIS E2E Acceptance (Manual Only)** with:
- `confirm_disposable_environment`: `true`
- `runner_label`: `windows-iis-e2e`
- `agent_port`: `19443`
- `app_port`: `15199`

Sanitized evidence and logs are automatically uploaded to artifact `serverforge-e2e-iis-evidence-<run_id>`, and only `ServerForge_E2E_gh<run_id>` resources are cleaned up.
