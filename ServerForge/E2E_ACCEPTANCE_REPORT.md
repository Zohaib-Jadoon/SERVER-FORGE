# ServerForge — End-to-End Acceptance Report (Phase 12.1)

**Last Updated:** 2026-11-20  
**Last Verified Windows CI Baseline:** Commit `b97cdfd56c5605a598620fee9683ac5ea56ea6f2` — GitHub Actions Run `44715138404` (**PASS**: `38/38` xUnit tests, Desktop & Agent installers built, SHA-256 verified, `50/50` installer smoke assertions)  
**Current Phase 12.1 Working State:** Server-side GitHub artifact provenance enforcement, hardened `e2e-disposable-iis.yml`, split TEST A / TEST B disposable IIS harness, and `+4` security/provenance xUnit tests (`42` total) — **Pending Windows CI Run #10**

---

## Independent Four-Gate Acceptance Status

| Gate | Scope | Current Status | Evidence / Blocker |
| --- | --- | --- | --- |
| **Gate 1 — Windows Build, Automated Tests & Installers** | Native `.NET 10` WPF + Agent build, xUnit suite, Inno Setup installers, SHA-256 checksums, 50/50 installer smoke tests | **VERIFIED AT RUN #9 (`38/38` + `50/50`) — PHASE 12.1 (`42` TESTS) PENDING CI RUN #10** | Run `44715138404` (`b97cdfd56c5605a598620fee9683ac5ea56ea6f2`) GREEN; Phase 12.1 security provenance changes ready for push |
| **Gate 2 — TEST A: Locally Built Sample Artifact on Actual Windows IIS** | Real IIS site/AppPool (`appcmd`, `w3wp.exe`, `AspNetCoreModuleV2`), `ManualSignedPackage` (`LOCAL TEST ARTIFACT`), v1 → v2 → HTTP 503 auto-rollback → Agent restart resilience | **BLOCKED — NO DISPOSABLE WINDOWS IIS VM ATTACHED** | Cannot execute IIS inside Linux container; never run against production IIS. Run `setup-disposable-iis.ps1` + `run-deployment-tests.ps1` + `verify-rollback.ps1` on a disposable Windows Server IIS VM |
| **Gate 3 — TEST B: Genuine Autonomous GitHub Actions Deployment & Rollback** | Dedicated disposable GitHub repo, root `.github/workflows/serverforge-deploy.yml`, test-only PAT, Desktop closed, ZERO `/api/v1/deployments/trigger` calls, real `GitHubAutoDeployWorker` v1 → v2 → unhealthy v3 rollback → non-retry after restart → v4 recovery | **BLOCKED — NO DISPOSABLE WINDOWS IIS VM / DISPOSABLE GITHUB REPO EXECUTED YET** | Dedicated `run-github-autonomous-test.ps1` implemented covering all 18 steps; awaiting execution on a disposable Windows IIS VM with a disposable test repo |
| **Gate 4 — Interactive Windows 11 WPF Desktop Acceptance** | Real Windows 11 GUI session across all 13 native WPF screens, setup wizard, pairing, backups, themes, close-to-tray, reopen, exit | **NOT VERIFIED** | Structured checklist ready in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`; requires real Windows 11 interactive desktop testing |

**Production Readiness Determination:** **NOT PRODUCTION-READY** until Gates 1 (CI Run #10), 2 (TEST A), 3 (TEST B), and 4 (Windows 11 GUI) all record real passing evidence.

---

## Phase 12.1 Critical Review Findings Addressed

### Finding 1 — Honest Split Between TEST A and TEST B
- **TEST A (`ServerForge/scripts/e2e/run-deployment-tests.ps1` + `verify-rollback.ps1`)**:
  - Registers the disposable app strictly in `DeploymentSourceMode.ManualSignedPackage` (`SourceMode = 2`, `AutoDeployEnabled = false`).
  - Explicitly passes `TriggeredAutomatically = false` and `ArtifactTrustSource = 'LOCAL TEST ARTIFACT (ManualSignedPackage)'`.
  - Records `"gitHubLivePollTested": false` and `"provesAutonomousGitHubPolling": false` in `deployment-evidence.json`.
  - Includes a live negative security probe (`TESTA.SEC.1`) confirming a `GitHubActionsArtifact` app rejects caller-supplied local ZIP paths with HTTP `400`.
- **TEST B (`ServerForge/scripts/e2e/run-github-autonomous-test.ps1`)**:
  - Uses a dedicated disposable GitHub repo containing root `.github/workflows/serverforge-deploy.yml` and `release-config.json`.
  - Syncs a test-only `E2E_DISPOSABLE_GITHUB_PAT` (never logged or saved in `.git/config`) to the Agent's DPAPI store.
  - Makes **ZERO calls** to `POST /api/v1/deployments/trigger`.
  - Pushes real commits via GitHub Contents API (`v1` healthy → `v2` healthy → `v3` unhealthy `simulateUnhealthy=true` → `v4` healthy recovery), waits for real GitHub Actions runs/artifacts, proves `GitHubAutoDeployWorker` deploys with Desktop closed, verifies HTTP 503 auto-rollback to `v2`, restarts the Agent to prove zero endless retries on `v3`, and records real `workflowRunId`, `artifactId`, `commitSha`, and `deploymentId` values in `github-autonomous-evidence.json`.

### Finding 2 — Server-Side GitHub Artifact Provenance Trust Boundary
- Added `IGitHubArtifactProvenanceVerifier` (`AgentGitHubProvenanceVerifier`) wired into `DeploymentOrchestrator` and `AgentHostFactory`.
- `POST /api/v1/deployments/trigger`:
  - **Rejects** (`HTTP 400`) any caller-supplied `ArtifactDownloadUrl` when `SourceMode == GitHubActionsArtifact`.
  - **Forces** `TriggeredAutomatically = false` on all external HTTP API calls so callers can never spoof autonomous polling.
- `DeploymentOrchestrator`:
  - In `GitHubActionsArtifact` mode, **fails closed** without a server-side `IGitHubArtifactProvenanceVerifier` (or in-process `VerifiedGitHubArtifact` from `GitHubAutoDeployWorker`), rejects caller-supplied paths/URLs, validates caller-supplied run/artifact IDs and digests against GitHub API truth, and verifies computed SHA-256 against the GitHub API digest.
  - In `ManualSignedPackage` mode, rejects remote HTTP/HTTPS URLs and invalid hashes, and normalizes trust labels so local packages can never masquerade as verified GitHub Actions provenance.
- `GitHubApiClient`:
  - Enforces HTTPS GitHub host allowlisting (`api.github.com` / `github.com`) on `archive_download_url`.

### Finding 3 — Hardened Optional Disposable IIS Workflow (`/.github/workflows/e2e-disposable-iis.yml`)
- Triggered **ONLY** via manual `workflow_dispatch`.
- Pre-dispatch validation job (`validate-inputs-and-runner`) passes all inputs via environment variables (zero direct `${{ inputs.* }}` interpolation inside shell/PowerShell code), validates `owner/repo` regex, branch regex, unprivileged non-conflicting ports (`1024..65535`, never `80`, `443`, or `9443`), and rejects `windows-latest` or any production-like runner label.
- Requires GitHub Environment approval (`disposable-windows-iis-e2e`), targets `[self-hosted, windows, <approved_runner_label>]`, and enforces a positive host identity marker (`C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker`) plus absence of production `ServerForgeAgent` services/identity files.

### Phase 12.1A — E2E Harness PowerShell 5.1 & 7 Audit Fixes
- Fixed `E2ECommon.psm1` for Windows PowerShell 5.1 (.NET Framework 4.8) AND PowerShell 7 (.NET 8+) compatibility:
  - Explicit `[string]::Empty` password overload on `X509Certificate2` constructor.
  - Per-request fail-closed `WebRequestHandler.ServerCertificateValidationCallback` fallback when `HttpClientHandler.ServerCertificateCustomValidationCallback` is unavailable on PowerShell 5.1.
- Fixed sample app workflow `.github/workflows/serverforge-deploy.yml` to upload flat `./publish-out/**` files (avoiding nested zip-inside-zip) and read `release-config.json` without direct `${{ inputs.* }}` PowerShell interpolation.
- Extended `cleanup-disposable-iis.ps1` to remove both TEST A (`ServerForge_E2E_<RunId>`) and TEST B (`ServerForge_E2E_<RunId>_GH`) owned sites/AppPools while asserting ownership prefixes and preserving all pre-existing sites.

---

## Real IIS Execution Evidence Template (To Be Filled Only After Genuine Disposable VM Execution)

- **TEST A (`deployment-evidence.json` + `rollback-evidence.json`):** `BLOCKED — NOT YET EXECUTED ON DISPOSABLE WINDOWS IIS VM`
- **TEST B (`github-autonomous-evidence.json`):** `BLOCKED — NOT YET EXECUTED ON DISPOSABLE WINDOWS IIS VM`
- **Interactive Windows 11 Desktop Acceptance:** `NOT VERIFIED`
