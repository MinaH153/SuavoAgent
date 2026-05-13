# Diagnostic Mesh — Queen-first

**Status:** v0.1 draft — pending /plan-eng-review + /plan-ceo-review + Codex re-review on encryption choice and the open-questions list (§8).
**Locked date:** 2026-05-12
**Scope:** Phase 1 (Queen + SuavoAgent only). Phase 2–6 sketched in §1 for direction-locking; not for implementation in this cycle.
**Complement to:** [docs/self-healing/phase-a-architecture.md](../self-healing/phase-a-architecture.md) — Phase A owns cloud-side audit substrate (audit_events hash chain, A1 silent-agent alarm, A6 attestation). Mesh owns dev-loop primitives + agent-side fingerprint compute + Sentry/GH-Actions surface. The two layers reference each other and ship independently.

---

## 1. Scope

### What this is

A thin overlay on Sentry (BAA signed 2026-05-11), Datadog, GitHub Actions, and the existing `%ProgramData%\SuavoAgent\logs\startup-crash.log` substrate. **NOT a from-scratch rebuild.** The mesh kills four specific friction patterns that surfaced on 2026-05-12 (Bug 24 verification session) and that will recur with Nadim if unfixed:

1. **Silent dev-loop friction** — 8-min `dotnet publish` with no heartbeat, PS 5.1 vs pwsh 7 trial-and-error, missing Yubikey/cert/SmartCard svc not detected until after the build, hidden UAC/SmartScreen dialogs behind CRD.
2. **Crash invisibility on the box** — `SuavoSetup.exe` exits 0xE0434352 (CLR unhandled), `Start-Process` returns 0 to PowerShell parent, no stderr crosses CRD, no fingerprint persists. Bug 24's actual failure mode tonight.
3. **No structured runtime crash signal** — existing `WriteCrash` in `Core/Program.cs:17-51` writes plain text to ProgramData; nothing aggregates, nothing groups, nothing alerts.
4. **CI catches none of this** — no Avalonia construction smoke test means InvalidCastException at MainWindow.axaml:34 ships green.

### Phase 1 deliverables (this cycle)

Four PRs, parallel:

| PR | Deliverable | Effort | Class |
|---|---|---|---|
| PR 1 | `scripts/Test-QueenShipPreflight.ps1` | 0.5d | dev-velocity |
| PR 2 | `publish.ps1` heartbeat patch | 1h | dev-velocity |
| PR 3 | CI Avalonia init smoke test + workflow | 0.5d | dev-velocity |
| PR 4 | `SuavoAgent.Diagnostics` library + ruleset-v1 + Sentry SDK wrap | 1d | operator-safety |

Total: ~3 working days. Ships before Nadim onboards. Cert wait is vendor-bound on SSL.com so these cycles are free.

### Phase 2-6 (deferred — direction-locking only)

| Phase | Trigger | Effort | Scope |
|---|---|---|---|
| 2 | After Phase 1 stable on Queen for 7d | 2d | GH Actions watches Sentry webhook → auto-create/bump/reopen issues by fingerprint. Cloud-side `fingerprint_registry` + `fingerprint_occurrences` Postgres schema activates. Signed ruleset OTA distribution endpoint opens (rule push to agent via existing `ConfigSyncWorker`). |
| 3 | When Nadim onboards (Pilot 1) | 3-4d | Extend `SuavoAgent.Diagnostics.Wire()` to Suavo web/iOS/edge contexts. Per-tenant encryption keys for diagnostic bundles. Audit every decrypt via existing audit_log pattern. |
| 4 | When Pilot 3 onboards | 1w | Cross-tenant pattern detection. "Fingerprint X hit 3 agents within 24h" surfaces automatically. Phase A's A1 alarm + this fingerprint detector compose. |
| 5 | When 3 patterns have 3+ recurrences in fleet | 2-4w | Per-pattern self-healing actions, gated, audited, reversible. Aligns with Mission Loop self-healing arc (`docs/self-healing/phase-a-architecture.md` Phase B-G). |
| 6 | Always | ongoing | Tighten ruleset-v1.json based on real Queen + pilot signal. Tighten PHI scrub corpus. Codex review every ruleset version bump. |

### Architecture decision — Option D (agent-edge compute + cloud-distributed signed rules)

Considered options:
- **A** — agent-side shared library, fingerprint version baked into library version. **Rejected** because grouping evolution requires agent OTA. Inflexible.
- **B** — in-place extension of each entry point's existing `WriteCrash`. **Rejected** because 5x duplication of PHI scrub + fingerprint logic across LocalService / NetworkService / LocalSystem / user-session / Avalonia trust contexts. PHI leaks become "works on Core" bugs.
- **C** — agent emits raw context, fingerprint computed cloud-side via Edge Function on Sentry webhook ingest. **Rejected** because the raw event crosses Sentry ingress before any cloud-side function fires. Sentry BAA does not grant minimum-necessary license (§164.502(b)) to send raw pharmacy stack frames when a deterministic non-PHI fingerprint suffices. Also: Sentry vendor schema changes would silently break issue identity without an agent code change. Operator-SLA failure-mode asymmetry (Edge Function down = silent fingerprint loss vs agent-edge library failure = graceful degrade to existing `WriteCrash`).
- **D — agent-edge compute + cloud-distributed signed rules. SELECTED.** Fingerprint computation lives in `SuavoAgent.Diagnostics` shared library on the workstation. Sentry receives only a scrubbed canonical event plus an explicit fingerprint tag. Cloud ships signed normalization rules (Phase 2+) that the agent caches; Phase 1 ships `ruleset-v1.json` embedded in the library.

Codex agreed (consulted 2026-05-12) and corrected three things that would have shipped wrong:
1. Fingerprint version-stability must come from a **signed cloud ruleset** (data), not from baking algorithm version into library version (code).
2. Joshua's working algorithm `{component}-{exception class}-{top frame hash}` would have produced unstable fingerprints under `publish.ps1`'s `PublishReadyToRun=true` + `PublishSingleFile=true` flags. Microsoft documents stack frames omitted by inlining + tiered JIT replacing R2R methods at runtime. See §3.
3. The original spec couldn't fingerprint Bug 23 at all (no exception thrown, just an invariant violation). Added `semantic_invariant_id?` to the canonical fingerprint shape.

---

## 2. System diagram

