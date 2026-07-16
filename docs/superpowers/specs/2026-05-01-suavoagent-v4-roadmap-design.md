# SuavoAgent v4 Roadmap — Meta Design

> The canonical near-term roadmap for SuavoAgent. Reconciles the May 1 2026 six-track product vision with the existing 9-phase self-healing substrate plan, the 22-subproject reliability roadmap, and Mission pillar 4. Gate-driven, performance-paced, no calendar.

**Locked date:** 2026-05-01
**Status:** v0.1 draft (locks to v1.0 after first wave gate trip)
**Owner:** Joshua Henein (founder)
**Depends on:**
- `docs/self-healing/invariants.md`
- `docs/self-healing/audit-schema.md`
- `docs/self-healing/event-registry.md`
- `docs/self-healing/field-registry.md`
- `docs/self-healing/action-grammar-v1.md`

---

## 0 · Why this doc exists

SuavoAgent has accumulated three overlapping plans over the last six weeks:

1. The **9-phase self-healing substrate plan** (`suavoagent-self-healing-9-phase-plan` memory, locked 2026-04-21). 18–24 months. Phase 0 → I. Mission Loop architecture rides on top.
2. The **22-subproject reliability roadmap** (`suavoagent-95pct-roadmap-22-subprojects` memory, 2026-04-22). Catastrophe-first ordering. ~5–7 months solo to 5 paying pharmacies.
3. The **May 1 six-track product vision** (`suavoagent-product-vision-2026-05-01` memory). HIPAA-first computer-use agent for pharmacy workstations — "Chrome Remote Desktop with a brain." Six product surfaces.

Plus the **Mission** (`MISSION-square-level-ecosystem`) which says SuavoAgent is pillar 4 of 6 and gets greenfield bandwidth only after pillars 1–3 are alive.

This roadmap **reconciles those four documents into a single executable plan** organized as a wave queue with performance gates. It does not invent new architecture; it sequences existing work and identifies the gaps.

---

## 1 · Mission alignment

This roadmap serves Mission pillar 4 ("SuavoAgent — the invisible bridge between pharmacy and fleet operator"). It does **not** displace pillars 1–3 (pharmacy dashboard, fleet operator dashboard, iOS app), which retain priority for new-feature bandwidth. SuavoAgent gets reliability work + Track 5 cursor v0 in parallel, and unlocks greenfield bandwidth on Tracks 2/4/5/6 only when the master gate trips (see §6).

When this roadmap conflicts with Mission, Mission wins.

---

## 2 · The six tracks (May 1 vision, canonical)

Tracks are persistent capability lanes. They do not "complete" — they mature over waves.

| # | Track | One-line definition |
|---|---|---|
| 1 | **Reliable install + self-healing** | One-paste install, watchdog, crash detection, config-sync health, remote repair, version drift detection, "heartbeating-but-unhealthy" alerts |
| 2 | **PioneerRx extraction** | Reverse-engineered SQL/UIA observation produces canonical `RxOrderCandidate` with provenance + confidence + evidence_id; no plain Rx numbers in cloud keys |
| 3 | **HIPAA-safe sync** | Minimum-necessary PHI through HMAC-authenticated agent sync path; zero PHI in telemetry, model prompts, logs, command payloads, or provenance metadata |
| 4 | **Pharmacy dashboard integration** | Multi-PC, per-machine health, pause/repair/remove, extraction confidence, missing-address flags, inbox correction, promote-to-delivery handoff, non-PHI fleet views |
| 5 | **Agentic remote control** | Observe/propose/show-cursor (visual-only, click-through, no movement/typing/labels). Approved narrow verbs later via signed commands + MFA/owner gates + audit + typed workflows |
| 6 | **Real-world production readiness** | Windows smoke install, signed release verification, PioneerRx shadow pilot, cloud health alerts, post-deploy smoke + migration checks, rollback paths |

**Tagline:** "Chrome Remote Desktop with a brain — HIPAA-first, pharmacy-aware, invisible to workflow, self-healing, auditable, controlled."

---

## 3 · Inventory snapshot (as of 2026-05-01)

What's already shipped per track. Future sessions read this to skip re-derivation.

### Track 1 — Reliable install + self-healing
**Shipped:**
- `bootstrap.ps1` hardened, +588 LOC in latest tranche (commit `6cd1250`)
- `src/SuavoAgent.Core/Health/RuntimeHealthEvidence.cs` (314 LOC)
- `src/SuavoAgent.Core/Cloud/AgentCredentialRecoveryClient.cs` — retired public-recovery compatibility boundary; now fails closed with `device_repair_required`
- `src/SuavoAgent.Core/Cloud/ConfigOverrideStore.cs` — cloud-driven config repair
- `scripts/Test-SuavoAgentReleaseProbe.ps1` (526 LOC)
- `scripts/vm-validate.ps1`
- `.github/workflows/{ci,hotfix,release}.yml` upgraded
- `docs/hardening/2026-04-29-agent-hardening-tranche.md` + `release-gate.md`

