# ServerForge — Engineering Memory & Verified Baseline

## Verified Native Platform Baseline (DO NOT REGRESS)
- **Repository:** `https://github.com/Zohaib-Jadoon/SERVER-FORGE`
- **Architecture:** Native `.NET 10 LTS` Windows WPF Desktop (`ServerForge.Desktop`, 13 native WPF screens + WinForms `NotifyIcon`) + Windows Service Agent (`ServerForge.Agent`, loopback/VPN mTLS) + SQLite EF Core + Inno Setup 6 installers.
- **Latest Verified Windows CI Run #9 (`44715138404` @ commit `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`):**
  - Native `.NET 10` solution build + sample app build: **PASS**
  - Automated xUnit tests: **38 / 38 PASS** (`0` skipped)
  - Desktop & Agent self-contained publishing + Inno Setup 6 installers + SHA-256 verification: **PASS**
  - `smoke-test-windows-installers`: **50 / 50 PASS**
  - Both Windows CI jobs: **GREEN**

## Phase 12.1 Security & Acceptance Hardening (Pending CI Run #10)
1. **Server-Side GitHub Artifact Provenance (Critical Review Finding 2):**
   - Added `IGitHubArtifactProvenanceVerifier` (`AgentGitHubProvenanceVerifier`) and wired it into `DeploymentOrchestrator` and `AgentHostFactory`.
   - `POST /api/v1/deployments/trigger` rejects (`HTTP 400`) caller-supplied `ArtifactDownloadUrl` for `GitHubActionsArtifact` apps and forces `TriggeredAutomatically = false` on all external API requests.
   - `DeploymentOrchestrator` fails closed in `GitHubActionsArtifact` mode without a server-side `IGitHubArtifactProvenanceVerifier` (or in-process `VerifiedGitHubArtifact` from `GitHubAutoDeployWorker`), verifies caller-supplied run/artifact IDs and digests against GitHub API truth, and strictly labels `ManualSignedPackage` as `LOCAL TEST ARTIFACT (ManualSignedPackage - NOT GitHub Provenance)` / `ManualSignedPackage`.
   - `GitHubApiClient.IsTrustedGitHubArtifactUrl` enforces HTTPS GitHub host allowlisting (`api.github.com` / `github.com`).
2. **Hardened Optional E2E Workflow (Critical Review Finding 3):**
   - `/.github/workflows/e2e-disposable-iis.yml` remains strictly `workflow_dispatch`-only, separated from `build-windows-installers.yml`.
   - Eliminated all direct `${{ inputs.* }}` interpolation into shell/PowerShell code; added pre-dispatch `validate-inputs-and-runner` job checking strict regex allowlists, unprivileged non-conflicting ports (`1024..65535`, never `9443`), rejecting `windows-latest` and production-like runner labels.
   - Added GitHub Environment approval (`disposable-windows-iis-e2e`) and positive Windows host identity marker enforcement (`C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker`).
3. **Honest Split of TEST A vs TEST B + PowerShell 5.1/7 Audit (Critical Review Finding 1 & Phase 12.1A):**
   - **TEST A (`run-deployment-tests.ps1` + `verify-rollback.ps1`)**: Explicitly uses `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`), adds live negative provenance probe (`TESTA.SEC.1`), on-disk `web.config` check, and Agent restart resilience check. Explicitly reports `gitHubLivePollTested = false`.
   - **TEST B (`run-github-autonomous-test.ps1`)**: New 18-step genuine autonomous GitHub Actions test with ZERO calls to `/api/v1/deployments/trigger`, pushing real commits (`v1`, `v2`, `v3-unhealthy`, `v4-recovery`) via GitHub Contents API to a disposable sample repo, waiting for real GitHub Actions runs/artifacts, proving `GitHubAutoDeployWorker` deploys with Desktop closed, auto-rolls back HTTP 503, resists endless retries across Agent restarts, and recovers on `v4`.
   - Fixed `E2ECommon.psm1` for PowerShell 5.1 compatibility (`[string]::Empty` `X509Certificate2` overload + `WebRequestHandler.ServerCertificateValidationCallback` pinning fallback) and fixed sample app workflow to upload flat `./publish-out/**`.
4. **Expanded Automated xUnit Suite (`38 → 42` tests, `0` skipped):**
   - `+2` Unit tests (`DeploymentOrchestrator_GitHubActionsMode_RejectsLocalPath_UntrustedUrl_ForgedIds_AndDigestMismatch`, `GitHubApiClient_RejectsUntrustedArtifactDownloadHost_AndNonHttpsUrls`).
   - `+2` Agent/mTLS tests (`LiveMtls_GitHubActionsMode_RejectsSpoofedLocalArtifact_ForgedIds_AndSpoofedAutoDeployFlag`, `AgentGitHubProvenanceVerifier_RejectsUnauthorizedRepo_BranchMismatch_MissingPat_AndForgedRunOrArtifactIds`).

## Remaining External Gates (Must Stay Honestly Reported)
1. **Windows CI Run #10:** Push Phase 12.1 commit and verify `42/42` xUnit + `50/50` smoke checks pass.
2. **TEST A on Disposable Windows IIS VM:** `BLOCKED` until executed on a real disposable Windows Server IIS VM.
3. **TEST B on Disposable Windows IIS VM + Disposable GitHub Repo:** `BLOCKED` until executed with a real disposable GitHub sample repo.
4. **Interactive Windows 11 WPF Desktop Acceptance:** `NOT VERIFIED` until completed on Windows 11 (`WINDOWS_DESKTOP_ACCEPTANCE.md`).