```
┌─────────────── Queen Workstation (Phase 1 Pilot 0) ──────────────────┐
│                                                                       │
│  Developer loop  (PRE-CERT, dev-velocity class):                      │
│  ┌──────────────────────────────────────────────────────────────┐    │
│  │ scripts/Test-QueenShipPreflight.ps1     [PR 1]               │    │
│  │   ├─ PS 7+? Yubikey? EV cert? SmartCard svc? .NET 8?         │    │
│  │   └─ Fail fast, one-line actionable error per check          │    │
│  │                                                                │    │
│  │ publish.ps1 (heartbeat patch)            [PR 2]               │    │
│  │   ├─ [Core] start=T+0s | elapsed=12s | "still compiling..."  │    │
│  │   └─ Ctrl+C indicator, per-project start/elapsed/finish      │    │
│  │                                                                │    │
│  │ .github/workflows/setup-smoke.yml        [PR 3]               │    │
│  │   └─ AvaloniaInitSmokeTest: construct App+MainWindow <5s     │    │
│  └──────────────────────────────────────────────────────────────┘    │
│                                                                       │
│  Runtime  (POST-CERT, operator-safety class):                         │
│  ┌─────────────────────────────────────────────────────────────────┐ │
│  │  5 Entry Points                                                  │ │
│  │  ┌─────────┐ ┌──────────┐ ┌────────┐ ┌─────────┐ ┌──────────┐ │ │
│  │  │  Core   │ │  Broker  │ │ Helper │ │Watchdog │ │  Setup   │ │ │
│  │  │LocalSvc │ │NetworkSv │ │UserSes │ │LocalSys │ │Avalonia  │ │ │
│  │  └────┬────┘ └────┬─────┘ └───┬────┘ └────┬────┘ └─────┬────┘ │ │
│  │       └───────────┴───────────┼───────────┴────────────┘      │ │
│  │                               ▼                                │ │
│  │              ┌──────────────────────────────────┐    [PR 4]   │ │
│  │              │   SuavoAgent.Diagnostics         │              │ │
│  │              │   ────────────────────────       │              │ │
│  │              │  • Wire(component, options)      │              │ │
│  │              │  • FingerprintComputer (fp-v1)   │              │ │
│  │              │  • PhiScrubber (SDK-side)        │              │ │
│  │              │  • Resources/ruleset-v1.json     │              │ │
│  │              │  • SentrySink (BAA wrapped)      │              │ │
│  │              │  • AvaloniaDispatcherHook        │              │ │
│  │              │  • Local WriteCrash fallback     │              │ │
│  │              └────┬──────────────────┬──────────┘              │ │
│  │                   │ scrubbed         │ graceful                │ │
│  │                   │ event + fp tag   │ degrade                 │ │
│  │                   ▼                  ▼                          │ │
│  └───────────────────┼──────────────────┼─────────────────────────┘ │
└──────────────────────┼──────────────────┼──────────────────────────┘
                       │                  │
                       │                  │ %ProgramData%\SuavoAgent\
                       │                  │   logs\startup-crash.log
                       │                  │   (existing, unchanged)
                       ▼
              ┌────────────────────┐
              │ Sentry (BAA) cloud │
              │  • fingerprint as  │
              │    grouping tag    │
              │  • SDK-side scrub  │
              │    enforced        │
              └─────────┬──────────┘
                        │ webhook (Phase 2)
                        ▼
        ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─ ─
        Phase 2+ (deferred, stubbed in Phase 1):
        ┌────────────────────────────────────────────────────┐
        │ • GH Actions Sentry webhook → auto issue           │
        │ • Supabase: fingerprint_registry + occurrences     │
        │ • Signed ruleset-v2 OTA → ConfigSyncWorker         │
        │ • Phase A audit chain (cross-link via                │
        │   `crash.log_uploaded` event family)               │
        └────────────────────────────────────────────────────┘
```

**Layer responsibilities — no overlap with Phase A:**

| Layer | Owner | Phase 1? |
|---|---|---|
| Dev-loop preflight + heartbeat + CI smoke | Mesh PR 1-3 | Yes |
| Agent-edge fingerprint compute + PHI scrub | Mesh PR 4 | Yes |
| Sentry as primary crash surface (BAA wrapped) | Mesh PR 4 | Yes |
| Local crash file fallback | Existing `WriteCrash` (unchanged) | Yes |
| Auto-issue creation from Sentry webhook | Mesh Phase 2 | No |
| `fingerprint_registry` + `fingerprint_occurrences` Postgres tables | Mesh Phase 2 | No (schema sketched in §6 for direction) |
| Signed ruleset OTA distribution | Mesh Phase 2 | No (format spec sketched in §3) |
| Hash-chained audit substrate (audit_events) | Phase A — A2 | No (separate arc) |
| Silent-agent freshness alarm (>15min heartbeat gap) | Phase A — A1 | No (separate arc) |
| Crash log raw upload to S3 with Object Lock | Phase A — A3 | **Superseded by Mesh PR 4** — Sentry replaces raw S3. Phase A's A3 design folds into Mesh. |
| Cryptographic binary + config attestation | Phase A — A6 | No (separate arc) |
| Self-healing actions per pattern | Mesh Phase 5 + Mission Loop | No |

---

## 3. Fingerprint algorithm — fp-v1 spec

### Canonical shape

```
fp-v1 = component | signal_kind | exception_type | stable_error_code | primary_failure_site | semantic_invariant_id?
```

Fields:

- **`component`** — fixed enum: `Core | Broker | Helper | Watchdog | Setup | Publish`. Identifies which entry point produced the signal. `Publish` covers PowerShell exit-code captures from `publish.ps1` (§7 PR 4).
- **`signal_kind`** — fixed enum: `managed_exception | win32 | unmanaged_native | invariant_violation | unobserved_task | exit_code | hang`. Joshua's original spec assumed all signals are managed exceptions; this enum lets Bug 23 (invariant_violation, no exception) and Bug 24 (exit_code from publish.ps1) fit the same shape.
- **`exception_type`** — fully-qualified type name of the .NET exception (`System.InvalidCastException`, `System.ComponentModel.Win32Exception`). Empty for non-exception signals.
- **`stable_error_code`** — extracted code that survives version changes:
  - Win32: `native_error=5` from `Win32Exception.NativeErrorCode`
  - COM/HRESULT: `hresult=0x80070005`
  - Process exit: `exit_code=0xE0434352`
  - Invariant violation: empty (semantic_invariant_id carries the identity)
