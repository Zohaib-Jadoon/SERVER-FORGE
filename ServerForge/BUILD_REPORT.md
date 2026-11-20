# ServerForge Build Report

## Latest Verified Windows CI Run (Prior to This Fix Pass)
- **Repository**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE`
- **GitHub Actions Run**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44679766730`
- **Branch / Commit**: `main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`
- **Workflow Trigger**: **SUCCEEDED** (`/.github/workflows/build-windows-installers.yml` on `windows-latest`)
- **Verified Outcomes from Run `44679766730`**:
  - Native `.NET 10` Windows solution compilation (`ServerForge.sln`): **PASSED** (previous WPF `CS0104` errors resolved; WinForms `NotifyIcon` preserved).
  - NuGet security advisories (`SQLitePCLRaw.lib.e_sqlite3`, `System.Security.Cryptography.Pkcs`): **NO LONGER REPORTED** after CPM pinning (`SQLitePCLRaw.bundle_e_sqlite3` `3.0.5`, `Microsoft.Data.SqlClient` `6.1.6`, `System.Security.Cryptography.Pkcs` `10.0.13`).
  - Automated xUnit test results: **17/18 PASSED** (Integration `1/1` PASSED, Agent `1/1` PASSED, Unit `15/16` PASSED).
  - Failing unit test: `ServerForge.UnitTests.DeploymentAndPersistenceTests.SqliteStore_PersistsPairedServer_RecoversInterruptedDeploys_AndRemovesManagementOnly` failed with `SQLite Error 19: 'FOREIGN KEY constraint failed'` inside `ServerForgeStore.UpsertApplicationAsync` because the test created a `ManagedApplication` with default `ProjectGroupId = Guid.Empty`.
  - Windows installers (`ServerForge-Desktop-Setup.exe`, `ServerForge-Agent-Setup.exe`, `SHA256SUMS.txt`): **NOT GENERATED** because `build-windows.ps1` halted on the failed unit test.

## Fixes Applied After Run `44679766730`
1. **Fixed Relational Test Fixture & Preserved SQLite FK Integrity (`ServerForgeUnitTests.cs`)**:
   - Created and persisted a real `ProjectGroup` before registering `ManagedApplication`, verified `PRAGMA foreign_keys == 1`, verified application updates, verified interrupted deployment recovery, and verified management-only removal preserves hosted release files on disk.
   - Added regression tests (`ApplicationConfigValidator_RejectsEmptyProjectGroupId`, `SqliteStore_EnforcesProjectGroupForeignKeys_AndRejectsInvalidDefaultGuids`) bringing total xUnit tests to **20** (18 Unit, 1 Agent, 1 Integration).
2. **Hardened `ProjectGroupId` & Default GUID Persistence Policy (`Validators.cs`, `ServerForgeStore.cs`, `Program.cs`, `MainViewModel.cs`, `AgentTransportService.cs`)**:
   - `ApplicationConfigValidator.Validate` now explicitly rejects `ProjectGroupId == Guid.Empty`.
   - `ServerForgeStore.UpsertApplicationAsync` rejects `Guid.Empty` (`ArgumentException`) and nonexistent `ProjectGroupId` (`InvalidOperationException`) instead of silently auto-creating random `"project-xxxxxxxx"` rows.
   - Added explicit idempotent `ServerForgeStore.EnsureDefaultProjectAsync` (single canonical `"default"` slug) used by Desktop (`MainViewModel.CreateApplicationAsync`) and Agent (`POST /api/v1/applications` when `ProjectGroupId == Guid.Empty`).
   - Added `GET/POST /api/v1/projects` on Agent, `SyncProjectAsync` + `PUT` 404-to-`POST` fallback in `AgentTransportService`, and `Guid.Empty` guards across `SaveEnvironmentVariableAsync`, `UpsertDomainAsync`, `RecordDeploymentAsync`, and entity ID initialization across store upsert methods.
