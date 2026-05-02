# Track 1+4 Health Composite + Dashboard Tile — Design

> Distinguishes "agent is sending heartbeats" from "agent is actually healthy" via a 4-component composite signal computed agent-side, plus a 3-state dashboard tile in the pharmacy portal. Wave 1 Sub-project B of the v4 meta-roadmap.

**Locked date:** 2026-05-02
**Status:** v0.1 draft (locks to v1.0 after Wave 1 gate trips)
**Owner:** Joshua Henein
**Wave:** 1, Sub-project B
**Source spec:** `docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md` §4 Wave 1
**Pairs with:** `docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md` (Sub-project A)
**Depends on:**
- `docs/self-healing/event-registry.md` (extended with `agent.health_composite`)
- Existing `HeartbeatWorker` + `SuavoCloudClient.AppendAuditAsync` infrastructure
- Existing `IpcPeerVerifier`, `IpcPipeServer`, `SchemaCanary`, `RxDetectionWorker` signals

---

## 0 · Why this spec exists

Today's pharmacy dashboard shows `is_online` based on heartbeat freshness alone. An agent can be heartbeating perfectly while:
- Helper has crashed and IPC is broken (no UIA observations possible)
- Schema canary has been failing for hours (extraction queries returning garbage)
- No `RxOrderCandidate` has emitted in 6 hours (extraction silently broken)

In all three cases, `is_online = true` but the agent is functionally useless. Joshua's Trip A pilot at Better Life surfaced exactly this class of bug: the agent appeared healthy on the dashboard for hours while the IPC peer-validation issue blocked all UIA captures.

This spec adds a composite signal that distinguishes "process is running" from "system is functional," plus a dashboard tile that surfaces the difference clearly.

---

## 1 · Architecture

Composite signal computed **agent-side**, emitted as new `agent.health_composite` event piggybacked on the existing heartbeat cycle. Cloud computes "silent" state from heartbeat absence (no agent code change). Dashboard renders a 3-state tile in `/pharmacy/agent`.

```
Agent (every heartbeat tick, default 30s)
        ↓
HealthComposite.Compute() — pure function on:
  helperAttached:        bool   (IpcPipeServer.IsConnected on Helper side)
  ipcConnected:          bool   (last heartbeat ack < 60s ago)
  schemaCanaryGreen:     bool   (last canary check passed)
  extractionRecent:      bool   (RxOrderCandidate emit < 30min ago, OR
                                 pharmacy outside business hours — gated)
        ↓
Result: agent.health_composite event
  status: "healthy" | "heartbeating-but-unhealthy"
  components: { helperAttached, ipcConnected, schemaCanaryGreen, extractionRecent }
        ↓
Cloud ingest → audit_events table (existing) — RLS-isolated per pharmacy
        ↓
Dashboard polls /api/pharmacy/agent/health → renders tile
        ↓
3 states (UX):
  ● HEALTHY (green)              all 4 components true
  ⚠ DEGRADED (amber)             heartbeat received, ≥1 component false
                                 → tooltip lists which components are false
  ○ SILENT (red, last-seen-Xm)   no heartbeat in last 5 minutes
```

### Source of truth

- **Component booleans** → individual agent subsystems (existing infrastructure)
- **Composite status** → agent's own self-assessment (`agent.health_composite` event)
- **Silent state** → cloud-derived from heartbeat absence (no agent code change)

### Off-hours handling

