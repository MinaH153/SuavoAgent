# Diagnostic Mesh — Queen-first

**Status:** v1.0 **LOCKED** — Codex re-review of §5 + §8 complete (2026-05-13); 8 items resolved, 0 conflicts with locked decisions. Ready for Phase 1 PR 1 implementation.
**Locked date:** 2026-05-13
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

**No-raw-frames invariant (Codex re-review v1.0):** Metadata token, MVID, raw stack frames, file paths, and line numbers are NOT sent to Sentry and NOT stored in `fingerprint_occurrences.context`. Symbolication uses `git_sha`, `component`, `exception_type`, `stable_error_code`, and `primary_failure_site` only. If deeper source reconstruction is needed for a specific occurrence, it is a one-time local Queen pull through the same PHI scrubber — not a standing cloud payload. The forbidden-keys enforcement at the ingest boundary lives in §6.

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
| Patient name dictionary | Loaded from `ruleset-v1.json` patient_names_seed array (MUST be empty in Phase 1 — Queen has no patients; populated only in Phase 2 from tenant-scoped edge-side detection via PioneerRx data discovery; **Census/global-name dictionaries are forbidden** in untagged free text because their false-positive rate redacts common operational words like `May`, `Will`, `King`, `Price`, `Brown`, `White`, `Long`) | `[PATIENT]` |
| UIA window titles | Any string field tagged `uia_title` in event extra | redacted by allowlist |
| SQL text | Any field tagged `sql_text` | parameter values stripped via sqlparse |

**Pharmacy-specific patterns (Codex re-review 2026-05-13, v1.0 lock):**

| Pattern class | Regex / mechanism | Sample input | Expected redaction |
|---|---|---|---|
| NDC 4-4-2 | `\b\d{4}-\d{4}-\d{2}\b` | `ndc=1234-5678-90` | `ndc=[NDC]` |
| NDC 5-4-2 | `\b\d{5}-\d{4}-\d{2}\b` | `12345-6789-01 dispensed` | `[NDC] dispensed` |
| NDC 5-3-2 | `\b\d{5}-\d{3}-\d{2}\b` | `drug 12345-678-90` | `drug [NDC]` |
| NDC 5-4-1 | `\b\d{5}-\d{4}-\d\b` | `pkg 12345-6789-0` | `pkg [NDC]` |
| NDC unhyphenated with label | `(?i)\b(ndc\|national[_\s-]?drug[_\s-]?code)\s*[:=]\s*"?\d{10,11}"?` | `"NDC":"12345678901"` | `"NDC":"[NDC]"` |
| DEA number | `\b[A-Z]{2}\d{7}\b` + DEA checksum validation (1st+3rd+5th digits, 2*(2nd+4th+6th), sum mod 10 == 7th digit) | `prescriber DEA AB1234563` | `prescriber DEA [DEA]` |
| Prescriber NPI field | `(?i)\b(prescriber_?npi\|provider_?npi\|npi)\s*[:=]\s*"?\d{10}"?` + NPI Luhn | `PrescriberNPI=1234567893` | `PrescriberNPI=[NPI]` |
| BCBS member ID context | `(?i)\b(bcbs\|blue\s*cross\|member_?id)\b.{0,24}\b[A-Z]{3}\d{6,14}\b` | `BCBS member ABC123456789` | `BCBS member [MEMBER_ID]` |
| Aetna member ID context | `(?i)\b(aetna\|member_?id)\b.{0,24}\b[Ww]\d{8,12}\b` | `Aetna ID W123456789` | `Aetna ID [MEMBER_ID]` |
| Cigna member ID context | `(?i)\b(cigna\|member_?id)\b.{0,24}\b[Uu]?\d{9,12}\b` | `Cigna member U123456789` | `Cigna member [MEMBER_ID]` |
| PioneerRx JSON IDs | `(?i)"(RxNumber\|PatientID\|PrescriberID\|PharmacyChainID)"\s*:\s*"?[^",}\s]+"?` | `"PatientID":"P12345"` | `"PatientID":"[PIONEERRX_ID]"` |
| PioneerRx XML IDs | `(?i)<(RxNumber\|PatientID\|PrescriberID\|PharmacyChainID)>[^<]+</\1>` | `<RxNumber>RX1234567</RxNumber>` | `<RxNumber>[PIONEERRX_ID]</RxNumber>` |
| PioneerRx SQL literals | `(?i)\b(RxNumber\|PatientID\|PrescriberID\|PharmacyChainID)\s*=\s*'[^']+'` | `where PatientID='P12345'` | `where PatientID='[PIONEERRX_ID]'` |

All scrub regexes MUST use `RegexOptions.NonBacktracking` to defeat catastrophic-backtracking inputs that could overrun the 10ms `PhiScrubber.Sanitize` budget. On regex compile or match failure, scrubber drops the event extras and proceeds with fingerprint-only signal.

If `PhiScrubber.IsDefinitelyPhi(event)` returns true with high-confidence, the event is **dropped entirely** and only the canonical fingerprint is forwarded with an empty extra. Counter incremented in local telemetry.

The PhiScrubber test corpus (PR 4 test plan) includes >50 known-PHI patterns the scrubber must defeat, plus the 13 pharmacy-specific patterns above. CI fails the PR if any test PHI pattern reaches the post-scrub event.

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

### Emit-rate contract — per-component budget