- **`primary_failure_site`** — normalized to `AssemblySimpleName.TypeFullName.MethodName(arity,paramTypeNames)`. Excludes file path, line number, native offset, MVID (`Module.ModuleVersionId`), and metadata token. The first in-app non-wrapper managed frame. Wrapper-detection list: known interop wrappers, AsyncStateMachine wrappers, lambda-display-class wrappers.
- **`semantic_invariant_id?`** — optional. Catalog-issued identifier like `complete-zero-actuation-log` for invariant violations. Lives in `ruleset-v1.json` under a versioned catalog.

### Why this shape survives `publish.ps1`'s build flags

`publish.ps1` ships agent binaries with `PublishReadyToRun=true` + `PublishSingleFile=true` + `PublishTrimmed=false`. The fingerprint must be stable against:

| Build behavior | Naive `{top frame hash}` impact | fp-v1 impact |
|---|---|---|
| Tiered JIT replaces R2R method with JIT-generated at runtime | Stack frame address/IL offset shifts | None — uses method **identity**, not offset |
| Inlining omits intermediate frames | Top frame changes if inliner kicks in | None — uses first in-app non-wrapper frame, walks past inlined wrappers |
| Single-file publish breaks `Assembly.Location` | Path-based hashes invalidate | None — uses `AssemblySimpleName` only |
| Module reload changes MVID | Module-version-id changes per build | None — MVID explicitly excluded |
| Method body changes (bugfix patch) | Hash changes | Method **name** unchanged unless renamed |

Metadata token + MVID are still **captured for symbolication** (so we can resolve to source after the fact) but live in the occurrence payload, not the fingerprint.

### Three calibration fingerprints (worked examples)

**Bug 22** — Helper-side `System.ComponentModel.Win32Exception (5): Access is denied.` from `SendInput`. Root cause: `DuplicateTokenEx(SecurityIdentification(1))` instead of `SecurityImpersonation(2)`.

```
fp-v1 = Helper | win32 | System.ComponentModel.Win32Exception | native_error=5 | operation=SendInput/actuation-token
```

Note: `primary_failure_site` here uses an **operation identifier** rather than a raw frame because the actual top frame is a P/Invoke interop wrapper that varies across .NET runtime versions. The ruleset normalizes Win32 errors from interop wrappers into the operation identifier of the caller.

**Bug 23** — `WorkflowExecutor.Complete()` returns success with zero `actuation_log` rows. No exception, no stack trace, just an invariant violation surfaced by the cloud-side audit-count gate.

```
fp-v1 = Core | invariant_violation | <empty> | <empty> | WorkflowExecutor.Complete | complete-zero-actuation-log
```

The `semantic_invariant_id` `complete-zero-actuation-log` lives in `ruleset-v1.json`'s invariant catalog. Invariant violations are emitted via `SuavoAgent.Diagnostics.Wire.ReportInvariant(id, context)` — they don't ride the unhandled-exception path.

**Bug 24** — Avalonia `System.InvalidCastException` at `MainWindow.axaml:34` during XAML compilation. `Start-Process` returned 0 to `publish.ps1` parent (managed exception → fast-fail exit code 0xE0434352 → process exit code 0 to OS).

```
fp-v1 = Setup | managed_exception | System.InvalidCastException | <empty> | Avalonia.MainWindow.InitializeComponent | resource=MainWindow.axaml
```

The `resource=MainWindow.axaml` is captured because Avalonia XAML compilation errors carry resource identity in the exception data. The line number (34) is **explicitly excluded** — a code change that moves line 34 to line 47 doesn't change the bug identity.

For the `publish.ps1` parent process that received exit code 0 from `Start-Process`, the mesh adds a separate fingerprint via `publish.ps1`'s exit-code capture wrapper:

```
fp-v1 = Publish | exit_code | <empty> | exit_code=0xE0434352 | dotnet-publish-SuavoSetup | <empty>
```

These two fingerprints are then **aliased** via the cloud alias table in Phase 2 (single bug, two surfaces).

### PHI scrubbing — minimum-necessary enforcement

§164.502(b) minimum-necessary applies to every outbound diagnostic event. The Sentry BAA covers Sentry as an allowable processor but **does not grant license to ship raw pharmacy context when a scrubbed equivalent identifies the same crash**. Every Sentry event passes through `PhiScrubber` in the SDK-side `BeforeSend` callback before any wire transmission.

Scrub patterns (initial; expand based on signal):

| Pattern class | Regex / mechanism | Replacement |
|---|---|---|
| File paths under `\Users\<name>` | `[\\/]Users[\\/][^\\/]+` | `[\\/]Users[\\/][USER]` |
| SSN-shape | `\d{3}-\d{2}-\d{4}` | `[SSN]` |
| DOB-shape | `\b(0?[1-9]\|1[0-2])[-/](0?[1-9]\|[12]\d\|3[01])[-/](19\|20)\d{2}\b` | `[DOB]` |
| Rx number shape (PioneerRx pattern) | `\bRX\d{6,12}\b` (case-insensitive) | `[RX_NUM]` |
| NPI shape | `\b\d{10}\b` constrained by NPI Luhn check | `[NPI]` |
| Patient name dictionary | Loaded from `ruleset-v1.json` patient_names_seed array (will be empty in Phase 1; populated by edge-side detection in Phase 2 from PioneerRx data discovery) | `[PATIENT]` |
| UIA window titles | Any string field tagged `uia_title` in event extra | redacted by allowlist |
| SQL text | Any field tagged `sql_text` | parameter values stripped via sqlparse |

If `PhiScrubber.IsDefinitelyPhi(event)` returns true with high-confidence, the event is **dropped entirely** and only the canonical fingerprint is forwarded with an empty extra. Counter incremented in local telemetry.

The PhiScrubber test corpus (PR 4 test plan) includes >50 known-PHI patterns the scrubber must defeat. CI fails the PR if any test PHI pattern reaches the post-scrub event.

---

## 4. PHI scrub + minimum-necessary discipline

Already covered in §3 (folded for tightness — PHI is so central to fp-v1 it belongs alongside the algorithm). Cross-references:

