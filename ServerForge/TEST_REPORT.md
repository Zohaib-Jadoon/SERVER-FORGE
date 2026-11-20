# ServerForge Test Report

> **Strict Honesty Policy**: Only tests verified in real GitHub Actions Windows CI runs are marked **PASSED**. New or modified tests not yet executed on `windows-latest` are marked **PENDING WINDOWS CI**; live Windows Server VM E2E checks remain **BLOCKED**.

## Verified Windows CI Run Evidence (`44679766730`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` (`main` @ `21da7f890ab7dddd0452adcadf282468df6c73da`)
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44679766730`
- **Compilation**: **PASSED** (`.NET 10` solution & WPF XAML compiled cleanly; NuGet advisories resolved).
- **Results**: **17 / 18 PASSED**
  - `ServerForge.IntegrationTests`: **1 / 1 PASSED**
  - `ServerForge.AgentTests`: **1 / 1 PASSED**
  - `ServerForge.UnitTests`: **15 / 16 PASSED**
- **Single Failure Diagnosed & Fixed**:
  - **Test**: `ServerForge.UnitTests.DeploymentAndPersistenceTests.SqliteStore_PersistsPairedServer_RecoversInterruptedDeploys_AndRemovesManagementOnly`
  - **Exception**: `Microsoft.EntityFrameworkCore.DbUpdateException` -> `Microsoft.Data.Sqlite.SqliteException: SQLite Error 19: 'FOREIGN KEY constraint failed'` in `ServerForgeStore.UpsertApplicationAsync`.
  - **Root Cause**: The test created a `ManagedApplication` without assigning `ProjectGroupId`, leaving it at `Guid.Empty`, which violated the required `ProjectGroup -> ManagedApplication` SQLite foreign key.
  - **Fix Applied**: Test fixture now creates and persists a real `ProjectGroup`, assigns `ProjectGroupId`, verifies `PRAGMA foreign_keys == 1`, verifies updating an existing app, verifies crash recovery, and verifies hosted files remain untouched on management removal. Added strict `ProjectGroupId` and default-GUID validation in `ApplicationConfigValidator`, `ServerForgeStore`, Desktop, and Agent, plus 2 new regression tests.

## Automated .NET xUnit Suites (Post-Fix Total: 20 Tests)

| Test Suite / Verification Area | Run `44679766730` | Post-Fix Status | Notes |
|---|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **PASSED** | **PENDING WINDOWS CI** | Re-verification needed after persistence/test/Agent/Desktop edits. |
| NuGet transitive vulnerability audit | **PASSED** | **VERIFIED** | `SQLitePCLRaw.lib.e_sqlite3` and `System.Security.Cryptography.Pkcs` advisories eliminated. |
| `SecurityAndValidationTests` (8 unit tests, incl. new `ApplicationConfigValidator_RejectsEmptyProjectGroupId`) | **7/7 PASSED** | **PENDING WINDOWS CI** | Added explicit `Guid.Empty` `ProjectGroupId` validation regression test. |
| `IisAndDatabaseSafetyTests` (4 unit tests) | **4/4 PASSED** | **PASSED (Run `44679766730`)** | Unmanaged IIS read-only lock, binding conflict detector, SQL/PG identifier validation, approval-gated restore. |
| `DeploymentAndPersistenceTests` (6 unit tests, incl. fixed FK test & new `SqliteStore_EnforcesProjectGroupForeignKeys_AndRejectsInvalidDefaultGuids`) | **4/5 PASSED (1 FK FAIL)** | **PENDING WINDOWS CI** | Fixed relational fixture; added regression tests for `Guid.Empty`, nonexistent `ProjectGroupId`, idempotent `EnsureDefaultProjectAsync`, direct EF Core SQLite FK `DbUpdateException`, and `Guid.Empty` guards across env vars, domains, and deployments. |
| `AgentIdentityAndPairingTests` (1 test) | **1/1 PASSED** | **PASSED (Run `44679766730`)** | Server cert generation, pairing persistence, Desktop cert rotation, rogue instance rejection. |
| `WindowsPlatformIntegrationTests` (1 test) | **1/1 PASSED** | **PASSED (Run `44679766730`)** | Live Windows DPAPI CurrentUser round-trip on `windows-latest`. |
| Windows Installer Packaging (`ISCC.exe`: Desktop + Agent `.exe` + `SHA256SUMS.txt`) | **NOT RUN** | **PENDING WINDOWS CI** | Blocked previously by the single FK test failure; `ServerForge-Agent.iss` `out`->`var` bug & `sc.exe` quoting proactively fixed. |
| Live Windows Server E2E (Agent service, IIS deploy & serving, WinSW service, auto-deploy while Desktop closed, rollback, installer install/upgrade/uninstall) | **NOT RUN** | **BLOCKED (Requires Windows Server VM)** | Separate manual/VM acceptance gate after Windows CI installers are generated. |

## Local Linux Container Static & Companion Checks
- Static C#/XAML & Inno Setup audit: Verified `using` directives, `ServerForgeStore` FK & GUID guards, `ProjectGroup` sync between Desktop and Agent, and valid Inno Setup 6 Pascal Script syntax (`var` parameters, single-quoted `sc.exe` `binPath=`).
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer companion utilities only; does NOT validate native C#/.NET execution.