`extractionRecent` defaults to `true` outside pharmacy business hours (per `pharmacy_profiles.hours`). Without this gate, every pharmacy would show DEGRADED overnight (the agent is fine; there's just nothing to extract).

### Conservative defaults principle

Every error path resolves toward "degraded" or "unknown" rather than "healthy." A working pharmacy seeing amber unnecessarily is a low-cost annoyance; a broken pharmacy showing green is a HIPAA-compliance disaster.

### Wave 1 retrofit scope

- Add `HealthCompositeCalculator` to SuavoAgent.Core
- Wire into existing `HeartbeatWorker`
- Add ingest-side typing on Suavo cloud
- New dashboard tile component on `/pharmacy/agent`
- The composite event payload uses `[OutboundPayload]` from Sub-project A — Track 3 invariant guards it from day one

---

## 2 · Components

### SuavoAgent .NET (4 components)

| Component | Path | What it does |
|---|---|---|
| `HealthCompositePayload` record | `src/SuavoAgent.Contracts/Models/HealthCompositePayload.cs` | `[OutboundPayload]` sealed record: `Status` (string `"healthy"` / `"heartbeating-but-unhealthy"` / `"initializing"`) + `Components` (record of 4 booleans) + `ComputedAt`. Mirrors `agent.health_composite` event shape. |
| `HealthCompositeCalculator` | `src/SuavoAgent.Core/Health/HealthCompositeCalculator.cs` | Pure function: takes `IHealthSignals` + `IBusinessHoursProvider` + `IClock` → returns `HealthCompositePayload`. Stateless, fully unit-testable. Wraps each signal probe in try/catch → failed signal defaults to `false` (conservative). |
| `IHealthSignals` interface + impl | `src/SuavoAgent.Core/Health/IHealthSignals.cs` + `HealthSignalsProvider.cs` impl | Abstraction over the 4 component sources: `HelperAttached` (from `IpcPeerVerifier`), `IpcConnected` (from `IpcPipeServer`), `SchemaCanaryGreen` (from `SchemaCanary`), `ExtractionRecent` (from `RxDetectionWorker.LastSuccessfulEmitAt`). |
| `HeartbeatWorker` integration | Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | On each heartbeat tick, compute composite + emit `agent.health_composite` event via existing `SuavoCloudClient.AppendAuditAsync`. Piggybacks on heartbeat cadence; no new timer. Composite emission failure does NOT block heartbeat critical path. |

### Suavo Next.js (3 components)

| Component | Path | What it does |
|---|---|---|
| Health API endpoint | `src/app/api/pharmacy/agent/health/route.ts` | `GET /api/pharmacy/agent/health?pharmacy_id=...&agent_install_id=...` → returns `{ status, components, last_event_at, last_heartbeat_at, silent }`. Auth-gated via `requirePharmacyApiContext`. Queries last `agent.health_composite` + last `heartbeat.emitted` for the pharmacy/install. |
| TS Zod schema | `src/lib/agent-health-composite.ts` | `outbound(z.object({ status, components: {...}, last_event_at, last_heartbeat_at, silent }))` — mirrors C# `HealthCompositePayload` + adds cloud-derived `silent` flag. Used by API endpoint + dashboard. Wrapped in `outbound()` per Sub-project A. |
| Dashboard tile | `src/components/suavo/agent/HealthCompositeTile.tsx` | 3-state UI: green dot + "Healthy" / amber + "Degraded — N issues" (with hover tooltip listing which components are false) / red + "Silent — last seen Xm ago". Polls every 30s via SWR with deduping. |

### Wiring into existing dashboard

Modify: `src/app/(pharmacy)/pharmacy/agent/page.tsx` — drop `<HealthCompositeTile />` into the existing state-card region. Keeps the existing `is_online` indicator (legitimate signal: "agent process is running") AND adds the composite tile (more nuanced signal: "system is functional"). Two indicators, distinct semantics.

### New event registration

Modify: `docs/self-healing/event-registry.md` — add `agent.health_composite` entry under the `## agent.*` namespace.

---

## 3 · Data flow

### Event emission (agent-side, every heartbeat tick)

```
HeartbeatWorker.Tick() (every 30s, default)
        ↓
healthSignals = IHealthSignals.Snapshot()
        ↓
{
  helperAttached:    IpcPeerVerifier.IsConnected,
  ipcConnected:      IpcPipeServer.LastAckAt > (now - 60s),
  schemaCanaryGreen: SchemaCanary.LastResult == Green,
  extractionRecent:  RxDetectionWorker.LastEmitAt > (now - 30min)
                     OR Pharmacy.IsOutsideBusinessHours(),
}
        ↓
HealthCompositeCalculator.Compute(signals, hoursProvider, clock):
  if (all 4 true) → HealthCompositePayload {
    Status = "healthy",
    Components = {...},
    ComputedAt = now,
  }
  else → HealthCompositePayload {
    Status = "heartbeating-but-unhealthy",
    Components = {...},
    ComputedAt = now,
  }
        ↓
Emit `agent.health_composite` audit event via existing
SuavoCloudClient.AppendAuditAsync(...) (HMAC-signed, hash-chained)
        ↓
Cloud audit_events row inserted (RLS-isolated per pharmacy)
```

### Cloud ingest (existing path, no changes)

The existing audit ingest endpoint accepts arbitrary registered event types. New event type registers in `event-registry.md`. Field-registry already covers all components (booleans = Operational tier, no PHI). Track 3 invariants (Sub-project A) guard the payload structure: `[OutboundPayload]` on `HealthCompositePayload` is verified clean by `SUAVO0001`.

### Dashboard render path

```
Dashboard mounts <HealthCompositeTile pharmacyId={...} agentInstallId={...} />
        ↓
SWR fetches /api/pharmacy/agent/health?pharmacy_id=...&agent_install_id=...
(refreshInterval: 30_000 ms, deduping window 5_000 ms)
        ↓
Server route handler:
  1. Auth: requirePharmacyApiContext(req) — resolves caller's pharmacy_id
  2. Validate query params with Zod (pharmacy_id matches caller's, agent_install_id is uuid)
  3. Query: last `agent.health_composite` event for (pharmacy_id, agent_install_id)
  4. Query: last `heartbeat.emitted` for same — compute silent = (now - last) > 5min
  5. Determine effective status:
     - if no composite event AND install_age < 2min → "initializing"
     - else if silent → "silent"
     - else → composite.status
  6. Combine into response, validate with outbound() Zod schema, return 200 JSON
        ↓
SWR caches → tile re-renders:
  if "silent"            → red dot + "Silent — last seen 12m ago"
  if "healthy"           → green dot + "Healthy"
  if "heartbeating-..."  → amber dot + "Degraded — 2 issues" + hover-tooltip
  if "initializing"      → grey dot + "Initializing"
```

### State transitions (observable on dashboard)

```
Install → Initializing (< 2min after first heartbeat) → Healthy
Healthy ↔ Degraded     (any component flips false → amber, all true again → green)
Healthy → Silent       (no heartbeat in 5min, regardless of last composite)
Degraded → Silent      (same)
Silent → Healthy/Degraded (heartbeat resumes; status reflects new composite)
```

---

## 4 · Error handling

| Failure mode | Mitigation |
|---|---|
| **Health signal source unavailable** (e.g., `IpcPeerVerifier` throws while computing `helperAttached`) | `HealthCompositeCalculator.Compute()` wraps each signal probe in `try/catch`. Failed signal defaults to `false` (conservative). Original exception → local log + `agent.health_signal_error` event with signal name. |
| **Composite event ingest fails** (network down, HMAC mismatch) | Existing retry pattern: `SuavoCloudClient.AppendAuditAsync` already has exponential backoff + queueing. Composite events are non-blocking — never delay heartbeat critical path. |
| **First-install state** (no composite event ever emitted yet) | API endpoint returns `{ status: "initializing", components: null, ... }` if install age < 2min. Tile renders "Initializing" placeholder, not "silent". |
| **Pharmacy hours lookup fails** (database error) | Calculator falls back to `extractionRecent = true` (treats as off-hours). Conservative: never falsely degrade because of cloud-side outage. Logs warning. |
| **Concurrent composite events from multi-PC pharmacy** | API endpoint queries `WHERE agent_install_id = $1` (specific install). Dashboard renders one tile per install (multi-PC view = multiple tiles). |
| **Component value invalid** (`LastEmitAt` is `DateTimeOffset?` and null) | Calculator treats null as "never seen" → `extractionRecent = false` (correct: agent hasn't emitted yet means recent extraction is false). |
| **Dashboard polling errors** | SWR built-in retry. Tile shows neutral grey + "Health unknown — retrying". After 3 consecutive failures, reverts to "agent state unknown" rather than masking a real problem. |
| **Clock skew between agent + cloud** | Silent detection uses cloud's `recorded_at`, not agent's `occurred_at`. Composite computation uses agent's local clock for `ComputedAt` (informational only). |
| **Composite event payload tampered in transit** | Existing HMAC + hash-chained audit prevents tamper; analyzer SUAVO0001 from Sub-project A prevents new PHI fields from sneaking in. |

**Conservative defaults principle:** every error path resolves toward "degraded" or "unknown" rather than "healthy."

---

## 5 · Testing strategy

Five test categories, all must-have:

### 1. `HealthCompositeCalculator` unit tests *(must-have)*

Pure-function tests with synthetic `IHealthSignals` provider. Cases:
- All 4 signals true + business hours → `status = "healthy"`
- All 4 signals true + outside business hours → `status = "healthy"` (extractionRecent gated by hours)
- 1 signal false (Helper disconnected) → `status = "heartbeating-but-unhealthy"`, components correctly reflect
- All 4 signals false → `status = "heartbeating-but-unhealthy"`, all components false
- Signal probe throws → that signal counts as `false`, calculator does not crash, error event logged
- `LastEmitAt = null` (never emitted) → `extractionRecent = false`
- `LastEmitAt = now - 31min` outside business hours → `extractionRecent = true` (gated)
- `LastEmitAt = now - 31min` inside business hours → `extractionRecent = false`
- Pharmacy hours lookup throws → `extractionRecent = true` (off-hours fallback)

### 2. `HealthCompositePayload` regression test *(must-have)*

Add to existing `RetrofittedTypesRegressionTests.cs` (Sub-project A test suite): assert SUAVO0001 silent on `HealthCompositePayload` (no PHI fields, just booleans + status string).

### 3. `HeartbeatWorker` integration test *(must-have)*

Mocked `IHealthSignals` + in-memory cloud client. Assert:
- Each heartbeat tick → exactly one `agent.health_composite` event emitted
- Event payload structure matches `HealthCompositePayload`
- Composite emission failure does NOT block heartbeat (queue + retry path verified)
- Composite emission failure logs error event but heartbeat remains healthy

### 4. API endpoint test *(must-have)*

Vitest + Supabase test client (existing pattern). Cases:
- Returns 401 if not authenticated
- Returns 403 if `pharmacy_id` query param doesn't match caller's pharmacy (impersonation guard)
- Returns `{ status: "initializing" }` if no composite event in last 24h AND install age < 2min
- Returns `{ status: "silent" }` if no heartbeat in last 5min
- Returns `{ status: "healthy", components: {...} }` if last composite was healthy + recent heartbeat
- Returns `{ status: "heartbeating-but-unhealthy", components: {...} }` if last composite was unhealthy
- Validates response with outbound Zod schema (catches drift from C# shape)
- 5+ concurrent requests for same pharmacy/install return consistent state

### 5. Dashboard tile component test *(must-have)*

Vitest + React Testing Library. Cases:
- `status = "healthy"` → green dot + "Healthy" text
- `status = "heartbeating-but-unhealthy"` with 2 false components → amber dot + "Degraded — 2 issues" + hover reveals which components
- `status = "silent"` → red dot + "Silent — last seen Xm ago" (uses relative-time formatter)
- `status = "initializing"` → grey dot + "Initializing" no relative time
- API error → grey dot + "Health unknown — retrying"
- Polling: SWR cache hits work; manual refresh triggers refetch

### 6. End-to-end test *(deferred, optional)*

Real agent install on Joshua's test box → 5-minute observation → assert dashboard transitions through `Initializing → Healthy`. Then force-disconnect Helper → assert tile flips to `Degraded` within one heartbeat cycle (~30s). Pilot-evidence-class — runs as part of Wave 1's master gate verification.

**Coverage:** ~30 unit tests + 8 endpoint tests + 6 component tests across both repos.

---

## 6 · Out-of-scope (deferred to future waves)

- **Multi-PC composite roll-up** — when a pharmacy has 3 agent installs, do you show one tile each (current plan) or roll up to a single pharmacy-level tile? Current plan: per-install tiles. Roll-up logic: future wave.
- **Time-series view of composite state** — last 24h history of healthy/degraded transitions. Useful for debugging "why did this go amber 2h ago?" Future enhancement.
- **Alerting on transitions** — Slack/SMS alert when a healthy pharmacy goes degraded for > N minutes. Belongs in Track 1 self-healing work, Wave 3+.
- **Component-level suggested fixes** — "Helper not attached → click here to restart Helper". Tied to Track 5 verb dispatch (Wave 5+).
- **Customer-visible health status** — patient-facing UI showing "your pharmacy is online." Not in scope; this is operator-facing only.

---

## 7 · Cross-references

### Existing canon

- `docs/self-healing/event-registry.md` — `agent.health_composite` will register under `## agent.*` namespace
- `docs/self-healing/field-registry.md` — booleans + status strings classify as Operational tier, no PHI
- `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` — existing heartbeat infrastructure
- `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs` — existing audit ingest

### Source spec

- `docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md` — meta-roadmap §4 Wave 1

### Sub-project A pairing

- `docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md` — Sub-project A. The new `HealthCompositePayload` benefits from Sub-project A's compile-time enforcement: `[OutboundPayload]` analyzer + Zod `outbound()` rule guard the new event from PHI leaks day one.

### Wave 1 closure

Wave 1's gate per meta-roadmap §4 trips when:
- Synthetic PHI-leak PR fails CI build (Sub-project A — verified)
- Dashboard correctly shows unhealthy state on Joshua's test box when Helper is force-disconnected (Sub-project B — this spec's pilot evidence)

Sub-project A is shipped (PR #285 + local SuavoAgent main). Sub-project B (this spec) closes the second half.

---

## 8 · Change log

- **2026-05-02 v0.1** — Initial draft from brainstorming session. Locks to v1.0 after Wave 1 gate trips.
