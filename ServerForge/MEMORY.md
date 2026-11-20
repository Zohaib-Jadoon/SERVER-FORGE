# ServerForge Engineering Memory

- **Canonical Repository**: `https://github.com/Zohaib-Jadoon/SERVER-FORGE.git`
- **Target Product**: Genuine native Windows x64 `.NET 10 LTS` WPF Desktop (`ServerForge-Desktop-Setup.exe`) + `.NET 10` Windows Service Agent (`ServerForge-Agent-Setup.exe`). React/Vite root app is strictly a read-only source browser.
- **Root Workflow Rule**: GitHub Actions CI MUST live at repository root `/.github/workflows/build-windows-installers.yml`, not inside `ServerForge/.github/workflows/`.
- **Security & Production Protection Invariants**:
  1. Never mutate pre-existing unmanaged IIS sites, AppPools, Classic ASP directories, or production databases without explicit adoption and dry-run review.
  2. Never accept a deployment without a verified, non-empty, provenance-checked artifact (exact 40-char SHA, authorized repo/branch, trusted SHA-256 digest from GitHub Actions API).
  3. Persist paired servers and pinned cert thumbprints in SQLite immediately so Desktop restarts reconnect seamlessly without re-pairing.
  4. Never store secrets in plaintext SQLite, logs, docs, or GitHub repositories; enforce DPAPI + redaction.
  5. Removing an application from ServerForge management MUST NEVER delete hosted files, IIS sites, or databases.
  6. Never auto-revert destructive database schema migrations during application rollback.
  7. Never fabricate build, test, or installer results when running on a Linux host without `.NET 10` Windows SDK or `ISCC.exe`.
  8. Preserve SQLite foreign-key enforcement (`ProjectGroup -> ManagedApplication`). `ManagedApplication.ProjectGroupId` MUST be a non-empty `Guid` referencing a persisted `ProjectGroup`; never disable FKs or silently fabricate random duplicate projects. Use explicit idempotent `ServerForgeStore.EnsureDefaultProjectAsync` (slug `"default"`) when an explicit project group has not been created yet.
  9. Inno Setup 6 Pascal Script does NOT support `out` parameters — always declare output parameters with `var` and avoid double-quoting `sc.exe` `binPath=` strings.
