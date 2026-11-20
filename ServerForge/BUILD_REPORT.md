# ServerForge Build Report

## Verified Windows CI Run History
- **Run #4 (`44684923525`)** — `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`: **PASSED**
  - `.NET 10` compilation: PASSED, zero NuGet advisories, `24/24` xUnit tests PASSED.
  - Unsigned self-contained `win-x64` installers generated & uploaded (`ServerForge-Desktop-Setup.exe` ~62.92 MB, `ServerForge-Agent-Setup.exe` ~46.64 MB, `SHA256SUMS.txt`).
- **Run #5 (`44688053844`)** — `d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`: **FAILED AT COMPILE (82 errors in `ServerForge.AgentTests`)** — fixed for Run #6.
- **Run #6 (`44689338386`)** — `4ad417c383ae524ece90861a77e10e92f03c56f3`: **COMPILE PASSED, 30/32 xUnit TESTS PASSED (2 SQLite Error 14 fixture failures)**
  - Native `.NET 10` Windows compilation (`ServerForge.sln`): **PASSED** (0 errors; WPF, Agent, and all 32 tests compiled cleanly).
  - NuGet vulnerability audit: **PASSED** (zero advisories).
  - Automated xUnit test suite: **30 / 32 PASSED** (`22/22` Unit, `7/9` Agent, `1/1` Integration).
    - Live Kestrel HTTPS/mTLS pairing, thumbprint pinning, replay rejection, rogue/anonymous client rejection, live desktop certificate rotation, and all 3 Desktop SetupWizard/13-screen/Theme/SQLite/Demo tests **PASSED** on real Windows.
  - Single root cause of the 2 failures (`LiveMtls_PersistsAcrossAgentAndDesktopRestarts_AndRecoversAfterConnectionLoss`, `LiveMtls_MultipleServers_PairPersistAndCommunicateIndependently`): `DesktopToAgentMtlsIntegrationTests.CreateDesktopStoreAsync(string desktopDir)` opened a SQLite database in a nested temp directory before creating `desktopDir` (`SQLite Error 14: unable to open database file`).
  - Installer packaging & `smoke-test-windows-installers` job: **BLOCKED** by those 2 test failures.

## Repairs Applied After Run #6 (`44689338386`) — Awaiting Windows CI Run #7
1. **Fixed `SQLite Error 14` in `DesktopToAgentMtlsIntegrationTests.CreateDesktopStoreAsync`**: Added `Directory.CreateDirectory(desktopDir);` prior to constructing/opening the SQLite database while preserving all 32 tests, SQLite schema migrator, DPAPI integration, and strict TLS/mTLS verification.
2. **Completed Solution-Wide SQLite Directory Initialization Audit**: Verified every `UseSqlite` / `SqliteConnection` call across `ServerForge.Desktop` (`App.xaml.cs`), `ServerForge.Agent` (`AgentHostFactory.cs`), `SqliteSchemaMigrator.cs`, `ServerForge.UnitTests`, and `ServerForge.AgentTests` creates its parent directory beforehand and never writes test data to production metadata folders.
3. **Hardened `ServerForge/scripts/smoke-test-installers.ps1` Before Run #7 First Execution**:
   - Added pre-flight production safety guards refusing execution if a production `ServerForgeAgent` service, paired `agent-identity.json`, or existing Desktop install is already present.
   - Switched to unique per-run disposable paths (`C:\Temp\ServerForgeSmoke-<id>`) and unique per-run service names (`ServerForgeAgentSmoke_<id>`).
   - Added deterministic `Wait-Until` polling for Windows Service startup, loopback port `9443` binding, and Inno Setup 6 async `unins000.exe` child cleanup, plus Windows Application Event Log capture on failure.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` or sign with an operator-owned certificate before broad distribution.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to trigger Windows CI Run #7 (both `build-test-package-windows` and `smoke-test-windows-installers`).

## Next Steps to Trigger Windows CI Run #7
```powershell
git checkout -b fix/phase11-ci-run7
# Extract updated archive into repository root, preserving /.github/workflows/build-windows-installers.yml
git add ServerForge/tests/ServerForge.AgentTests/DesktopToAgentMtlsIntegrationTests.cs `
        ServerForge/scripts/smoke-test-installers.ps1 `
        ServerForge/MEMORY.md ServerForge/BUILD_REPORT.md `
        ServerForge/TEST_REPORT.md ServerForge/DEVELOPMENT_STATUS.md `
        ServerForge/PRODUCTION_READINESS_REPORT.md
git commit -m "Fix SQLite Error 14 test directory creation in mTLS integration tests and harden Windows installer smoke test safeguards"
git push -u origin fix/phase11-ci-run7
```
