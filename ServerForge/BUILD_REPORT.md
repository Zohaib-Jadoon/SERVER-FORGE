# ServerForge Build Report

## Verified Windows CI Run History
- **Run #4 (`44684923525`)** — `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`: **PASSED**
  - `.NET 10` compilation: PASSED, zero NuGet advisories, `24/24` xUnit tests PASSED.
  - Unsigned self-contained `win-x64` installers generated & uploaded (`ServerForge-Desktop-Setup.exe` ~62.92 MB, `ServerForge-Agent-Setup.exe` ~46.64 MB, `SHA256SUMS.txt`).
- **Run #5 (`44688053844`)** — `d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`: **FAILED AT COMPILE (82 errors in `ServerForge.AgentTests`)** — fixed for Run #6.
- **Run #6 (`44689338386`)** — `4ad417c383ae524ece90861a77e10e92f03c56f3`: **COMPILE PASSED, 30/32 xUnit TESTS PASSED** (2 `SQLite Error 14` fixture failures — fixed for Run #7).
- **Run #7 (`44690875542`)** — `main` @ `0aef82a8ed92c47a2b03663912026e66b8545a92`: Job 1 (`32/32` tests + installers) GREEN; Job 2 failed at Desktop uninstall relative `/LOG=` (fixed for Run #8).
- **Run #8 (`44699230392`)** — `main` @ `2207141082e1e8024c0ff2036995d81a63ec1caa`: **100% GREEN ACROSS BOTH WINDOWS CI JOBS** (`32/32` xUnit tests, installers, `50/50` smoke assertions).
- **Run #9 (`44715138404`)** — `main` @ `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`: **100% GREEN ACROSS BOTH WINDOWS CI JOBS (VERIFIED PHASE 12 BASELINE)**
  - Native `.NET 10` Windows compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`): **PASSED** (0 errors, 0 NuGet advisories).
  - Automated xUnit test suite: **38 / 38 PASSED** (`25/25` Unit, `12/12` Agent, `1/1` Integration).
  - Self-contained `win-x64` publishing & Inno Setup 6 compilation: **PASSED** (`ServerForge-Desktop-Setup.exe`, `ServerForge-Agent-Setup.exe`, `SHA256SUMS.txt` verified & uploaded).
  - `smoke-test-windows-installers`: **50 PASSED, 0 FAILED** — Desktop install/upgrade/uninstall, Agent Windows Service install/restart/upgrade/uninstall, identity/cert persistence, loopback-only management endpoint (`127.0.0.1:9443`), and hosted-file preservation all verified on real Windows.

## Phase 12.1 Deliverables Added After Run #9 (`44715138404`) — Awaiting Windows CI Run #10 & Disposable IIS VM Execution
1. **Server-Side GitHub Artifact Provenance Hardening (Critical Review Finding 2)**:
   - Added `VerifiedGitHubArtifact`, `IGitHubArtifactProvenanceVerifier`, `AgentGitHubProvenanceVerifier`, `GitHubApiClient.IsTrustedGitHubArtifactUrl`, and `IDeploymentEngine.EnqueueAndExecuteWithVerifiedArtifactAsync`.
   - Hardened `POST /api/v1/deployments/trigger` (`AgentHostFactory.cs`) and `DeploymentOrchestrator`: in `GitHubActionsArtifact` mode, caller-supplied `ArtifactDownloadUrl`, local file paths, untrusted URLs, forged `VerifiedWorkflowRunId`/`VerifiedArtifactId`, self-declared SHA-256s, and spoofed `TriggeredAutomatically = true` are all rejected; the Agent resolves and downloads verified artifacts server-side via its own DPAPI-protected GitHub PAT.
   - Preserved explicit `ManualSignedPackage` mode strictly labeled `LOCAL TEST ARTIFACT` so local sample packages can never masquerade as verified GitHub Actions provenance.
   - Updated `GitHubAutoDeployWorker` and `MainViewModel` (`TriggerSelectedApplicationDeploymentAsync`) to align cleanly with server-side provenance resolution.
2. **4 New Security & Provenance Regression xUnit Tests (`38 -> 42` Total, `0` Skipped)**:
   - Added 2 unit tests in `ServerForgeUnitTests.cs` (`25 -> 27`) and 2 live Agent/mTLS tests in `AutonomousDeploymentAndRollbackTests.cs` (`12 -> 14` Agent tests).
3. **Hardened Optional Disposable IIS E2E Workflow (Critical Review Finding 3)**:
   - Hardened `/.github/workflows/e2e-disposable-iis.yml`: `workflow_dispatch`-only, all inputs passed exclusively through `env:` (zero `${{ inputs.* }}` interpolation in shell/PowerShell code), strict regex/port/runner validation in a pre-dispatch `validate-inputs-and-runner` job, removed `windows-latest` default, rejected production-like runner labels, required GitHub Environment `disposable-windows-iis-e2e`, and enforced positive disposable VM marker `C:\ProgramData\ServerForgeE2E\DISPOSABLE_IIS_E2E_HOST.marker`.
   - Left `/.github/workflows/build-windows-installers.yml` untouched to preserve the verified Run #9 green pipeline.
4. **Honest Split Between TEST A & TEST B + PowerShell 5.1/7 & Sample Workflow Fixes (Critical Review Finding 1)**:
   - **TEST A** (`run-deployment-tests.ps1`, `verify-rollback.ps1`): Real IIS deployment & rollback using locally built, hash-verified sample artifacts strictly in `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`, `gitHubLivePollTested = false`), plus live negative provenance probe (`TESTA.SEC.1`), on-disk `web.config` verification, and Agent Windows Service restart resilience.
   - **TEST B** (`run-github-autonomous-test.ps1`): Complete 18-step real GitHub Actions autonomous deployment & rollback harness using a dedicated disposable GitHub repo, `E2E_DISPOSABLE_GITHUB_PAT`, Desktop closed, and **ZERO** calls to `/api/v1/deployments/trigger`.
   - Fixed real-world bugs discovered during Phase 12.1A audit: PowerShell 5.1 compatibility in `E2ECommon.psm1` (`WebRequestHandler` certificate pinning fallback + `[string]::Empty` `X509Certificate2` overload) and nested zip-inside-zip bug in `samples/disposable-test-app/.github/workflows/serverforge-deploy.yml` (now uploads flat `./publish-out/**` and uses `release-config.json` with `env: INPUT_APP_VERSION`).

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` or sign with an operator-owned certificate before distribution.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, IIS, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to trigger Windows CI Run #10, then execute TEST A and TEST B on a disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`DISPOSABLE_IIS_TEST_GUIDE.md`).

## Next Steps to Trigger Windows CI Run #10
```powershell
git checkout -b feat/phase12-1-provenance-and-honest-e2e
# Extract updated archive into repository root, preserving /.github/workflows/
git add .github/workflows/e2e-disposable-iis.yml `
        ServerForge/src/ `
        ServerForge/tests/ `
        ServerForge/samples/disposable-test-app/ `
        ServerForge/scripts/e2e/ `
        ServerForge/E2E_ACCEPTANCE_REPORT.md `
        ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md `
        ServerForge/DISPOSABLE_IIS_TEST_GUIDE.md `
        ServerForge/INSTALLATION.md ServerForge/MEMORY.md `
        ServerForge/BUILD_REPORT.md ServerForge/TEST_REPORT.md `
        ServerForge/DEVELOPMENT_STATUS.md ServerForge/PRODUCTION_READINESS_REPORT.md
git commit -m "Phase 12.1: server-side GitHub provenance, hardened E2E workflow, TEST A/TEST B split, and PS 5.1/artifact fixes"
git push -u origin feat/phase12-1-provenance-and-honest-e2e
```