3. **Proactive Publish & Inno Setup Packaging Fixes (`ServerForge-Agent.iss`, `ServerForge-Desktop.iss`, `build-installers.ps1`)**:
   - Fixed a latent **Inno Setup 6 Pascal Script compilation error** in `ServerForge-Agent.iss` (`out ResultCode: Integer` -> `var ResultCode: Integer`, since Inno Setup Pascal Script does not support `out` parameters).
   - Fixed `sc.exe` `binPath=` double-quoting bug in `ServerForge-Agent.iss` (`BinPath := ExpandConstant('{app}\{#MyAppExeName}');`).
   - Updated `ServerForge-Desktop.iss` `DefaultDirName` to `{autopf}\ServerForge Desktop` for clean `PrivilegesRequired=lowest` + override support.
   - Hardened `build-installers.ps1` `ISCC.exe` resolution to check `Program Files (x86)`, `Program Files`, Chocolatey (`C:\ProgramData\chocolatey\bin\ISCC.exe`), and `PATH`.

## Current Build & Packaging Status
- **Google AI Studio Container**: Linux x64 (`gVisor`), no `.NET 10` Windows Desktop SDK, no `ISCC.exe`, no `.git` clone / GitHub push credentials.

| Target | Status | Evidence / Reason |
|---|---|---|
| Root GitHub Actions CI Trigger | **VERIFIED** | Confirmed on runs `44678399756` and `44679766730`. |
| `ServerForge.sln` compilation (`dotnet build`) | **VERIFIED (Run `44679766730`); RE-VERIFY PENDING CI** | Prior run compiled cleanly; post-fix changes await push to `Zohaib-Jadoon/SERVER-FORGE`. |
| NuGet Vulnerability Audit | **VERIFIED (Run `44679766730`)** | Advisories for `SQLitePCLRaw.lib.e_sqlite3` and `System.Security.Cryptography.Pkcs` no longer reported. |
| Full xUnit Test Suite (20 tests) | **PENDING WINDOWS CI** | 17/18 passed in run `44679766730`; FK failure fixed + 2 regression tests added. |
| `ServerForge-Desktop-Setup.exe` & `SHA256SUMS.txt` | **PENDING WINDOWS CI** | Will compile via `ISCC.exe` once all 20 xUnit tests pass on `windows-latest`. |
| `ServerForge-Agent-Setup.exe` & `SHA256SUMS.txt` | **PENDING WINDOWS CI** | `out` -> `var` Pascal Script bug and `sc.exe` quoting proactively fixed ahead of `ISCC.exe`. |
| Read-Only Source Explorer Companion (`tsc --noEmit` + `vite build`) | **VERIFIED** | Inspects source archive only; does NOT verify C#/.NET compilation or Windows installers. |

## Next Steps to Trigger Windows CI
```powershell
git checkout -b fix/sqlite-fk-and-installer-build
# Extract updated archive into repository root, preserving /.github/workflows/build-windows-installers.yml
git status
git add ServerForge/src/ServerForge.Core/Validation/Validators.cs `
        ServerForge/src/ServerForge.Infrastructure/Persistence/ServerForgeStore.cs `
        ServerForge/src/ServerForge.Agent/Program.cs `
        ServerForge/src/ServerForge.Desktop/Services/AgentTransportService.cs `
        ServerForge/src/ServerForge.Desktop/ViewModels/MainViewModel.cs `
        ServerForge/tests/ServerForge.UnitTests/ServerForgeUnitTests.cs `
        ServerForge/installers/Agent/ServerForge-Agent.iss `
        ServerForge/installers/Desktop/ServerForge-Desktop.iss `
        ServerForge/scripts/build-installers.ps1 `
        ServerForge/BUILD_REPORT.md ServerForge/TEST_REPORT.md `
        ServerForge/DEVELOPMENT_STATUS.md ServerForge/PRODUCTION_READINESS_REPORT.md `
        ServerForge/MEMORY.md
git commit -m "Fix SQLite ProjectGroup FK test failure, harden persistence validation, and fix Agent Inno Setup script"
git push -u origin fix/sqlite-fk-and-installer-build
```