`Wire.Report` is bounded by a per-component emit budget to protect against runaway invariant-violation loops or pathological re-throw chains. Default budget: **10 events/sec per component** (Core, Broker, Helper, Watchdog, Setup). Beyond the budget, events still write to the local `events.jsonl` journal — the audit trail is intact — but are dropped from the Sentry post path. A `mesh.rate_limited_total` counter increments per dropped Sentry post and is itself surfaced via the next `mesh.heartbeat` (see below).

### Self-heartbeat contract — mesh proves itself alive

`Wire.Heartbeat` fires every 5 minutes from a `MeshHeartbeatWorker` (hosted service registered in each long-running entry point's host builder). The heartbeat emits a synthetic `mesh.heartbeat` event through the **full Wire.Report path** (scrub → fingerprint → local journal → Sentry POST), exercising every link continuously. If Queen's `mesh.heartbeat` signal is absent from Sentry for > 30 minutes, Phase A's A1 silent-agent alarm pages Joshua. The mesh's liveness is a continuously-verified claim, not a hopeful assumption.

The heartbeat payload includes: `component`, agent version, time-since-process-start, `mesh.events_emitted_total` counter, `mesh.rate_limited_total` counter, `mesh.sentry_post_success_rate` gauge, `mesh.local_journal_write_rate` gauge, ruleset version in use, whether cached cloud ruleset signature is verified.

### Failure-mode contract

| Failure | Mesh behavior | Operator visibility |
|---|---|---|
| Sentry unreachable | Local `events.jsonl` continues. Fingerprint still computed. On reconnect, last N events flush to Sentry via background queue. Crash handler never blocked. | `mesh.sentry_post_success_rate` drops in next heartbeat; cloud alert if persistent. |
| Sentry SDK init fails | `Wire()` swallows + logs. Existing `WriteCrash` continues. **Process startup is not blocked by diagnostics init.** | Local logs show diagnostics-disabled state; first heartbeat tags `sentry_init=false`. |
| `Diagnostics:Dsn` missing from appsettings / env | Preflight (PR 1) fails fast at publish time. If somehow shipped without DSN, Wire init logs warning + falls back to local-journal-only mode. | Preflight rejects ship; cloud alert if Queen heartbeat lacks Sentry tag. |
| Runaway emit loop (>10 events/sec/component) | `mesh.rate_limited_total` increments; excess events local-journal-only, dropped from Sentry path. | Operator sees rate-limit counter on heartbeat. |
| `mesh.heartbeat` absent from Sentry for > 30min | Phase A A1 silent-agent alarm fires + pages Joshua via Twilio + Slack. | High-severity alert. |
| ruleset-v1.json malformed (embedded resource corrupted) | Fail-closed: fingerprint compute uses ruleset-v0 (hardcoded minimal fallback). Telemetry counter incremented. | Local logs show ruleset-fallback state. **CI MSBuild schema-validation gate prevents this from shipping** (§7 PR 4). |
| Phase 2 cloud rule push delivers invalid ruleset | Agent rejects via embedded public-key signature verification. Falls back to last-known-good cached ruleset. **Never** falls back to ruleset-v0 unless cache is also corrupt. | Phase 2 surface, not Phase 1. |
| Crash handler exceeds 50ms p99 budget | Per-stage timeouts above kick in; handler completes in bounded time at cost of degraded fidelity. | CI regression alarm. |

---

## 5. Encryption scheme — Vault for Phase 1, KMS for Phase 3 (v1.0 locked)

### Decision: supabase Vault for Phase 1; defer AWS KMS to Phase 3

### Reasoning

Phase 1 is single-tenant (Queen-only). Per-tenant encryption keys for diagnostic bundles are a Phase 3 requirement (when Nadim onboards as tenant 1). For Phase 1:

- The diagnostic bundle that needs encryption is the **scrubbed canonical event + occurrence context** (env vars filtered, recent log tail, build SHA). PHI is already removed at the edge by the SDK-side `PhiScrubber` (§3). Encryption-at-rest is defense-in-depth, **not** the primary safeguard.
- supabase Vault is already in use elsewhere in the codebase. Adoption cost is zero.
- AWS KMS adds: AWS account setup, IAM design, cross-region replication thinking, KMS keyring management. All of which is **Phase A's territory** (A2 hash-chained audit substrate, A6 attestation). Adding AWS account work to Mesh Phase 1 conflicts with the "wrap existing infra" principle in this doc's §1.

### When KMS becomes load-bearing

Phase 3 (Nadim onboards). At that point Phase A's KMS keyring is likely set up, and the mesh can lean on the same keyring for per-tenant diagnostic-bundle keys. The migration from Vault → KMS is a one-shot re-encrypt of the existing fingerprint_occurrences rows; impact is low.

### Codex re-review v1.0 — Phase 3 direction LOCKED

Phase 3 (Nadim onboards as tenant 1; multi-tenant pre-scrubbed bundles in `fingerprint_occurrences.context`) MUST migrate diagnostic-bundle envelope encryption from Supabase Vault to AWS KMS before multi-tenant bundles land. The composition (pre-scrub at edge + RLS + `audit_log` row-write) is sufficient for §164.502(b) + §164.312(b) + §164.312(e) at Queen-only single-tenant scale, but is NOT sufficient at multi-tenant scale because (a) "pre-scrubbed" is not "non-sensitive" — residual PHI risk remains, (b) app-level read audit is bypassable by service-role/direct-SQL paths, and (c) §164.312(a)(2)(iv) encryption-at-rest is addressable, not optional, once multi-tenant residual-ePHI risk exists.

| Guarantee | Supabase Vault | AWS KMS |
|---|---|---|
| Key rotation | Gap for automated per-key rotation in this design; requires manual re-key/re-encrypt discipline. | Customer-managed symmetric keys support automatic/on-demand rotation; old key material remains usable for decrypt. |
| Decrypt audit | Gap at cryptographic boundary; app can write `audit_log`, but Vault view decrypts do not create per-row KMS-style decrypt events. | `Decrypt`/key-use API calls are CloudTrail-auditable; ties decrypts to tenant, role, request ID. |
| BYOK | Gap for this Phase 1/2 design; not a first-class tenant BYOK control. | Imported key material and external/custom key-store patterns available when needed. |
| HSM-backed | Supabase-managed backend key separation; acceptable for Phase 1 but opaque at per-key operation level. | KMS keys protected by AWS KMS HSM boundary and managed key lifecycle controls. |
| Cross-region replication | Database backup/replication preserves Vault ciphertext, but key/region semantics are Supabase-managed. | Multi-Region KMS keys support interoperable decrypt across configured Regions. |
| IAM granularity | Postgres grants/RLS/service-role discipline; weak separation between app read and decrypt authority. | Key policies, IAM, grants, aliases, and per-principal decrypt permissions. |
| BAA status | Supabase BAA-covered platform, acceptable if project/account is under BAA and data is configured accordingly. | AWS KMS is HIPAA-eligible under AWS BAA; customer still owns configuration. |

**Directional answer:** Phase 3 should migrate from Vault to KMS because per-decrypt audit and automated key rotation become load-bearing once multi-tenant diagnostic bundles exist. Migration mechanics: one-shot re-encrypt of `fingerprint_occurrences.context` rows under new KMS-backed envelope keys at Nadim onboard; coordinate with Phase A A2 hash-chain anchoring + A6 attestation so audit-chain and encrypt-chain rotate together.

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

### `context` JSONB allowlist + forbidden keys (Codex re-review v1.0)

`fingerprint_occurrences.context` JSONB schema is allowlisted, not denylisted, to enforce the no-raw-frames invariant from §3:

**Allowed keys at ingest:** `git_sha`, `ruleset_version`, `agent_version`, `signal_kind`, `agent_session_id`, scrubbed counter values (`mesh.events_emitted_total`, `mesh.rate_limited_total`, `mesh.sentry_post_success_rate`), timeout flags (`phi_scrub_timed_out`, `fingerprint_timed_out`, `local_journal_timed_out`), circuit-breaker state (`sentry_circuit_open`, `last_sentry_post_at`), calibration tags (`bug_class`).

**Forbidden keys at ingest (Edge Function REJECTS occurrence on presence):** `raw_stack`, `stacktrace`, `frames`, `file_path`, `line`, `column`, `mvid`, `metadata_token`, `locals`, `arguments`, `sql_text_raw`, `uia_title_raw`, `request_body`, `response_body`, `connection_string`, `auth_token`.

Enforcement: the Sentry-webhook → fingerprint_occurrences Edge Function (Phase 2) MUST reject any payload containing a forbidden key with a 4xx and surface the violation to `mesh.context_schema_violations_total`. Phase 1 agents are responsible for NOT emitting these keys in the first place via `SuavoAgent.Diagnostics.SentrySink.BeforeSend`; the Phase 2 ingest check is defense-in-depth against future regressions.

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
| **Sentry DSN present** (either `$env:SUAVO_SENTRY_DSN` set OR `Diagnostics:Dsn` present in `appsettings.json`) | "Set `$env:SUAVO_SENTRY_DSN` to the BAA-covered Sentry DSN, or add `Diagnostics:Dsn` to appsettings.json. Mesh ships in local-journal-only mode without it." |
| Git tree clean (no uncommitted changes in src/) | "Commit or stash before publishing." |
| Build cache fresh (`obj/`, `bin/` older than HEAD) | "Run with -CleanFirst or `dotnet clean` if last build > 1d ago." |

**Output — green/red ASCII summary card (D3 delight):** preflight ends with a printed card showing every check + actionable fix-it suggestion. Format:

```
+========================================================+
|         QUEEN SHIP PREFLIGHT — 2026-05-13              |
+========================================================+
| [PASS] PowerShell 7.6.0                                |
| [PASS] .NET SDK 8.0.404                                |
| [PASS] Yubikey present (vendor=Yubico)                 |
| [FAIL] EV cert: $env:SUAVO_CERT_THUMBPRINT not set     |
|   → fix: $env:SUAVO_CERT_THUMBPRINT = '<sha1>'         |
| [PASS] SmartCard service running                       |
| [PASS] Sentry DSN present (env)                        |
| [PASS] Git tree clean                                  |
| [WARN] Build cache 3d old                              |
|   → fix: rerun with -CleanFirst                        |
+========================================================+
| RESULT: 1 FAIL, 1 WARN, 6 PASS — publish aborted.      |
+========================================================+
```

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
- `src/SuavoAgent.Diagnostics/SuavoAgent.Diagnostics.csproj` (refs: Sentry .NET SDK, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Hosting.Abstractions for `MeshHeartbeatWorker`, JsonSchema.Net for ruleset validation)
- `src/SuavoAgent.Diagnostics/Wire.cs` — public surface: `Wire.AttachUnhandledHooks(component, options)` + `Wire.ReportException(component, ex, stage)` + `Wire.ReportInvariant(id, context)` + `Wire.ReportExitCode(component, exitCode, command)` + `Wire.Heartbeat(component)` + `Wire.Report(signal)` (internal unified dispatch)
- `src/SuavoAgent.Diagnostics/WireOptions.cs` — `LocalCrashLogPath`, `LocalJournalPath`, `Dsn` (read from `IConfiguration` "Diagnostics:Dsn"), `EnableSentry`, `EmitBudgetPerSecond` (default 10), `HeartbeatInterval` (default 5min)
- `src/SuavoAgent.Diagnostics/EmitBudget.cs` — per-component token-bucket rate limiter; on exhaustion increments `mesh.rate_limited_total` and routes event to local journal only, dropping Sentry POST
- `src/SuavoAgent.Diagnostics/MeshHeartbeatWorker.cs` — `BackgroundService` registered in Core/Broker/Helper/Watchdog hosts; invokes `Wire.Heartbeat(component)` every 5min, exercising full Wire path. Setup uses an Avalonia `DispatcherTimer` equivalent
- `src/SuavoAgent.Diagnostics/FingerprintComputer.cs` — fp-v1 algorithm (with 10ms hard timeout)
- `src/SuavoAgent.Diagnostics/PhiScrubber.cs` — SDK-side scrub (with 10ms hard timeout, fail-closed)
- `src/SuavoAgent.Diagnostics/LocalJournal.cs` — best-effort `events.jsonl` writer (20ms timeout)
- `src/SuavoAgent.Diagnostics/RulesetV1.cs` — loads + validates ruleset-v1.json (build-time schema gate; runtime validates again on load). Also reads `calibration_fingerprints` mapping for D2 (Sentry tag `bug-class:bug-22|23|24` set when computed fingerprint matches a calibration entry)
- `src/SuavoAgent.Diagnostics/Resources/ruleset-v1.json` — embedded resource. Initial population: Codex's three calibration fingerprints + the canonical invariant catalog seed (only `complete-zero-actuation-log` for Phase 1) + `calibration_fingerprints` mapping (bug-class → fingerprint) for D2 operator tag
- `src/SuavoAgent.Diagnostics/Resources/ruleset-v1.schema.json` — JSON Schema for ruleset-v1.json. Validated at build time via MSBuild target (see csproj below)
- `src/SuavoAgent.Diagnostics/build/ValidateRulesetSchema.targets` — MSBuild target invoked pre-`EmbedResources` that runs `JsonSchema.Net.Cli` (pinned) against `Resources/ruleset-v1.json` using `Resources/ruleset-v1.schema.json`. Build fails on schema violation. Catches malformed JSON at CI time, never at runtime
- `src/SuavoAgent.Diagnostics/SentrySink.cs` — Sentry SDK wrap with `BeforeSend → PhiScrubber → SetFingerprint → SetTag('git_sha', BuildContext.GitSha) → SetTag('bug-class', match?)`. POST is fire-and-forget; never on the crash handler's critical path. Reads DSN from `IConfiguration`
- `src/SuavoAgent.Diagnostics/BuildContext.cs` — generated at build time via MSBuild target embedding `git rev-parse HEAD` short SHA. Exposed as `BuildContext.GitSha` static. (D1 delight: every fingerprint occurrence ships with git SHA tag.)
- `src/SuavoAgent.Diagnostics/AvaloniaDispatcherHook.cs` — Avalonia `Dispatcher.UIThread.UnhandledException` hook + `Application.Current.OnUnhandledException` if available
- `src/SuavoAgent.Diagnostics/tools/SuavoReportCrash.csproj` + `SuavoReportCrash.cs` — tiny CLI tool that publish.ps1 invokes on non-zero `$LASTEXITCODE`. Captures component + exit code + project + recent stdout + build SHA + parent PowerShell version, runs the same Wire dispatch path
- `tools/tail-events.ps1` — D4 delight: 30-line PowerShell that pretty-prints recent `events.jsonl` entries (last N, default 20) with color-coding by `signal_kind` for on-Queen debugging
- `tests/SuavoAgent.Diagnostics.Tests/SuavoAgent.Diagnostics.Tests.csproj`
- `tests/SuavoAgent.Diagnostics.Tests/PhiScrubberTests.cs` — 50+ PHI test corpus
- `tests/SuavoAgent.Diagnostics.Tests/FingerprintComputerTests.cs` — determinism + Bug 22/23/24 calibrations + 100-iter p99 < 50ms harness
- `tests/SuavoAgent.Diagnostics.Tests/EmitBudgetTests.cs` — sustained-emit harness: 1000 events/sec inputs verified to result in ≤10 Sentry POSTs/sec/component AND ≥990 local journal writes/sec/component AND `mesh.rate_limited_total` incrementing correctly
- `tests/SuavoAgent.Diagnostics.Tests/MeshHeartbeatWorkerTests.cs` — heartbeat fires every 5min; payload includes all required counters + gauges; integration test simulates 30min absence + verifies Phase A A1 alarm trigger (stubbed)
- `tests/SuavoAgent.Diagnostics.Tests/RulesetV1Tests.cs` — schema validation + signature verification stub + calibration_fingerprints mapping correctness
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

**CI gate — `mesh-required-check` consolidated workflow:**

The 4 mesh-Phase-1 PR checks combine into a single GitHub-required check named `mesh-required-check`, triggered on any PR touching:
- `src/SuavoAgent.Diagnostics/**`
- `Resources/ruleset-v1.json` or `Resources/ruleset-v1.schema.json`
- Any of `src/SuavoAgent.{Core,Broker,Helper,Watchdog,Setup}/Program.cs`
- `Directory.Build.props` / `global.json` / `Directory.Packages.props`
- `publish.ps1` / `scripts/Test-QueenShipPreflight.ps1`

The check runs (in parallel where possible): (a) AvaloniaInitSmokeTest (PR 3), (b) FingerprintComputer determinism across the 5-config build matrix (PR 4), (c) Pester preflight unit tests (PR 1), (d) MSBuild ruleset schema validation (PR 4), (e) WireOrderingTests Roslyn scan (PR 4), (f) EmitBudgetTests sustained-load harness (PR 4), (g) PhiScrubber 50+ corpus (PR 4). Block merge on any failure.

**Rollout:**
- Ships behind `Diagnostics:Enabled` config flag, **default `false`** in `appsettings.json`.
- After PR merges to `main`, flip flag via `ConfigSyncWorker` cloud config push to Queen. Verify 48h on Queen with synthetic crashes + watch Sentry events for `mesh.heartbeat` arriving every 5min + `bug-class` tags + `git_sha` tags on calibration reproductions.
- After 48h Queen burn-in, flip default to `true` in `appsettings.json` in a follow-up PR.

---

## 8. Resolved questions (was: open questions; all 8 RESOLVED at v1.0 via Codex re-review 2026-05-13)

1. **Universal Wire() coverage — recursion safety.** The wire-ordering invariant in §7 PR 4 mandates `Wire.AttachUnhandledHooks` as `Program.Main` literal line 1, and Setup wraps AppBuilder.Configure in try/catch. Open for Codex: what if the handler ITSELF throws (e.g., PhiScrubber regex panic, Sentry SDK init AppDomain.UnhandledException recursion)? `WireOrderingTests.cs` catches static ordering but doesn't prove recursion safety. Recommend: nested try/catch inside `Wire.ReportException` with a SentinelException class that explicit-noops on second-entry.

   **RESOLVED v1.0 (Codex re-review 2026-05-13).** Runtime recursion contract — `Wire.Report(...)` MUST maintain both `static readonly AsyncLocal<int> WireDepth` and `[ThreadStatic] static bool FatalHandlerActive`:
   - **Depth 0→1** is the normal path.
   - **Depth 1→2** is allowed only for handler-self-failure and MUST emit a minimal local-journal-only `mesh.wire_handler_failed` marker. No Sentry POST, no scrub on already-scrubbed payload.
   - **Depth ≥3** MUST call `Environment.FailFast("SuavoAgent.Diagnostics Wire recursive failure", originalException)` on unhandled-exception paths; silent drop is forbidden because it hides a fatal observability failure.

   Handler catch ordering (fixed, fail-closed PHI safety first):
   1. `PhiScrubber.Sanitize(...)` first with `RegexOptions.NonBacktracking` and a 10ms timeout; on scrubber failure, drop all extras and continue with fingerprint-only signal.
   2. `FingerprintComputer.Compute(...)`; on failure, use `fp-fallback:{component}:{signal_kind}`.
   3. `LocalJournal.Append(...)` only; no Sentry POST, no flush, no SDK close from inside the catch handler.
   4. Swallow only after the local marker attempt completes or times out.

   **Sentry SDK composition rule:** Wire owns unhandled-exception capture. Sentry must be initialized as a transport sink only, with SDK default `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` hooks **removed or not installed** (Sentry .NET SDK: `SentryOptions.DisableAppDomainUnhandledExceptionCapture()` + `DisableTaskUnobservedTaskExceptionCapture()`). `BeforeSend` may scrub and tag, but MUST NOT throw; if it throws, the event is dropped and `mesh.sentry_before_send_failed_total` increments. Otherwise Wire and Sentry double-capture the same fatal exception and can recurse through `BeforeSend`.

2. **SDK-side scrub completeness.** PHI scrubber test corpus has 50+ patterns. Are there pharmacy-specific patterns we're missing — NDC codes, DEA numbers, prescriber NPIs, insurance member IDs, PioneerRx-specific identifiers? Should ruleset-v1.json's `patient_names_seed` array be populated for Phase 1 (Queen has no patients), or wait for Nadim?

   **RESOLVED v1.0 (Codex re-review 2026-05-13).** 13 pharmacy-specific patterns added to §3 scrub patterns table: 5 NDC formats (4-4-2, 5-4-2, 5-3-2, 5-4-1, unhyphenated-with-label), DEA with 7th-digit checksum validation, prescriber NPI field-context (more reliable than naked-number Luhn alone), BCBS/Aetna/Cigna member-ID field-context, PioneerRx JSON/XML/SQL identifiers (`RxNumber`, `PatientID`, `PrescriberID`, `PharmacyChainID`). All regexes MUST use `RegexOptions.NonBacktracking`. `patient_names_seed` STAYS EMPTY in Phase 1 — Census/global-name dictionaries are forbidden because their false-positive rate redacts operational words (`May`, `Will`, `King`, `Price`, `Brown`, `White`, `Long`). Name dictionaries are Phase 2 only, tenant-scoped, from edge-side patient discovery.

3. **Offline fingerprint compute.** Verify: agent computes fingerprint + writes local `events.jsonl` with no network, no Sentry, no Supabase. Sentry SDK init failure path is `try`/`catch` at the wire boundary — does this hold for every Wire() invocation pattern?

   **RESOLVED v1.0 (Codex re-review 2026-05-13).** Chaos test matrix — all four failures MUST run **simultaneously** asserting handler-local p99 < 50ms, zero synchronous retries, bounded queue depth, uninterrupted `events.jsonl` writes:

   | Scenario | Exact failure mode | Required .NET bound |
   |---|---|---|
   | Sentry TCP-RST mid-POST | Endpoint accepts TCP, then resets during envelope upload; Sentry worker records `HttpRequestException`/`IOException`; event remains local-journaled. | `SentryOptions.ConfigureClient = c => c.Timeout = TimeSpan.FromSeconds(2)`; `MaxQueueItems = 30`; **no `Flush()` on Wire path**. |
   | DNS timeout for `sentry.io` | Resolver never returns; no crash caller waits for DNS. | `CreateHttpMessageHandler` returns `SocketsHttpHandler` with `ConnectTimeout = 250ms` and a `ConnectCallback` wrapping `Dns.GetHostAddressesAsync(...).WaitAsync(250ms)`. |
   | Certificate validation failure | Expired/untrusted root produces TLS auth failure; certificate validation stays enabled. | Same `HttpClient.Timeout = 2s`; failure opens `sentry_circuit` for 60s to prevent retry storms. |
   | Supabase Vault/ruleset cache unreachable | Phase 2 cloud ruleset fetch times out; Phase 1 embedded `ruleset-v1.json` remains authoritative. | Ruleset fetch is never on crash path; cloud ruleset client timeout = 1s; fallback = last-known-good, then embedded v1. |

   **Codex correction (load-bearing):** Sentry .NET SDK has NO public `SentryOptions.SendTimeout` property. Earlier spec wording naming it was wrong. Bound the transport via `ConfigureClient` (sets `HttpClient.Timeout`), `CreateHttpMessageHandler` (sets `SocketsHttpHandler` connect timeout + `ConnectCallback` DNS deadline), `ShutdownTimeout ≤ 250ms`, `FlushTimeout ≤ 250ms`. The `ShutdownTimeout`/`FlushTimeout` knobs are for `suavo-report-crash.exe` only — Wire's unhandled-exception path NEVER calls synchronous flush or close.

4. **Ruleset signing for Phase 2.** Signed rules cached with version + expiry + rollback + max output length + fail-closed behavior to last-known-good ruleset. Phase 2 — but the public-key pinning **must be baked into Phase 1's SuavoAgent.Diagnostics library** because adding the signing public key in Phase 2 requires re-shipping all agent versions. Which keyring? (Suggest: reuse `cmd-signing-key.pub.pem` from `publish.ps1`'s ECDsa key generation.)

   **RESOLVED v1.0 (Codex re-review 2026-05-13) — REVERSED.** Do NOT reuse `cmd-signing-key.pub.pem`. Crypto-domain separation requires a separate ruleset signing key: a command-signing private key controls agent BEHAVIOR; a ruleset-signing key controls diagnostic GROUPING + scrub/fingerprint behavior. One compromise must not own both planes. Phase 1 ships a second ECDsa P-256 keypair:
   - **Private key:** `ruleset-signing-key` in the same protected signing environment as command signing, but **separate material**.
   - **Public key:** embedded resource `src/SuavoAgent.Diagnostics/Resources/ruleset-signing-key.pub.pem`.
   - **Ruleset header fields:** `ruleset_version`, `key_id`, `signed_at`, `expires_at`, `signature_alg = "ECDSA_P256_SHA256"`.

   **Ed25519 rejected** for Phase 1: .NET 8 `System.Security.Cryptography` does not expose a first-class Ed25519 signing API on Windows; adopting it requires NSec or BouncyCastle and adds dependency risk for negligible payload savings on 50-200KB rulesets. P-256 is already in the stack via `publish.ps1`'s ECDsa pattern.

   **Key rotation:** the `key_id` field in the ruleset header is mandatory in Phase 1 so future rotation adds a new embedded public key + accepts both old and new `key_id` during the overlap window — no agent rebuild needed for rotation.

