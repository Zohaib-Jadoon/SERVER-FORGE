# ServerForge Test Report

> **Strict Honesty Policy**: Every verification line below explicitly distinguishes: **Verified in GitHub Actions**, **Implemented — Pending CI Run #10**, **Verified on Interactive Windows 11 Desktop**, **Verified on Disposable Windows IIS Environment**, or **Blocked / Not Executed**.

## Four Independent Verification Statuses (Phase 12.1)
1. **Windows Build, Automated Tests & Installers**: **VERIFIED IN RUN #9 (`44715138404` @ `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`) — 38/38 xUnit + 50/50 Installer Smoke PASSED**; Phase 12.1 adds **+4** provenance/security tests (**42 total, 0 skipped**) pending **Windows CI Run #10**.
2. **TEST A — Local Sample Artifact Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`run-deployment-tests.ps1` + `verify-rollback.ps1`, strictly `ManualSignedPackage` / `LOCAL TEST ARTIFACT`).
3. **TEST B — Real GitHub Actions Autonomous Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM + disposable GitHub repository + `E2E_DISPOSABLE_GITHUB_PAT` (`run-github-autonomous-test.ps1`, Desktop closed, ZERO `/api/v1/deployments/trigger` calls).
4. **Interactive Windows 11 Native WPF Desktop Verification**: **NOT VERIFIED** — Requires manual interactive Windows 11 execution of all 13 screens (`WINDOWS_DESKTOP_ACCEPTANCE.md`).

## Verified Windows CI Run #9 Evidence (`44715138404`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` @ `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44715138404`
- **Job 1 (`build-test-package-windows`)**: **100% PASSED**
  - Compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`): **PASSED** (0 errors across all projects; zero NuGet advisories).
  - Automated xUnit Results: **38 / 38 PASSED** (`25/25` Unit, `12/12` Agent including live Kestrel HTTPS/mTLS, autonomous worker anti-retry-loop, interrupted-restart recovery, and Desktop ViewModel tests, `1/1` Integration).
  - Publishing & Inno Setup 6 Packaging: **PASSED** (both self-contained `win-x64` installers compiled, SHA-256 verified, and uploaded).
- **Job 2 (`smoke-test-windows-installers`)**: **100% PASSED — 50 PASSED, 0 FAILED**
  - Verified Desktop silent install, runtime files, Uninstall registry, upgrade preserving `%LOCALAPPDATA%\ServerForge`, canary survival, and clean registered uninstall.
  - Verified Agent silent install, Windows Service Auto/recovery, first-boot identity/pairing artifacts, loopback-only `127.0.0.1:9443`, `Restart-Service` identity/cert persistence, upgrade preserving `appsettings.json`, and clean uninstall removing service/binary/registry while preserving `C:\ServerForgeApps` hosted files.

## Complete Verification Matrix (42 Total xUnit Tests + 50 Installer Smoke Checks + TEST A / TEST B E2E Harness)

| Verification Area | Classification | Evidence / Notes |
|---|---|---|
| Full `.NET 10` solution, WPF XAML & Sample App compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`) | **Verified in GitHub Actions (Run #9 `44715138404`)** | Re-verify in Run #10 after Phase 12.1 server-side provenance changes. |
| Baseline Unit, Security, IIS Safety, SQLite, DPAPI, Desktop ViewModel, Live mTLS & Phase 12 Tests (38 xUnit tests) | **Verified in GitHub Actions (Run #9 `44715138404`)** | `38 / 38 PASSED` on real Windows (`25` Unit, `12` Agent, `1` Integration). |
| Automated Desktop & Agent Windows Installer Smoke Suite (50 assertions) | **Verified in GitHub Actions (Run #9 `44715138404`)** | **50 PASSED, 0 FAILED** across install, upgrade, restart, loopback, data preservation, and clean uninstall. |
| Phase 12.1 Provenance Unit Tests (+2): `DeploymentOrchestrator_GitHubActionsMode_RejectsLocalPath_UntrustedUrl_ForgedIds_AndDigestMismatch`, `GitHubApiClient_RejectsUntrustedArtifactDownloadHost_AndNonHttpsUrls` | **Implemented — Pending GitHub Actions Run #10** | Added in `ServerForgeUnitTests.cs` (`25 -> 27` unit tests). |
| Phase 12.1 Live Agent & Verifier Tests (+2): `LiveMtls_GitHubActionsMode_RejectsSpoofedLocalArtifact_ForgedIds_AndSpoofedAutoDeployFlag`, `AgentGitHubProvenanceVerifier_RejectsUnauthorizedRepo_BranchMismatch_MissingPat_AndForgedRunOrArtifactIds` | **Implemented — Pending GitHub Actions Run #10** | Added in `AutonomousDeploymentAndRollbackTests.cs` (`12 -> 14` Agent tests; `42` total xUnit tests, `0` skipped). |
| **TEST A**: Real IIS Local Sample Artifact Deploy, Update, Unhealthy Rollback & Agent Restart Resilience (`run-deployment-tests.ps1` + `verify-rollback.ps1`) | **Blocked — Requires Disposable Windows IIS VM** | Strictly `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`) + live provenance negative probe `TESTA.SEC.1` + on-disk `web.config` check. |
| **TEST B**: Real GitHub Actions Autonomous Push-to-IIS Deploy, Update, Unhealthy Rollback, Restart Non-Retry & Recovery (`run-github-autonomous-test.ps1`) | **Blocked — Requires Disposable Windows IIS VM + Disposable GitHub Repo** | Desktop closed; ZERO calls to `/api/v1/deployments/trigger`; server-side GitHub API download + `E2E_DISPOSABLE_GITHUB_PAT`. |
| Interactive Windows 11 WPF Desktop Acceptance (All 13 Screens, Setup Wizard, Dark/Light Theme, System Tray UX) | **Not Verified (Requires Interactive Windows 11 Desktop)** | Complete checklist & evidence record in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`. |

## Local Linux Container Static & Companion Checks
- Audited all 8 Phase 12.1 PowerShell E2E scripts (`E2ECommon.psm1` + 7 harness scripts) for Windows PowerShell 5.1/7 compatibility (zero `?.`/`??` operators, `WebRequestHandler` fallback, `[string]::Empty` `X509Certificate2` overload), balanced syntax, strict `ServerForge_E2E_` ownership guards, positive disposable host marker checks, secret scrubbing, and exact `PlatformEnums` values (`AspNetCore=5`, `GitHubActionsArtifact=0`, `ManualSignedPackage=2`, `Successful=8`, `Failed=9`, `RolledBack=10`, `Cancelled=11`).
- Audited `/.github/workflows/e2e-disposable-iis.yml` for zero `${{ inputs.* }}` interpolation in `run:` blocks, `workflow_dispatch`-only trigger, `disposable-windows-iis-e2e` environment approval gate, and removal of `windows-latest`.
- Audited all 42 xUnit `[Fact]` tests (`0` skipped) and companion utility unit tests (`src/utils/solutionTree.test.ts`). Companion checks are never counted as Windows `.NET` or IIS verification.