**In-flight (open PRs):**
- PR #36 PIAG-1 agent runner
- PR #37 PIAG-1 bootstrap `-RunPiag` switch (stacked on #36)

**Gap:**
- Dashboard "heartbeating-but-unhealthy" surface
- Version drift detection (N-1/N-2/N-3 surfaced on dashboard)
- Self-healing reinstall path (signed `repair_install` command end-to-end)
- Yubikey EV-signed installer (cert pending — SSL.com order `co-861kueeu2a3`)

### Track 2 — PioneerRx extraction
**Shipped:**
- `src/SuavoAgent.Contracts/Models/RxOrderCandidate.cs` — canonical type exists
- Spec B/C/D infrastructure (April 2026): UIA observer, SQL adapter, RoutineDetector, ActionCorrelator, SqlTokenizer, SchemaCanary
- `docs/superpowers/specs/2026-04-19-workflow-template-autonomous-intelligence.md` — Workflow Template Extractor design

**In-flight:**
- PR #35 UIA3 with UIA2 fallback feature flag

**Gap:**
- End-to-end emit of `RxOrderCandidate` from extractor → cloud sync → dashboard with confidence + evidence_id + missing-address flagging
- Multi-PC extraction correlation (same Rx seen on multiple agent installs at one pharmacy)

