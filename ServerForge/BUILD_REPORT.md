# ServerForge Build Report

## Latest Verified Windows CI Run (Run #3 — Prior to This Fix Pass)
- **Repository**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE`
- **GitHub Actions Run**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44681873469`
- **Branch / Commit**: `main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`
- **Workflow Trigger**: **SUCCEEDED** (`/.github/workflows/build-windows-installers.yml` on `windows-latest`)
- **Verified Outcomes from Run `44681873469`**:
  - Native `.NET 10` Windows solution compilation (`ServerForge.sln`): **PASSED** (WPF `CS0104` fixed; WinForms `NotifyIcon` preserved).
  - NuGet security advisories (`SQLitePCLRaw.lib.e_sqlite3`, `System.Security.Cryptography.Pkcs`): **RESOLVED** (no longer reported).
  - Previous SQLite foreign-key failure (`ProjectGroupId`): **FIXED** (`SqliteStore_EnforcesProjectGroupForeignKeys_AndRejectsInvalidDefaultGuids` and `ApplicationConfigValidator_RejectsEmptyProjectGroupId` PASSED).
  - Automated xUnit test results: **19/20 PASSED** (`1/1` Integration, `1/1` Agent, `17/18` Unit).
  - Single failure: `SqliteStore_PersistsPairedServer_RecoversInterruptedDeploys_AndRemovesManagementOnly` failed at `ServerForgeStore.cs:568` with `System.NotSupportedException: SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses.`
  - Inno Setup compilation & Windows installers (`ServerForge-Desktop-Setup.exe`, `ServerForge-Agent-Setup.exe`, `SHA256SUMS.txt`): **NOT REACHED** because `build-windows.ps1` halted on the failed unit test.

## Fixes Applied After Run `44681873469`
1. **SQLite-Native Canonical UTC ISO-8601 `DateTimeOffset` Strategy (`UtcIso8601DateTimeOffsetConverter.cs`, `ServerForgeDbContext.cs`)**:
   - Registered `UtcIso8601DateTimeOffsetConverter` (`ValueConverter<DateTimeOffset, string>`) via `ConfigureConventions` for all `DateTimeOffset` and `DateTimeOffset?` properties across all 11 entities.
   - Persists every timestamp normalized to UTC in fixed-width `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'` format (`TEXT`), enabling EF Core 10 to translate `ORDER BY`, `WHERE`, range scans, and `LIMIT`/`OFFSET` directly in SQLite without client-side table loads and with exact chronological ordering across arbitrary source UTC offsets.
2. **Audited & Repaired All 3 Vulnerable Timestamp Queries (`ServerForgeStore.cs`)**:
   - `GetLocalAdminAsync`: `.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).FirstOrDefaultAsync(ct)`
   - `GetDeploymentHistoryAsync`: `.AsNoTracking().OrderByDescending(d => d.QueuedAtUtc).ThenByDescending(d => d.Id)` + offset/limit pagination overload `(Guid? applicationId, int skip, int take, CancellationToken ct)` with `CancellationToken` propagation and argument guards.
   - `GetRecentAuditLogsAsync`: `.AsNoTracking().OrderByDescending(a => a.TimestampUtc).ThenByDescending(a => a.Id)` + offset/limit pagination overload `(int skip, int take, CancellationToken ct)`.
3. **Versioned Safe SQLite Schema Migrator (`SqliteSchemaMigrator.cs`, `ServerForgeDbContext.InitializeSqliteAsync`)**:
   - Replaces raw `EnsureCreatedAsync` with versioned migrations tracked in `__ServerForgeSchemaVersions` (currently `v2`).
   - Fresh installs run `EnsureCreatedAsync` cleanly and stamp `v1` + `v2`.
   - Existing databases requiring upgrades are backed up first via `SqliteConnection.BackupDatabase` (`<dbDir>/backups/<dbName>.pre-v2.<stamp>.bak`, `Pooling=False` to avoid Windows file locks), upgraded atomically inside a SQLite transaction (`v1` additive baseline table/index compatibility + `v2` in-place canonical UTC ISO-8601 timestamp backfill across all 11 tables), and rolled back safely (`SqliteMigrationException` with `BackupFilePath`) on any failure without ever deleting existing databases.
