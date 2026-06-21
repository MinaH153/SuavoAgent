# "Suavo Doctor" Self-Diagnostic — Design

**Date:** 2026-06-20
**Origin:** Nadim/DIB install. Every real cause (missing VC++ Redist → dead brain; LocalService → SQL `ANONYMOUS LOGON`; UiaFirst defaulted off) was invisible from the cloud and took a manual on-box Claude Code session to find. We shipped ~5 blind releases before that one diagnostic. This must be a built-in, repeatable capability — not a manual AI session each time.

**Goal:** One command (and one dashboard panel) that traces every layer of the agent end-to-end and reports the **exact failing layer + remediation**, reading no PHI. It is both an operator self-service tool and the first thing support runs.

**Tech stack:** new `suavo doctor` entrypoint in Core (or a `SuavoAgent.Doctor` CLI sharing Core's DI), reusing `DiagnosticsSnapshotBuilder`, the IPC ping path, the local-inference probe, and the cloud heartbeat client. Output: human table + `doctor-report.json`.

## Global constraints
- HIPAA: reads only service state, dependency presence/versions, connectivity booleans, config *keys* (never values for secrets/credentials), and effective config. No prescription/patient data; reuse `OutboundPhiGuard` redaction for anything emitted.
- Stealth: never provisions or prompts for vendor-visible access. PMS reachability is checked via the active modality (UiaFirst = UI attach; SqlFirst = passive probe with already-configured creds).
- Must run on a locked-down box as the Core service account and degrade gracefully (a layer it can't inspect reports `unknown`, never crashes the run).

## The layer trace (ordered; each → ok | warn | fail with remediation)
1. **Version & identity** — running Core version, agentId, pharmacyId, machine fingerprint; is it the expected version?
2. **Runtime deps** — VC++ 2015-2022 x64 (`vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll`), .NET runtime, native brain libs at NativeLibraryPath, CPU AVX vs deployed variant. *(This is the layer that bit Nadim.)*
3. **Services** — Core/Helper/Broker/Watchdog state + LogOn account (flag LocalService when SqlFirst is active → predicts the remote-Windows-Auth `ANONYMOUS LOGON`).
4. **Core↔Helper pipe** — ping round-trip; report stranded/flapping.
5. **Brain (Tier-2 local inference)** — model file present + SHA verified; trigger a one-token inference; report `NativeApi`/`TypeInitializationException` explicitly with "install VC++ Redist" remediation; report effective `provisioning_state`/model load time.
6. **Cloud** — heartbeat 200 vs 401 (auth) vs 503 (ruleset, non-blocking); config-sync health; effective overrides applied (keys + non-secret values).
7. **Pricing modality** — effective `PricingExecutor` (UiaFirst/SqlFirst). UiaFirst: can it see PioneerRx? (warn if PioneerRx closed). SqlFirst: passive TCP+TLS+auth probe result (cert trust, login outcome) — and remind that asking for a SQL login breaks stealth; prefer UiaFirst.
8. **Effective config sanity** — surface the keys most likely to be misconfigured (PricingExecutor, SqlTrustServerCertificate, RelaxIpcClientPathValidation must be false, ReceiptOnlyMode, LearningMode).

## Output
- **Terminal:** a table — `LAYER | STATUS | DETAIL | FIX` — with the first failing layer highlighted. A one-line verdict ("Brain down: VC++ Redist missing — run `winget install Microsoft.VCRedist.2015+.x64` and restart Core").
- **`doctor-report.json`** next to the logs for support upload.
- **Dashboard panel** ("Run diagnostics") that triggers the same trace via a signed command and renders the report — so an operator (or you, remotely) gets ground truth without an on-box shell. This directly fixes "dashboard said healthy while the box was broken."

## Modes
- `suavo doctor` — full trace, human output.
- `suavo doctor --json` — machine output for support tooling / CI on a test box.
- `suavo doctor --layer brain` — single layer for fast iteration.
- Dashboard "Run diagnostics" button — remote trigger, PHI-safe report.

## Why this is also a moat
Self-diagnosis + remote diagnostics is a support cost-killer and a trust signal at the exact moment a pharmacy is deciding whether to keep the agent. It is the productized form of the on-box Claude session that saved this install.

## Out of scope
- Auto-remediation beyond what preflight already does (doctor *reports*; the installer *fixes*). A future `suavo doctor --fix` can call the same remediations, gated.

## Testable deliverables
1. Each layer probe unit-tested with mocked inputs (dep present/absent, pipe up/stranded, brain load ok/`NativeApi` throw, heartbeat 200/401/503, modality UiaFirst/SqlFirst).
2. `suavo doctor` on a box with the brain forced-broken prints brain=FAIL with the VC++ remediation as the verdict.
3. `doctor-report.json` schema stable + PHI-guarded (no secret values, no patient data).
4. Dashboard "Run diagnostics" round-trips a signed command and renders the report; never shows healthy when a layer failed.