### Track 3 — HIPAA-safe sync
**Shipped:**
- `src/SuavoAgent.Core/Cloud/CloudErrorSanitizer.cs`
- `PhiScrubber` (multi-pass with address patterns, insurance-id pattern, Codex CRITICAL #5 closed in PR #38)
- Append-only `audit_events` table with immutable triggers + RFC 8785 canonical JSON hash chain
- `docs/self-healing/field-registry.md` — 5-tier classification (Public / Operational / PHI-Adjacent / PHI-Direct / Secret)
- `docs/self-healing/redaction-rulesets/v1.0.0.yaml`
- Cloud-side `sanitizeSnapshotData` filter

**In-flight:**
- PR #33 Codex MEDIUM (audit-archive PHI scrub via `PhiScrubber.ScrubText` on export + helper consecutive-failure counter)
- PR #34 last CRITICAL (drop PHI from `SerializeRxBatch` + chained-audit row on every sync)

**Gap:**
- **Compile-time PHI typeguards** — today PHI exclusion is runtime-enforced via `PhiScrubber`. Goal: any `PHI-Direct` field from `field-registry.md` appearing in an outbound schema = build-fails. CI gate.
- Synthetic-PHI-leak test PR fixture (canary that proves the gate works)

### Track 4 — Pharmacy dashboard integration
Lives in the **Suavo repo** (`~/Code/Suavo`), not SuavoAgent. Cross-repo coordination required.

**Shipped (Suavo side):**
- State card + cockpit + agent console hero on `/pharmacy/*`
- Install activity telemetry + Agent Console
- Multi-agent FleetPanel (per 2026-04-25 onsite sweep)

**Gap:**
- Multi-PC support on a single pharmacy (each PC its own state card)
- Per-machine pause/repair/remove commands (wired to Track 5 verb dispatch when ready)
- Inbox correction UI for missing-address `RxOrderCandidate`s
- Promote-to-delivery handoff flow
- Non-PHI fleet views (operational visibility for fleet operators without PHI access)
- Heartbeating-but-unhealthy badge

### Track 5 — Agentic remote control
**Shipped:**
- `src/SuavoAgent.Contracts/Ipc/IntentCursorContracts.cs` — cursor IPC contract
- `src/SuavoAgent.Core/Ipc/IntentCursorClient.cs` — Core-side client (78 LOC, just landed in `6cd1250`)
- `src/SuavoAgent.Contracts/Ipc/IpcPeerAttestationStore.cs`
- `src/SuavoAgent.Core/Ipc/IpcPeerVerifier.cs`
- `docs/self-healing/action-grammar-v1.md` — full Phase D Action Grammar v0.1 spec (`IVerb` interface, `VerbMetadata`, schema versioning, risk tiers, blast radius, BAA scope, rollback envelopes, 5 universal verbs spec, canary rollout, CI enforcement)

**Gap:**
- Helper-side cursor overlay window (visible, click-through, no movement, no typing, no labels) — the visible-cursor UI for Track 5 stage-0
- Operator dashboard panel showing proposed actions + preview + "observe-only, no execution path" badge
- New audit event `cursor.proposed` registered in `event-registry.md`
- `src/SuavoAgent.Verbs/` C# project (Phase D deliverable from `action-grammar-v1.md`)
- Concrete `RestartServiceVerb : IVerb` implementation (Track 5 stage-1)
- Cloud-side signed command dispatch (HMAC + per-pharmacy key + fence ID)
- Operator MFA gate
- AWS Cedar policy engine wired for per-pharmacy verb allowlist

### Track 6 — Real-world production readiness
**Shipped:**
- `docs/hardening/release-gate.md`
- `scripts/Test-SuavoAgentReleaseProbe.ps1` (526 LOC release probe)
- CI workflow upgrades (`ci.yml`, `hotfix.yml`, `release.yml`)
- `docs/self-healing/phase-a-architecture.md`

**In-flight:**
- PR #36 + PR #37 PIAG-1 stack (paired with Suavo cloud PR `SuavoLLC/MKM#192`)

**Gap:**
- Yubikey FIPS hardware token EV cert delivery (SSL.com order pending) + signtool integration
- Signed bootstrap.ps1 + signed `.cmd` + signed binaries
- Shadow extraction pilot at second pharmacy
- Trip A reinstall completion at Nadim with v3.13.13 + post-Trip A hardening
- Post-deploy smoke probe running every 6h on every pharmacy with dashboard reporting

---

## 4 · Wave queue

Wave structure: **parallel coordinated pushes**, gate-driven, no calendar. Each wave touches multiple tracks. Waves can overlap when blocked on external evidence (e.g., Wave 3 prep starts while Wave 2's pilot install soak runs). Master gate (§6) sits above all waves.

### Wave 0 — Baseline reset

**Goal:** Clean slate before new work. Triage 5 open PRs + commit this meta-roadmap.
**Tracks touched:** 1, 2, 3, 6

**Deliverables:**
- This document committed to `main`
- `roadmap_gates` operational table migration drafted (Postgres, append-only, RLS-isolated per-pharmacy where pilot evidence applies)
- New audit event types registered in `docs/self-healing/event-registry.md`: `wave.gate_tripped`, `wave.gate_failed` (see §8)
- PR #33 (Codex MEDIUM — audit-archive PHI scrub + helper failure counter) — **merge**
- PR #34 (drop PHI from `SerializeRxBatch` + chained audit on every sync — last CRITICAL) — **merge**
- PR #36 + PR #37 PIAG-1 stack — **synchronize with paired Suavo PR `SuavoLLC/MKM#192`**, merge as a unit
- PR #35 (UIA3 fallback) — **decision documented**: merge now / hold for pilot evidence / close. Default recommendation: hold for Wave 4 pilot evidence.

**Gate:**
- Mechanical: `gh pr list --repo MinaH153/SuavoAgent --state open` returns no stale PRs (all 5 either merged or held with rationale comment); meta-roadmap doc on `main`; `roadmap_gates` migration committed (not yet applied — applies in Wave 1).
- Joshua-certified: meta-roadmap reads true.

### Wave 1 — Invariants compile-enforced + health surface

**Goal:** PHI guard moves runtime → compile-time. Dashboard distinguishes "heartbeating" from "actually healthy."
**Tracks touched:** 1, 3, 4

**Deliverables:**
- **CI gate (Track 3 keystone):** build fails if any `PHI-Direct` field from `field-registry.md` appears in any outbound schema (events, verbs, sync payloads, heartbeat, model prompts). TypeScript-equivalent validator on cloud side. Runs in `ci.yml` on every PR.
- Synthetic PHI-leak canary fixture in CI (a hidden PR template that intentionally leaks; gate must reject it; live test of the gate)
- "Heartbeating-but-unhealthy" composite signal: Helper attached AND IPC connected AND schema canary green AND extraction success in last N minutes. New `agent.health_composite` event type.
- Dashboard surface in `/pharmacy/agent` (Suavo repo) for the new health signal — three states: healthy / heartbeating-but-unhealthy / silent
- `roadmap_gates` migration applied to prod (Supabase project `zsufzmxkccznvolrlkzy`)

**Gate:**
- Mechanical: synthetic PHI-leak PR fails CI build (proves gate works)
- Pilot-evidence: dashboard correctly shows unhealthy state on Joshua's test box when Helper is force-disconnected
- Audit: `wave.gate_tripped` row in `roadmap_gates` with `wave_id="W1"`, `certified_by="ci+joshua"`

### Wave 2 — Cursor v0 (visual-only)

**Goal:** First piece of Track 5 — visible cursor + propose, no execution path exists yet.
**Tracks touched:** 5, 4, 3 (audit)

**Deliverables:**
- **Helper-side overlay window:** transparent topmost window with cursor sprite at proposed click location. Click-through (`WS_EX_TRANSPARENT`), no movement (sprite is static at proposal coordinates), no typing capture, no labels rendered. PHI-safe by construction (overlay reads no PMS data).
- Wire existing `IntentCursorClient.cs` plumbing to cloud → Helper proposal channel
- Operator dashboard panel (Suavo repo): renders proposed action + "what would happen if executed" preview + explicit "observe-only — no execution path exists in this build" badge with link to Track 5 stage-1 timeline
- New audit event `cursor.proposed` registered in `event-registry.md`. Payload: `{proposal_id, screen_hash, target_element_hash, predicted_action_type, confidence, evidence_id}`. Zero PHI in payload (target_element described by structural hash, not text).
- Smoke test script: invokes a synthetic proposal at Joshua's test box, verifies overlay renders + clicks pass through + audit chain has the event

**Gate:**
- Mechanical: smoke test passes on Joshua's test box
- Pilot-evidence: cursor overlay renders correctly on real PioneerRx screen (full-screen + multi-monitor)
- Joshua-certified: cursor feels right ("Tesla test" — premium, intentional, not a generic SaaS overlay)

### Wave 3 — Reliability sweep + first pilot install attempt

**Goal:** Bootstrap one-paste hardened against every known bug class. Self-healing reinstall path live. Fresh install at Nadim.
**Tracks touched:** 1, 4

**Deliverables:**
- Bootstrap **audited against** all 6 Windows-install gotchas (`feedback-windows-install-lessons.md`); any uncovered gap closed:
  1. ExecutionPolicy `Bypass -Scope Process -Force` or cmd.exe + curl path
  2. Pure ASCII (no Unicode em-dashes / box-drawing)
  3. `Invoke-WebRequest -OutFile -UseBasicParsing` instead of `irm -OutFile` on PS 5.1
  4. Explicit `icacls /grant "NT AUTHORITY\LocalService:(OI)(CI)M"` on `C:\ProgramData\SuavoAgent\` and `C:\Program Files\Suavo\Agent\`
  5. `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>` in all `.csproj`
  6. Stop services + taskkill + wait before binary download
- Bootstrap **audited against** the 4 Better Life bug classes (Trip A 2026-04-25); any uncovered gap closed:
  - Encoding/BOM
  - `$legacyDir:` drive-ref bug
  - NPI placeholder substitution
  - Helper orphan cleanup
- Audit summary committed as `docs/hardening/2026-MM-DD-bootstrap-coverage-audit.md` listing each gotcha + bug class as covered/closed-in-this-wave
- Version drift detection: cloud detects N-1/N-2/N-3 pharmacies, dashboard surfaces them per agent install
- Self-healing reinstall path: cloud signs `repair_install` command (signed envelope per existing pattern) → agent stops services → re-runs bootstrap → restarts services → re-emits attestation. Audit chain captures every step.
- Fresh install attempt at Nadim with v3.13.13+ (Trip A reinstall completion)

**Gate:**
- Pilot-evidence: pilot install survives 7 consecutive days at Nadim with no manual rescue, no remote intervention, dashboard shows continuous green health composite
- Audit: `wave.gate_tripped` row with `certified_by="pilot:<nadim_id_hash>"`
- **Master-gate counter ticks: 1 of 3**

### Wave 4 — Extraction E2E + dashboard depth

**Goal:** `RxOrderCandidate` flows cleanly from PioneerRx → cloud → dashboard with confidence + evidence + missing-address flagging. Multi-PC dashboard support.
**Tracks touched:** 2, 4, 3

**Deliverables:**
- Extractor emits full `RxOrderCandidate`:
  - `rx_hash` (per-pharmacy salted SHA-256 of Rx number)
  - `medication` (Operational tier — drug name without patient context)
  - `ndc` (NDC code)
  - `quantity`
  - `delivery_address` (PHI-Direct, traverses HMAC sync path only — never telemetry)
  - `provenance` (`sql` / `uia` / `both`)
  - `confidence` (0.0–1.0)
  - `warnings[]` (string array — missing address, ambiguous patient, schema drift, etc.)
  - `evidence_id` (UUID linking to local evidence blob — UIA snapshot or SQL row hash)
- Cloud sync path: receives, validates against field-registry CI gate, stores, indexes
- Dashboard (`/pharmacy/agent` and `/pharmacy/inbox`):
  - Per-Rx confidence chip (color-coded by threshold)
  - Missing-address flag with inbox correction UI (operator types address → emits `address_correction` event → cloud updates → re-syncs to fleet)
  - Source badge (SQL / UIA / both)
  - Evidence drill-down (read-only view of UIA snapshot or SQL row hash, never raw PHI)
  - Promote-to-delivery handoff (one-click → produces fleet `delivery_task`)
- Multi-PC: each pharmacy can have multiple agent installs, dashboard shows per-machine state cards on `/pharmacy/agent/machines/`
- Decision applied: PR #35 UIA3 fallback merged, held, or closed based on Wave 4 pilot evidence

**Gate:**
- Pilot-evidence: live extraction at Nadim (or wherever pilot is running) emits N≥10 `RxOrderCandidate` rows in cloud DB, missing-address flagging fires correctly on synthetic test (intentionally absent address field), promote-to-delivery handoff produces a real fleet `delivery_task`
- Joshua-certified: dashboard feels intentional, not generic. Tesla test on the inbox correction UX.

### Wave 5 — First signed verb execution (Track 5 stage-1)

**Goal:** One universal verb (`restart_service`) executes end-to-end under signed-command + MFA + audit + rollback envelope.
**Tracks touched:** 5, 3, 4, 6

**Deliverables:**
- `src/SuavoAgent.Verbs/` C# project per `action-grammar-v1.md`
- `RestartServiceVerb : IVerb` implementing full lifecycle:
  - `CheckPreconditions` (service registered, currently in non-RUNNING state >5min grace)
  - `CaptureRollback` (no rollback needed — idempotent)
  - `Execute` (sc.exe + watchdog timeout 90s)
  - `VerifyPostconditions` (service reaches RUNNING within 90s)
- Cloud-side signed command dispatch:
  - HMAC-SHA256 with per-pharmacy signing key
  - Fence ID (kill-switch state) per signed envelope
  - Schema version pin (CrowdStrike lesson — fail-closed on mismatch)
- Operator MFA gate on approval (TOTP via existing pharmacy-staff TOTP flow on Suavo)
- AWS Cedar policy engine wired for per-pharmacy verb allowlist
  - Per-(pharmacy, verb) policy in source control + PR-reviewable
  - Default deny for new pharmacies
- Full audit chain entries (already specified in `event-registry.md`):
  - `verb.proposed` → `verb.policy_evaluated` → `verb.approved` → `verb.signed` → `verb.dispatched` → `verb.executed` → `verb.verified`
  - On failure: `verb.failed` → `verb.rolled_back`
- Canary rollout machinery (per `action-grammar-v1.md §Canary`):
  - Dev tier: Joshua's test box, 24h soak, all postconditions pass
  - Pilot tier: Nadim's pharmacy, 48h soak, no rollbacks, no operator complaints
  - Auto-halt on anomaly (error rate > 2× baseline)
- Operator dashboard panel: "Restart Core service" button → Cedar policy check → MFA challenge → cloud signs → agent executes → live audit feed → success/failure with rollback evidence

**Gate:**
- Mechanical: `restart_service` v1.0.0 invocation on Joshua's test box completes successfully (verb.proposed → verb.verified chain present in audit_events table)
- Pilot-evidence: `restart_service` invocation at Nadim's pharmacy completes successfully with all 7 audit events present, MFA challenge satisfied, rollback envelope captured (even though restart is idempotent), postconditions verified
- Joshua-certified: MFA UX is right — fast enough to use, strict enough to be safe

### Wave 6 — Signed installer + second pharmacy shadow

**Goal:** Yubikey EV-signed installer eliminates SmartScreen warning. Second pharmacy onboarded in shadow mode (read-only, no writeback, no automation).
**Tracks touched:** 6, 1, 2

**Deliverables:**
- Yubikey FIPS hardware token receives EV cert (SSL.com order `co-861kueeu2a3` — external dependency)
- signtool integration in `release.yml`:
  - Signed `bootstrap.ps1` (Authenticode signature on the script itself)
  - Signed `.cmd` installer wrapper
  - Signed binaries (`SuavoAgent.Core.exe`, `SuavoAgent.Broker.exe`, `SuavoAgent.Helper.exe`, `SuavoAgent.Watchdog.exe`)
- Verification: `signtool verify /pa /v <file>` returns valid for every shipped artifact
- Second pharmacy install with shadow extraction:
  - Read-only PioneerRx watch (SQL + UIA observation, no writeback verbs available)
  - No automation (no Track 5 verbs invoked at this pharmacy yet)
  - `RxOrderCandidate` flowing to cloud DB for 7+ days
- Post-deploy smoke probe running every 6h on every pharmacy:
  - Probe runs `Test-SuavoAgentReleaseProbe.ps1` against all known agent installs
  - Reports to dashboard `/admin/agent-fleet/health`
  - Alerts Slack/SMS on failure

**Gate:**
- Mechanical: fresh install on a clean Win11 box completes with no SmartScreen warning (`signtool verify` returns valid on every artifact)
- Pilot-evidence: second pharmacy emits `heartbeat.emitted` events continuously for 7 consecutive days with healthy composite signal AND `RxOrderCandidate` rows accumulating in cloud DB
- **Master-gate counter ticks: 2 of 3**

### Wave 7+ — TBD post-master-gate

When master gate trips (3rd consecutive pilot survives 7+ days), this roadmap is paused. Joshua + Claude session reviews evidence in `roadmap_gates`. New meta-roadmap drafted for post-master-gate territory. Likely candidates (NOT pre-committed):

- Track 5 stage-2: more universal verbs (`rotate_api_key`, `apply_config_override`, `invoke_schema_canary`, `rerun_bootstrap_probe`) + plan-review L3 via Temporal (Phase E from 9-phase plan)
- Track 2: cross-PMS adapters (Computer-Rx, McKesson EnterpriseRx — strategic per second-PMS spec)
- Track 6: HITRUST cert track kickoff
- Track 4: marketplace tile depth (pillar 5 of Mission)

**Hard rule:** do not pre-plan Wave 7+ in this document. Plan it when the gate trips.

---

## 5 · Wave structure summary

| Wave | Theme | Tracks | Master-gate progress |
|---|---|---|---|
| W0 | Baseline reset | 1/2/3/6 | — |
| W1 | Invariants + health | 1/3/4 | — |
| W2 | Cursor v0 | 5/4/3 | — |
| W3 | Reliability + 1st pilot | 1/4 | **1 of 3** |
| W4 | Extraction E2E + dashboard | 2/4/3 | — |
| W5 | First signed verb (`restart_service`) | 5/3/4/6 | — |
| W6 | Signed installer + 2nd pilot | 6/1/2 | **2 of 3** |
| W7+ | TBD post-master-gate | all | **3 of 3 → unlock** |

---

## 6 · The master gate

**Definition:** 3 consecutive pilot installs each surviving 7+ days without manual rescue and without remote intervention. Pilot installs are at distinct pharmacies (or distinct fresh installs at the same pharmacy following a deliberate uninstall + reinstall test).

**Counter mechanics:**
- Counter increments only when a pilot install reaches 7 days of healthy composite signal with zero `incident_resolution` events in `audit_events`
- Counter resets to **0** if any **in-counter pilot crashes mid-soak** (i.e., before reaching 7 days). No "almost 7 days" exceptions. The gate's strictness is the point.
- A pilot's tick is **locked once it reaches 7 days**. A subsequent crash on day 8+ is a Track 1 reliability incident handled within the standard wave-fail recovery flow — it does NOT un-tick the counter and does NOT un-trip a tripped master gate.
- Concurrent pilots count toward the same counter (e.g., Nadim hits day 7 while second pharmacy hits day 5; counter is at 1 with 1 in-progress)
- Each pilot tick is recorded as a `wave.gate_tripped` row with `certified_by LIKE 'pilot:%'`
- **Pilot sourcing:** Wave 3 produces pilot 1 (Nadim reinstall). Wave 6 produces pilot 2 (second pharmacy shadow). Pilot 3 is unscheduled — it can be a third pharmacy onboarded after Wave 6 OR a deliberate uninstall + fresh install at Nadim or the second pharmacy. Master gate trip is asynchronous; it happens whenever pilot 3's 7-day soak completes, not on a fixed wave boundary.

**Effect when tripped:**
- Tracks 2, 4, 5, 6 unlock greenfield bandwidth (per the "freeze → sequence" framing)
- Track 5 stage-2 (more verbs, plan-review L3) becomes available to plan
- This roadmap is archived to `docs/superpowers/specs/archive/` and a new roadmap is drafted

**Effect when reset:**
- All in-progress wave work continues
- Track 5 stage-1 (Wave 5) verb execution remains available (proven safe at one pilot)
- Track 5 stage-2 stays gated
- Counter reset audit row: `wave.gate_failed` with `root_cause_class="pilot-crash-midsoak"` and full failure summary

---

## 7 · Wave-fail recovery model

No calendar pressure means failure is information, not a deadline miss. Six recovery rules:

1. **Gate misses on first attempt** → wave stays open. Diagnose via live evidence first (per `feedback-real-fixes-not-patches.md`: vercel logs, db query, repro). Apply fix. Re-attempt gate. No panic.
2. **Partial evidence** (e.g., pilot install survives 4 days then crashes day 5) → wave stays open. Counter does NOT advance. Treat day-5 crash as a new Track 1 reliability gap, add to wave's deliverables, fix, re-attempt full 7-day soak.
3. **Scope error** (wave's deliverables don't actually cover the real failure mode) → re-cut: amend deliverables, document new bug class in the relevant feedback memory, re-attempt gate. "Wave drift" is normal.
4. **External blocker** (Yubikey not delivered, second pharmacy not lined up, Codex review pending) → wave stays open, prep next wave's independent work in parallel. Never sit idle.
5. **Architectural error** (e.g., `RxOrderCandidate` schema doesn't fit PioneerRx partial-fill semantics) → escalate to schema decision, log in `docs/self-healing/decisions/`, possibly retroactive PR to amend Phase 0 invariants. Gate doesn't trip until schema is right.
6. **Master-gate counter resets hard** if any of the 3 surviving pilots crashes mid-soak. Hard rule.

Every failure attempt emits `wave.gate_failed` (see §8).

---

## 8 · Gate verification + audit trail

### Three evidence classes

| Class | What counts | Example waves |
|---|---|---|
| **Mechanical** | CI / script verifiable, zero human judgment | W0 (`gh pr list` shows no stale), W1 (synthetic-PHI-leak PR fails build), W2 (`cursor.proposed` event in chain after smoke), W6 (`signtool verify` returns valid) |
| **Pilot-evidence** | Real pharmacy live evidence, observable in production | W3 (dashboard shows agent online + healthy 7d at fresh pilot), W4 (live `RxOrderCandidate` rows in cloud DB), W5 (full `verb.proposed → executed → verified` chain at pilot), W6 (second pharmacy 7d shadow extraction) |
| **Joshua-certified** | Manual founder call (Tesla test for product feel) | W0 (meta-roadmap reads true), W2 (cursor feels right), W4 (dashboard feels intentional), W5 (MFA UX is right) |

Each wave's gate spec names which class(es) apply.

### New audit event types (registered in Wave 0)

Add to `docs/self-healing/event-registry.md`:

```
### `wave.gate_tripped`
- Category: `governance`
- Severity: `info`
- Actor: `system` | `operator`
- Payload: {
    wave_id: string,                  // "W0" | "W1" | ...
    evidence_summary: string,         // one-paragraph human-readable
    certified_by: string,             // "ci" | "pilot:<pharmacy_id_hash>" | "joshua"
    evidence_event_ids: string[],     // pointers to supporting audit events
    tripped_at: timestamptz
  }

### `wave.gate_failed`
- Category: `governance`
- Severity: `warn`
- Actor: `system` | `operator`
- Payload: {
    wave_id: string,
    attempt_number: number,           // 1, 2, 3, ...
    failure_summary: string,
    root_cause_class: string,         // "code-bug" | "scope-error" | "blocker-external"
                                      // | "architectural-error" | "pilot-crash-midsoak"
    remediation_plan_committed_at: timestamptz | null,
    next_attempt_estimated: 'unknown' | 'when-blocker-clears' | 'after-fix'
  }
```

### `roadmap_gates` operational table (Wave 0 deliverable)

```sql
CREATE TABLE roadmap_gates (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  wave_id TEXT NOT NULL,
  status TEXT NOT NULL CHECK (status IN ('open', 'tripped', 'reset')),
  attempt_number INTEGER NOT NULL DEFAULT 1,
  evidence_summary TEXT,
  certified_by TEXT,
  pilot_pharmacy_id_hash TEXT,        -- only set when certified_by LIKE 'pilot:%'
  failure_summary TEXT,
  root_cause_class TEXT,
  recorded_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  recorded_by_audit_event_id UUID REFERENCES audit_events(id),

  UNIQUE (wave_id, attempt_number, status)
);

-- Append-only enforced by trigger (mirroring audit_events pattern)
CREATE OR REPLACE FUNCTION reject_roadmap_gates_mutation()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'roadmap_gates is append-only. Mutation rejected.';
END;
$$;

CREATE TRIGGER roadmap_gates_no_update BEFORE UPDATE ON roadmap_gates
  FOR EACH ROW EXECUTE FUNCTION reject_roadmap_gates_mutation();
CREATE TRIGGER roadmap_gates_no_delete BEFORE DELETE ON roadmap_gates
  FOR EACH ROW EXECUTE FUNCTION reject_roadmap_gates_mutation();

CREATE INDEX roadmap_gates_wave_idx ON roadmap_gates (wave_id, recorded_at DESC);
CREATE INDEX roadmap_gates_pilot_idx ON roadmap_gates (pilot_pharmacy_id_hash) WHERE pilot_pharmacy_id_hash IS NOT NULL;
```

This is the meta-roadmap's own audit chain. Every gate decision verifiable, every failure classified, no wave dies silently.

### Master-gate-trip ceremony

When `roadmap_gates` shows 3 distinct rows where:
- `status = 'tripped'` AND
- `certified_by LIKE 'pilot:%'` AND
- Each pilot has 7+ days of continuous healthy `heartbeat.emitted` events since their respective `recorded_at`

…the meta-roadmap is paused. Joshua + Claude session reviews evidence. New meta-roadmap drafted. Old roadmap archived to `docs/superpowers/specs/archive/2026-05-01-suavoagent-v4-roadmap-design.md`.

A `wave.gate_tripped` row with `wave_id="MASTER"` is written. This is the moment Tracks 2/4/5/6 unlock greenfield bandwidth.

---

## 9 · Cross-cutting invariants (in force from Wave 1 on)

Once these ship, every subsequent wave inherits them. No exceptions, no "just this once."

1. **Track 3 — PHI compile-enforcement.** Any `PHI-Direct` field from `field-registry.md` appearing in any outbound schema (events, verbs, sync payloads, heartbeat, model prompts) = build-fails. CI-enforced, not discipline-enforced.
2. **Track 1 — Master gate.** No executable verbs deployed beyond Wave 5's single `restart_service` and no fleet-wide rollouts of any kind until master gate trips. Cedar policy default-deny for any verb outside the canary tier per pharmacy.
3. **CrowdStrike lesson.** Every change to a verb schema, action grammar version, or audit event shape goes through canary rollout (1 → 5% → 25% → 100%) with auto-halt on anomaly. No "content isn't code" exemptions.
4. **Audit immutability.** `audit_events` and `roadmap_gates` are append-only enforced by trigger AND by `CHECK (false)` deferred constraint. Any operational pressure to "fix" a row results in a new row that references and corrects the old, never an UPDATE.
5. **One source of truth.** `RxOrderCandidate`, `IVerb`, `VerbMetadata`, `AuditEvent` shapes live in `src/SuavoAgent.Contracts/` and are imported wherever needed. Mirrored copies are forbidden (per `feedback-one-line-of-truth-2026-04-27`).
6. **Wave gate audit.** Every wave gate decision (trip OR fail) emits the corresponding audit event. No wave changes status silently.

---

## 10 · Forward references

Per-track sub-specs to be created as the wave queue advances. Each sub-spec spawns its own `superpowers:writing-plans` cycle and its own implementation subprojects.

| Spec to write | When | Path |
|---|---|---|
| Track 1 deep design (self-healing reinstall, version drift, watchdog) | Before Wave 3 | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-1-self-healing-design.md` |
| Track 2 deep design (RxOrderCandidate schema + extractor flow) | Before Wave 4 | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-2-extraction-design.md` |
| Track 3 CI gate spec (PHI compile-enforcement implementation) | Wave 1 prerequisite | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-3-phi-ci-gate-design.md` |
| Track 4 dashboard depth spec (multi-PC, repair, inbox correction) | Before Wave 4 | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-4-dashboard-design.md` (lives in Suavo repo, mirror reference here) |
| Track 5 cursor overlay spec | Wave 2 prerequisite | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-5-cursor-overlay-design.md` |
| Track 5 first signed verb spec (`RestartServiceVerb` + dispatch + Cedar) | Wave 5 prerequisite | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-5-restart-service-verb-design.md` |
| Track 6 Yubikey signing spec | Wave 6 prerequisite | `docs/superpowers/specs/YYYY-MM-DD-suavoagent-track-6-signed-installer-design.md` |

The meta-roadmap does **not** dictate sub-spec content. Each sub-spec follows the standard brainstorm → write spec → writing-plans → implement cycle.

---

## 11 · Cross-references

### Existing canon (read these before deep work on any track)

- `docs/self-healing/invariants.md` — Phase 0 invariants (PHI redaction rules, audit shape, key custody, action grammar foundation)
- `docs/self-healing/audit-schema.md` — hash-chained audit chain, RFC 8785 canonical JSON, S3 Object Lock pipeline
- `docs/self-healing/event-registry.md` — all event types canonical (40+ across 11 domains)
- `docs/self-healing/field-registry.md` — 5-tier classification (Public / Operational / PHI-Adjacent / PHI-Direct / Secret) with full inventory
- `docs/self-healing/action-grammar-v1.md` — Phase D Action Grammar v0.1 (`IVerb`, risk tiers, blast radius, BAA scope, rollback envelopes, 5 universal verbs)
- `docs/self-healing/key-custody.md` — signing key lifecycle
- `docs/self-healing/redaction-rulesets/v1.0.0.yaml` — PHI redaction rules
- `docs/self-healing/phase-a-architecture.md` — Phase A observability deliverable

### Memory references (load these for strategic context)

- `MISSION-square-level-ecosystem` — Mission canonical (this roadmap serves pillar 4)
- `suavoagent-product-vision-2026-05-01` — May 1 six-track product vision (this roadmap is its execution plan)
- `suavoagent-self-healing-9-phase-plan` — long-form 18–24 month substrate plan (this roadmap implements Phase 0/A/D incrementally)
- `suavoagent-mission-loop-architecture` — Mission Loop overlay (pickups in W7+ post-master-gate)
- `suavoagent-95pct-roadmap-22-subprojects` — 22-subproject reliability roadmap (subset is reflected in Tracks 1/6)
- `suavoagent-self-healing-moat-positioning` — patent + HITRUST + acquirer narrative
- `suavoagent-product-reality-check-2026-04-22` — brutal baseline (0 proven autonomous workflows; this roadmap is what closes that gap)
- `feedback-windows-install-lessons` — 6 install gotchas (Wave 3 deliverable input)
- `feedback-one-line-of-truth-2026-04-27` — invariant #5 above
- `feedback-real-fixes-not-patches` — wave-fail recovery rule #1

### Existing prior specs (April 2026)

- `2026-04-11-learning-agent-design.md`
- `2026-04-11-suavoagent-hardening-design.md`
- `2026-04-13-behavioral-learning-design.md`
- `2026-04-13-schema-canary-design.md`
- `2026-04-13-self-improving-feedback-design.md`
- `2026-04-13-writeback-design.md`
- `2026-04-14-collective-intelligence-design.md`
- `2026-04-14-v3-hardening-universal-intelligence-design.md`
- `2026-04-18-tiered-brain-architecture.md`
- `2026-04-19-workflow-template-autonomous-intelligence.md`

These remain canonical for their respective domains. This meta-roadmap does not supersede them — it sequences their continued maturation.

---

## 12 · How this doc evolves

- **Wave gate trips** add a `wave.gate_tripped` audit row but do NOT amend this doc. The doc is the plan; the audit is the history.
- **Wave gate failures** that reveal scope errors (recovery rule #3) do amend this doc — the affected wave's deliverables section is updated with the new scope, and a change log entry is added below.
- **Architectural errors** (recovery rule #5) may amend Phase 0 invariants in `docs/self-healing/`. If they do, this doc cross-references the amendment.
- **Master gate trip** archives this doc to `docs/superpowers/specs/archive/` and a new roadmap is drafted.

---

## 13 · Change log

- **2026-05-01 v0.1** — Initial draft. Locked from brainstorming session 2026-05-01 with Joshua. Locks to v1.0 after Wave 0 gate trips.
