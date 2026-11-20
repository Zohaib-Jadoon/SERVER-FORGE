# ServerForge Test Report

> **Strict Honesty Policy**: Every verification line below explicitly distinguishes: **Verified in GitHub Actions**, **Implemented — Pending CI Run #9**, **Verified on Interactive Windows 11 Desktop**, **Verified on Disposable Windows IIS Environment**, or **Blocked / Not Executed**.

## Verified Windows CI Run #8 Evidence (`44699230392`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` @ `2207141082e1e8024c0ff2036995d81a63ec1caa`
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44699230392`
- **Job 1 (`build-test-package-windows`)**: **100% PASSED**
  - Compilation (`ServerForge.sln`): **PASSED** (0 errors across all 14 projects; zero NuGet advisories).
  - Automated xUnit Results: **32 / 32 PASSED** (`22/22` Unit, `9/9` Agent including all 5 live Kestrel HTTPS/mTLS tests and all 3 Desktop ViewModel tests, `1/1` Integration).
  - Publishing & Inno Setup 6 Packaging: **PASSED** (both self-contained `win-x64` installers compiled, SHA-256 verified, and uploaded).
- **Job 2 (`smoke-test-windows-installers`)**: **100% PASSED — 50 PASSED, 0 FAILED**
  - Verified Desktop silent install, runtime files, Uninstall registry, upgrade preserving `%LOCALAPPDATA%\ServerForge`, canary survival, and clean registered uninstall.
  - Verified Agent silent install, Windows Service Auto/recovery, first-boot identity/pairing artifacts, loopback-only `127.0.0.1:9443`, `Restart-Service` identity/cert persistence, upgrade preserving `appsettings.json`, and clean uninstall removing service/binary/registry while preserving `C:\ServerForgeApps` hosted files.

## Complete Verification Matrix (38 Total xUnit Tests + 50 Installer Smoke Checks + Disposable IIS E2E Harness)

| Verification Area | Classification | Evidence / Notes |
|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **Verified in GitHub Actions (Run #8 `44699230392`)** | Re-verify in Run #9 with `ServerForge.SampleApp.csproj` build check added to `build-windows.ps1`. |
| Existing Core, Security, IIS Safety, SQLite Migration, DPAPI, Desktop ViewModel & Live mTLS Tests (32 xUnit tests) | **Verified in GitHub Actions (Run #8 `44699230392`)** | `32 / 32 PASSED` on real Windows (`22` Unit, `9` Agent, `1` Integration). |
| Automated Desktop & Agent Windows Installer Smoke Suite (50 assertions) | **Verified in GitHub Actions (Run #8 `44699230392`)** | **50 PASSED, 0 FAILED** across install, upgrade, restart, loopback, data preservation, and clean uninstall. |
| Phase 12 Unit Tests (+3): `AspNetCore` `web.config` env injection & rollback config preservation, isolated loopback IIS binding/health probe, GitHub negative artifact/API rejection | **Implemented — Pending GitHub Actions Run #9** | Added in `ServerForgeUnitTests.cs` (`22 -> 25` unit tests). |
| Phase 12 Live Integration Tests (+3): Live Kestrel mTLS deploy/update/rollback over loopback HTTP target, Autonomous Worker anti-retry-loop & recovery commit, Interrupted Agent restart recovery | **Implemented — Pending GitHub Actions Run #9** | Added in `AutonomousDeploymentAndRollbackTests.cs` (`9 -> 12` Agent tests; `38` total xUnit tests). |
| Phase 12 Disposable Windows IIS 6-Script E2E Harness (`ServerForge/scripts/e2e/*.ps1`) & Optional Manual Workflow (`e2e-disposable-iis.yml`) | **Implemented — Not Executed on Disposable IIS VM (Blocked in Linux Container)** | Execute `ServerForge/DISPOSABLE_IIS_TEST_GUIDE.md` on a disposable Windows Server VM with IIS + `.NET 10` Hosting Bundle. |
| Interactive Windows 11 WPF Desktop Acceptance (All 13 Screens, Setup Wizard, Dark/Light Theme, System Tray UX) | **Not Verified (Requires Interactive Windows 11 Desktop)** | Complete checklist & evidence record in `ServerForge/WINDOWS_DESKTOP_ACCEPTANCE.md`. |
| Live Remote Windows Server VM Autonomous GitHub Push-to-IIS Deployment & Rollback | **Blocked (Requires Disposable Windows Server VM + Disposable GitHub Repo)** | Full procedure documented in `ServerForge/DISPOSABLE_IIS_TEST_GUIDE.md` §4 and `ServerForge/samples/disposable-test-app/README.md`. |

## Local Linux Container Static & Companion Checks
- Audited all 7 Phase 12 PowerShell E2E scripts (`E2ECommon.psm1` + 6 harness scripts) for PowerShell 5.1/7 compatibility (zero `?.`/`??` operators), balanced syntax, strict `ServerForge_E2E_` ownership guards, secret scrubbing, and exact `PlatformEnums` values (`AspNetCore=5`, `Successful=8`, `RolledBack=10`, `Cancelled=11`).
- Audited all 38 xUnit `[Fact]` tests (`0` skipped) and companion utility unit tests (`src/utils/solutionTree.test.ts`). Companion checks are never counted as Windows `.NET` or IIS verification.
