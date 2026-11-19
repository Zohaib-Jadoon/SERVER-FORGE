# ServerForge Installation & Secure Pairing Guide

## Part A: Installing ServerForge Agent on Your Remote Windows Server

1. Copy `ServerForge-Agent-Setup.exe` to your Windows Server (Windows Server 2019 / 2022 / 2025 x64).
2. Right-click and run **As Administrator** (required to register the Windows Service and lock `C:\ProgramData\ServerForge\Agent` ACLs).
3. The installer verifies Microsoft IIS (`w3wp.exe`), creates ACL-protected directories, installs and starts the `ServerForgeAgent` service, and generates a 30-minute one-time pairing code at:
   `C:\ProgramData\ServerForge\Agent\pairing-code.txt`
4. Configure `C:\Program Files\ServerForge\Agent\appsettings.json` so `Agent.BindAddress` points to your private VPN adapter IP (e.g., `10.10.0.5`), then restart the service:
   ```powershell
   Restart-Service ServerForgeAgent
   ```
5. Ensure Windows Firewall allows TCP `9443` **only** from your VPN subnet / local workstation IP — never from `Any` / Public internet.

## Part B: Installing ServerForge Desktop on Windows 11

1. Run `ServerForge-Desktop-Setup.exe` (no manual .NET 10 runtime installation required — self-contained `win-x64`).
2. Complete the 10-step First-Launch Setup Wizard:
   - Create your local administrator profile (stored locally in SQLite with PBKDF2-SHA512).
   - Verify your local Windows DPAPI vault.
   - Connect your GitHub account with a Fine-Grained Personal Access Token (`Contents: Read`, `Actions: Read`, `Metadata: Read`).
   - Enter your server's private VPN IP, port `9443`, one-time pairing code, and server certificate thumbprint from `pairing-code.txt`.
   - Run the Read-Only Server Diagnostics check to inspect IIS sites, AppPools, runtimes, and SQL Server instances without modifying anything.
