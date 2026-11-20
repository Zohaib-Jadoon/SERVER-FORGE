# ServerForge Development Status

## Verified GitHub Actions Milestones
- **Run #1 (`44678399756`)** (`main` @ `f2261702b0f4dfd8a0a1bb6d4d0c20aa056cd732`): Confirmed root workflow `/.github/workflows/build-windows-installers.yml` triggers on `windows-latest`; surfaced WPF `CS0104` and NuGet advisories.
- **Run #2 (`44679766730`)** (`main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`): `.NET 10` compilation PASSED, NuGet advisories RESOLVED, `17/18` xUnit tests passed; surfaced SQLite FK `ProjectGroupId` test failure.
- **Run #3 (`44681873469`)** (`main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`): `.NET 10` compilation PASSED, SQLite FK PASSED, `19/20` xUnit tests passed; surfaced SQLite `DateTimeOffset` `ORDER BY` failure.
- **Run #4 (`44684923525`)** (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`): **FULL GREEN BUILD & INSTALLER PACKAGING**:
  - Native `.NET 10` Windows compilation: **PASSED**.
  - Automated xUnit tests: **24 / 24 PASSED**.
  - Self-contained `win-x64` Desktop (`~62.92 MB`) & Agent (`~46.64 MB`) Inno Setup 6 installers + `SHA256SUMS.txt`: **GENERATED & UPLOADED**.
- **Run #5 (`44688053844`)** (`d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`): **FAILED AT TEST COMPILATION (82 errors in `ServerForge.AgentTests`)**
  - Caused by `<UseWPF>true</UseWPF>` suppressing implicit `System.IO` in `ServerForge.AgentTests`, `TestDbContextFactory` missing explicit `CreateDbContextAsync`, and invalid `DatabaseCount` named argument on `DatabaseHealthDto`.

## Repairs Completed for Windows CI Run #6
- Added explicit `using System.IO;` to all 3 `ServerForge.AgentTests` files while keeping WPF/WinForms collision guards intact.
- Implemented `CreateDbContextAsync` explicitly on `TestDbContextFactory` and typed `factory` as `IDbContextFactory<ServerForgeDbContext>`.
- Fixed `DatabaseHealthDto` 6-parameter constructor call and aligned `DnsGuidanceOutput` assertion with `DomainValidator.BuildManualDnsInstructions`.
- Fixed `AgentHostFactory` to set `WebApplicationOptions.ContentRootPath = AppContext.BaseDirectory` when running as a Windows Service (`WindowsServiceHelpers.IsWindowsService()`), ensuring `appsettings.json` is always read from the install folder instead of `C:\Windows\System32`.
- Hardened `smoke-test-installers.ps1` `finally` cleanup (StrictMode scope, disposable service stop/delete fallback, Desktop fallback uninstall, always capture Agent logs on failure).

## 8-Stage Independent Verification Status (Never Collapsed Into One Percentage)

| Stage | Status | Evidence |
|---|---|---|
| 1. Source implementation completed | **COMPLETED** | All 11 `.NET 10` projects, 13 WPF screens, 32 xUnit tests, Inno scripts, 18-check smoke suite, and disposable sample app implemented. |
| 2. Windows CI compilation verified | **VERIFIED IN RUN #4 (`44684923525`); FAILED IN RUN #5 (`44688053844`); REPAIRED — PENDING RUN #6** | All 3 compilation root causes fixed on disk; awaits push for CI Run #6. |
| 3. Installer packaging verified | **VERIFIED IN RUN #4 (`44684923525`); RE-VERIFY PENDING RUN #6** | Desktop (`~62.92 MB`) + Agent (`~46.64 MB`) + `SHA256SUMS.txt` verified in Run #4; updated `.iss` scripts await CI Run #6. |
| 4. Installer execution verified (install/service/upgrade/uninstall) | **PENDING CI RUN #6** | Automated `smoke-test-windows-installers` job (18 checks) executes once build job passes. |
| 5. Native desktop interactive GUI verified (Windows 11) | **NOT VERIFIED** | Requires manual interactive Windows 11 checklist (`INSTALLATION.md` §5); CI does NOT test WPF GUI rendering. |
| 6. Agent mTLS & IIS integration verified | **PARTIAL — mTLS PENDING CI RUN #6; LIVE IIS BLOCKED** | 5 live loopback Kestrel mTLS tests run in CI Run #6; live IIS provisioning requires Windows Server VM. |
| 7. Autonomous GitHub deployment verified | **BLOCKED (Requires Disposable Windows Server VM)** | Unit provenance checks passed in Run #4; live push-to-IIS deployment requires disposable VM runbook. |
| 8. Rollback verified | **PARTIAL — UNIT PASSED (Run #4); LIVE VM BLOCKED** | `DeploymentOrchestrator` auto-rollback unit tests passed; live VM rollback with `SIMULATE_UNHEALTHY=true` requires disposable VM. |

## Current Project Verdict
**RUN #5 COMPILATION ERRORS REPAIRED ON DISK — PENDING WINDOWS CI RUN #6 (BOTH BUILD/32-TEST/INSTALLER JOB AND 18-CHECK SMOKE-TEST JOB); INTERACTIVE WINDOWS 11 GUI & LIVE WINDOWS SERVER IIS E2E NOT YET VERIFIED.**
