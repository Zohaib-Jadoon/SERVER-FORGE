# ServerForge Test Report

> **Strict Honesty Policy**: Every verification line below explicitly distinguishes: **Verified in GitHub Actions**, **Failed in CI Run #11 — Repaired Pending CI Run #12**, **Verified on Interactive Windows 11 Desktop**, **Verified on Disposable Windows IIS Environment**, or **Blocked / Not Executed**.

## Four Independent Verification Statuses (Phase 12.2 — Run #12 Repair)
1. **Windows Build, Automated Tests & Installers**: **VERIFIED AT RUN #10 (`44720053575` @ `5226d810789413e01775c9e9f4e0348d080e6b61`) — 42/42 xUnit + 50/50 Installer Smoke PASSED**; **Run #11 (`44739965264` @ `1ae32e9b4224373c8cc568aa965ab3b3954cac6f`) FAILED AT COMPILE (19 test errors)** — all 19 errors are repaired on disk (**48 total xUnit tests, 0 skipped**) pending **Windows CI Run #12**.
2. **TEST A — Local Sample Artifact Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`run-disposable-iis-acceptance.ps1` or `run-deployment-tests.ps1` + `verify-rollback.ps1`, strictly `ManualSignedPackage` / `LOCAL TEST ARTIFACT`).
3. **TEST B — Real GitHub Actions Autonomous Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM + disposable GitHub repository + `E2E_DISPOSABLE_GITHUB_PAT` (`run-disposable-iis-acceptance.ps1 -IncludeTestB` or `run-github-autonomous-test.ps1`, Desktop closed, ZERO `/api/v1/deployments/trigger` calls).
4. **Interactive Windows 11 Native WPF Desktop Verification**: **NOT VERIFIED** — Requires manual interactive Windows 11 execution of all 13 screens (`WINDOWS_DESKTOP_ACCEPTANCE.md`).

## Verified Windows CI Run #10 Evidence (`44720053575`) & Run #11 Failure (`44739965264`)
- **Last Green Baseline**: `Zohaib-Jadoon/SERVER-FORGE` @ `5226d810789413e01775c9e9f4e0348d080e6b61` — Run #10 (`https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44720053575`)
  - **Job 1 (`build-test-package-windows`)**: **100% PASSED** — `.NET 10` compilation (0 errors), **42 / 42** xUnit PASSED (`27` Unit, `14` Agent, `1` Integration), self-contained installers + `SHA256SUMS.txt` built & uploaded.
  - **Job 2 (`smoke-test-windows-installers`)**: **100% PASSED — 50 PASSED, 0 FAILED** across Desktop and Agent Windows Service lifecycles.
- **Failed Run #11 (`https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44739965264` @ `1ae32e9b4224373c8cc568aa965ab3b3954cac6f`)**:
  - Failed during C# test compilation with **19 errors**: `3 × CS0246` (`LoopbackSampleIisHost`), `4 × CS0103` (`CreateSampleAspNetCoreArtifactAsync`) + `8 × CS8130` tuple inference cascades, `3 × CS1061` (`AgentIdentityStore.UpdateState`), and `1 × CS9007` (interpolated raw JSON closing braces at `ServerForgeUnitTests.cs:1491`).
  - **Zero** Phase 12.2 tests executed and **zero** new installers were produced in Run #11. All 19 errors are now fixed on disk for **Run #12**.

## Complete Verification Matrix (48 Total xUnit Tests + 50 Installer Smoke Checks + TEST A / TEST B E2E Harness)

