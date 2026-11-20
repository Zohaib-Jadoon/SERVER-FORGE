# ServerForge Build Report

## Latest Verified Windows CI History
- **Run #4 (`44684923525`)** — `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`: **PASSED**
  - `.NET 10` compilation: PASSED, zero NuGet advisories.
  - Automated xUnit tests: **24 / 24 PASSED**.
  - Unsigned self-contained `win-x64` installers generated & uploaded: `ServerForge-Desktop-Setup.exe` (`~62.92 MB`), `ServerForge-Agent-Setup.exe` (`~46.64 MB`), `SHA256SUMS.txt`.
- **Run #5 (`44688053844`)** — `d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`: **FAILED (82 C# compiler errors in `ServerForge.AgentTests`)**
  - `ServerForge.Desktop`, `ServerForge.Agent`, and all core/infrastructure libraries compiled cleanly, while `ServerForge.AgentTests` failed due to 3 root causes introduced in Phase 11:
    1. **Missing `System.IO` directives (`CS0103` on `Path`/`Directory`/`File`)**: Adding `<UseWPF>true</UseWPF>` to `ServerForge.AgentTests.csproj` caused `Microsoft.NET.Sdk.WindowsDesktop` to suppress implicit `System.IO` global usings across all 3 test files (`AgentIdentityAndPairingTests.cs`, `DesktopToAgentMtlsIntegrationTests.cs`, `DesktopStartupAndViewModelTests.cs`).
    2. **Default interface method invocation (`CS1061`, cascading `CS8130`/`CS8183`)**: `TestDbContextFactory` implemented `IDbContextFactory<ServerForgeDbContext>.CreateDbContext()` without explicitly implementing `CreateDbContextAsync`, and tests invoked `CreateDbContextAsync()` on the concrete type instead of the interface.
    3. **Invalid DTO named parameter (`CS1739`)**: `DesktopStartupAndViewModelTests.cs` passed non-existent `DatabaseCount: 2` to `DatabaseHealthDto(Guid, string, DatabaseEngineType, bool, double, string?)`.

## Repairs Applied After Run #5 (`44688053844`) — Awaiting Windows CI Run #6
1. Added explicit `using System.IO;` to `AgentIdentityAndPairingTests.cs`, `DesktopToAgentMtlsIntegrationTests.cs`, and `DesktopStartupAndViewModelTests.cs` while preserving `<Using Remove="System.Windows.Forms" />` and `<Using Remove="System.Drawing" />` collision protections.
2. Explicitly implemented `CreateDbContextAsync(CancellationToken)` on `TestDbContextFactory` and typed `factory` as `IDbContextFactory<ServerForgeDbContext>` in both `DesktopToAgentMtlsIntegrationTests.cs` and `DesktopStartupAndViewModelTests.cs`.
3. Fixed `DatabaseHealthDto` constructor call in `DesktopStartupAndViewModelTests.cs` to match the 6-parameter contract without modifying production contracts.
4. Fixed a pre-detected runtime assertion mismatch in `DesktopStartupAndViewModelTests.cs` (`DomainValidator.BuildManualDnsInstructions` emits `"Host / Name: orders"`, not the full FQDN).
5. Fixed `AgentHostFactory.cs` to set `WebApplicationOptions.ContentRootPath = AppContext.BaseDirectory` when `WindowsServiceHelpers.IsWindowsService()` is true so the installed `ServerForge.Agent` Windows Service always reads `appsettings.json` from its install directory instead of `C:\Windows\System32`.
6. Hardened `ServerForge/scripts/smoke-test-installers.ps1` `finally` block (and StrictMode variable scope) so disposable services, installers, temp directories, and Agent failure logs are always cleaned up and captured even if a mid-run assertion throws.

## Signing & Environment Transparency
- **Installers are UNSIGNED**: No Authenticode certificate is configured in CI; verify `SHA256SUMS.txt` before execution or sign with an operator-owned certificate.
- **Google AI Studio Container**: Linux x64 (`gVisor`) without `.NET 10` Windows SDK, `ISCC.exe`, or GitHub push credentials. Push to `Zohaib-Jadoon/SERVER-FORGE` to execute Windows CI Run #6.

## Next Steps to Trigger Windows CI Run #6
```powershell
git checkout -b fix/phase11-ci-run6
# Extract updated archive into repository root, preserving /.github/workflows/build-windows-installers.yml
git add ServerForge/tests/ServerForge.AgentTests/ `
        ServerForge/src/ServerForge.Agent/AgentHostFactory.cs `
        ServerForge/scripts/smoke-test-installers.ps1 `
        ServerForge/MEMORY.md ServerForge/BUILD_REPORT.md `
        ServerForge/TEST_REPORT.md ServerForge/DEVELOPMENT_STATUS.md `
        ServerForge/PRODUCTION_READINESS_REPORT.md
git commit -m "Fix Phase 11 AgentTests compilation errors (System.IO, IDbContextFactory async, DatabaseHealthDto), Agent service ContentRootPath, and smoke-test cleanup"
git push -u origin fix/phase11-ci-run6
```
