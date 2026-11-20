# ServerForge Engineering Memory

- **Canonical Repository**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE.git`
- **Target Product**: Genuine native Windows x64 `.NET 10 LTS` WPF Desktop (`ServerForge-Desktop-Setup.exe`) + `.NET 10` Windows Service Agent (`ServerForge-Agent-Setup.exe`). React/Vite root app is strictly a read-only source browser.
- **Root Workflow Rule**: GitHub Actions CI MUST live at repository root `/.github/workflows/build-windows-installers.yml`, not inside `ServerForge/.github/workflows/`.
- **Verified CI Milestones**:
  - Run `44684923525` (`main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`): First full green Windows CI run — `.NET 10` build PASSED, `24/24` xUnit tests PASSED, self-contained `win-x64` Desktop (`~62.92 MB`) & Agent (`~46.64 MB`) Inno Setup 6 installers + `SHA256SUMS.txt` GENERATED and uploaded.
- **Security & Production Protection Invariants**:
  1. Never mutate pre-existing unmanaged IIS sites, AppPools, Classic ASP directories, or production databases without explicit adoption and dry-run review.
  2. Never accept a deployment without a verified, non-empty, provenance-checked artifact (exact 40-char SHA, authorized repo/branch, trusted SHA-256 digest from GitHub Actions API).
  3. Persist paired servers and pinned cert thumbprints in SQLite immediately so Desktop restarts reconnect seamlessly without re-pairing.
  4. Never store secrets in plaintext SQLite, logs, docs, or GitHub repositories; enforce DPAPI + redaction (`ISecretProtector.RedactSecrets`).
  5. Removing an application from ServerForge management MUST NEVER delete hosted files, IIS sites, or databases.
  6. Never auto-revert destructive database schema migrations during application rollback.
  7. Never fabricate build, test, or installer results when running on a Linux host without `.NET 10` Windows SDK or `ISCC.exe`, never claim CI tests interactive WPF GUI rendering, and never claim unsigned installers are signed.
  8. Preserve SQLite foreign-key enforcement (`ProjectGroup -> ManagedApplication`). Use explicit idempotent `ServerForgeStore.EnsureDefaultProjectAsync` (slug `"default"`) when no explicit project group exists.
  9. Inno Setup 6 Pascal Script does NOT support `out` parameters — declare output parameters with `var`, use `AnsiString` for `LoadStringFromFile`, guard `WizardSilent()` in Setup only, and use `SuppressibleMsgBox` so `/VERYSILENT` installs never hang when IIS is absent.
  10. Never rely on EF Core's default SQLite `DateTimeOffset` mapping in `ORDER BY` or range queries; always persist `DateTimeOffset` / `DateTimeOffset?` via `UtcIso8601DateTimeOffsetConverter` (`yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`) with deterministic primary-key tie-breakers.
  11. Never drop or recreate existing ServerForge SQLite metadata databases during upgrades; evolve schema exclusively via `SqliteSchemaMigrator` (`__ServerForgeSchemaVersions`) with a pre-upgrade `SqliteConnection.BackupDatabase` `.bak` copy (`Pooling=False`) and atomic transactional rollback on failure.
  12. WPF `App.xaml` MUST use `ShutdownMode="OnExplicitShutdown"` (never `OnMainWindowClose`) because closing `SetupWizardWindow` before `MainWindow` is created otherwise terminates the application on first launch.
  13. Windows Schannel (`Kestrel` + `HttpClientHandler` mTLS) requires persisted CNG private keys (`X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable`) reloaded from PFX rather than ephemeral in-memory keys returned directly from `CertificateRequest.CreateSelfSigned(...)`.
  14. Always call `SqliteConnection.ClearAllPools()` in test `finally` blocks on Windows before deleting temp SQLite directories.
