# ServerForge — Production Readiness Report (Phase 12.2)

**Overall Status:** **NOT PRODUCTION-READY**  
**Last Verified Windows CI Run:** Run #10 (`44720053575`) @ commit `5226d810789413e01775c9e9f4e0348d080e6b61`  
**Phase 12.2 Status:** Autonomous ETag/artifact-readiness fix (SQLite schema v3), strict workflow/artifact identity & safe redirect/ZIP hardening, single safe disposable IIS entry point (`run-disposable-iis-acceptance.ps1`), and `+6` regression tests (`48` total enabled, `0` skipped) complete on disk — pending Windows CI Run #11 and external Windows IIS / Windows 11 acceptance runs.

---

## Four-Gate Release Readiness Scorecard

| # | Release Gate | Status | Evidence / Required Action |
| --- | --- | --- | --- |
| **1** | **Windows `.NET 10` Build, Automated Tests & Installers** | **VERIFIED AT RUN #10 (`42/42` + `50/50`) — PHASE 12.2 (`48` TESTS) PENDING CI RUN #11** | Run #10 (`44720053575`) passed all 42 xUnit tests (including server-side GitHub provenance rejection), installer builds, SHA-256 checksums, and 50/50 smoke assertions. Push Phase 12.2 to verify **48/48** xUnit + **50/50** smoke in Run #11. |
| **2** | **TEST A — Locally Built Sample Artifact Deployed to Real IIS** | **BLOCKED (REQUIRES DISPOSABLE WINDOWS IIS VM)** | Single safe entry point `run-disposable-iis-acceptance.ps1` + PS 5.1/7 scripts ready (`ManualSignedPackage`, `LOCAL TEST ARTIFACT`, live provenance guard probe, `web.config` check, HTTP 503 rollback, Agent restart resilience). Must execute on a disposable Windows IIS VM marked `SERVERFORGE_DISPOSABLE_E2E_HOST=true`. |
| **3** | **TEST B — Real GitHub Actions Autonomous Deployment & Rollback** | **BLOCKED (REQUIRES DISPOSABLE WINDOWS IIS VM + DISPOSABLE GITHUB REPO)** | `run-disposable-iis-acceptance.ps1 -IncludeTestB` / `run-github-autonomous-test.ps1` implements all 18 steps with 0 `/api/v1/deployments/trigger` calls and now exercises the Phase 12.2 HTTP 304 decoupled polling worker. Must execute against a disposable GitHub repo on a disposable Windows IIS VM. |
| **4** | **Interactive Windows 11 WPF Desktop GUI Acceptance** | **NOT VERIFIED** | 17-point interactive checklist in `WINDOWS_DESKTOP_ACCEPTANCE.md` covering all 13 native WPF screens, themes, backups, and close-to-tray. Requires interactive Windows 11 testing. |

---

## Security, Reliability & Trust Boundary Controls Verified in Code (Phase 12.1 + 12.2)

- **Decoupled Autonomous Polling & Crash-Safe Candidate Persistence:** `GitHubAutoDeployWorker` decouples branch-head ETag tracking (`LastBranchHeadETag`) from durable per-application candidate readiness in SQLite (`AutonomousDeploymentCandidates`, schema v3 with automatic pre-upgrade `.bak` backup). Commits detected while GitHub Actions is still running keep checking artifact readiness even under **HTTP 304 Not Modified**, survive Agent restarts, allow newer commits to supersede older pending commits, enforce bounded backoff & rate-limit pauses (`Retry-After` / `X-RateLimit-Reset`), and prevent duplicate/in-flight/endless-failed retries.
- **Strict Workflow & Artifact Identity (`GitHubApiClient`):** Enforces exact authorized `owner/repo`, exact branch + 40-char commit SHA, and exact non-blank workflow path — resolving `GET repos/{repo}/actions/workflows/{workflow_id}` whenever `path` is missing from workflow run list responses. Fails closed on ambiguous multi-artifact runs instead of picking the first artifact.
- **Safe Redirects, Size Caps & Archive Hardening:** `IGitHubClient` uses `AllowAutoRedirect = false`, validates HTTPS redirect hosts (`*.githubusercontent.com`, `*.blob.core.windows.net`), strips `Authorization: Bearer <PAT>` on redirects, enforces `512 MB` compressed / `2 GB` uncompressed caps + `Content-Length` truncation checks with bounded retries, verifies SHA-256 **before** ZIP parsing, and validates ZIP archive integrity against corrupt archives, Zip Slip, and zip bombs.
- **Server-Side GitHub Artifact Provenance:** `GitHubActionsArtifact` mode requires independent server-side verification via `IGitHubArtifactProvenanceVerifier` (`AgentGitHubProvenanceVerifier`). External callers to `POST /api/v1/deployments/trigger` cannot pass local file paths/arbitrary URLs (`HTTP 400`), cannot forge workflow run or artifact IDs, cannot override digests, and cannot spoof `TriggeredAutomatically = true`.
- **E2E Workflow & Disposable Host Safeguards:** `/.github/workflows/e2e-disposable-iis.yml` and `run-disposable-iis-acceptance.ps1` enforce exact positive disposable host marker `SERVERFORGE_DISPOSABLE_E2E_HOST=true`, absence of production `ServerForgeAgent` services, zero `${{ inputs.* }}` shell interpolation, and guaranteed owned-only cleanup (`ServerForge_E2E_*`).
- **Preserved Production Safeguards:** Pre-existing IIS sites remain `ReadOnlyUnmanaged`; loopback-only default management endpoint (`127.0.0.1:9443`); fail-closed pinned-thumbprint mTLS; DPAPI secret protection; hosted-file preservation on removal/uninstall.

---

## Conditions Required Before Declaring Production-Ready

1. Windows CI Run #11 (`build-windows-installers.yml`) completes **GREEN** with **48/48** xUnit tests and **50/50** installer smoke checks.
2. **TEST A** passes on a genuine disposable Windows Server IIS VM with sanitized `deployment-evidence.json` and `rollback-evidence.json` recorded in `E2E_ACCEPTANCE_REPORT.md`.
3. **TEST B** passes on a genuine disposable Windows Server IIS VM + disposable GitHub repo with real `workflowRunId`, `artifactId`, `commitSha`, and `deploymentId` recorded in `github-autonomous-evidence.json` and `E2E_ACCEPTANCE_REPORT.md`.
4. **Interactive Windows 11 Desktop Acceptance** (`WINDOWS_DESKTOP_ACCEPTANCE.md`) passes all 17 checks across all 13 native WPF screens with zero blocking defects.
