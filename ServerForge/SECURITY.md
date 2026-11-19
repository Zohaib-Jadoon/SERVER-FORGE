# ServerForge Security Architecture

- **Mutual TLS (mTLS) + Thumbprint Pinning**: Desktop generates an ECDSA P-384 client certificate; Agent generates an ECDSA P-384 server certificate. Both validate the peer's pinned thumbprint using constant-time comparison and fail closed on mismatch or expiry.
- **Expiring One-Time Pairing Codes**: Pairing codes are 128-bit CSPRNG tokens stored hashed (SHA-256), valid for a single use before expiry. Once paired, the pairing endpoint rejects all subsequent calls (`409 Conflict`).
- **Windows DPAPI Secret Isolation**: Desktop secrets use `DataProtectionScope.CurrentUser`; Agent secrets use `DataProtectionScope.LocalMachine` inside ACL-restricted `C:\ProgramData\ServerForge\Agent` (Administrators & SYSTEM only). Secrets are never written to plaintext SQLite columns, GitHub repos, or logs.
- **Secret Redaction**: `DpapiSecretProtector.RedactSecrets` scrubs known app secrets, GitHub PATs (`ghp_`, `github_pat_`), Bearer tokens, and connection string passwords before any log or UI emission.
- **Path Traversal & Zip-Slip Prevention**: `PathBoundaryGuard` normalizes and verifies every extracted archive entry and release directory against the app's authorized root.
- **No Unrestricted Remote PowerShell**: All system commands use `SafeProcessRunner` (`ProcessStartInfo.ArgumentList` with `UseShellExecute = false`) and fixed operation templates.
- **Database Safety**: SQL Server and PostgreSQL ports are never opened publicly by ServerForge; database restores require explicit operator approval (`explicitOperatorApproval: true`); destructive migrations are never run automatically.
