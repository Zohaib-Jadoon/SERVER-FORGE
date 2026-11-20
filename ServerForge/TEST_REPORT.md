# ServerForge Test Report

> **Strict Honesty Policy**: Only tests verified in real GitHub Actions Windows CI runs are marked **PASSED**. New or modified tests not yet executed on `windows-latest` are marked **PENDING WINDOWS CI**; live Windows Server VM E2E checks remain **BLOCKED**.

## Verified Windows CI Run Evidence (Run #3 — `44681873469`)
- **Repository / Commit**: `Zohaib-Jadoon/SERVER-FORGE` (`main` @ `e4ab9076b67ca039279b6b71d10408fd503e1d0e`)
- **Run URL**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE/actions/runs/44681873469`
- **Compilation**: **PASSED** (`.NET 10` solution & WPF XAML compiled cleanly; NuGet advisories resolved).
- **Results**: **19 / 20 PASSED**
  - `ServerForge.IntegrationTests`: **1 / 1 PASSED**
  - `ServerForge.AgentTests`: **1 / 1 PASSED**
  - `ServerForge.UnitTests`: **17 / 18 PASSED**
- **Single Failure Diagnosed & Fixed**:
  - **Test**: `ServerForge.UnitTests.DeploymentAndPersistenceTests.SqliteStore_PersistsPairedServer_RecoversInterruptedDeploys_AndRemovesManagementOnly`
  - **Exception**: `System.NotSupportedException: SQLite does not support expressions of type 'DateTimeOffset' in ORDER BY clauses.`
  - **Location**: `ServerForge/src/ServerForge.Infrastructure/Persistence/ServerForgeStore.cs:568` (`query.OrderByDescending(d => d.QueuedAtUtc).Take(take).ToListAsync(ct)`).
  - **Related Audited Queries Fixed Simultaneously**:
    1. `ServerForgeStore.GetLocalAdminAsync` (`db.AdminProfiles.OrderBy(a => a.CreatedAtUtc)`)
    2. `ServerForgeStore.GetDeploymentHistoryAsync` (`query.OrderByDescending(d => d.QueuedAtUtc)`)
    3. `ServerForgeStore.GetRecentAuditLogsAsync` (`db.AuditLogs.OrderByDescending(a => a.TimestampUtc)`)
  - **Fix Applied**: Added `UtcIso8601DateTimeOffsetConverter` (`ValueConverter<DateTimeOffset, string>` storing fixed-width UTC `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`), deterministic tie-breakers (`.ThenBy(a => a.Id)` / `.ThenByDescending(d => d.Id)`), offset/limit pagination overloads, and `SqliteSchemaMigrator` with pre-upgrade backup and atomic legacy timestamp backfill.

## Automated .NET xUnit Suites (Post-Fix Total: 24 Tests)

| Test Suite / Verification Area | Run `44681873469` | Post-Fix Status | Notes |
|---|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **PASSED** | **PENDING WINDOWS CI** | Re-verification needed after `ServerForge.Infrastructure` and `ServerForge.UnitTests` updates. |
| NuGet transitive vulnerability audit | **PASSED** | **VERIFIED** | Zero reported advisories (`SQLitePCLRaw.lib.e_sqlite3`, `System.Security.Cryptography.Pkcs`). |
| `SecurityAndValidationTests` (8 unit tests) | **8/8 PASSED** | **PASSED (Run `44681873469`)** | Pairing tokens, mTLS pinning, path guard, redaction, validators, `.env`, `Guid.Empty` `ProjectGroupId`. |
| `IisAndDatabaseSafetyTests` (4 unit tests) | **4/4 PASSED** | **PASSED (Run `44681873469`)** | Unmanaged IIS read-only lock, binding conflict detector, SQL/PG identifier validation, approval-gated restore. |
| `DeploymentAndPersistenceTests` — existing 6 unit tests (incl. FK regression test & `SqliteStore_PersistsPairedServer...`) | **5/6 PASSED (1 `DateTimeOffset` FAIL)** | **PENDING WINDOWS CI** | `DateTimeOffset` `ORDER BY` fixed via `UtcIso8601DateTimeOffsetConverter`. |
| `DeploymentAndPersistenceTests` — 4 new timestamp & migration regression tests | **NEW** | **PENDING WINDOWS CI** | Covers empty/single/multiple deployment history, mixed UTC offsets, same-timestamp deterministic ordering, pagination limits, cancellation, recent audit logs, local admin retrieval, reopen persistence, fresh install + legacy schema upgrade + backup, and fail-safe rollback on corrupt legacy timestamps. |
| `AgentIdentityAndPairingTests` (1 test) | **1/1 PASSED** | **PASSED (Run `44681873469`)** | Server cert generation, pairing persistence, Desktop cert rotation, rogue instance rejection. |
| `WindowsPlatformIntegrationTests` (1 test) | **1/1 PASSED** | **PASSED (Run `44681873469`)** | Live Windows DPAPI CurrentUser round-trip on `windows-latest`. |
| Windows Installer Packaging (`ISCC.exe`: Desktop + Agent `.exe` + `SHA256SUMS.txt`) | **NOT REACHED** | **PENDING WINDOWS CI** | Will execute automatically once all 24 xUnit tests pass on `windows-latest`. |
| Live Windows Server E2E (Agent service, IIS deploy & serving, WinSW service, auto-deploy while Desktop closed, rollback, installer install/upgrade/uninstall) | **NOT RUN** | **BLOCKED (Requires Windows Server VM)** | Separate manual/VM acceptance gate after Windows CI installers are generated. |

## Local Linux Container Static & Companion Checks
- Static C# & Inno Setup audit: Verified `UtcIso8601DateTimeOffsetConverter`, `SqliteSchemaMigrator` DDL/column alignment, deterministic `ServerForgeStore` queries, `24` xUnit `[Fact]` tests, and Inno Setup Pascal Script validity.
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer companion utilities only; does NOT validate native C#/.NET execution.
