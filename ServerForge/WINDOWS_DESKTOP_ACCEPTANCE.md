# ServerForge — Interactive Windows 11 Desktop GUI Acceptance Checklist (Phase 12.2)

**Current Status:** **NOT VERIFIED**  
**Reason:** Interactive WPF visual, tray, dialog, and multi-screen rendering checks require a real Windows 11 desktop session with an interactive logged-in user. No automated headless test or Linux container can substitute for this gate.

**Target Build Under Test:** `ServerForge-Desktop-Setup.exe` (verified in CI Run #10 `44720053575`; re-test with CI Run #13 after Run #13 push)  
**Native Technology Preserved:** Pure `.NET 10 LTS` WPF (`ServerForge.Desktop`, 13 native WPF pages + Windows Forms `NotifyIcon` system tray; zero React/Electron/WebView).

---

## Tester Metadata (Complete During Real Windows 11 Execution)

| Field | Value |
| --- | --- |
| Tester Name | _TBD_ |
| Execution Date (UTC) | _TBD_ |
| Windows 11 Version / Build | _TBD (e.g., Windows 11 24H2 Build 26100)_ |
| Display Scaling / DPI | _TBD (test 100% and 125%/150%)_ |
| Desktop Installer SHA-256 | _TBD (match `SHA256SUMS.txt`)_ |
| Paired Disposable Agent Host | _TBD (disposable Windows IIS VM only)_ |

---

## 17-Point Interactive Windows 11 Acceptance Matrix

Mark each item **PASS** or **FAIL** ONLY after executing it manually on Windows 11. Attach screenshots or exact error messages for any failure.

| # | Acceptance Area | Native WPF Screen / Behavior | Verification Steps | Expected Result | Status |
| --- | --- | --- | --- | --- | --- |
| 1 | **Installer & First Launch** | Inno Setup Wizard + Cold Start | Verify `SHA256SUMS.txt`, run `ServerForge-Desktop-Setup.exe` interactively, launch from Start Menu / Desktop shortcut | Installs cleanly without UAC corruption or missing `.NET` runtime prompts; main window renders crisply with zero clipped text | `NOT VERIFIED` |
| 2 | **Initial Setup Wizard** | `SetupWizardPage` | Create local operator admin credentials on first run; restart Desktop and sign in | Password salted/hashed in SQLite (`LocalAdminProfile`); setup wizard does not reappear after completion | `NOT VERIFIED` |
| 3 | **Dashboard Loading** | `DashboardPage` | Open Dashboard after login; observe KPI tiles, server status cards, recent deployments | Loads smoothly without UI freezes, layout overflow, or unhandled exceptions | `NOT VERIFIED` |
| 4 | **Server Pairing (mTLS)** | `ServersPage` | Enter disposable Agent IP/port, one-time pairing code, and pinned server thumbprint; test invalid thumbprint first, then valid pairing | Wrong thumbprint fails closed with actionable error; valid pairing persists mTLS cert and shows `Connected` | `NOT VERIFIED` |
| 5 | **Project Registration** | `ProjectsPage` | Create a new `ProjectGroup`, verify it syncs to paired Agent and survives Desktop restart | Project persists in both Desktop and Agent SQLite stores; duplicate/empty slugs rejected cleanly | `NOT VERIFIED` |
| 6 | **GitHub Repository Import** | `GitHubPage` | Save test-only Fine-Grained PAT (DPAPI-encrypted), validate token, search/list disposable repo, explicitly authorize repository | PAT masked in UI, never shown in logs; rate limit and authorized repo badge display accurately | `NOT VERIFIED` |
| 7 | **Application Configuration** | `ApplicationsPage` | Register ASP.NET Core disposable app (`GitHubActionsArtifact` and `ManualSignedPackage` modes), set branch, port, health check path, retention count | Validation catches bad slugs/ports/traversal paths; saved app displays accurate runtime and management badge | `NOT VERIFIED` |
| 8 | **Deployment Initiation** | `DeploymentsPage` | Trigger manual deployment: verify `GitHubActionsArtifact` requires only 40-char Commit SHA (Agent verifies server-side) while `ManualSignedPackage` requires local path + 64-char SHA-256 | Clear UI distinction between server-verified GitHub Actions artifacts and local signed packages; invalid inputs blocked before send | `NOT VERIFIED` |
| 9 | **Deployment Monitoring & Rollback** | `DeploymentsPage` / `ReleasesPage` | Watch live deployment state transitions, inspect release list (`Active` / `LastKnownGood`), trigger rollback | State badges, timestamps, error summaries, and active release indicators update accurately | `NOT VERIFIED` |
| 10 | **Environment Variables** | `EnvironmentVariablesPage` | Add plain and secret Production variables, import/export `.env`, verify secret masking | Secret values display masked (`••••••••`); `.env` export redacts secrets by default (`<REDACTED_SECRET>`) | `NOT VERIFIED` |
| 11 | **Database Connections** | `DatabasesPage` | Configure SQL Server / PostgreSQL connection record, run connection test | Connection string protected via DPAPI; clear latency/error result shown without leaking passwords | `NOT VERIFIED` |
| 12 | **Domain & IIS Configuration** | `DomainsPage` / `IisInventoryPage` | View discovered IIS sites (`ReadOnlyUnmanaged` lock badge on pre-existing sites), configure domain binding + DNS instructions, test conflict detection | Unmanaged IIS sites cannot be altered without explicit adoption dry-run; DNS instructions render clearly | `NOT VERIFIED` |
| 13 | **Unified Logs & Audit Trail** | `LogsPage` | Filter by category/severity, inspect Agent + audit log entries, verify secret redaction | Zero plaintext PATs, pairing codes, or DB passwords appear anywhere in logs | `NOT VERIFIED` |
| 14 | **Backup Configuration** | `BackupsPage` | Configure backup directory, inspect retention/backup actions, trigger dry-run or test backup | Safe path validation enforced; destructive restore requires explicit confirmation | `NOT VERIFIED` |
| 15 | **Theme Switching** | `SettingsPage` + Global Theme | Toggle Light / Dark theme across all 13 WPF pages | Every page maintains readable contrast, crisp borders, and zero invisible text/icons in both themes | `NOT VERIFIED` |
| 16 | **Close-to-Tray Behavior** | Main Window + `NotifyIcon` | Click window close (`X`) button while minimize-to-tray is enabled | Window hides cleanly to Windows 11 system tray; `ServerForge.Desktop` stays alive without zombie windows | `NOT VERIFIED` |
| 17 | **Reopen, Exit & Clean Uninstall** | Tray Context Menu + Uninstaller | Double-click / click `Open` in tray menu to restore window; click `Exit` in tray menu to terminate cleanly; test Windows Settings uninstall | Window restores cleanly, `Exit` terminates process cleanly (`0` background processes), and uninstaller removes app binaries while preserving user data | `NOT VERIFIED` |

---

## Sign-Off Rule

Do **NOT** mark Interactive WPF Desktop Verification as `PASS` in `PRODUCTION_READINESS_REPORT.md` until all 17 rows above have real Windows 11 execution evidence and `0` blocking defects.
