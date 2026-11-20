# ServerForge — Production Readiness Report (Phase 12.1)

**Overall Status:** **NOT PRODUCTION-READY**  
**Last Verified Windows CI Run:** Run #9 (`44715138404`) @ commit `b97cdfd56c5605a598620fee9683ac5ea56ea6f2`  
**Phase 12.1 Status:** Hardened on disk (`42` enabled xUnit tests, server-side GitHub provenance enforcement, hardened E2E workflow, split TEST A / TEST B harness) — pending Windows CI Run #10 and external Windows IIS / Windows 11 acceptance runs.

---

## Four-Gate Release Readiness Scorecard

| # | Release Gate | Status | Evidence / Required Action |
| --- | --- | --- | --- |
| **1** | **Windows `.NET 10` Build, Automated Tests & Installers** | **VERIFIED AT RUN #9 (`38/38` + `50/50`) — PHASE 12.1 (`42` TESTS) PENDING CI RUN #10** | Run #9 (`44715138404`) passed all 38 xUnit tests, installer builds, SHA-256 checksums, and 50/50 smoke assertions. Push Phase 12.1 to verify **42/42** xUnit + **50/50** smoke in Run #10. |
| **2** | **TEST A — Locally Built Sample Artifact Deployed to Real IIS** | **BLOCKED (REQUIRES DISPOSABLE WINDOWS IIS VM)** | Scripts audited & fixed for PS 5.1/7 (`ManualSignedPackage`, `LOCAL TEST ARTIFACT`, live provenance guard probe, `web.config` check, HTTP 503 rollback, Agent restart resilience). Must execute on a disposable Windows IIS VM. |
| **3** | **TEST B — Real GitHub Actions Autonomous Deployment & Rollback** | **BLOCKED (REQUIRES DISPOSABLE WINDOWS IIS VM + DISPOSABLE GITHUB REPO)** | Dedicated `run-github-autonomous-test.ps1` implements all 18 steps with 0 `/api/v1/deployments/trigger` calls. Must execute against a disposable GitHub repo on a disposable Windows IIS VM. |
| **4** | **Interactive Windows 11 WPF Desktop GUI Acceptance** | **NOT VERIFIED** | 17-point interactive checklist in `WINDOWS_DESKTOP_ACCEPTANCE.md` covering all 13 native WPF screens, themes, backups, and close-to-tray. Requires interactive Windows 11 testing. |

---

## Security & Trust Boundary Controls Verified in Code (Phase 12.1)

- **Server-Side GitHub Artifact Provenance:** `GitHubActionsArtifact` mode now requires independent server-side verification via `IGitHubArtifactProvenanceVerifier` (`AgentGitHubProvenanceVerifier`). External callers to `POST /api/v1/deployments/trigger` cannot pass local file paths/arbitrary URLs (`HTTP 400`), cannot forge workflow run or artifact IDs, cannot override digests, and cannot spoof `TriggeredAutomatically = true`.
- **HTTPS GitHub Host Allowlisting:** `GitHubApiClient` rejects any `archive_download_url` not using `https://` on `api.github.com` or `github.com`.
- **Clear Package Separation:** `ManualSignedPackage` requires an explicit local file path and 64-character SHA-256 digest, rejects remote URLs, and is explicitly labeled `LOCAL TEST ARTIFACT (ManualSignedPackage - NOT GitHub Provenance)` or `ManualSignedPackage` so it can never be mistaken for GitHub provenance.
- **E2E Workflow Injection & Host Protection:** `/.github/workflows/e2e-disposable-iis.yml` passes all inputs via environment variables, validates inputs/ports/runner labels in a pre-dispatch job, removes `windows-latest`, requires GitHub Environment approval (`disposable-windows-iis-e2e`), and verifies a positive disposable VM marker plus absence of production `ServerForgeAgent` services.
- **Preserved Production Safeguards:** Pre-existing IIS sites remain `ReadOnlyUnmanaged`; loopback-only default management endpoint (`127.0.0.1:9443`); fail-closed pinned-thumbprint mTLS; DPAPI secret protection; hosted-file preservation on removal/uninstall.

---

## Conditions Required Before Declaring Production-Ready

1. Windows CI Run #10 (`build-windows-installers.yml`) completes **GREEN** with **42/42** xUnit tests and **50/50** installer smoke checks.
2. **TEST A** passes on a genuine disposable Windows Server IIS VM with sanitized `deployment-evidence.json` and `rollback-evidence.json` recorded in `E2E_ACCEPTANCE_REPORT.md`.
3. **TEST B** passes on a genuine disposable Windows Server IIS VM + disposable GitHub repo with real `workflowRunId`, `artifactId`, `commitSha`, and `deploymentId` recorded in `github-autonomous-evidence.json` and `E2E_ACCEPTANCE_REPORT.md`.
4. **Interactive Windows 11 Desktop Acceptance** (`WINDOWS_DESKTOP_ACCEPTANCE.md`) passes all 17 checks across all 13 native WPF screens with zero blocking defects.