- §164.502(b) minimum-necessary — only the smallest signal that identifies the crash crosses the wire.
- §164.312(e) transmission safeguards — Sentry .NET SDK uses HTTPS + TLS 1.2+ to the BAA-covered region. SDK-side scrubbing in `BeforeSend` is the LAST line of defense before transmission.
- §164.312(b) audit controls — every diagnostic event is locally journaled to `%ProgramData%\SuavoAgent\diagnostics\events.jsonl` (rotated daily, 30-day retention) regardless of Sentry outcome. Local journal contains the scrubbed event; raw stack/context is never persisted locally either, because we want the local journal to be safe to ship as a crash bundle if Sentry is unreachable.

### Performance contract — local-first SLA

The crash handler is on the hot path of a process that is already dying. Wall-clock budget is tight; the entire local-side dispatch (PhiScrubber → FingerprintComputer → LocalJournal.Append → startup-crash.log defense-in-depth write) must complete in **< 50ms p99** before any network I/O is initiated.

| Stage | Budget | Behavior on overrun |
|---|---|---|
| `PhiScrubber.Sanitize` | < 10ms | Hard timeout via `CancellationToken`. On timeout: drop event entirely (fail-closed PHI safety) and increment counter. |
| `FingerprintComputer.Compute` | < 10ms | Hard timeout. On timeout: emit `fp-fallback` synthetic fingerprint with component + signal_kind only. |
| `LocalJournal.Append` (events.jsonl) | < 20ms | Best-effort. On timeout (disk full / slow): write to startup-crash.log fallback only. |
| `startup-crash.log` defense-in-depth | < 10ms | Always best-effort, swallowed on failure (matches existing `WriteCrash` behavior). |
| **Sentry POST** | **N/A** | **Fire-and-forget after local completion.** Initiated on a background task; crash handler does not await. Sentry SDK uses its own internal queue + retry. |

Test gate: 100-iteration determinism harness in `tests/SuavoAgent.Diagnostics.Tests/` asserts handler-local-time p99 < 50ms across synthetic Bug 22 / 23 / 24 reproductions. CI fails the PR if p99 regresses.

### Failure-mode contract

| Failure | Mesh behavior | Operator visibility |
|---|---|---|
| Sentry unreachable | Local `events.jsonl` continues. Fingerprint still computed. On reconnect, last N events flush to Sentry via background queue. Crash handler never blocked. | None in Phase 1. Phase A's A3 path (renamed) consumes the flush. |
| Sentry SDK init fails | `Wire()` swallows + logs. Existing `WriteCrash` continues. **Process startup is not blocked by diagnostics init.** | Local logs show diagnostics-disabled state. |
| ruleset-v1.json malformed (embedded resource corrupted) | Fail-closed: fingerprint compute uses ruleset-v0 (hardcoded minimal fallback). Telemetry counter incremented. | Local logs show ruleset-fallback state. **CI MSBuild schema-validation gate prevents this from shipping** (§7 PR 4). |
| Phase 2 cloud rule push delivers invalid ruleset | Agent rejects via embedded public-key signature verification. Falls back to last-known-good cached ruleset. **Never** falls back to ruleset-v0 unless cache is also corrupt. | Phase 2 surface, not Phase 1. |
| Crash handler exceeds 50ms p99 budget | Per-stage timeouts above kick in; handler completes in bounded time at cost of degraded fidelity. | CI regression alarm. |

---

## 5. Encryption scheme recommendation (**FOR CODEX REVIEW**)

### Decision: supabase Vault for Phase 1; defer AWS KMS to Phase 3

### Reasoning

Phase 1 is single-tenant (Queen-only). Per-tenant encryption keys for diagnostic bundles are a Phase 3 requirement (when Nadim onboards as tenant 1). For Phase 1:

- The diagnostic bundle that needs encryption is the **scrubbed canonical event + occurrence context** (env vars filtered, recent log tail, build SHA). PHI is already removed at the edge by the SDK-side `PhiScrubber` (§3). Encryption-at-rest is defense-in-depth, **not** the primary safeguard.
- supabase Vault is already in use elsewhere in the codebase. Adoption cost is zero.
- AWS KMS adds: AWS account setup, IAM design, cross-region replication thinking, KMS keyring management. All of which is **Phase A's territory** (A2 hash-chained audit substrate, A6 attestation). Adding AWS account work to Mesh Phase 1 conflicts with the "wrap existing infra" principle in this doc's §1.

### When KMS becomes load-bearing

Phase 3 (Nadim onboards). At that point Phase A's KMS keyring is likely set up, and the mesh can lean on the same keyring for per-tenant diagnostic-bundle keys. The migration from Vault → KMS is a one-shot re-encrypt of the existing fingerprint_occurrences rows; impact is low.

### Risk to call out for Codex

supabase Vault is OK for column-level encryption but doesn't equivalently provide:
- KMS-grade automated key rotation
- Audited decrypt events per row read

For Phase 1 (Queen-only, scrubbed-bundles-only), the case to skip these guarantees is straightforward. **For Phase 3 (multi-tenant, real pharmacy operational data) the case is more debatable.** This is the explicit Codex-review item for the encryption section.

---

## 6. Schema sketch — fingerprint_registry + fingerprint_occurrences (Phase 2 cloud-side, sketched for direction)

### Phase 2 migration (NOT shipped in Phase 1)