5. **Cloud alias merging.** When a ruleset version bump re-groups Bug 22 and Bug 22-variant-2 into one fingerprint, cloud merges via `alias_of`. Does this require raw stack frames cloud-side to verify the merge is correct, or can we trust the agent-emitted canonical fingerprint? (Codex's answer: no raw frames needed; agent emits canonical, cloud just maintains alias graph.)

   **RESOLVED v1.0 (Codex re-review 2026-05-13) — CONFIRMED.** Prior answer stands. No raw frames cloud-side. Cloud only maintains the `alias_of` graph. Codex caught one dangerous leak: §3's prior wording "Metadata token + MVID are captured for symbolication ... in the occurrence payload" weakened the no-raw-frames invariant for no operational gain. **§3 hardened**: no MVID, no metadata token, no raw frames cloud-side; symbolication uses `git_sha` + `component` + `exception_type` + `stable_error_code` + `primary_failure_site` only. **§6 hardened**: `context` JSONB allowlist + forbidden-keys list enforced at the Phase 2 ingest Edge Function.

6. **supabase Vault vs AWS KMS for diagnostic bundles.** Phase 1: Vault is sufficient (bundles are pre-scrubbed at edge). Phase 3 multi-tenant: is KMS-grade key rotation + audited decrypts load-bearing, or does the pre-scrub + RLS + audit_log composition already meet the bar? Codex specifically wanted: "audited decrypt events per row read" comparison.

   **RESOLVED v1.0 (Codex re-review 2026-05-13).** Phase 3 MUST migrate from Vault to KMS before multi-tenant pre-scrubbed bundles land. Full comparison table + directional answer + migration mechanics in §5. Phase 1 Vault choice stands.

7. **`suavo-report-crash.exe` self-defense.** PR 4 now bakes in approach (b) — a tiny CLI tool publish.ps1 invokes on non-zero `$LASTEXITCODE`. Open for Codex: how does the tool itself avoid the same CLR fast-fail class it's meant to capture (Bug 24 recursion)? Recommend: tool ships as native AOT (`PublishAot=true`) to eliminate the CLR-fast-fail surface entirely, OR uses `IL2CPP`-style fallback. Also: should the tool also capture the publish.ps1 PID + parent PowerShell version + Yubikey presence (preflight echoes) so the diagnostic includes the developer's environment shape?

   **RESOLVED v1.0 (Codex re-review 2026-05-13).** `suavo-report-crash.exe` ships **Native AOT (`PublishAot=true`) AND stdlib-only**. It MUST NOT reference `JsonSchema.Net`, Sentry SDK, Avalonia, or the full `SuavoAgent.Diagnostics` library — dragging schema validation or SDK init into a tiny crash reporter recreates the recursion class it exists to prevent. `JsonSchema.Net` remains build-time-only for `ruleset-v1.json` validation; runtime crash-report schema checks are handwritten: required args present, enum values valid, string lengths bounded, stdout tail capped, JSON written with `System.Text.Json` source generation or `Utf8JsonWriter`.

   **publish.ps1 fallback (preserves original exit code on diagnostic failure):**
   ```powershell
   $originalExit = $LASTEXITCODE
   try {
     & "$PSScriptRoot\tools\suavo-report-crash.exe" --component Publish --exit-code $originalExit ...
   } catch {
     $pid = [System.Diagnostics.Process]::GetCurrentProcess().Id
     Set-Content -Path "$env:TEMP\suavo-crash-report-failed.$pid.txt" `
       -Value "suavo-report-crash failed; originalExit=$originalExit; error=$($_.Exception.GetType().FullName)"
   }
   exit $originalExit
   ```

   **Environment shape captured by the tool (YES to context capture):** publish.ps1 PID, parent PowerShell executable + version, YubiKey-present boolean, SmartCard service state, signing mode, EV cert thumbprint **HASH** (SHA-256 of the thumbprint string, not the raw thumbprint). Capture NO private key material, NO certificate subject if it contains personal identity (some EV certs have personal name in subject CN).

8. **ruleset-v1.json initial population strategy.** Phase 1 ships with the 3 calibration crashes (Bug 22/23/24). Should we also seed a larger set of "things we expect to see" — Win32 errors 5/1326/1450 with named operations, common COM HRESULT classes, .NET fast-fail codes — or wait for real Queen signal? Risk of over-seeding: fingerprints we never see in the wild bloat the rule cache + create false confidence in coverage. Recommendation: ship Phase 1 with ONLY the 3 calibration crashes; expand from real signal.

   **RESOLVED v1.0 (Codex re-review 2026-05-13) — SPLIT.** The "only 3 calibrations" recommendation is half right: avoids fake coverage but creates a coverage cliff. The lock is a structural split into two ruleset categories:

   - **`calibration_fingerprints`** — Bug 22 / Bug 23 / Bug 24 only. These are the ONLY entries that count as covered at launch. They populate `fingerprint_registry` immediately and drive A1/A2 alerts.
   - **`candidate_patterns`** — expected-but-unobserved classes that provide docking points for future real signal without claiming coverage. Phase 1 seeds: Win32 `native_error` 5, 1326, 1450; process exits `0xC0000005`, `0xE0434352`, `0x80004003`; Windows service error `1067`; and 5-7 known Avalonia/PioneerRx exception classes.

   **Required fields per candidate entry:** `source: "seed"`, `confidence: "low"`, `first_observed_at: null`, `observed_count: 0`, `counts_as_coverage: false`.

   **Dashboard enforcement:** candidate patterns are hidden by default, excluded from coverage percentages, excluded from A1/A2 alert thresholds, and promoted to observed only after a real local Wire event matches them. `confidence: low` alone is insufficient; without `counts_as_coverage: false` and `first_observed_at: null`, seeded rules create stale dashboard entries that recreate the false-confidence problem one layer down.

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
- **2026-05-13 v0.3** — /plan-ceo-review EXPANSION mode (3 issues + 4 silent delights + 1 TODO):
  - §3 (now folded into §4 contracts): Sentry DSN provenance locked to `Diagnostics:Dsn` in appsettings or `$env:SUAVO_SENTRY_DSN` env var. PR 1 preflight verifies presence. Closes the only CRITICAL GAP from CEO threat model.
  - §4 added Emit-rate contract: per-component `EmitBudget` default 10 events/sec. Excess events still local-journal but dropped from Sentry path. `mesh.rate_limited_total` counter surfaced in heartbeat.
  - §4 added Self-heartbeat contract: `MeshHeartbeatWorker` fires every 5min through full Wire path. Phase A A1 silent-agent alarm pages Joshua if Queen heartbeat absent > 30min. Mesh proves itself alive continuously.
  - §7 PR 1 preflight: added Sentry DSN check + green/red ASCII summary card output (D3 delight).
  - §7 PR 4 file list expanded: WireOptions, EmitBudget.cs token bucket, MeshHeartbeatWorker BackgroundService, BuildContext.cs (git SHA at build time, D1 delight), Sentry tag `bug-class:bug-22|23|24` via calibration_fingerprints mapping (D2 delight), `tools/tail-events.ps1` (D4 delight). New tests: EmitBudgetTests, MeshHeartbeatWorkerTests.
  - §7 PR 4 + PR 3 consolidated into `mesh-required-check` GitHub-required CI check spanning Avalonia smoke + fingerprint determinism + Pester preflight + ruleset schema + WireOrderingTests + EmitBudgetTests + PhiScrubber corpus.
  - §10 EXPANSION-mode trajectory locked: 10x version = mesh as agent self-knowledge layer (operations + corrections + cross-tenant pattern transplant + customer-facing transparency). Phase 1 architecture (Option D + signed rules + per-tenant keys) already supports it. Reversibility 4/5.
  - 1 TODO added: TODO-MESH-4 Avalonia smoke screenshot artifact (D5 deferred — exceeds 30-min delight threshold).
- **2026-05-13 v0.4** — Codex re-review of §5 encryption + §8 open questions complete. 8 items resolved with concrete spec edits, 0 conflicts with locked decisions:
  - §3 PHI scrub table extended with 13 pharmacy-specific patterns (NDC 4 variants, DEA + checksum, prescriber NPI field-context, BCBS/Aetna/Cigna member IDs, PioneerRx JSON/XML/SQL identifiers). All regexes use `RegexOptions.NonBacktracking`. `patient_names_seed` STAYS EMPTY in Phase 1 — Census/global-name dictionaries forbidden (operational false-positive rate).
  - §3 no-raw-frames invariant hardened: no MVID, no metadata token, no raw frames cloud-side. Symbolication via `git_sha` + `component` + `exception_type` + `stable_error_code` + `primary_failure_site` only.
  - §5 Phase 3 direction LOCKED: Vault → KMS migration before multi-tenant pre-scrubbed bundles. Full comparison table + §164.312(a)(2)(iv) reasoning + migration mechanics (one-shot re-encrypt + coordinate with Phase A A2/A6).
  - §6 `fingerprint_occurrences.context` JSONB allowlist + forbidden-keys enforcement at Phase 2 ingest Edge Function (`raw_stack`, `stacktrace`, `frames`, `file_path`, `line`, `mvid`, `metadata_token`, `locals`, `arguments`, `sql_text_raw`, `uia_title_raw`, `request_body`, `response_body`, `connection_string`, `auth_token`).
  - §8.1 Wire recursion contract: `AsyncLocal<int> WireDepth` + `[ThreadStatic] FatalHandlerActive`. Depth 0→1 normal, 1→2 degraded local-only, ≥3 `Environment.FailFast`. Handler catch ordering fixed (PhiScrubber → Fingerprint → LocalJournal → swallow). Sentry SDK composition: Wire owns unhandled hooks, SDK's `DisableAppDomainUnhandledExceptionCapture()` + `DisableTaskUnobservedTaskExceptionCapture()` mandatory.
  - §8.3 chaos test matrix: 4 simultaneous failures (TCP-RST, DNS timeout, cert validation, Vault unreachable). Codex caught load-bearing error: Sentry .NET SDK has NO public `SentryOptions.SendTimeout`; use `ConfigureClient` + `CreateHttpMessageHandler` + `SocketsHttpHandler.ConnectTimeout=250ms` + `ConnectCallback` DNS deadline.
  - §8.4 ruleset signing key REVERSED — do NOT reuse `cmd-signing-key.pub.pem`. Crypto-domain separation mandates separate ECDsa P-256 keypair (`ruleset-signing-key.pub.pem` embedded resource); Ed25519 rejected (.NET 8 stdlib lacks first-class Ed25519 signing on Windows). `key_id` field added to ruleset header for rotation without agent rebuild.
  - §8.7 `suavo-report-crash.exe` ships Native AOT + stdlib-only. NO JsonSchema.Net/Sentry SDK/Avalonia at runtime; build-time-only schema validation. publish.ps1 fallback preserves original exit code on diagnostic failure + writes `%TEMP%\suavo-crash-report-failed.<pid>.txt` marker. Captures publish PID + parent PS version + YubiKey + SmartCard + signing mode + **EV cert thumbprint hash** (SHA-256 of thumbprint, not raw).
  - §8.8 ruleset population SPLIT: `calibration_fingerprints` (Bug 22/23/24 only, counts as coverage) + `candidate_patterns` (5-7 Win32/.NET/Avalonia/PioneerRx seeds with `confidence: low` + `counts_as_coverage: false` + `first_observed_at: null` + `observed_count: 0`). Dashboard excludes candidates from coverage % + alert thresholds.
- **2026-05-13 v1.0 LOCKED** — All §8 questions resolved, all body sections (§3, §5, §6) updated with Codex's hardened constraints. Status header bumped from draft to LOCKED. Ready for Phase 1 PR 1 implementation per [[feedback-codex-review-every-spec]] standing rule.
