# OTA Crash-Loop Guard — Design

Status: decision core SHIPPED (TDD) · wiring is the box-validated follow-up · 2026-06-04 · Self-Healing Wave-0 P0 (highest-severity gap).

## Problem

`SelfUpdater.SwapBinaries` (`SelfUpdater.cs:352-414`) swaps the Core+Broker+Helper set atomically and leaves `.old` files, BUT there is **no post-restart health check before cutover and no recovery if the new set crash-loops**. A bad OTA takes a box — or, fleet-wide, every box — dark with no automated recovery (operator must RDP). This is the single biggest fleet-risk multiplier.

## Decision core (SHIPPED)

`src/SuavoAgent.Core/Cloud/OtaProbation.cs` — a pure, fully-unit-tested evaluator. The new set runs **on probation**: it auto-commits once it proves healthy, and auto-downgrades to the last-good `.old` set if it crash-loops.

```
Evaluate(OtaProbationState) →
  !HasPendingProbation            → NoProbation     (normal path)
  HealthConfirmed                 → Commit          (reap .old, clear flag)  ← healthy ALWAYS wins
  BootsSinceSwap > MaxBoots, old  → Downgrade       (restore .old, restart)
  BootsSinceSwap > MaxBoots, !old → EscalateNoRollback (can't self-heal, alert, stay up)
  else                            → Continue        (on trial, keep running)
```

`BootsSinceSwap` includes the current start; boundary is strictly-greater so the Max-th boot still gets a full attempt (never pre-empt a boot that might come up healthy — Codex Q4). Healthy precedence is checked before crash-count so a set that finally boots healthy commits.

## Wiring plan (FOLLOW-UP — must be validated on Mina's live box; mocked tests miss DOA)

1. **At successful swap** (in `CheckPendingUpdate`, before `Exit(1)`): atomically write `ProgramData\SuavoAgent\ota-probation.json` = `{version, swappedAtUtc, bootsSinceSwap:0}`. The `.old` set is already left by `SwapBinaries`.
2. **Early in `Program.cs` startup** (after the bootstrap block, before DI/risky init — Core has crashable work after this: CredentialProtector/DPAPI, DB init, RuleEngine/Brain probe): if the probation flag exists → increment `bootsSinceSwap` and **persist atomically (temp+rename)**, then `Evaluate`. **Treat a persist failure as fatal/escalated, not Continue** (Codex Q1 — else a crash before the write loses the count and the loop never converges).
3. **Downgrade** → restore the `.old` set **all-or-nothing with its own rollback**, mirroring `SwapBinaries:394-413` (Codex Q3 — a partial restore is the exact Core/Broker/Helper version skew the swap guard refuses). Because Broker is a **separate service** and Helper may **orphan** (their `.exe` are locked while running — Codex Q2), Core cannot reverse their files in-process: restore on disk, then **queue a Watchdog restart** (`WatchdogRestartRequestWriter`) so the old set is actually loaded, and let Broker cycle + reconcile the Helper (`SessionWatcher` orphan-kill).
4. **Late Commit** fires from a **strong** healthy milestone — first cloud heartbeat **ACK** + IPC-up, not mere process start (Codex Q4) — → reap `.old` + delete the probation flag.
5. **[P1] Gate `CleanupOldBinaries`** (called unconditionally at `HeartbeatWorker.RunAsync` startup before any ACK — Codex Q6) behind `NoProbation`: it currently **deletes the rollback `.old` set during probation**, making downgrade impossible. It must only reap on the Commit path.
6. **Stale-flag reconciliation** (Codex Q5): a probation flag keyed by `{version, swappedAtUtc}` older than N hours with neither a confirmed-healthy nor a crash-loop self-resolves (clear flag) — but stale cleanup must **never** delete an active probation's `.old` set.

## Test plan (follow-up)

Decision core: done (11 tests, every row + boundary). Wiring: integration tests for atomic flag write + crash-before-persist; all-or-nothing downgrade restore + partial-failure rollback; `CleanupOldBinaries` no-op under probation. Then **live-box**: stage a deliberately-crashing Core OTA, confirm auto-downgrade to `.old` after the boot budget, agent recovers online — sandbox/pilot box only.