4. **Expanded xUnit Regression Suite (`ServerForgeUnitTests.cs`)**:
   - Added 4 comprehensive test suites covering empty/single/multiple deployment history, mixed UTC offsets, same-timestamp deterministic tie-breaking, pagination limits, cancellation tokens, audit log ordering, local admin retrieval, SQLite reopen persistence, fresh install + legacy schema upgrade with backup verification, and fail-safe rollback on corrupt legacy timestamps (**24 total xUnit tests**: 22 Unit, 1 Agent, 1 Integration).

## Current Build & Packaging Status
- **Google AI Studio Container**: Linux x64 (`gVisor`), no `.NET 10` Windows Desktop SDK, no `ISCC.exe`, no `.git` clone / GitHub push credentials.

| Target | Status | Evidence / Reason |
|---|---|---|
| Root GitHub Actions CI Trigger | **VERIFIED** | Confirmed on runs `44678399756`, `44679766730`, and `44681873469`. |
| `ServerForge.sln` compilation (`dotnet build`) | **VERIFIED (Run `44681873469`); RE-VERIFY PENDING CI** | Prior run compiled cleanly; post-fix changes await push to `Zohaib-Jadoon/SERVER-FORGE`. |
| NuGet Vulnerability Audit | **VERIFIED (Run `44681873469`)** | Zero reported advisories for `SQLitePCLRaw.lib.e_sqlite3` or `System.Security.Cryptography.Pkcs`. |
| Full xUnit Test Suite (24 tests) | **PENDING WINDOWS CI** | 19/20 passed in run `44681873469`; `DateTimeOffset` `ORDER BY` fixed + 4 new regression tests added. |
| `ServerForge-Desktop-Setup.exe` & `SHA256SUMS.txt` | **PENDING WINDOWS CI** | Will publish (`win-x64` self-contained) and compile via `ISCC.exe` on `windows-latest` once all 24 tests pass. |
| `ServerForge-Agent-Setup.exe` & `SHA256SUMS.txt` | **PENDING WINDOWS CI** | Will publish (`win-x64` self-contained) and compile via `ISCC.exe` on `windows-latest` once all 24 tests pass. |
| Read-Only Source Explorer Companion (`tsc --noEmit` + `vite build`) | **VERIFIED** | Inspects source archive only; does NOT verify C#/.NET compilation or Windows installers. |

## Next Steps to Trigger Windows CI (Run #4)
```powershell
git checkout -b fix/sqlite-datetimeoffset-and-migrations
# Extract updated archive into repository root, preserving /.github/workflows/build-windows-installers.yml
git status
git add ServerForge/src/ServerForge.Infrastructure/Persistence/UtcIso8601DateTimeOffsetConverter.cs `
        ServerForge/src/ServerForge.Infrastructure/Persistence/SqliteSchemaMigrator.cs `
        ServerForge/src/ServerForge.Infrastructure/Persistence/ServerForgeDbContext.cs `
        ServerForge/src/ServerForge.Infrastructure/Persistence/ServerForgeStore.cs `
        ServerForge/tests/ServerForge.UnitTests/ServerForgeUnitTests.cs `
        ServerForge/BUILD_REPORT.md ServerForge/TEST_REPORT.md `
        ServerForge/DEVELOPMENT_STATUS.md ServerForge/PRODUCTION_READINESS_REPORT.md `
        ServerForge/MEMORY.md
git commit -m "Fix SQLite DateTimeOffset ORDER BY translation and add versioned SQLite schema migrator with pre-upgrade backup"
git push -u origin fix/sqlite-datetimeoffset-and-migrations
```
