# ServerForge Engineering Memory

- **Target Platform**: Native Windows x64, .NET 10 LTS, WPF (Windows 11 Fluent `ThemeMode`) + Windows Worker Service Agent.
- **Safety Invariants**:
  1. Never mutate pre-existing unmanaged IIS sites, AppPools, Classic ASP directories, or production databases.
  2. Never store secrets in plaintext SQLite, logs, or GitHub repos; enforce DPAPI + log redaction.
  3. Agent management API binds strictly to loopback/private VPN with fail-closed mutual TLS thumbprint pinning.
  4. Autonomous GitHub deployments run in `ServerForgeAgent` so Desktop does not need to stay open.
  5. Never auto-revert destructive database schema migrations during application rollback.
