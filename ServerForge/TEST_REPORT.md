# ServerForge Test Report

> **Strict Honesty Policy**: Only checks executed on real Windows runners are marked **PASSED**. Run #5 (`44688053844`) failed during `ServerForge.AgentTests` compilation (82 errors). All 3 root causes plus runtime/service hardening have been fixed on disk and await **Windows CI Run #6**. Interactive Windows 11 WPF GUI checks remain **NOT VERIFIED**, and live Windows Server VM IIS deployment/rollback checks remain **BLOCKED**.

## Verified Windows CI Run Evidence
- **Run #4 (`44684923525`)** — `main` @ `31da1d6f862cf6ee6d5064135bcfe9da6a59ae2c`: **PASSED** (`24/24` xUnit tests + both installers).
- **Run #5 (`44688053844`)** — `d3a7a7d6d11e4c1a355e31a8f2995c28104bd859`: **FAILED AT COMPILE (82 errors in `ServerForge.AgentTests`)**
  - `CS0103` (`Path`/`Directory`/`File` missing `using System.IO;` under `<UseWPF>true</UseWPF>`).
  - `CS1061` / `CS8130` / `CS8183` (`TestDbContextFactory` missing explicit `CreateDbContextAsync` implementation).
  - `CS1739` (`DatabaseHealthDto` invalid named parameter `DatabaseCount`).
  - Zero tests executed and installer/smoke-test jobs did not run.

## Automated Test & Verification Matrix (Post-Run #5 Fix Total: 32 xUnit Tests + 18 Installer Smoke Checks)

| Test Suite / Verification Area | Run #4 (`44684923525`) | Run #5 (`44688053844`) | Post-Fix Status | Notes |
|---|---|---|---|---|
| Full `.NET 10` solution compilation (`ServerForge.sln`) | **PASSED** | **FAILED (82 errors in `AgentTests`)** | **PENDING CI RUN #6** | All 3 compilation root causes fixed + `AgentHostFactory` Windows Service `ContentRootPath` fixed. |
| NuGet vulnerability audit | **PASSED** | **PASSED** | **VERIFIED** | Zero advisories (`SQLitePCLRaw.lib.e_sqlite3`, `System.Security.Cryptography.Pkcs`). |
| `SecurityAndValidationTests` (8 unit tests) | **8/8 PASSED** | Not reached | **PENDING CI RUN #6** | Previously passed in Run #4. |
| `IisAndDatabaseSafetyTests` (4 unit tests) | **4/4 PASSED** | Not reached | **PENDING CI RUN #6** | Previously passed in Run #4. |
| `DeploymentAndPersistenceTests` (10 unit tests) | **10/10 PASSED** | Not reached | **PENDING CI RUN #6** | Previously passed in Run #4. |
| `AgentIdentityAndPairingTests` (1 test) | **1/1 PASSED** | Compile failed (`System.IO`) | **PENDING CI RUN #6** | Added `using System.IO;`. |
| `WindowsPlatformIntegrationTests` (1 test) | **1/1 PASSED** | Not reached | **PENDING CI RUN #6** | Previously passed in Run #4. |
| `DesktopToAgentMtlsIntegrationTests` (5 live Kestrel mTLS tests) | NEW | Compile failed (`System.IO`, `CreateDbContextAsync`) | **PENDING CI RUN #6** | Fixed `System.IO` & `TestDbContextFactory.CreateDbContextAsync`. |
| `DesktopStartupAndViewModelTests` (3 Desktop startup/ViewModel tests) | NEW | Compile failed (`System.IO`, `CreateDbContextAsync`, `DatabaseCount`) | **PENDING CI RUN #6** | Fixed `System.IO`, `TestDbContextFactory`, `DatabaseHealthDto` 6-arg constructor, and DNS guidance assertion. |
| Automated Windows Installer Smoke Tests (`smoke-test-installers.ps1` — 18 checks) | NEW | Not reached | **PENDING CI RUN #6** | Hardened `finally` cleanup & failure log capture + StrictMode variable scoping. |
| Interactive Windows 11 WPF GUI Rendering & Tray UX | NOT RUN | NOT RUN | **NOT VERIFIED** | Manual checklist in `INSTALLATION.md` §5; not claimed by CI. |
| Live Disposable Windows Server VM E2E (IIS deploy, autonomous push deploy, rollback) | NOT RUN | NOT RUN | **BLOCKED (Requires Windows Server VM)** | Reproducible sample app & 20-step runbook at `/ServerForge/samples/disposable-test-app/`. |

## Local Linux Container Static & Companion Checks
- Audited all 3 `ServerForge.AgentTests` files, `AgentHostFactory.cs`, `AgentContracts.cs`, `Validators.cs`, and `smoke-test-installers.ps1`; confirmed `32` xUnit `[Fact]` tests remain enabled with zero disabled tests and zero TLS bypasses.
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer companion utilities only — never counted as Windows `.NET` verification.
