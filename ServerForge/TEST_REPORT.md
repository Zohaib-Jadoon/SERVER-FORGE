# ServerForge Test Report

> **Strict Honesty Policy**: Tests that cannot run in this Linux container are marked **BLOCKED**, never **PASSED**.

## Automated .NET xUnit Suites (Source Written, Awaiting Windows Runner Execution)

| Test Suite / Verification Area | Status | Notes |
|---|---|---|
| Full `.NET 10` solution & WPF XAML compilation | **BLOCKED** | No `dotnet` SDK in Linux container. |
| `SecurityAndValidationTests` (pairing tokens, mTLS thumbprints, path boundary, secret redaction, domain & app validators, `.env`) | **BLOCKED (Ready)** | Implemented in `tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`. |
| `IisAndDatabaseSafetyTests` (unmanaged IIS read-only lock, binding conflict detection, SQL/PG identifier validation, approval-gated restore) | **BLOCKED (Ready)** | Implemented in `tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`. |
| `DeploymentAndPersistenceTests` (missing/untrusted artifact rejection, 1st/2nd deploy + failed update + rollback, SQLite pairing persistence, interrupted deploy crash recovery, management-only removal, GitHub 304 ETag) | **BLOCKED (Ready)** | Implemented in `tests/ServerForge.UnitTests/ServerForgeUnitTests.cs`. |
| `AgentIdentityAndPairingTests` (server cert generation, pairing persistence, Desktop cert rotation, rogue instance rejection) | **BLOCKED (Ready)** | Implemented in `tests/ServerForge.AgentTests/AgentIdentityAndPairingTests.cs`. |
| `WindowsPlatformIntegrationTests` (live Windows DPAPI CurrentUser round-trip) | **BLOCKED (Ready)** | Implemented in `tests/ServerForge.IntegrationTests/WindowsPlatformIntegrationTests.cs`. |
| Live Windows Server E2E (IIS serving, WinSW service, auto-deploy while Desktop closed, installer install/upgrade/uninstall) | **BLOCKED** | Requires disposable Windows Server VM & GitHub test repository. |

## Local Linux Container Checks (Preview Source Explorer Only)
- `src/utils/solutionTree.test.ts` (`npx --yes tsx --test`): Verifies the read-only source explorer helper utilities only; does NOT validate native C#/.NET behavior.
