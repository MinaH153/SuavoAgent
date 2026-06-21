# SuavoAgent QA — Critical Review Backlog (2026-06-21, wave 1)

Source: 4 parallel adversarial reviewers (security/HIPAA, UiaFirst pricing, reliability/self-recovery, concurrency/IPC). Each verified mitigations before flagging; strong verified-safe baseline (OutboundPhiGuard, EncryptedScreenStore, signed-command path, IPC serialization, pricing defensive posture).

Status legend: ⬜ open · 🔧 in progress · ✅ fixed (commit)

## ✅ ALL 5 CRITICALS RESOLVED (branch `fix/qa-critical-wave1`, opus security-reviewed — no must-fix)
- **C1** `59ef431` — LookupTimeout 30s→90s (clears the Helper ~42s budget, below the 5min wedge).
- **C2** `59ef431` — `IpcConnected` = event pipe AND command pipe not stranded (`ConsecutiveStrandFailures`, excludes benign headless/idle → no false-unhealthy). No contract change.
- **C3** `5cf2bd7` — Watchdog `RepairBackoff` (5min) on NotInstalled; +1 test.
- **C4** `f70c60d` — `PioneerRxConfig.AllowedBaaScopeTags` (default empty → fail-closed) + tested `IsBaaScopeAuthorized` gate; 7 tests. No live caller (verbs are scaffolds) so fail-closed bricks nothing.
- **C5** `f70c60d` — removed both relax-flag accept branches; unreadable/empty path always rejects (legit Core via SID check; original flap covered by SID check; flag inert + warned).

Follow-ons surfaced by the security review (added to Important/queued below): C1 dead-Helper worst case ~90s→270s before abort (bounded/resumable) + the `ct`-propagation into `_pricing.Lookup`; C4 per-verb scope granularity (currently any allowlisted tag authorizes any verb — defense-in-depth behind the cloud's per-verb check).

## 🔴 CRITICAL — fix order C1→C5

- 🔧 **C1 — Pricing timeout disagreement** (`PricingJobRunner.cs:27` `LookupTimeout=30s`). 30s < workflow worst-case UIA budget (~42s) → slow-but-working PioneerRx aborted mid-lookup, miscounted `HelperUnreachable`, job aborts after 3 → false failures on the sale. Helper `_pricing.Lookup` is synchronous (no `ct`) → orphaned UIA on a dead pipe. **Fix:** raise `LookupTimeout` above the workflow max (≥60s); thread `ct` into `_pricing.Lookup` (follow-on). Confidence High.
- 🔧 **C2 — Health composite lies "healthy" while command pipe stranded** (`HealthSignalsProvider.cs:44-45`, `HealthCompositeCalculator.cs:40`). `HelperAttached`/`IpcConnected` read the *event* pipe, not the command pipe → cloud green while the agent can't act. **Fix:** add `ActuationReadinessSnapshot.Ready` as a 4th health component. Confidence High.
- 🔧 **C3 — Watchdog repair thrash** (`WatchdogDecision.cs:67-74`). `NotInstalled` → `EscalateRepair` every 15s, no cooldown → `bootstrap.ps1` in a tight loop. **Fix:** add `LastRepairAttemptAt` + repair backoff (~5min). Confidence High.
- ⬜ **C4 — BaaScopeTag never enforced** (`PioneerRxCommandHandler.cs:53-83`). Class doc promises "deviations fail closed" but no code reads the scope tag → any boundary-crossing caller drives any PioneerRx click/type regardless of BAA scope. **Fix:** validate `BaaScopeTag` against the enabled-scope allowlist; reject on mismatch. §164.502(b)/§164.504(e). Confidence High. (precedence-1 security)
- ⬜ **C5 — Relax-flag bypass** (`IpcCommandServer.cs:879-898`). `RelaxIpcClientPathValidation=true` → same-user impostor named `SuavoAgent.Core` (self-tightened DACL → unreadable image path) bypasses verification → drives PHI actuation. **Fix:** delete the relax hatch, or require Broker attestation even in relax mode. §164.312(a)(1). Confidence High. (precedence-1 security; only with non-default flag)

## 🟠 IMPORTANT
- Pricing: job reports `Completed` when PioneerRx closed (500 ERROR rows, brain-eval off by default) · stale `MainWindow` → correctness miss as not-found · grid cell/header column-alignment assumption (needs on-box confirm).
- Reliability: `NativeLibProvisioner._downloadStarted` static blocks brain re-provisioning after a partial download · `HelperSelfHeal` silent after 4 restarts (no escalation) · brain `IsReady=true` lies when idle-unloaded (telemetry — expose `IsLoaded`).
- Concurrency: oversize (>64KB) IPC response strands the caller for its full timeout (emit a bounded `response_too_large` error frame).
- Security: PioneerRx `type` PHI-guard tension (blocks real writeback / drifted denylist) · `capture_screen` audit by-convention only (latent — close when first caller is wired).

## 🟡 MINOR
Cert-pin optional + non-standard SPKI encoding · DPAPI `LocalMachine` scope (verify file ACL) · presence glow race · RemoteRepair deleted on failure · OTA-probation skips valid rollback on IncrementBoots failure · OTA stale `Watchdog.new` swap · Watchdog hang-epoch reset on OTA cycle · pricing comment/circuit-breaker/NDC-substring nits · schema-canary no caching.

## Follow-on review waves (queued)
Setup/Broker internals · the learning/moat engine (harvest, template learning, replay) · build/CI hygiene · test-coverage gaps.
