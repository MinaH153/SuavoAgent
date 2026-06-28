# Spec: Canonical installer path — token-in-filename signed exe (2026-06-28)

## Goal
ONE signed `SuavoSetup.exe` everywhere. The authenticated dashboard download
serves it **renamed** `SuavoSetup-<token>.exe`; on launch the exe reads its own
filename, extracts `<token>`, exchanges it via the existing
`POST /api/agent/register` → zero-touch auto-pair. No/invalid/renamed token →
**graceful fallback to the existing device-code pairing** (unchanged). Retire
the `.bat`/`.cmd` from user-facing paths. Net: signed **and** zero-touch, single
file, no re-signing (renaming preserves the Authenticode signature + hash + the
SmartScreen reputation we're building).

## Cloud (Suavo) — 3 changes

### 1. NEW `GET /api/agent/download`
- Auth + pharmacy resolution (impersonation-aware) + `suavoagent` subscription
  gate — **reuse the exact gates from `/api/agent/installer-file/route.ts`**.
- Mint a single-use token — reuse the same logic: burn prior unused tokens for
  the pharmacy, insert `agent_install_tokens` `{pharmacy_id, token:"sai_"+48hex,
  tier, expires_at:+24h, created_by, source:'pharmacy_self_service'}`.
- Resolve the latest signed Windows installer via `fetchDownloadManifest()`
  (`platforms.windows.file_url` = GitHub release asset; `file_size_bytes`).
- **Delivery = Supabase Storage signed-URL (NOT a Vercel byte-proxy).** Codex
  flagged Vercel streaming-function constraints (duration/response/cost) for a
  44.5 MB asset, so we do NOT pipe the exe through the route. Instead:
  - Lazy-mirror: ensure `agent-installers/SuavoSetup-<version>.exe` exists in a
    private Supabase Storage bucket; if absent, fetch once from the GitHub
    release and upload (the only time bytes pass through the function — once per
    version, not per download). The mirrored bytes are identical → signature +
    hash preserved.
  - Return `createSignedUrl(path, ttl, { download: "SuavoSetup-<token>.exe" })`
    — Supabase sets `Content-Disposition: attachment; filename="…"` on the
    signed URL — and **302-redirect** the browser to it (or return JSON for the
    client to navigate).
  - GitHub/Storage failure → 502 + friendly message. Short signed-URL TTL.
  - (Clean long-term: mirror as a release-pipeline step instead of lazy; out of
    scope this pass.)

### 2. Rewire the two authenticated download UIs onto it
- Onboarding `step-download-agent.tsx` `handleDownload`: `/api/agent/installer`
  (.bat) → navigate to `/api/agent/download`. Update copy: "download the signed
  installer and double-click it" (not "right-click → Run as Administrator .bat");
  keep the SmartScreen expectation note already shipped on the download page.
- Cockpit reinstall card (`useInstallerDownload` hook → currently
  `/api/agent/installer-file` .cmd): point at `/api/agent/download`.
- Public `/suavoagent/download` (anonymous): UNCHANGED — plain `SuavoSetup.exe`,
  device-code pairing (the no-token fallback).

### 3. Deprecate (don't delete)
`/api/agent/installer` (.bat) + `/api/agent/installer-file` (.cmd): no longer
referenced by UI; mark deprecated, remove in a later pass.

## Agent (SuavoAgent .NET) — 1 change

### `SetupConfig.Load(args)` — insert a filename-token step
Resolution order becomes: `setup.json` (existing) → **filename token (NEW)** →
CLI args (existing) → null (→ device-code screen, existing).
- **`Load()` only DETECTS, never does I/O.** Codex: `Gui/App.axaml.cs` calls
  `SetupConfig.Load(args)` **synchronously before the window shows** — so the
  `/register` HTTPS call must NOT live in `Load()` or it freezes the installer.
  `Load()` cheaply reads `Environment.ProcessPath` → filename and, if it matches
  `SuavoSetup-<token>.exe` (token starts `sai_`, len ≥ 12), returns a *pending-
  token* marker. **Tolerate the browser dedup suffix** — strip a trailing
  ` (N)` so `SuavoSetup-<token> (1).exe` still parses.
- **The exchange is an async GUI step.** When the GUI sees a pending token it
  shows a new **"Connecting to your pharmacy…"** view that, off the UI thread
  (timebox ~30 s), does: `POST {CloudUrl}/api/agent/register`
  `{licenseKey:"0000000000", installToken:<token>, machineName,
  machineFingerprint:GetMachineFingerprint(), agentVersion:<own version>}` —
  **mirror `bootstrap.ps1`'s register call exactly** (lines ~1459-1532).
- Success: build a `SetupConfig` from the response `{apiKey, agentId,
  pharmacyId}` + defaults (`CloudUrl`=prod default, `ReleaseTag`=own version,
  `LearningMode`=false). Write `initialOverrides` → `config-overrides.json`
  (mirror bootstrap). Return config → GUI runs the NORMAL configured install
  (Welcome → SystemCheck → Consent → Destination → Progress).
- ANY failure (no token / expired / 4xx / network / timeout) → return null →
  existing device-code pairing. **Graceful, never hang, never brick the
  installer on a network blip.**

## Security / risk
- Token in filename = single-use + 24 h TTL + pharmacy-scoped + rate-limited
  (10/60 s/IP) + audited at `/register`. **Correction (Codex):** the token is
  NOT machine-fingerprint-bound — `agent_install_tokens` has no fingerprint
  column; `/register` upserts `agent_instances` on the *self-asserted*
  `(pharmacy_id, machine_fingerprint)`. So whoever holds the token can register
  a workstation to that pharmacy. This is the **same** exposure as today's
  `.bat`, which bakes the identical token into a plaintext script — token-in-
  filename is not worse. Mitigations: single-use, short TTL, rate-limit, audit
  log; a stolen token at most adds one extra workstation that shows in the
  dashboard (supersede + revoke available). Not a regression; flag for a later
  hardening pass (bind the token to first-fingerprint-seen) if desired.
- The exe makes ONE timeboxed HTTPS call during Load; fully graceful fallback is
  the load-bearing safety property — verify it falls to device-code on every
  failure mode.
- No DB schema change. No new secrets. No PHI in filename/token (opaque token).

## Test plan
- Cloud: unit-test the download route (auth gate, subscription gate, token mint,
  Content-Disposition filename, stream headers, 502 on manifest miss).
- Agent: unit-test the filename parser (valid/invalid/renamed/no-token) and the
  exchange→SetupConfig mapping + the fallback-returns-null paths (mock HTTP).
- E2E (on-box, owed): download from dashboard → `SuavoSetup-<token>.exe` →
  double-click → auto-pairs with no code; rename to `SuavoSetup.exe` →
  device-code screen.

## Out of scope (this pass)
Supabase-Storage signed-URL optimization; deleting the deprecated `.bat`/`.cmd`
routes; the anonymous public download page (stays device-code).
