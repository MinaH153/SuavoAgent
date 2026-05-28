# SuavoAgent v4 — End-to-end install + distribution overhaul

**Author:** Joshua Henein
**Status:** Draft (awaiting Codex review per `feedback-codex-review-every-spec`)
**Created:** 2026-05-28
**Scope:** Cross-cutting — `MinaH153/SuavoAgent` (client) + `SuavoLLC/MKM` (dashboard + APIs)
**Estimated duration:** 8-10 weeks solo

---

## 1. Context

As of 2026-05-28 the SuavoAgent install/distribution surface looks like this:

- **Public download page** at `https://suavollc.com/suavoagent/download` serves a generic ZIP. The ZIP contains 5 EXEs and **no `setup.json`**, so `SuavoSetup.exe` immediately errors with `No setup.json found` for anyone who finds the URL. The download page is operational smoke-test only.
- **`bootstrap.ps1`** (the real path used in pilot installs) requires the user to open Admin PowerShell, paste an `irm | iex` one-liner, and either pre-know a `-PharmacyId` + `-ApiKey` or pass an `-InstallToken` minted via a manual API call.
- **No Mac support.** `SuavoSetup.exe` and all 4 runtime binaries are Windows-only.
- **Local LLM model not bundled.** `LocalFileModelManager.cs` requires the operator to manually drop the `.gguf` file at the configured `ModelPath`. Native `llama.dll` / `ggml.dll` also operator-placed.
- **No in-app auto-update.** Update manifest signature infrastructure exists (`update-manifest-vX.Y.Z.sig` in releases), but nothing consumes it client-side yet.

