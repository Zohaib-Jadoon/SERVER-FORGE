# ServerForge — Windows 11 Interactive Desktop WPF Acceptance Runbook

> **VERIFICATION STATUS: NOT VERIFIED — REQUIRES EXECUTION ON AN ACTUAL INTERACTIVE WINDOWS 11 DESKTOP.**
> Unattended GitHub Actions `windows-latest` runners verify compilation, ViewModel logic, SQLite persistence, and silent installer/uninstaller behavior, **NOT** visual WPF rendering, HiDPI layout, theme legibility, keyboard shortcuts, or system tray interaction. Do NOT mark any item below as `PASSED` until physically verified on Windows 11.

## 1. Prerequisites
- Windows 11 x64 workstation (100%, 125%, or 150% DPI scaling noted in Evidence Table).
- Downloaded `ServerForge-Desktop-Setup.exe` and `SHA256SUMS.txt` from a green `build-windows-installers.yml` run (baseline: Run `44699230392`).
- SHA-256 checksum verified locally via `Get-FileHash .\ServerForge-Desktop-Setup.exe -Algorithm SHA256`.
- Optional for live server tests: a paired disposable Windows Server VM running `ServerForge-Agent-Setup.exe` over private VPN (WireGuard / Tailscale), plus a disposable GitHub Fine-Grained PAT (`Contents: Read`, `Actions: Read`, `Metadata: Read`) and optional disposable SQL Server / PostgreSQL lab instance.

## 2. Core Startup, Auth, Theme & Tray Acceptance Checklist

| ID | Verification Step | Expected Result | Status |
|---|---|---|---|
| **W1** | Verify SHA-256 & install `ServerForge-Desktop-Setup.exe` | SmartScreen notice acknowledged (unsigned); installs cleanly under standard user without closing unrelated apps. | `NOT VERIFIED` |
| **W2** | First-launch Setup Wizard startup | `SetupWizardWindow` opens crisply with zero XAML exceptions or clipped controls. | `NOT VERIFIED` |
| **W3** | Setup Wizard validation errors | Submitting `<8`-character password or mismatched Confirm Password displays inline validation error and blocks advancement. | `NOT VERIFIED` |
| **W4** | Local admin creation & DPAPI self-test | Valid admin credentials save PBKDF2-SHA512 hash; Windows DPAPI `CurrentUser` self-test passes cleanly. | `NOT VERIFIED` |
| **W5** | Setup Wizard Finish -> `MainWindow` transition | Clicking Finish closes `SetupWizardWindow` and opens `MainWindow` smoothly (does NOT prematurely exit application). | `NOT VERIFIED` |
| **W6** | Fluent Dark / Light theme switching | Toggling theme in **Settings** updates backgrounds, borders, cards, badges, and text contrast cleanly across all 13 screens without unreadable text. | `NOT VERIFIED` |
| **W7** | Persistence across Desktop restart | Close & relaunch `ServerForge.Desktop.exe` -> Setup Wizard does NOT reappear; admin profile, theme preference, paired servers, and projects reload from SQLite. | `NOT VERIFIED` |
| **W8** | Close-to-tray & explicit Exit | Clicking `X` minimizes Desktop to Windows system tray; double-click tray icon restores window; right-click tray icon -> **Exit Desktop (Agents keep running)** terminates process cleanly (verified in Task Manager). | `NOT VERIFIED` |

## 3. All 13 Native WPF Screens Acceptance Checklist

| # | Screen | Shortcut | Interactive Verification Steps | Status |
|---|---|---|---|---|
| **1** | **Overview** | `Ctrl+1` | Dashboard KPIs, server health cards, recent deployments, and quick actions render cleanly with no clipped text or layout overflow. | `NOT VERIFIED` |
| **2** | **Projects** | `Ctrl+2` | Add a new disposable project (`E2E Verification`), edit its description, verify persistence after restart, verify invalid/empty name validation error. | `NOT VERIFIED` |
| **3** | **Applications** | `Ctrl+3` | Add & edit disposable `ServerForge.SampleApp` (`AspNetCore`, port `15199`, health `/health`), test invalid slug/port validation error, verify **Remove from Management** preserves hosted files on disk. | `NOT VERIFIED` |
| **4** | **GitHub** | `Ctrl+4` | Connect disposable Fine-Grained PAT, validate token/rate limit, search repositories, click **Import to New App**, confirm PAT is never displayed in plaintext after save. | `NOT VERIFIED` |
| **5** | **Deployments** | `Ctrl+5` | Trigger deployment to disposable Agent, inspect real-time status progression & live deployment log lines, verify rollback status & timestamp after unhealthy release. | `NOT VERIFIED` |
| **6** | **Domains & SSL** | `Ctrl+6` | Configure disposable local hostname (`sample-e2e.local`), verify protocol/IP validation errors are rejected, inspect manual DNS guidance and binding conflict detector. | `NOT VERIFIED` |
| **7** | **Environment Variables** | `Ctrl+7` | Add Production variables (`APP_VERSION=1.0.0`, `SIMULATE_UNHEALTHY=false`, plus a Secret variable), verify secret masking (`••••••••`), verify `.env` export redacts secrets (`<REDACTED_SECRET>`). | `NOT VERIFIED` |
| **8** | **Databases** | `Ctrl+8` | Where disposable SQL Server / PostgreSQL lab instance is available: test connection string (encrypted via DPAPI), inspect health/version, verify restore requires explicit approval. | `NOT VERIFIED` |
| **9** | **Servers** | `Ctrl+9` | Pair disposable Windows Server Agent via private VPN IP + port `9443` + one-time pairing code + pinned thumbprint; verify wrong thumbprint fails closed; restart Desktop & confirm auto-reconnect without re-pairing. | `NOT VERIFIED` |
| **10** | **Monitoring** | `Ctrl+M` | Inspect CPU, RAM, disk, IIS site status, and service health telemetry streamed over mTLS from disposable Agent. | `NOT VERIFIED` |
| **11** | **Logs** | `Ctrl+L` | Filter logs by category/search term, confirm no GitHub PATs or passwords appear unredacted in any displayed or exported log line. | `NOT VERIFIED` |
| **12** | **Backups** | `Ctrl+B` | Configure a disposable backup job schedule & retention policy, save, restart Desktop, confirm job persists cleanly. | `NOT VERIFIED` |
| **13** | **Settings** | `Ctrl+,` | Toggle Dark/Light theme, verify isolated Demo Mode toggle warning & isolation (never touches live SQLite), verify certificate thumbprint & rotation controls. | `NOT VERIFIED` |

## 4. Operator Evidence Collection Template (Complete After Execution)

```markdown
### Windows 11 Interactive Sign-Off Record
- **Date (UTC)**: YYYY-MM-DDTHH:MM:SSZ
- **Operator**: <name>
- **Windows 11 Build & Scaling**: e.g., Windows 11 24H2 (Build 26100), 1920x1080 @ 125%
- **Installer Artifact & CI Run ID**: ServerForge-Desktop-Setup.exe (Run #<id>, SHA-256: <hex>)
- **Checklist Result**: <X> / 21 PASSED, <Y> FAILED, <Z> BLOCKED
- **Screenshots Captured (Sanitized — No PATs/Pairing Codes)**:
  - [ ] Setup Wizard + MainWindow Dark Theme
  - [ ] MainWindow Light Theme
  - [ ] Servers Paired (Connected over mTLS)
  - [ ] Deployments Live Log + Automatic Rollback Entry
  - [ ] System Tray Context Menu
- **Defects Observed**: None / <list>
```
