# ServerForge Test Report

> **Strict Honesty Policy**: Every verification line below explicitly distinguishes: **Verified in GitHub Actions**, **Implemented — Pending CI Run #11**, **Verified on Interactive Windows 11 Desktop**, **Verified on Disposable Windows IIS Environment**, or **Blocked / Not Executed**.

## Four Independent Verification Statuses (Phase 12.2)
1. **Windows Build, Automated Tests & Installers**: **VERIFIED IN RUN #10 (`44720053575` @ `5226d810789413e01775c9e9f4e0348d080e6b61`) — 42/42 xUnit + 50/50 Installer Smoke PASSED**; Phase 12.2 adds **+6** autonomous polling & strict GitHub provenance tests (**48 total, 0 skipped**) pending **Windows CI Run #11**.
2. **TEST A — Local Sample Artifact Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`run-disposable-iis-acceptance.ps1` or `run-deployment-tests.ps1` + `verify-rollback.ps1`, strictly `ManualSignedPackage` / `LOCAL TEST ARTIFACT`).
3. **TEST B — Real GitHub Actions Autonomous Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM + disposable GitHub repository + `E2E_DISPOSABLE_GITHUB_PAT` (`run-disposable-iis-acceptance.ps1 -IncludeTestB` or `run-github-autonomous-test.ps1`, Desktop closed, ZERO `/api/v1/deployments/trigger` calls).
4. **Interactive Windows 11 Native WPF Desktop Verification**: **NOT VERIFIED** — Requires manual interactive Windows 11 execution of all 13 screens (`WINDOWS_DESKTOP_ACCEPTANCE.md`).

## Verified Windows CI Run #10 Evidence (`44720053575`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` @ `5226d810789413e01775c9e9f4e0348d080e6b61`
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44720053575`
- **Job 1 (`build-test-package-windows`)**: **100% PASSED**
  - Compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`): **PASSED** (0 errors across all projects; zero NuGet advisories).
  - Automated xUnit Results: **42 / 42 PASSED** (`27/27` Unit, `14/14` Agent including live Kestrel HTTPS/mTLS, server-side GitHub provenance rejection, autonomous worker anti-retry-loop, interrupted-restart recovery, and Desktop ViewModel tests, `1/1` Integration).
  - Publishing & Inno Setup 6 Packaging: **PASSED** (both self-contained `win-x64` installers compiled, SHA-256 verified, and uploaded).
- **Job 2 (`smoke-test-windows-installers`)**: **100% PASSED — 50 PASSED, 0 FAILED**
  - Verified Desktop silent install, runtime files, Uninstall registry, upgrade preserving `%LOCALAPPDATA%\ServerForge`, canary survival, and clean registered uninstall.
  - Verified Agent silent install, Windows Service Auto/recovery, first-boot identity/pairing artifacts, loopback-only `127.0.0.1:9443`, `Restart-Service` identity/cert persistence, upgrade preserving `appsettings.json`, and clean uninstall removing service/binary/registry while preserving `C:\ServerForgeApps` hosted files.

## Complete Verification Matrix (48 Total xUnit Tests + 50 Installer Smoke Checks + TEST A / TEST B E2E Harness)

| Verification Area | Classification | Evidence / Notes |
|---|---|---|
| Full `.NET 10` solution, WPF XAML & Sample App compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`) | **Verified in GitHub Actions (Run #10 `44720053575`)** | Re-verify in Run #11 after Phase 12.2 autonomous polling, SQLite v3 & `GitHubApiClient` hardening. |
| Baseline Unit, Security, Provenance, IIS Safety, SQLite, DPAPI, Desktop ViewModel & Live mTLS Tests (42 xUnit tests) | **Verified in GitHub Actions (Run #10 `44720053575`)** | `42 / 42 PASSED` on real Windows (`27` Unit, `14` Agent, `1` Integration). |
| Automated Desktop & Agent Windows Installer Smoke Suite (50 assertions) | **Verified in GitHub Actions (Run #10 `44720053575`)** | **50 PASSED, 0 FAILED** across install, upgrade, restart, loopback, data preservation, and clean uninstall. |
| Phase 12.2 Strict GitHub Identity & Archive Unit Tests (+3): workflow ID path resolution/rejection, strict artifact selection/size/ZIP/safe-redirect hardening, and run/rate-limit state classification | **Implemented — Pending GitHub Actions Run #11** | Added in `ServerForgeUnitTests.cs` (`27 -> 30` unit tests) + updated legacy v0→v3 migration test. |
| Phase 12.2 Autonomous Polling & Durable State Tests (+3): HTTP 304 continuation after commit detected before artifact ready, Agent restart candidate persistence/resume under HTTP 304, commit superseding + failed-workflow terminal stop + rate-limit backoff | **Implemented — Pending GitHub Actions Run #11** | Added in `AutonomousDeploymentAndRollbackTests.cs` (`14 -> 17` Agent tests; `48` total xUnit tests, `0` skipped). |
| **TEST A**: Real IIS Local Sample Artifact Deploy, Update, Unhealthy Rollback & Agent Restart Resilience (`run-disposable-iis-acceptance.ps1` / `run-deployment-tests.ps1` + `verify-rollback.ps1`) | **Blocked — Requires Disposable Windows IIS VM** | Strictly `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`) + live provenance negative probe `TESTA.SEC.1` + on-disk `web.config` check. |
| **TEST B**: Real GitHub Actions Autonomous Push-to-IIS Deploy, Update, Unhealthy Rollback, Restart Non-Retry & Recovery (`run-disposable-iis-acceptance.ps1 -IncludeTestB` / `run-github-autonomous-test.ps1`) | **Blocked — Requires Disposable Windows IIS VM + Disposable GitHub Repo** | Desktop closed; ZERO calls to `/api/v1/deployments/trigger`; server-side GitHub API download + `E2E_DISPOSABLE_GITHUB_PAT`. |
| Interactive Windows 11 WPF Desktop Acceptance (All 13 Screens, Setup Wizard, Dark/Light Theme, System Tray UX) | **Not Verified (Requires Interactive Windows 11 Desktop)** | Complete checklist & evidence record in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`. |

## Local Linux Container Static & Companion Checks
- Audited all 9 Phase 12.2 PowerShell E2E scripts (`E2ECommon.psm1`, `run-disposable-iis-acceptance.ps1`, and 7 step scripts) for Windows PowerShell 5.1/7 compatibility (zero `?.`/`??` operators, `WebRequestHandler` fallback, `[string]::Empty` `X509Certificate2` overload), balanced syntax, strict `ServerForge_E2E_` ownership guards, exact positive disposable host marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, secret scrubbing, and exact mandatory parameter alignment.
- Audited `/.github/workflows/e2e-disposable-iis.yml` for zero `${{ inputs.* }}` interpolation in `run:` blocks, `workflow_dispatch`-only trigger, `disposable-windows-iis-e2e` environment approval gate, exact `SERVERFORGE_DISPOSABLE_E2E_HOST=true` marker check, and removal of `windows-latest`.
- Audited all 48 xUnit `[Fact]` tests (`0` skipped), C# brace/using balance across modified files, and companion utility unit tests (`src/utils/solutionTree.test.ts`). Companion checks are never counted as Windows `.NET` or IIS verification.