```sql
CREATE TABLE fingerprint_registry (
  id                       BIGSERIAL PRIMARY KEY,
  fingerprint              TEXT      NOT NULL UNIQUE,
  ruleset_version          TEXT      NOT NULL,
  component                TEXT      NOT NULL CHECK (component IN ('Core','Broker','Helper','Watchdog','Setup','Publish')),
  signal_kind              TEXT      NOT NULL CHECK (signal_kind IN ('managed_exception','win32','unmanaged_native','invariant_violation','unobserved_task','exit_code','hang')),
  exception_type           TEXT,
  stable_error_code        TEXT,
  primary_failure_site     TEXT,
  semantic_invariant_id    TEXT,
  first_seen_at            TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_seen_at             TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  occurrence_count         BIGINT      NOT NULL DEFAULT 0,
  alias_of                 BIGINT      REFERENCES fingerprint_registry(id),
  resolved_at              TIMESTAMPTZ,
  resolved_by_pr           TEXT,
  notes                    TEXT
);

CREATE TABLE fingerprint_occurrences (
  id                BIGSERIAL PRIMARY KEY,
  fingerprint_id    BIGINT NOT NULL REFERENCES fingerprint_registry(id),
  pharmacy_id       TEXT   NOT NULL,
  agent_version     TEXT   NOT NULL,
  occurred_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  context           JSONB,  -- scrubbed bundle only; PHI never appears here
  sentry_event_id   TEXT
);

CREATE INDEX fingerprint_registry_last_seen_idx
  ON fingerprint_registry (last_seen_at DESC);

CREATE INDEX fingerprint_occurrences_pharmacy_idx
  ON fingerprint_occurrences (pharmacy_id, occurred_at DESC);

CREATE INDEX fingerprint_occurrences_fingerprint_idx
  ON fingerprint_occurrences (fingerprint_id, occurred_at DESC);

ALTER TABLE fingerprint_registry     ENABLE ROW LEVEL SECURITY;
ALTER TABLE fingerprint_occurrences  ENABLE ROW LEVEL SECURITY;

-- Fleet admins (Joshua + designated operators) see all
CREATE POLICY "Fleet admin reads all fingerprints"
  ON fingerprint_registry FOR SELECT TO authenticated
  USING (auth.uid() IN (SELECT id FROM fleet_admin_users));

-- Pharmacies see only their own occurrences
CREATE POLICY "Pharmacies read own occurrences"
  ON fingerprint_occurrences FOR SELECT TO authenticated
  USING (pharmacy_id = current_pharmacy_id());
```