The eSigner cloud-signing pipeline shipped today (PR #97 + v3.14.6) closed half of Track 6 readiness. v4 closes the rest of Track 1 (Reliable install + self-healing) plus the deployable-at-scale prerequisites for Tracks 2-5.

### Why now (sequencing)

The pre-revenue strategy is "harden product → production-ready → aggressive marketing and sales push." Nadim is the first paying pharmacy. Maged is the test environment for restaurant vertical. Today's install UX works for Joshua-deploys-it-himself with a screwdriver, but does not survive *"I send Nadim a link and he installs it without me on a Zoom call."* v4 is what turns SuavoAgent from "ops-deployed agent" into "consumer-grade product Pharmacy IT can self-install."

### Scope decision (selected 2026-05-28)

**Option A — end-to-end: SuavoAgent client + Suavo dashboard side.** Coherent product feature. The agent's device-code auth handshake doesn't work without the dashboard endpoints to mint tokens and approve bindings, and the polished `/download` page isn't shippable without OS-detection on the Suavo side. Bundling them avoids "we shipped the agent but it can't auth yet."

### Cross-references

- **Vision context:** `[[suavoagent-product-vision-2026-05-01]]` — six tracks, beachhead → empire framing
- **eSigner signing pipeline:** `[[suavoagent-esigner-live-v3-14-6]]` — what's already shipped that v4 builds on
- **Avoid this pattern:** `[[feedback-vercel-env-add-newline-trap]]` — same `printf '%s'` vs `echo` discipline applies to model SHA env injection
- **Install gotchas already learned:** `feedback-windows-install-lessons.md` — ExecutionPolicy / Unicode / `irm-OutFile` / ACLs / native DLLs / locked EXEs
- **Existing Suavo-side spec:** `~/Code/Suavo/docs/superpowers/specs/2026-05-02-admin-suavoagent-install-dashboard-design.md` — predecessor to v4 dashboard work; v4 extends this with device-code flow + workstation binding UI

---

## 2. Goals

1. **One-click install per OS.** Pharmacy admin sees `Download for Mac` or `Download for Windows`, clicks, runs the installer. No PowerShell paste, no ZIP extraction, no setup.json shuffle.
2. **Browser-based workstation binding.** First launch → device code → browser → admin approves → workstation joins the pharmacy tenant. Same shape as Tailscale `tailscale up`.
3. **Polished native UX on both platforms.** Signed installers (EV on Win, Developer ID + notarized on Mac), native menus / tray icons, native notifications.
4. **Local LLM auto-download via signed manifest.** Ships Tier 2 model on first launch via the same ECDsa-signed manifest pattern used for ruleset OTA today. Operator stops dropping `.gguf` files by hand.
5. **Background auto-update with channel pinning.** Stable / canary channels; pharmacy IT can pin; updates apply on next clean agent restart, never mid-shift.
6. **`--doctor` diagnostic.** PASS/FAIL on PioneerRx, cert chain, model integrity, dashboard reachability, watchdog state, last sync, last update — JSON output mode for support tooling.
7. **3 consecutive pilot installs survive 7+ days with no rescue.** This is Track 1's readiness gate per `[[suavoagent-product-vision-2026-05-01]]` and is v4's success criterion.

---

## 3. Non-goals

Explicitly NOT in scope for v4 (open separate work):

- **Linux client.** Pharmacy workstations are Windows. macOS is added for Suavo HQ ops + future fleet-operator-on-Mac scenarios. Linux is deferred to v5 when a customer demands it.
- **Mobile dashboard (iOS / Android).** Pharmacy admins are on desktop. The web dashboard is responsive enough for owner-on-phone read-only checks.
- **Tier 3 cloud LLM endpoint hardening.** Tier 3 already exists at `/api/agent/reason` (partial). Its polish is a parallel workstream — v4 only requires Tier 3 to be **reachable**, not feature-complete.
- **PMS adapters beyond PioneerRx.** ComputerRx canary stays where it is. v4 doesn't add ComputerRx / QS/1 / BestRx adapters.
- **Patentable Track 5 surface.** The agentic remote control (observe/propose/show-cursor → signed-command narrow verbs with Cedar policy + Temporal saga) is the v5 frontier. v4 stays at observe-only.
- **iOS driver app.** Different repo, different surface, different release cadence.

---

## 4. Architecture decisions (ADRs)

### ADR 1 — Cross-platform via Avalonia (already chosen)

The Setup GUI is already Avalonia 11 (verified today at `src/SuavoAgent.Setup/Gui/App.axaml`). Avalonia compiles to native binaries on macOS arm64/x64 and Windows x64. **Mac support is a packaging concern, not a UI rewrite.** No SwiftUI, no Electron. The same XAML files render natively on both OSes.

**Consequence:** v4 does not reuse the existing Windows-only `SuavoAgent.Setup.csproj` runtime targets — it adds `osx-arm64` and `osx-x64` to `RuntimeIdentifiers`, plus a Mac-specific `Info.plist`. ~1 day of `csproj` plumbing, not a UI rewrite.

### ADR 2 — Tailscale-style device-code auth, not per-user OAuth

Pharmacy workstations are deployed appliances, not personal devices. The right vocabulary is "admin approves this workstation for this pharmacy", not "user signs in with their account." Tailscale's tagged-device + expiring-auth-key model is the analogue. Claude Desktop's per-user Anthropic-account login is **not** the analogue and copying it conflates pharmacist identity with workstation identity (HIPAA hazard).

**Auth flow:**
1. Agent on first launch generates an 8-character device code (Crockford base32 — `ABC-123-XYZ`, dashed for readability, no ambiguous chars).
2. Agent opens default browser to `https://suavollc.com/install?code=ABC-123-XYZ`.
3. Pharmacy admin signs into Suavo dashboard (existing auth path — Supabase Auth + MFA), lands on the install-code redemption page.
4. Admin sees "Approve this workstation for `Queen Pharmacy → 5310 Fountain Grass Ave`?" + workstation hardware fingerprint preview + last-IP preview.
5. Admin clicks Approve → server mints a single-use, expiring `tskey-suavo-...` auth key tagged with `pharmacy_id` + `workstation_id`, stores hash, returns key once.
6. Agent (which has been short-polling `GET /api/agent/device-code/ABC-123-XYZ/status` every 2 seconds) sees `status=approved` + receives the auth key, persists encrypted (DPAPI on Win, Keychain on Mac), deletes the device code locally, transitions to "bound" state.

**Headless / Intune install path** retains `SuavoSetup.exe --auth-key=tskey-suavo-...` for SCCM/Intune unattended deploys. Pharmacy admin pre-mints the auth key in the dashboard, hands it to IT.

### ADR 3 — MSI on Windows, DMG (notarized) on Mac

Windows: WiX 4 builds an MSI that lays down `C:\Program Files\Suavo\Agent\`, registers Core/Broker/Watchdog as Windows Services, ACLs `%ProgramData%\SuavoAgent\` correctly, adds Add/Remove Programs entry, supports `msiexec /i SuavoAgent.msi AUTH_KEY=tskey-...` for unattended. **EV-signed MSIs get instant SmartScreen reputation** because the EV identity is the trust anchor — eliminates today's "Windows protected your PC" panel.

Mac: a single `.app` bundle (containing Core/Broker/Helper/Watchdog inside the bundle's `MacOS/` and helpers running as launchd `LaunchAgent`s) wrapped in a signed + notarized `.dmg`. First-run installs to `/Applications/SuavoAgent.app`, copies `LaunchAgent` plist to `~/Library/LaunchAgents/`. Apple Developer ID Application + Installer certs (org account confirmed available 2026-05-28).

### ADR 4 — Signed combined manifest for binaries AND model

Today's release has `update-manifest-vX.Y.Z.txt` + `.sig`. v4 evolves this into one signed JSON covering every distribution artifact: 5 binaries (per OS), the local LLM GGUF, the native `llama.dll`/`ggml.dll` per OS, and any new platform-specific bits. Single ECDsa P-256 signature (reusing the existing `RulesetSignatureVerifier` keypair pattern).

**Manifest schema (sketch — v2 post-Codex review):**
```json
{
  "schema_version": 2,
  "channel": "stable",
  "version": "4.0.0",
  "minimum_version": "3.14.6",
  "rollout": { "percent": 100, "cohorts": ["pilot-set-A"] },
  "released_at": "2026-07-15T00:00:00Z",
  "artifacts": {
    "win-x64": {
      "minimum_os": { "windows_build": 17763 },
      "installer": { "url": "...", "sha256": "...", "size": 268435456 },
      "binaries": {
        "SuavoAgent.Core.exe": { "sha256": "...", "size": 44485232 },
        ...
      },
      "native": {
        "llama.dll": { "url": "...", "sha256": "..." }
      }
    },
    "osx-arm64": { "minimum_os": { "macos_version": "13.0" }, ... },
    "osx-x64":   { "minimum_os": { "macos_version": "13.0" }, ... }
  },
  "models": {
    "qwen3-4b-instruct-2507-q4_k_m": {
      "url": "https://models.suavollc.com/qwen3-4b-instruct-2507-q4_k_m.gguf",
      "sha256": "...",
      "size": 2700000000,
      "context_window": 262144,
      "recommended_for": ["minimum-ram-8gb"]
    },
    "llama-3.2-1b-q4_k_m": {
      "url": "https://models.suavollc.com/llama-3.2-1b-q4_k_m.gguf",
      "sha256": "...",
      "size": 808530432,
      "context_window": 2048,
      "recommended_for": ["minimum-ram-4gb"]
    },
    "llama-3.1-8b-instruct-q4_k_m": {
      "url": "https://models.suavollc.com/llama-3.1-8b-instruct-q4_k_m.gguf",
      "sha256": "...",
      "size": 4900000000,
      "context_window": 131072,
      "recommended_for": ["minimum-ram-16gb"]
    }
  }
}
```

**Codex-driven additions (2026-05-28 review):**
- `rollout.percent` + `rollout.cohorts` — canary stop-the-line capability; agents check their cohort eligibility before pulling
- `minimum_os` per-artifact — old Windows/macOS boxes fail-fast before download instead of failing mid-install
- **NOT** added: per-artifact individual signatures. Codex confirmed: manifest sig + per-artifact SHA-256 + OS code-signing is sufficient unless artifacts are installable without the manifest (they aren't in our flow)

**Per-pharmacy managed policy override:** does NOT live in the public manifest. Instead, dashboard issues an auth-scoped `managed_policy_digest` to the agent at sync time. Agent echoes the digest in `--doctor` and heartbeat so drift between admin's stated policy and applied policy is visible. Keeps tenant configuration out of public infrastructure.

**Trust root:** the agent ships with the ECDsa P-256 public key embedded as a resource (same pattern as `Resources/ruleset-pubkey-1.pub`). Manifest verification is *fail-closed*: agent refuses to update or download models if signature is invalid.

### ADR 5 — Local LLM choice on first run

The agent detects hardware on first install (RAM, CPU cores, integrated/discrete GPU) and picks a model from the signed manifest accordingly:

| RAM | Pick | Why |
|---|---|---|
| 4-8 GB | `llama-3.2-1b-q4_k_m` (~800 MB) | Minimum-viable Tier 2 for low-end pharmacy POS terminals |
| 8-16 GB | **`qwen3-4b-instruct-2507-q4_k_m` (~2.7 GB)** | Default. Stronger instruction/reasoning/agent benchmarks than Phi-3-mini at same size class, **256K context vs Phi-3's 4K** — material for screen-state reasoning that has to fit observed UI + history. Codex 2026-05-28 recommendation. |
| 16+ GB | `llama-3.1-8b-instruct-q4_k_m` (~4.9 GB) | Higher-quality reasoning for pharmacies with modern hardware |

**Decision discipline:** model SKUs in this table are **point-in-time picks revalidated quarterly.** Pinning a model into the spec doesn't commit us to it forever — the dashboard's managed policy can roll the entire fleet to a new SKU once we've validated it on a canary cohort. Phi-3-mini was the obvious 2026-04 choice; Qwen3-4B-Instruct-2507 is the 2026-05 choice; reassess on next benchmark sweep. Track changes in `~/Code/obsidian-vault/wiki/concepts/tier-2-model-pickbook.md`.

**Override:** pharmacy admin can force a specific model via dashboard managed settings (e.g. for compliance audit reproducibility — pin one model version across the whole fleet).

**Apple Silicon path:** llama.cpp on macOS uses Metal acceleration automatically when present (Avalonia native is arm64). Same model files (GGUF is platform-agnostic).

---

## 5. Phases

### Phase 1 — Cross-platform packaging (~2 wks)

**Goal:** One release tag (`v4.0.0-rc1`) produces signed Mac DMG + Windows MSI from a single CI run.

**Deliverables:**
- `.github/workflows/release.yml` extended: matrix build over `[win-x64, osx-arm64, osx-x64]`
- WiX 4 project at `installer/windows/SuavoAgent.wxs` — service registration, ACLs, MSI features (Core, Helper, Watchdog as MSI features)
- macOS bundle script at `installer/macos/bundle.sh` — assembles `.app` bundle, signs with Developer ID Application, codesigns sub-binaries, builds `.dmg` via `create-dmg`, notarizes via `notarytool` (using app-specific password from GitHub secrets), staples the ticket
- Apple Developer Program credentials in GitHub secrets: `APPLE_ID`, `APPLE_APP_PASSWORD`, `APPLE_TEAM_ID`, `APPLE_DEVELOPER_ID_APPLICATION_P12`, `APPLE_DEVELOPER_ID_INSTALLER_P12` (P12 base64'd)
- CI matrix produces 3 artifacts: `SuavoAgent-v4.0.0-rc1-win-x64.msi`, `SuavoAgent-v4.0.0-rc1-osx-arm64.dmg`, `SuavoAgent-v4.0.0-rc1-osx-x64.dmg`
- All 3 artifacts attached to GitHub release alongside today's loose binaries (during transition period)

**Files touched (SuavoAgent repo):**
- New: `installer/windows/*.wxs`, `installer/macos/bundle.sh`, `installer/macos/Info.plist.template`
- Edit: `.github/workflows/release.yml`, `src/SuavoAgent.Setup/SuavoAgent.Setup.csproj` (add osx-arm64/x64 RIDs)
- New tests: `tests/SuavoAgent.Installer.Tests/` — MSI feature smoke (Wix log assertions), DMG bundle smoke (codesign + spctl verify, notarization status check)

**Out-of-scope this phase:** device-code auth (still requires setup.json), local model download (still operator-placed), auto-update (still manual).

**Estimated:** 8-10 days. Mac packaging is the longest tail because notarization race conditions are real and `notarytool` retries are needed.

### Phase 2 — OS-detecting `/download` page rewrite (~3 days)

**Goal:** `https://suavollc.com/download` (renamed from `/suavoagent/download`) auto-detects OS and shows the right CTA.

**Deliverables (Suavo repo):**
- `src/app/download/page.tsx` — server component reads `User-Agent` from request headers, picks primary CTA, lists alternatives in a "Other platforms" section
- Manifest update mechanism: `/api/download/manifest` returns latest stable URLs per OS from the signed combined manifest (no more hand-edited `suavoagent-download-manifest.json`)
- 301 redirect from `/suavoagent/download` → `/download` (preserves bookmarks)
- Existing "Signed by MKM Technologies LLC · SHA-256 verified · SmartScreen-trusted" badge stays, gains a "+ notarized for macOS" sibling when on Mac CTA
- Removes the per-pharmacy ZIP confusion entirely — binary is universal; pharmacy binding moves to in-app device-code auth (Phase 3)

**Files touched (Suavo repo):**
- New: `src/app/download/page.tsx`, `src/app/download/page-client.tsx`, `src/app/api/download/manifest/route.ts`
- Edit: `src/content/suavoagent-download-manifest.json` (transitional — read-only display) — eventually deleted when API serves it
- New: `src/app/api/download/manifest/__tests__/route.test.ts`

**Estimated:** 2-3 days. Mostly Next.js plumbing.

### Phase 3 — In-app device-code auth onboarding (~1 wk)

**Goal:** Replace `setup.json`-in-zip with browser-based workstation binding.

**Agent side (SuavoAgent repo):**
- New Avalonia screen `Views/DeviceCodeView.axaml` — shows 8-char code + "Open browser" button + countdown + "Cancel" + "Try a different code" fallback
- New `Setup/DeviceCodeService.cs` — POSTs to `/api/agent/device-code` to get code, short-polls `/api/agent/device-code/{code}/status`, on `approved` retrieves auth key, persists via `IEncryptedCredentialStore` (DPAPI on Win, Keychain on Mac wrapper)
- Updated `Program.cs` boot path — if no `AuthKey` in encrypted store AND no `--auth-key` CLI flag, kick off device-code flow
- Replace `setup.json` consumption — `SetupConfig.Load` now also checks encrypted credential store as a source

**Dashboard side (Suavo repo):**
- New: `src/app/install/page.tsx` — landing page that consumes `?code=ABC-123-XYZ` URL param, shows workstation hardware fingerprint preview pulled from the dashboard, prompts admin to choose pharmacy + (optionally) friendly workstation name, "Approve" button
- New: `src/app/api/agent/device-code/route.ts` (POST — agent creates code) and `src/app/api/agent/device-code/[code]/status/route.ts` (GET — agent polls)
- New: `src/app/api/agent/device-code/[code]/approve/route.ts` (POST — admin approves binding)
- New: `src/app/admin/agents/workstations/page.tsx` — list of bound workstations with status + Revoke + Pause + Repair actions
- Existing `/admin/agents/install-tokens` (per `2026-05-02-admin-suavoagent-install-dashboard-design.md`) becomes the *headless / Intune* pre-mint flow; device-code is the *interactive* path

**Data model (Supabase migrations):**
- `agent_device_codes` — `code TEXT PRIMARY KEY, created_at TIMESTAMPTZ, expires_at TIMESTAMPTZ NOT NULL, ip TEXT, fingerprint JSONB, status TEXT CHECK (status IN ('pending','approved','expired','cancelled')), approved_by UUID REFERENCES auth.users, approved_for_pharmacy_id UUID REFERENCES pharmacy_profiles, bound_workstation_id UUID REFERENCES agent_workstations`
- `agent_workstations` — extends existing `pharmacy_install_state` table (already exists per 2026-05-02 spec); adds `auth_key_hash TEXT NOT NULL, auth_key_revoked_at TIMESTAMPTZ, friendly_name TEXT, hardware_fingerprint JSONB, bound_at TIMESTAMPTZ`
- RLS policies: device codes readable only by their requesting agent (via short-lived agent-scoped token) AND by pharmacy admins of the bound pharmacy

**Files touched:**
- SuavoAgent: ~10 new files (Avalonia view + viewmodel + service + encrypted-store interface + DPAPI impl + Keychain impl + tests)
- Suavo: ~8 new files (5 API routes + 2 pages + 1 RLS migration)

**Open questions for Codex review:**
- Should device code be 8 chars or 6 chars? (UX tradeoff vs. brute-force window — codes expire in 15 min so brute force is bounded)
- What's the polling interval? (2 sec is responsive but generates load; 5 sec is gentler. Default to 2 sec for first 60 sec, back off to 5 sec, then 10 sec at 5 min mark)
- Hardware fingerprint shown to admin — what fields? (hostname, OS version, CPU model, RAM, MAC address hash, install timestamp) — must NOT leak PHI; the workstation's PMS database content is irrelevant to fingerprinting

**Estimated:** 5-7 days. Tight loop between agent + dashboard; needs careful coordination.

### Phase 4 — Local LLM auto-download via signed manifest (~1 wk)

**Goal:** First-run download of the chosen GGUF + native deps from a signed manifest. **Ships the "Week 2c" milestone that's been deferred since `LocalFileModelManager.cs` was first written.**

**Deliverables (SuavoAgent repo):**
- Extend `LocalFileModelManager` with `DownloadAsync` method (today it only has `VerifyAsync`)
- New `SignedManifestClient` (Core/Cloud namespace) — fetches `manifest.json` + `manifest.json.sig`, verifies via `RulesetSignatureVerifier` (existing keypair infrastructure), returns parsed manifest
- Hardware-detection helper: `HardwareProfiler.cs` — RAM, logical CPU count, AVX support, Apple Silicon detection (via `Environment.OSArchitecture`), discrete GPU check (best-effort)
- Model selection logic in `LocalFileModelManager` — picks `models.<id>` entry from manifest based on `HardwareProfile`
- Download with resume + progress: HTTP `Range` requests, integrity check at end (SHA-256 against manifest), atomic rename on completion
- Native deps (`llama.dll` / `ggml.dll` on Win, `libllama.dylib` / `libggml.dylib` on Mac) downloaded same way to `%ProgramData%\SuavoAgent\native\` or `~/Library/Application Support/SuavoAgent/native/`
- New Avalonia screen `Views/ModelDownloadView.axaml` — first-run progress UI (model name + size + ETA + cancel)
- Cancellation: cancel → next launch resumes; no auto-rollback to "model failed, run rules-only" without explicit operator action

**Files touched:**
- SuavoAgent: ~8 new files (extended ModelManager + ManifestClient + HardwareProfiler + Avalonia view/viewmodel + tests)
- Models hosting: requires new Suavo Cloudflare R2 bucket `models.suavollc.com` (write process: model is uploaded once, manifest signed, both deployed atomically)
- Manifest signing tool: `scripts/sign-manifest.ts` in Suavo repo (mirrors `scripts/sign-ruleset.ts` shipped 2026-05-15 in PR #540)

**Open questions for Codex review:**
- Which models do we initially publish? Phi-3-mini-q4_k_m is the safest default but llama-3.2-1b is faster on low-end terminals. Initial publish list: phi-3-mini-q4_k_m + llama-3.2-1b-q4_k_m + llama-3.1-8b-instruct-q4_k_m (3 SKUs, ~9 GB total uploaded once)
- Disk-space pre-check before download — if `<size>` not available at `%ProgramData%`, the agent should refuse and surface a clean error
- Bandwidth pacing — should the model download be throttled? Pharmacy workstations often share a constrained DSL line; saturating it during install hours could be noticed. Default to background priority

**Estimated:** 5-7 days. Most of the surface is plumbing; the hardware detection + model selection logic + manifest schema is where the design care goes.

### Phase 5 — Channel-pinned auto-update (~5 days)

**Goal:** Background updater applies signed-manifest releases on the next clean agent restart. Channel pinning by pharmacy admin via managed settings.

**Deliverables (SuavoAgent repo):**
- New `Workers/UpdateWorker.cs` — runs every 6 hr (jittered ±30 min), fetches manifest for the pinned channel, compares versions, decides whether to download
- Update applies on next clean restart, never mid-shift — `UpdateWorker` writes to `pending-update.json`, `SuavoAgent.Watchdog` notices on next restart and applies
- Managed settings file at `%ProgramData%\SuavoAgent\managed.json` (Win) / `/Library/Managed Preferences/com.suavollc.agent.plist` (Mac) — pharmacy IT pins `autoUpdatesChannel: "stable" | "latest"` and `minimumVersion: "4.0.0"`
- Per-agent override via dashboard managed settings (server-pushed to override the local file)
- `ApplyUpdateAsync` does fail-safe: keep last 2 versions side-by-side, swap symlink/junction, then **two-stage health probe** (Codex 2026-05-28 finding — 60s probe was naively-short):
  - **Stage 1 (2 minutes):** Core/Broker/Helper/Watchdog service liveness, IPC bus health, auth introspection roundtrip, SQL Server / PioneerRx reachability, model file SHA hash + llama.cpp load, state.db migration read/write, heartbeat upload roundtrip
  - **Stage 2 (15-30 minutes OR first synthetic non-PHI Rx workflow, whichever first):** synthetic detection workflow that exercises the Tier 1 → Tier 2 → cloud sync path end-to-end on a non-PHI canary record
  - Auto-rollback fires on Stage 1 fail (fast) OR Stage 2 fail within 30 min window (slower; agent stays on new version meanwhile in a "candidate" state with elevated telemetry)
  - Reasoning: heartbeat-only health probes look healthy for 5+ minutes on a workstation that's between Rx batches but is actually broken
- Mac auto-update via Sparkle-style appcast feed (Sparkle is overkill but the appcast XML format is standard — we can serve our own from the signed manifest)

**Files touched:**
- SuavoAgent: ~6 new files (UpdateWorker + ApplyUpdate + ManagedSettings + Watchdog integration + Sparkle-compat appcast endpoint + tests)
- Suavo (server-side): new `/api/manifest/sparkle-appcast.xml` route serves Mac-compatible appcast feed derived from the signed manifest

**Open questions for Codex review:**
- Mid-update interruption (Watchdog kills Core mid-swap) — rollback path needs to be tested under chaos
- Channel demotion (`latest` → `stable`) shouldn't auto-downgrade to an older version. Force-pin requires admin action
- Update telemetry must NOT leak PHI — only version, channel, success/failure, duration — never anything from `state.db`

**Estimated:** 4-5 days. Mostly done patterns from Sparkle / Squirrel.Windows; the auto-rollback health probe is the careful bit.

### Phase 6 — `--doctor` diagnostic command (~2 days)

**Goal:** One command to surface install integrity, auth state, model integrity, dashboard reachability, watchdog status. First step in every support ticket from now on.

**Deliverables (SuavoAgent repo):**
- `Core/Diagnostics/DoctorCommand.cs` — runs 13 checks in parallel where independent, sequential where dependent
- Checks: (1) PioneerRx detection, (2) SQL Server reachability + auth, (3) cert chain on Authenticode signature, (4) local model integrity (SHA-256 against manifest), (5) dashboard reachability (`/api/agent/heartbeat` HEAD request), (6) auth key validity (server-side token introspection), (7) Watchdog service state, (8) Core service state, (9) Broker IPC socket health, (10) Helper user-session state, (11) last successful sync timestamp, (12) last update attempt + outcome, **(13) PHI residue report — counts of files / bytes still under each retention category, time since last cleanup, last revoke event (added by Codex 2026-05-28 review §13)**
- Output mode: human-readable PASS/FAIL table (default), `--json` for support tooling
- Remediation hints — each failure includes a suggested fix or link to runbook
- Exit code: 0 if all pass, 1 if any fail, 2 if unrunnable

**Files touched:**
- SuavoAgent: ~4 new files (DoctorCommand + CheckRegistry + 12 individual check implementations + tests)
- Suavo (server-side): new `/api/agent/introspect-token` route for auth-key validity check (no-op if token presented is invalid; useful for support tickets)

**Open questions for Codex review:**
- Should `--doctor` output be uploaded to dashboard automatically? (Tradeoff: support speed vs. PHI risk if output bug leaks state — default to manual paste, allow opt-in upload)
- Failure remediation hints — should they be inline strings or runbook URLs? (URLs decay; inline strings rot — current lean is inline + linked-to-runbook for deep dives)

**Estimated:** 2-3 days. Most checks are 1-line wrappers around existing health probes.

### Phase 7 — Native menu / tray polish (~1 wk)

**Goal:** Native menu bar icon (Mac) / system tray icon (Win) with About box, Show logs, Restart agent, Reauth workstation, Check for updates.

**Deliverables (SuavoAgent repo):**
- New `Helper/TrayIcon/` — platform-conditional implementation (`TrayIconWin.cs` using Win32 `NotifyIcon` via `Vanara.PInvoke.Shell32`, `TrayIconMac.cs` using `NSStatusBar` via `Xamarin.Mac` or `Avalonia.Native` hooks)
- About box: version, Authenticode cert subject DN (publisher), trust root fingerprint, channel, last update timestamp, "Copy diagnostic info" button
- Menu items: Show dashboard, Show logs (opens `%ProgramData%\SuavoAgent\logs\` in Explorer / Finder), Restart agent, Reauth workstation (revokes local auth key + triggers Phase 3 device-code flow), Check for updates (manual update probe), Pause / Resume agent, About
- Notifications: model-download-complete, update-available, update-applied, sync-failed-3-times, workstation-revoked-by-admin
- macOS bonus: app appears in `cmd-tab` switcher with proper icon + menu bar dropdown when window closed

**Files touched:**
- SuavoAgent.Helper: ~10 new files (TrayIcon + AboutBox + NotificationService + platform-specific impls + tests)

**Estimated:** 4-5 days. Tray/menu plumbing is mature on Win; Mac side via Avalonia.Native is the unknown.

---

## 6. Dashboard side (Suavo repo) — consolidated summary

Pages added or modified:
- `/download` — OS-detecting download page (Phase 2)
- `/install?code=...` — device-code redemption (Phase 3)
- `/admin/agents/workstations` — bound workstation list with status + actions (Phase 3)
- `/admin/agents/workstations/[id]` — workstation detail / logs / revoke (Phase 3)
- `/admin/agents/install-tokens` — existing per 2026-05-02 spec; gains UI for pre-minting Intune auth keys (Phase 3)
- `/admin/agents/settings` — managed settings UI (channel pinning, model override, allow/deny destructive Tier-2) (Phase 5)

APIs added:
- `POST /api/agent/device-code` (Phase 3)
- `GET /api/agent/device-code/[code]/status` (Phase 3)
- `POST /api/agent/device-code/[code]/approve` (Phase 3, RLS-protected by pharmacy admin role)
- `GET /api/manifest/combined.json` + `.sig` (Phase 4 + 5, signed)
- `GET /api/manifest/sparkle-appcast.xml` (Phase 5, Mac auto-update)
- `POST /api/agent/introspect-token` (Phase 6)

Storage:
- New Cloudflare R2 bucket `models.suavollc.com` hosting GGUF files + native deps + installer artifacts (Mac DMG + Win MSI mirrored from GitHub Releases)
- Reasoning: GitHub Releases bandwidth quota is fine for low-volume but multi-GB model downloads at scale will outgrow free tier. R2 is the cost-effective CDN.

---

## 7. Security model

### Trust roots
1. **Authenticode / Apple notarization** for binaries — already in place (EV SSL.com + Apple Developer ID after Phase 1)
2. **ECDsa P-256 manifest signature** for combined update + model manifest — reuses `RulesetSignatureVerifier` pattern, embeds pubkey as Resource in agent binary
3. **HMAC-pinned cloud sync** — already in place
4. **Device-code auth + single-use auth key** — new in Phase 3, generated server-side, hashed at rest

### Token lifecycle
- Device codes: 15 min TTL, single-use, expire on first approve or first non-approval lookup (whichever first)
- Install tokens (Intune path): 30 day TTL by default, single-use, pharmacy-scoped, revocable from dashboard
- Auth keys (after binding): no TTL by default, revocable from dashboard, automatic re-issue on workstation reauth flow
- All tokens stored as SHA-256 hash server-side; plain value returned only once at mint time

### HIPAA invariants (Track 3, existential)
- **Zero PHI** in: telemetry, update manifest requests, model download requests, device-code requests, auth key headers (only token bytes, never patient data), Sentry payloads, GitHub Actions logs
- Device-code hardware fingerprint must NOT include any pharmacy database content — only host-level hardware
- Model prompts MUST be sanitized via existing `PiiScrubber` before Tier 3 cloud send; Tier 2 local LLM gets unsanitized PHI (it never leaves the workstation)
- Audit log captures every install, every reauth, every revoke, every channel pin change

### Encrypted credential storage (v2 post-Codex review)

**Codex 2026-05-28 rejected the original "DPAPI-LocalMachine + AES-GCM-keyed-from-machine-UUID" design.** Both keys live on the same host, so a SYSTEM/admin extraction defeats both. Right pattern:

- **Windows**: TPM-backed non-exportable device key (Windows 10+ TPM 2.0 universally present on pharmacy-grade hardware). Persistent auth credential is a server-rotated short-lived token (e.g. 24-hour TTL refreshed via heartbeat), unwrapped per-use by the TPM. Service-SID ACLs on the credential blob path so the Helper user-session process can't read what only Core needs. Falls back to DPAPI-LocalMachine + AES on TPM-less machines with a loud warning + dashboard alert ("workstation lacks TPM — credential security degraded").
- **Mac**: System Keychain (or daemon-owned item) with `SecAccessControl` tied to Suavo's designated code-signing requirement (DesignatedRequirement string from the EV cert chain). Bundle-ID scoping alone is **insufficient** — a same-user malicious app on a shared pharmacist workstation could read a user-Keychain entry. Helper user-session process gets a short-lived IPC capability handed by Core, not direct Keychain access.

The auth-key persistence interface (`IEncryptedCredentialStore`) keeps the same shape; the platform impls underneath change. This is a real-shift not a name-change.

---

## 8. Migration / rollout

### Backward compatibility during transition

- `bootstrap.ps1` stays functional for the entire v4 cycle and beyond — it becomes the *fallback* install path
- Existing pilots running v3.x continue to receive updates via current ad-hoc path until they upgrade to v4
- v3 → v4 upgrade: the v3 agent's `UpdateWorker` (if shipped before v4 cutover) treats v4 as the next stable release; v3 without `UpdateWorker` requires manual upgrade via re-run of `bootstrap.ps1` pointed at the v4 MSI

### Rollout sequencing (v2 post-Codex review — reordered to fix update-failure trap)

**Codex 2026-05-28 flagged the original Phase 1→7 ship order as a trap:** if Phase 5 (auto-update) ships before Phase 6 (`--doctor`) and Phase 7 (tray icon), a pilot pharmacy hitting a bad auto-update has no diagnostic and no UI surface to find out the agent is broken. The 60s watchdog only catches hard startup failure, not "agent alive but unusable." Reordered:

1. **Phase 1 ship** as v3.15.0-rc1 — installs but doesn't change behavior; verifies Mac + Win packaging works end-to-end on Joshua's test boxes
2. **Phase 2 ship** as Suavo PR independently — `/download` page works against today's release artifacts; can land before Phase 1 completes
3. **Phase 3 ship** as v3.16.0-rc1 — device-code auth available alongside `setup.json`; new installs prefer device-code, existing installs unchanged
4. **Phase 4 ship** as v3.17.0-rc1 — model auto-download replaces operator-placed model; existing operator-placed models continue to work (verification path is the same)
5. **Phase 6 ship FIRST** as v3.18.0-rc1 — `--doctor` diagnostic available; first step in every support ticket from now on, before auto-update can break anything silently
6. **Phase 7 (partial — broken-state tray surfacing) ship** as v3.18.1-rc1 — tray icon with About + Show logs + "Agent is broken, click for --doctor output" — minimum surface to make a broken auto-update visible
7. **Phase 5 ship** as v3.19.0-rc1 — first agent with auto-update; the LAST manual upgrade required is the upgrade to this version; now safely landable because both --doctor and tray-broken-state-surfacing are already deployed in the pilot pharmacies
8. **Phase 7 (remaining polish) ship** as v3.20.0 or v4.0.0 — full menu items, notifications, About-box-with-cert-DN, Reauth-workstation flow, etc.

The v4.0.0 cut = Phase 7 complete (first agent that fully auto-installs, auto-updates, and has the complete native UX). Tentatively shipped Wk 8 of the timeline.

---

## 9. Open questions (for Codex review per `[[feedback-codex-review-every-spec]]`)

1. **Avalonia.Native on Mac maturity** — Avalonia 11 Mac support is officially production-ready but tray-icon support and Keychain integration are less battle-tested. Risk of needing a thin Swift shim. Codex: research current state of Avalonia + Mac tray icons + native menu bar at production-grade.
2. **Model hosting cost** — Cloudflare R2 egress is free up to 10 TB/mo. At 4-5 GB per first-time install × scale of 100 pharmacies × multiple workstations per pharmacy = bounded but worth budgeting. Codex: estimate steady-state cost vs. alternatives (HuggingFace, S3, self-hosted).
3. **HIPAA-grade audit on device-code approvals** — every approve action by an admin should be auditable, immutable, retrievable. Existing `audit_log` table handles this if we wire it correctly. Codex: review whether the device-code approval flow creates the right audit_log entries with the right metadata.
4. **Mac `LaunchAgent` vs `LaunchDaemon`** — *Resolved by Codex 2026-05-28:* `SuavoAgent.Core` runs as a **LaunchDaemon** (machine-scoped, root, survives operator logout — required for heartbeat, revoke enforcement, policy application, update integrity across pharmacist shift changes). `SuavoAgent.Helper` stays a **LaunchAgent** (per-user-session, needs the active session graphics context for screen capture + UI actuation). Broker IPC bridges the two.
5. **Re-auth flow** — what happens when an admin revokes a workstation? Agent should detect (next heartbeat fails with 401), prompt operator with "This workstation has been unbound — please reauth", trigger device-code flow. Codex: stress-test the failure modes (admin revokes mid-shift; agent has un-synced state; what happens to PHI in the work-in-progress queue?).
6. **MSI uninstall PHI handling** — when pharmacy uninstalls SuavoAgent, what happens to local `state.db` + delivery receipts + audit logs? Default: leave them in place (operator must explicitly delete), with a `msiexec /x SuavoAgent.msi PURGE_DATA=true` option for clean removal. Codex: HIPAA-compliant uninstall pattern review.
7. **macOS notarization race** — Apple's notarization service occasionally takes 30+ minutes during peak hours, occasionally fails with "no such record" requiring retry. Codex: research current best practice for CI retry + fallback.

---

## 10. Success criteria

v4 is "done" when ALL of:

- [ ] 3 consecutive pilot installs across 3 different pharmacies survive 7+ days with no remote intervention (Track 1 readiness gate)
- [ ] Mac install: Joshua's MacBook gets a clean install via `/download` → DMG → device-code → bound workstation in under 10 minutes including local model download
- [ ] Windows install: Nadim's workstation gets the same flow in under 15 minutes
- [ ] Auto-update: one full version bump applied silently overnight on a real pilot pharmacy with no operator notification beyond a tray-icon "Updated to v4.1.0" pop-up
- [ ] `--doctor` runs successfully on all 3 pilots, passes all 12 checks
- [ ] Combined manifest is ECDsa-signed and verified by 3 different test environments before any update applies
- [ ] No PHI in any GitHub Actions log, Sentry payload, or telemetry record across any v4 install (verified by spot-check of 10 random installs)

---

## 11. Estimated timeline (v2 post-Codex review)

```
Wk 1-2  : Phase 1 (cross-platform packaging)
Wk 2    : Phase 2 (download page rewrite) — parallel with Phase 1
Wk 3-4  : Phase 3 (device-code auth onboarding)
Wk 5    : Phase 4 (model auto-download — Qwen3-4B + Llama-3.2-1B + Llama-3.1-8B)
Wk 5.5  : Phase 6 (--doctor diagnostic) — ships BEFORE Phase 5 per Codex reorder
Wk 6    : Phase 7-partial (tray icon broken-state surfacing only) — minimum surface to make a bad auto-update visible
Wk 6-7  : Phase 5 (channel-pinned auto-update) — now safe because --doctor + tray broken-state are deployed first
Wk 7-8  : Phase 7-remaining (full menu polish, About box, notifications, Reauth-workstation flow)
Wk 8-10 : Pilot field testing — 3 pharmacies, 7+ day soak each
```

Total: 8-10 weeks of focused solo work. Apple Developer Program org enrollment is already complete — no vendor purgatory blocking Phase 1.

---

## 12. Next steps

1. **Codex review** of this spec (see task #20). Focus on §9 open questions.
2. Address Codex findings, freeze spec at v1.
3. Start Phase 1.
4. After Phase 1 lands, parallelize Phase 2 with start of Phase 3.

## 13. PHI residue lifecycle (Codex 2026-05-28 — biggest gap closed)

**Codex flagged this as the biggest hole I underspecified.** On revoke / unbind / uninstall, the spec did not define what happens to:
- Queued PHI in the sync buffer
- DPAPI-encrypted delivery receipts on disk (retained 2555 days by default)
- `state.db` SQLite file
- Encrypted screen-capture frame cache (vision pipeline scratch)
- Tier-2 LLM context / KV cache spillover
- Crash dumps from `Wire.AttachUnhandledHooks` (potentially PHI-bearing under bug conditions)
- Backups, swap files, hibernation images

### Three-state lifecycle

| State | Trigger | Local data state | Auth state |
|---|---|---|---|
| **bound** (steady-state) | Initial device-code approval | Full PHI access; encrypted at rest | Active auth key + TPM-wrapped |
| **revoked-sealed** | Admin clicks "Revoke" in dashboard | Capture + sync STOP; PHI stays encrypted at rest, no new writes; receipts retained per `ReceiptRetentionDays` | Auth key invalidated server-side; local wrapping key intact for receipt access |
| **revoked-purged** | Admin clicks "Revoke & purge" (separate, deliberate action) | All PHI deleted: state.db, receipt files, scratch caches, crash dumps; local TPM wrapping key destroyed; non-PHI audit proof preserved (install history, version, signed manifest digests) | Auth key invalidated; agent goes back to unbound state, requires device-code reauth to rebind |

### Implementation invariants

1. **Auth-key revoke is decoupled from PHI purge.** Server-side revoke of the auth-key is instant (next heartbeat 401s and agent stops capture). PHI purge is a deliberate second action — accidental clicks don't destroy 7-year retention records.
2. **State transitions are auditable.** Every transition (bound→revoked-sealed, revoked-sealed→revoked-purged, any→bound via reauth) writes an immutable entry to `audit_log` with the admin's identity + timestamp + reason.
3. **`--doctor` reports residue status.** New check #13: PHI residue report — counts of files / bytes still under each retention category, time since last cleanup, last revoke event. Surfaces "you have 47 receipts from a revoked workstation that should have been purged 30 days ago."
4. **`uninstall` is `revoked-sealed` by default; `msiexec /x SuavoAgent.msi PURGE_DATA=true` (Win) and `SuavoAgent.app --purge-on-uninstall` (Mac) opt into `revoked-purged`.**
5. **Crash dumps NEVER contain PHI by invariant.** `Wire.AttachUnhandledHooks` already scrubs; v4 adds an integration test that fuzzes crash paths and asserts no PHI substrings reach disk.
6. **Receipt files outlive workstation auth.** A pharmacy that revokes a workstation but keeps the receipts for an audit doesn't lose them. The receipt store is keyed by `pharmacy_id`, not by `workstation_id`.

### Dashboard UI surface

`/admin/agents/workstations/[id]` gains two buttons:
- **"Revoke (keep receipts)"** — sealed state; explainer text "Agent stops capturing immediately. Existing delivery receipts and audit logs stay on the workstation for compliance retention. You can fully purge them later."
- **"Revoke & purge"** — purged state; confirm dialog "This permanently deletes all delivery receipts (47 files, 2.3 GB), the agent's local database, and all encrypted caches on this workstation. This cannot be undone. Audit logs are preserved. Type the workstation name to confirm."

### Spec changes referencing this section

- §5 Phase 3 (device-code auth) — bound state transition diagram
- §5 Phase 6 (`--doctor`) — added check #13 (PHI residue report)
- §7 (security model) — TPM wrapping key destruction on purge
- §8 (rollout) — backward-compat for v3.x receipts during transition (treat as sealed by default)

## 14. Brand canonicalization (decided 2026-05-28)

Single canonical hierarchy across v4 surfaces:

- **MKM Technologies LLC** — holding company; visible only on legal docs, code-signing cert subjects, contracts. Customer-invisible.
- **Suavo** — product brand; pharmacy delivery + ops OS. Customer-facing.
- **SuavoAgent** — the desktop client; downloads from `suavollc.com/download`. The thing pharmacies install.

The Setup GUI footer stays *"Suavo LLC · HIPAA-compliant pharmacy intelligence"* — Suavo LLC is the legal d/b/a, customer-visible.
