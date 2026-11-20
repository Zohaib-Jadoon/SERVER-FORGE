# ServerForge Test Report

> **Strict Honesty Policy**: Tests that have not yet executed on Windows after the latest fix pass are marked **PENDING WINDOWS CI** or **BLOCKED**, never **PASSED**.

## Previous Windows CI Run Evidence
- **Run `44678399756`** (`main` @ `f2261702b0f4dfd8a0a1bb6d4d0c20aa056cd732`): **FAILED** before `dotnet test` executed due to `CS0104` in `ServerForge.Desktop/Views/Pages/PageViews.cs`.
- **Additional Windows Test Safeguard Applied**: Added `Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();` in `SqliteStore_PersistsPairedServer_RecoversInterruptedDeploys_AndRemovesManagementOnly()` cleanup to prevent Windows file locks when deleting temporary SQLite test databases.

## Automated .NET xUnit Suites (Awaiting Post-Fix Windows CI Execution)

| Test Suite / Verification Area | Status | Notes |
|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation (`ServerForge.sln`) | **PENDING WINDOWS CI** | `CS0104` fixed in `PageViews.cs` and WinForms implicit usings isolated in `ServerForge.Desktop.csproj`. |
| NuGet transitive vulnerability check (`SQLitePCLRaw.lib.e_sqlite3`, `System.Security.Cryptography.Pkcs`) | **PENDING WINDOWS CI** | Upgraded/pinned `SQLitePCLRaw.bundle_e_sqlite3` `3.0.5`, `Microsoft.Data.SqlClient` `6.1.6`, `System.Security.Cryptography.Pkcs` `10.0.13`. |
| `SecurityAndValidationTests` (pairing tokens, mTLS thumbprints, path boundary, secret redaction, domain & app validators, `.env`) | **PENDING WINDOWS CI** | Implemented in `tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`. |
| `IisAndDatabaseSafetyTests` (unmanaged IIS read-only lock, binding conflict detection, SQL/PG identifier validation, approval-gated restore) | **PENDING WINDOWS CI** | Implemented in `tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`. |
| `DeploymentAndPersistenceTests` (missing/untrusted artifact rejection, 1st/2nd deploy + failed update + rollback, SQLite pairing persistence, interrupted deploy crash recovery, management-only removal, GitHub 304 ETag) | **PENDING WINDOWS CI** | Implemented in `tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`. |
| `AgentIdentityAndPairingTests` (server cert generation, pairing persistence, Desktop cert rotation, rogue instance rejection) | **PENDING WINDOWS CI** | Implemented in `tests/ServerForge.AgentTests/AgentIdentityAndPairingTests.cs`. |
| `WindowsPlatformIntegrationTests` (live Windows DPAPI CurrentUser round-trip) | **PENDING WINDOWS CI** | Implemented in `tests/ServerForge.IntegrationTests/WindowsPlatformIntegrationTests.cs`. |
| Live Windows Server E2E (Agent service, IIS deployment & serving, WinSW service, auto-deploy while Desktop closed, rollback, installer install/upgrade/uninstall) | **BLOCKED (Requires Windows Server VM)** | Must be verified independently after Windows CI compilation and installer generation succeed. |

## Local Linux Container Static & Companion Checks
- Static C#/XAML audit: Verified all 16 `x:Class` declarations match partial classes, all 11 core interfaces have complete implementations, all XAML resource keys exist in `FluentTokens.xaml`, zero WinForms/WPF namespace ambiguities remain, and all `<PackageReference>` items match `Directory.Packages.props`.
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer helper utilities only; does NOT validate native C#/.NET execution.