| Verification Area | Classification | Evidence / Notes |
|---|---|---|
| Full `.NET 10` solution, WPF XAML & Sample App compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`) | **Verified in Run #10 (`44720053575`); Failed in Run #11 (19 test errors) — Repaired Pending Run #12** | All 19 test compilation errors fixed (`LiveLoopbackTargetHost.StartAsync()`, `CreateAspNetCoreSampleArtifactAsync`, `StoreProtectedGitHubPat`, `JsonSerializer.Serialize`). |
| Baseline Unit, Security, Provenance, IIS Safety, SQLite, DPAPI, Desktop ViewModel & Live mTLS Tests (42 xUnit tests) | **Verified in GitHub Actions (Run #10 `44720053575`)** | `42 / 42 PASSED` on real Windows (`27` Unit, `14` Agent, `1` Integration). |
| Automated Desktop & Agent Windows Installer Smoke Suite (50 assertions) | **Verified in GitHub Actions (Run #10 `44720053575`)** | **50 PASSED, 0 FAILED** across install, upgrade, restart, loopback, data preservation, and clean uninstall. |
| Phase 12.2 Strict GitHub Identity & Archive Unit Tests (+3): workflow ID path resolution/rejection, strict artifact selection/size/ZIP/safe-redirect hardening, and run/rate-limit state classification | **Repaired — Pending GitHub Actions Run #12** | `CS9007` eliminated via `JsonSerializer.Serialize(...)` across all 3 unit tests in `ServerForgeUnitTests.cs` (`27 -> 30` unit tests) + legacy v0→v3 migration test. |
| Phase 12.2 Autonomous Polling & Durable State Tests (+3): HTTP 304 continuation after commit detected before artifact ready, Agent restart candidate + DPAPI PAT persistence/resume under HTTP 304, commit superseding + failed-workflow terminal stop + rate-limit backoff | **Repaired — Pending GitHub Actions Run #12** | Fixed helper names, `LiveLoopbackTargetHost.StartAsync()`, `StoreProtectedGitHubPat`, and reloaded `AgentIdentityStore` check in `AutonomousDeploymentAndRollbackTests.cs` (`14 -> 17` Agent tests; `48` total, `0` skipped). |
| **TEST A**: Real IIS Local Sample Artifact Deploy, Update, Unhealthy Rollback & Agent Restart Resilience (`run-disposable-iis-acceptance.ps1` / `run-deployment-tests.ps1` + `verify-rollback.ps1`) | **Blocked — Requires Disposable Windows IIS VM** | Strictly `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`) + live provenance negative probe `TESTA.SEC.1` + on-disk `web.config` check. |
| **TEST B**: Real GitHub Actions Autonomous Push-to-IIS Deploy, Update, Unhealthy Rollback, Restart Non-Retry & Recovery (`run-disposable-iis-acceptance.ps1 -IncludeTestB` / `run-github-autonomous-test.ps1`) | **Blocked — Requires Disposable Windows IIS VM + Disposable GitHub Repo** | Desktop closed; ZERO calls to `/api/v1/deployments/trigger`; server-side GitHub API download + `E2E_DISPOSABLE_GITHUB_PAT`. |
| Interactive Windows 11 WPF Desktop Acceptance (All 13 Screens, Setup Wizard, Dark/Light Theme, System Tray UX) | **Not Verified (Requires Interactive Windows 11 Desktop)** | Complete checklist & evidence record in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`. |

## Local Linux Container Static & Companion Checks
- Audited all 42 C# files under `ServerForge/` for zero nonexistent helper references (`LoopbackSampleIisHost`, `CreateSampleAspNetCoreArtifactAsync`, `.UpdateState(`), zero `CS9007` raw-interpolated brace hazards, exact delimiter balance, and exact `48` enabled / `0` skipped `[Fact]` tests.
- Audited all 9 Phase 12.2 PowerShell E2E scripts (`E2ECommon.psm1`, `run-disposable-iis-acceptance.ps1`, and 7 step scripts) for Windows PowerShell 5.1/7 compatibility, balanced syntax, exact mandatory parameters, and exact positive disposable host marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`.
- Executed companion utility tests (`src/utils/solutionTree.test.ts`), TypeScript lint, and build checks. Companion checks are never counted as Windows `.NET` or IIS verification.
