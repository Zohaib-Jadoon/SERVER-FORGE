# ServerForge Test Report

> **Strict Honesty Policy**: Every verification line below explicitly distinguishes: **Verified in GitHub Actions**, **Failed in CI Run #12 (1 Test) — Repaired Pending CI Run #13**, **Verified on Interactive Windows 11 Desktop**, **Verified on Disposable Windows IIS Environment**, or **Blocked / Not Executed**.

## Four Independent Verification Statuses (Phase 12.2 — Run #13 Mock Repair)
1. **Windows Build, Automated Tests & Installers**: **VERIFIED AT RUN #10 (`44720053575` @ `5226d810789413e01775c9e9f4e0348d080e6b61`) — 42/42 xUnit + 50/50 Installer Smoke PASSED**; **Run #12 (`44745867909`) PASSED `.NET 10` COMPILE (0 errors) AND 47/48 xUnit TESTS** (1 unit test mock-routing failure at `ServerForgeUnitTests.cs:1562`; installers & smoke skipped). Fixed exact `AbsolutePath` routing on disk (**48 total xUnit tests, 0 skipped**) pending **Windows CI Run #13**.
2. **TEST A — Local Sample Artifact Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle (`run-disposable-iis-acceptance.ps1` or `run-deployment-tests.ps1` + `verify-rollback.ps1`, strictly `ManualSignedPackage` / `LOCAL TEST ARTIFACT`).
3. **TEST B — Real GitHub Actions Autonomous Deployment & Rollback on Real IIS**: **BLOCKED (Not Executed in Linux Container)** — Requires disposable Windows Server VM + disposable GitHub repository + `E2E_DISPOSABLE_GITHUB_PAT` (`run-disposable-iis-acceptance.ps1 -IncludeTestB` or `run-github-autonomous-test.ps1`, Desktop closed, ZERO `/api/v1/deployments/trigger` calls).
4. **Interactive Windows 11 Native WPF Desktop Verification**: **NOT VERIFIED** — Requires manual interactive Windows 11 execution of all 13 screens (`WINDOWS_DESKTOP_ACCEPTANCE.md`).

## Verified Windows CI Evidence (Run #10 `44720053575` & Run #12 `44745867909`)
- **Last Fully Green Baseline (Both Jobs)**: `Zohaib-Jadoon/SERVER-FORGE` @ `5226d810789413e01775c9e9f4e0348d080e6b61` — Run #10 (`https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44720053575`): `42/42` xUnit + installers + `SHA256SUMS.txt` + `50/50` smoke checks PASSED.
- **Run #12 (`https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44745867909`)**:
  - Native `.NET 10` compilation: **PASSED (0 errors)** (all 19 Run #11 errors confirmed fixed).
  - Automated xUnit suite: **47 / 48 PASSED** — all `17/17` Agent tests (including all 3 Phase 12.2 HTTP 304 / restart / superseding / rate-limit polling tests), `1/1` Integration, and `29/30` Unit tests PASSED.
  - **Single Failure**: `GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity` (`ServerForgeUnitTests.cs:1562`, `Assert.True(resolvedRes.Found)`, actual `false`) because `uri.Contains("/artifacts")` in the test mock matched `/actions/artifacts/556/zip` and returned artifact-list JSON instead of `validZipBytes`. Fixed on disk via exact `AbsolutePath` matching + `ResolutionStatus`/`ErrorMessage` diagnostics pending **Run #13**.

## Complete Verification Matrix (48 Total xUnit Tests + 50 Installer Smoke Checks + TEST A / TEST B E2E Harness)

| Verification Area | Classification | Evidence / Notes |
|---|---|---|
| Full `.NET 10` solution, WPF XAML & Sample App compilation (`ServerForge.sln` + `ServerForge.SampleApp.csproj`) | **Verified in GitHub Actions (Run #12 `44745867909`)** | **PASSED (0 errors)** with all Phase 12.2 production & test code. |
| Baseline Unit, Security, Provenance, IIS Safety, SQLite, DPAPI, Desktop ViewModel & Live mTLS Tests (42 baseline xUnit tests) | **Verified in GitHub Actions (Run #10 `44720053575` & Run #12 `44745867909`)** | All `42 / 42` baseline tests PASSED in both Run #10 and Run #12. |
| Phase 12.2 Autonomous Polling & Durable SQLite v3 Agent Tests (+3): HTTP 304 continuation, Agent restart + DPAPI PAT persistence/resume, commit superseding + terminal failure + rate-limit backoff | **Verified in GitHub Actions (Run #12 `44745867909`)** | **17 / 17 Agent tests PASSED** on real Windows in Run #12 (`AutonomousDeploymentAndRollbackTests.cs`). |
| Phase 12.2 Strict GitHub Identity & Archive Unit Tests (+3): strict artifact selection/size/ZIP/safe-redirect hardening, run/rate-limit state classification, and workflow-ID path resolution | **2/3 Verified in Run #12 (`44745867909`); 1/3 Mock-Routing Repaired Pending Run #13** | Fixed `uri.Contains("/artifacts")` collision in `GitHubApiClient_ResolvesMissingWorkflowPathByWorkflowId_AndRejectsUnmatchedOrMissingWorkflowIdentity` using exact `AbsolutePath` routing (`workflows/555`, `runs/911/artifacts`, `artifacts/556/zip`) without weakening production code. |
| Automated Desktop & Agent Windows Installer Smoke Suite (50 assertions) | **Verified in GitHub Actions (Run #10 `44720053575`); Skipped in Run #12 — Re-verify in Run #13** | **50 PASSED, 0 FAILED** in Run #10; will re-run automatically once Job 1 reaches `48/48` in Run #13. |
| **TEST A**: Real IIS Local Sample Artifact Deploy, Update, Unhealthy Rollback & Agent Restart Resilience (`run-disposable-iis-acceptance.ps1` / `run-deployment-tests.ps1` + `verify-rollback.ps1`) | **Blocked — Requires Disposable Windows IIS VM** | Strictly `ManualSignedPackage` (`LOCAL TEST ARTIFACT`, `TriggeredAutomatically = false`) + live provenance negative probe `TESTA.SEC.1` + on-disk `web.config` check. |
| **TEST B**: Real GitHub Actions Autonomous Push-to-IIS Deploy, Update, Unhealthy Rollback, Restart Non-Retry & Recovery (`run-disposable-iis-acceptance.ps1 -IncludeTestB` / `run-github-autonomous-test.ps1`) | **Blocked — Requires Disposable Windows IIS VM + Disposable GitHub Repo** | Desktop closed; ZERO calls to `/api/v1/deployments/trigger`; server-side GitHub API download + `E2E_DISPOSABLE_GITHUB_PAT`. |
| Interactive Windows 11 WPF Desktop Acceptance (All 13 Screens, Setup Wizard, Dark/Light Theme, System Tray UX) | **Not Verified (Requires Interactive Windows 11 Desktop)** | Complete checklist & evidence record in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`. |

## Local Linux Container Static & Companion Checks
- Verified exact `AbsolutePath` mock routing against `GitHubApiClient.cs` endpoints, zero remaining `/artifacts` substring collisions in download paths, balanced delimiters (`0/0/0`) in the modified test method, and exact `48` enabled / `0` skipped `[Fact]` tests across `ServerForge/tests/`.
- Executed companion utility tests (`src/utils/solutionTree.test.ts` — `3/3` pass), TypeScript lint, and build checks. Companion checks are never counted as Windows `.NET` or IIS verification.