`alias_of` is the cloud-side merge mechanism Codex called out: when a ruleset version bump changes how the same crash fingerprints (e.g., we discover Bug 22's wrapper-stripping logic was too eager and need to re-group), insert the new fingerprint and point its `alias_of` at the old one. The cloud merges occurrence counts for dashboards; the agent never knows.

Idempotency, retention, and timestamp-past-prod-last-applied discipline per [[feedback-migration-idempotency-before-merge]] and [[feedback-migration-timestamp-past-prod-last-applied]] standing rules.

---

## 7. The 4 Phase 1 PRs

### PR 1 — `scripts/Test-QueenShipPreflight.ps1`

**Goal:** detect the entire class of "build fails because environment isn't ready" that burned 2 hours on 2026-05-12.

**Files:**
- `scripts/Test-QueenShipPreflight.ps1` (NEW, ~120 lines)
- `tests/PreflightTests/Test-Preflight.Tests.ps1` (NEW, Pester tests)
- `publish.ps1` (MODIFIED: call preflight as first action; abort with explicit message on failure)

**Checks (single-line actionable per failure):**

| Check | Action on failure |
|---|---|
| PowerShell version >= 7.0 (no PS 5.1) | "Install pwsh: winget install Microsoft.PowerShell. Then re-run via pwsh.exe." |
| pwsh.exe on PATH | (same) |
| .NET SDK 8.0+ installed | "Install: winget install Microsoft.DotNet.SDK.8" |
| Yubikey reader present (via `Get-PnpDevice` query) | "Insert Yubikey or run with -SkipSigning to publish unsigned." |
| EV cert thumbprint present in CurrentUser\My (when `$env:SUAVO_CERT_THUMBPRINT` set) | "Set `$env:SUAVO_CERT_THUMBPRINT` to the SHA1 of your code-signing cert, or run with -SkipSigning." |
| SmartCard service running | "Start-Service SCardSvr; or run with -SkipSigning." |
| Git tree clean (no uncommitted changes in src/) | "Commit or stash before publishing." |
| Build cache fresh (`obj/`, `bin/` older than HEAD) | "Run with -CleanFirst or `dotnet clean` if last build > 1d ago." |

**Test plan:**
- Pester unit tests for each check function (mocked registry/cert/service queries).
- Each check produces a single-line actionable error on failure (asserted via regex).
- End-to-end smoke: run on Queen via remote-pwsh, validate output format under both success + each failure mode.

**Rollout:**
- Ships as part of `publish.ps1`'s first step. No flag, no opt-out — the preflight runs every time. If a check fails, abort with exit code 1 and message; no partial publish.

### PR 2 — `publish.ps1` heartbeat patch

**Goal:** kill the 8-min silent build that made the developer think the build hung and Ctrl+C'd, wasting 10 more minutes on the 90s cancel + retry.

**Files:**
- `publish.ps1` (MODIFIED: wrap `dotnet publish` calls with per-project timing + heartbeat)
- No new tests; manual verification on Queen.

**Behavior:**

```
[Core]     start=T+0s   | publishing...
[Core]     start=T+0s   | elapsed=30s  | still compiling (Microsoft.Extensions.Hosting)
[Core]     start=T+0s   | elapsed=60s  | still compiling (Serilog)
[Core]     start=T+0s   | elapsed=87s  | DONE in 87s | SuavoAgent.Core.exe (52.3 MB)
[Broker]   start=T+87s  | publishing...
...
```

Heartbeat tick every 30s. Captures last-line-from-dotnet-publish-stdout so the operator sees what's compiling.

On Ctrl+C: write "[*] BUILD INTERRUPTED at <project> after <elapsed>s. Re-run with -CleanFirst if rebuild seems incomplete." Then exit.

**Test plan:**
- Run on Mac dev box: verify per-project timing emits.
- Run on Queen: verify heartbeat appears even when first build is slow.
- Ctrl+C interruption test: verify "interrupted" message + last-project state captured.

**Rollout:**
- Ships unflagged. Every `publish.ps1` invocation gets heartbeat.

### PR 3 — Avalonia init smoke test + CI workflow

**Goal:** Bug 24 (Avalonia InvalidCastException at MainWindow.axaml:34) would have been caught at PR time. Class of bug, not just this instance.

**Files:**
- `tests/SuavoAgent.Setup.Tests/AvaloniaInitSmokeTest.cs` (NEW)
- `tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj` (MODIFIED: ensure xunit + Avalonia.Headless package refs)
- `.github/workflows/setup-smoke.yml` (NEW)

**Workflow trigger filter** — Bug 24's actual cause was an Avalonia package version bump in `Directory.Build.props`, NOT a source change in `src/SuavoAgent.Setup/**`. The trigger MUST fire on all of:

```yaml
on:
  pull_request:
    paths:
      - 'src/SuavoAgent.Setup/**'
      - 'src/SuavoAgent.Setup.csproj'
      - 'Directory.Build.props'      # catches Avalonia package bumps (Bug 24's class)
      - 'global.json'                # .NET SDK version changes
      - 'Directory.Packages.props'   # if CentralPackageManagement is adopted later
```

**Test contents:**

```csharp
[Fact]
public async Task App_Initializes_Without_Exception()
{
    var appBuilder = AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var (app, mainWindow) = await Task.Run(() =>
    {
        appBuilder.SetupWithoutStarting();
        var window = new MainWindow();
        window.Show();
        return (appBuilder.Instance, window);
    }, cts.Token);

    Assert.NotNull(mainWindow);
    Assert.False(mainWindow.IsLoaded == false, "MainWindow failed to load within 5s");
}
```

**Test plan:**
- Test runs in CI matrix on `windows-latest` runner.
- Pre-merge negative test: revert the Bug 24 fix locally, run smoke test, assert it fails with the InvalidCastException class.
- Asserts construction + initialization completes within 5s. Anything longer than 5s is also a failure (catches hang regressions).

**Rollout:**
- Required check on every PR matching the trigger filter above. Block merge on failure.

### PR 4 — `SuavoAgent.Diagnostics` library + ruleset-v1 + Sentry SDK wrap

**Goal:** every crash on Queen produces a structured fingerprint posted to Sentry with SDK-side PHI scrub. Local fallback unchanged. Calibrated against Bug 22 / Bug 23 / Bug 24 reproductions.

**Wire-ordering invariant — REQUIRED for correctness:**

`Wire.AttachUnhandledHooks(...)` MUST be the **literal first statement** of `Program.Main` (or the file-scoped top-level equivalent) in every entry point. Calls AFTER any framework `Configure()` call (Avalonia, Microsoft.Extensions.Hosting builder, etc.) leak Bug-24-class crashes during framework initialization. The pattern per entry point:

```csharp
// Core / Broker / Helper / Watchdog (Microsoft.Extensions.Hosting entry points)
// File-scoped top-level program — line 1, before ANY using-scope or
// builder.Services call:
SuavoAgent.Diagnostics.Wire.AttachUnhandledHooks(
    component: "Core",
    new WireOptions
    {
        LocalCrashLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "startup-crash.log"),
        LocalJournalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "diagnostics", "events.jsonl"),
        EnableSentry = true,  // gated by appsettings later via ConfigSyncWorker
    });

// ... only NOW: var builder = Host.CreateApplicationBuilder(...);
```

```csharp
// Setup (Avalonia GUI installer)
// Program.cs Main, line 1 — BEFORE BuildAvaloniaApp().StartWithClassicDesktopLifetime():
SuavoAgent.Diagnostics.Wire.AttachUnhandledHooks("Setup", new WireOptions { ... });

// Wrap AppBuilder.Configure in try/catch so XAML-compilation exceptions
// during Configure (Bug 24's class) also reach Wire:
try
{
    BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
}
catch (Exception ex)
{
    SuavoAgent.Diagnostics.Wire.ReportException("Setup", ex, stage: "AvaloniaConfigure");
    throw;  // existing fast-fail behavior preserved
}

// In BuildAvaloniaApp(), the Avalonia dispatcher hook is installed
// inside the builder pipeline so dispatcher-thread exceptions also route:
public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .AfterSetup(_ => SuavoAgent.Diagnostics.AvaloniaDispatcherHook.Install());
```

```powershell
# publish.ps1 — equivalent for the build host
# Exit-code capture is handled by suavo-report-crash.exe (per Open Q §8.7).
# On any non-zero $LASTEXITCODE from dotnet publish:
if ($LASTEXITCODE -ne 0) {
    & "$PSScriptRoot\tools\suavo-report-crash.exe" `
        --component "Publish" `
        --exit-code $LASTEXITCODE `
        --project $proj.Name `
        --recent-stdout $lastStdoutTail `
        --build-sha (git rev-parse HEAD)
    exit 1
}
```

**Files (new):**
- `src/SuavoAgent.Diagnostics/SuavoAgent.Diagnostics.csproj` (refs: Sentry .NET SDK, Microsoft.Extensions.Logging.Abstractions, JsonSchema.Net for ruleset validation)
- `src/SuavoAgent.Diagnostics/Wire.cs` — public surface: `Wire.AttachUnhandledHooks(component, options)` + `Wire.ReportException(component, ex, stage)` + `Wire.ReportInvariant(id, context)` + `Wire.ReportExitCode(component, exitCode, command)`
- `src/SuavoAgent.Diagnostics/FingerprintComputer.cs` — fp-v1 algorithm (with 10ms hard timeout)
- `src/SuavoAgent.Diagnostics/PhiScrubber.cs` — SDK-side scrub (with 10ms hard timeout, fail-closed)
- `src/SuavoAgent.Diagnostics/LocalJournal.cs` — best-effort `events.jsonl` writer (20ms timeout)
- `src/SuavoAgent.Diagnostics/RulesetV1.cs` — loads + validates ruleset-v1.json (build-time schema gate; runtime validates again on load)
- `src/SuavoAgent.Diagnostics/Resources/ruleset-v1.json` — embedded resource. Initial population: Codex's three calibration fingerprints + the canonical invariant catalog seed (only `complete-zero-actuation-log` for Phase 1)
- `src/SuavoAgent.Diagnostics/Resources/ruleset-v1.schema.json` — JSON Schema for ruleset-v1.json. Validated at build time via MSBuild target (see csproj below)
- `src/SuavoAgent.Diagnostics/build/ValidateRulesetSchema.targets` — MSBuild target invoked pre-`EmbedResources` that runs `JsonSchema.Net.Cli` (or equivalent) against `Resources/ruleset-v1.json` using `Resources/ruleset-v1.schema.json`. Build fails on schema violation. Catches malformed JSON at CI time, never at runtime
- `src/SuavoAgent.Diagnostics/SentrySink.cs` — Sentry SDK wrap with `BeforeSend → PhiScrubber → SetFingerprint`. POST is fire-and-forget; never on the crash handler's critical path
- `src/SuavoAgent.Diagnostics/AvaloniaDispatcherHook.cs` — Avalonia `Dispatcher.UIThread.UnhandledException` hook + `Application.Current.OnUnhandledException` if available
- `src/SuavoAgent.Diagnostics/tools/SuavoReportCrash.csproj` + `SuavoReportCrash.cs` — tiny CLI tool that publish.ps1 invokes on non-zero `$LASTEXITCODE`. Captures component + exit code + project + recent stdout + build SHA, runs the same Wire dispatch path
- `tests/SuavoAgent.Diagnostics.Tests/SuavoAgent.Diagnostics.Tests.csproj`
- `tests/SuavoAgent.Diagnostics.Tests/PhiScrubberTests.cs` — 50+ PHI test corpus
- `tests/SuavoAgent.Diagnostics.Tests/FingerprintComputerTests.cs` — determinism + Bug 22/23/24 calibrations + 100-iter p99 < 50ms harness
- `tests/SuavoAgent.Diagnostics.Tests/RulesetV1Tests.cs` — schema validation + signature verification stub
- `tests/SuavoAgent.Diagnostics.Tests/WireOrderingTests.cs` — Roslyn-based test (or simple reflection scan) that asserts every entry point's `Program.Main` has `Wire.AttachUnhandledHooks` as the literal first statement

**Files (modified):**
- `src/SuavoAgent.Core/Program.cs` — replace the existing `WriteCrash` static methods with `Wire.AttachUnhandledHooks("Core", ...)` as Program.Main literal line 1. Local file fallback preserved (Wire's options include `LocalCrashLogPath` pointing to the existing `startup-crash.log` path).
- `src/SuavoAgent.Broker/Program.cs` — same
- `src/SuavoAgent.Helper/Program.cs` — same
- `src/SuavoAgent.Watchdog/Program.cs` — same
- `src/SuavoAgent.Setup/Program.cs` + `src/SuavoAgent.Setup/App.axaml.cs` — same + AppBuilder.Configure wrapped in try/catch as shown above + `AvaloniaDispatcherHook.Install()` called from `BuildAvaloniaApp().AfterSetup(...)`
- `publish.ps1` — on every non-zero `$LASTEXITCODE` invoke `suavo-report-crash.exe` (see PowerShell sample above)
- `Directory.Build.props` — add ProjectReference to SuavoAgent.Diagnostics for the 5 entry points

**Test plan:**
- **FingerprintComputer determinism — production matrix.** CI build matrix runs the determinism harness in the FULL cross-product:

  | Config | Framework | SelfContained | R2R | SingleFile | Why included |
  |---|---|---|---|---|---|
  | `Debug-fxdep` | Debug | false | false | false | Local dev parity |
  | `Release-fxdep` | Release | false | false | false | Release branch parity |
  | `Release-self-contained` | Release | true | false | false | Pre-R2R baseline |
  | `Release-self-contained-R2R` | Release | true | true | false | R2R inlining surface |
  | **`Release-self-contained-R2R-SingleFile`** | **Release** | **true** | **true** | **true** | **PRODUCTION CONFIG (publish.ps1) — determinism MUST hold here** |

  Same synthetic crash → identical fingerprint across all 5 configurations AND across 100 runs per configuration. PR blocks merge on ANY divergence. The last row is what `publish.ps1` ships; if fingerprints aren't stable there, the mesh is broken in production.

- **PHI scrubber corpus:** 50+ test inputs covering patient names, DOBs in 6 formats, Rx#, SSN, NPI (Luhn-checked), file paths with usernames, SQL text with literal values, UIA window titles. Assert post-scrub event contains NONE of the test PHI patterns.
- **ruleset-v1.json schema validation:** build-time MSBuild target asserts `Resources/ruleset-v1.json` validates against `Resources/ruleset-v1.schema.json`. CI fails on schema violation. Runtime test also re-validates after embedded-resource load (defense-in-depth).
- **Bug 22 calibration:** synthetic `Win32Exception(5)` thrown from a `[DllImport]` interop wrapper → fingerprint matches `Helper | win32 | System.ComponentModel.Win32Exception | native_error=5 | operation=SendInput/actuation-token`.
- **Bug 23 calibration:** call `Wire.ReportInvariant("complete-zero-actuation-log", ...)` → fingerprint matches `Core | invariant_violation | <empty> | <empty> | WorkflowExecutor.Complete | complete-zero-actuation-log`.
- **Bug 24 calibration:** synthetic Avalonia XAML compilation `InvalidCastException` from `MainWindow.InitializeComponent` → fingerprint matches `Setup | managed_exception | System.InvalidCastException | <empty> | Avalonia.MainWindow.InitializeComponent | resource=MainWindow.axaml`.
- **Wire-ordering invariant:** `WireOrderingTests.cs` scans every entry-point Program.cs and asserts the literal first executable statement is `Wire.AttachUnhandledHooks`. Fails CI if a future PR re-orders.
- **Local-first time bound:** 100-iter p99 < 50ms harness across Bug 22 / 23 / 24 synthetic reproductions. Asserts `PhiScrubber.Sanitize` < 10ms, `FingerprintComputer.Compute` < 10ms, `LocalJournal.Append` < 20ms, total local dispatch < 50ms BEFORE any Sentry I/O begins.
- **Sentry sink with mocked endpoint:** `BeforeSend` invoked, scrubbed event payload + fingerprint tag verified. POST is fire-and-forget (asserted by checking the SDK queue depth, not by awaiting POST).
- **Local fallback:** Sentry endpoint unreachable → `events.jsonl` continues, no startup blocking, no thrown exception out of `Wire.AttachUnhandledHooks`, no crash-handler delay (POST queued, returns immediately).
- **`Wire()` idempotency:** calling twice doesn't double-register handlers.
- **Per-entry-point integration:** for each of 5 entry points, integration test wires + crashes + verifies emit + verifies local fallback + verifies handler-local-time SLA.

**Rollout:**
- Ships behind `Diagnostics:Enabled` config flag, **default `false`** in `appsettings.json`.
- After PR merges to `main`, flip flag via `ConfigSyncWorker` cloud config push to Queen. Verify 48h on Queen with synthetic crashes + watch Sentry events.
- After 48h Queen burn-in, flip default to `true` in `appsettings.json` in a follow-up PR.

---

## 8. Open questions (for Codex review + /plan-eng-review + /plan-ceo-review)

1. **Universal Wire() coverage — recursion safety.** The wire-ordering invariant in §7 PR 4 mandates `Wire.AttachUnhandledHooks` as `Program.Main` literal line 1, and Setup wraps AppBuilder.Configure in try/catch. Open for Codex: what if the handler ITSELF throws (e.g., PhiScrubber regex panic, Sentry SDK init AppDomain.UnhandledException recursion)? `WireOrderingTests.cs` catches static ordering but doesn't prove recursion safety. Recommend: nested try/catch inside `Wire.ReportException` with a SentinelException class that explicit-noops on second-entry.

2. **SDK-side scrub completeness.** PHI scrubber test corpus has 50+ patterns. Are there pharmacy-specific patterns we're missing — NDC codes, DEA numbers, prescriber NPIs, insurance member IDs, PioneerRx-specific identifiers? Should ruleset-v1.json's `patient_names_seed` array be populated for Phase 1 (Queen has no patients), or wait for Nadim?

3. **Offline fingerprint compute.** Verify: agent computes fingerprint + writes local `events.jsonl` with no network, no Sentry, no Supabase. Sentry SDK init failure path is `try`/`catch` at the wire boundary — does this hold for every Wire() invocation pattern?

4. **Ruleset signing for Phase 2.** Signed rules cached with version + expiry + rollback + max output length + fail-closed behavior to last-known-good ruleset. Phase 2 — but the public-key pinning **must be baked into Phase 1's SuavoAgent.Diagnostics library** because adding the signing public key in Phase 2 requires re-shipping all agent versions. Which keyring? (Suggest: reuse `cmd-signing-key.pub.pem` from `publish.ps1`'s ECDsa key generation.)

5. **Cloud alias merging.** When a ruleset version bump re-groups Bug 22 and Bug 22-variant-2 into one fingerprint, cloud merges via `alias_of`. Does this require raw stack frames cloud-side to verify the merge is correct, or can we trust the agent-emitted canonical fingerprint? (Codex's answer: no raw frames needed; agent emits canonical, cloud just maintains alias graph.)

6. **supabase Vault vs AWS KMS for diagnostic bundles.** Phase 1: Vault is sufficient (bundles are pre-scrubbed at edge). Phase 3 multi-tenant: is KMS-grade key rotation + audited decrypts load-bearing, or does the pre-scrub + RLS + audit_log composition already meet the bar? Codex specifically wanted: "audited decrypt events per row read" comparison.

7. **`suavo-report-crash.exe` self-defense.** PR 4 now bakes in approach (b) — a tiny CLI tool publish.ps1 invokes on non-zero `$LASTEXITCODE`. Open for Codex: how does the tool itself avoid the same CLR fast-fail class it's meant to capture (Bug 24 recursion)? Recommend: tool ships as native AOT (`PublishAot=true`) to eliminate the CLR-fast-fail surface entirely, OR uses `IL2CPP`-style fallback. Also: should the tool also capture the publish.ps1 PID + parent PowerShell version + Yubikey presence (preflight echoes) so the diagnostic includes the developer's environment shape?

8. **ruleset-v1.json initial population strategy.** Phase 1 ships with the 3 calibration crashes (Bug 22/23/24). Should we also seed a larger set of "things we expect to see" — Win32 errors 5/1326/1450 with named operations, common COM HRESULT classes, .NET fast-fail codes — or wait for real Queen signal? Risk of over-seeding: fingerprints we never see in the wild bloat the rule cache + create false confidence in coverage. Recommendation: ship Phase 1 with ONLY the 3 calibration crashes; expand from real signal.

---

## 9. References

- `docs/self-healing/phase-a-architecture.md` — cloud-side audit substrate (A1 silent-agent alarm, A2 audit_events hash chain, A3 crash-log aggregation → folded into Mesh, A4 version drift, A5 bootstrap --probe, A6 attestation).
- `docs/watchdog.md` — 4-tier self-healing watchdog. Phase 5 of Mesh self-healing builds on Watchdog tier-3 + tier-4 + Mission Loop.
- `src/SuavoAgent.Core/Program.cs:17-51` — existing `WriteCrash` pattern that Mesh PR 4 supersedes (with local fallback preserved).
- `publish.ps1` — current 218-line publish script that PR 1 + PR 2 modify.
- Memory: `MISSION-square-level-ecosystem.md` — Pillar #4 SuavoAgent invisible bridge. Mesh is the observability layer of Pillar #4.
- Memory: `suavoagent-product-vision-2026-05-01.md` — HIPAA-first computer-use agent for pharmacy. Mesh is Track 1 (Reliability) infrastructure.
- Codex consultation 2026-05-12 (agent id `ae50cc4e6a3f18e11`) — architectural review of A/B/C/D options; selection of D; fp-v1 algorithm specification; PHI minimum-necessary reasoning citing HHS BAA guidance + Sentry SDK scrubbing docs.

## 10. Change log

- **2026-05-12 v0.1** — Initial draft. Locked architecture: Option D (agent-edge compute + cloud-distributed signed rules). 4 PRs in Phase 1. Codex review consulted on architectural choice + fingerprint algorithm.
- **2026-05-12 v0.2** — /plan-eng-review SMALL CHANGE pass (4 issues + 1 critical gap):
  - §4 added Performance contract: < 50ms p99 local-first SLA before any Sentry I/O, with per-stage timeouts + 100-iter test harness.
  - §7 PR 3 trigger filter expanded to `Directory.Build.props` + `global.json` + `Directory.Packages.props` (Bug 24's actual cause was a package bump, not a source change in src/SuavoAgent.Setup/**).
  - §7 PR 4 added explicit Wire-ordering code patterns for all 5 entry points + WireOrderingTests.cs static enforcement.
  - §7 PR 4 added `Resources/ruleset-v1.schema.json` + MSBuild target for build-time schema validation. Eliminates a runtime failure class at CI time.
  - §7 PR 4 test plan now pins the full CI build matrix (5 configurations including the production R2R + SingleFile target).
  - §7 PR 4 baked in `suavo-report-crash.exe` tool path for publish.ps1 exit-code capture (was Open Q §8.7).
  - §8 Q1 and Q7 reshaped: ordering and tool baseline now answered; remaining open items are recursion safety + native-AOT consideration.
- Pending: /plan-ceo-review + Codex re-review on §5 encryption + §8 open questions before v1.0 lock.
