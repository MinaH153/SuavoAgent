# General Agentic Loop Orchestrator — Design

Status: DESIGN (pre-build) · 2026-06-04 · Phase 2 of the general-agentic-loop build.

## Goal

Make SuavoAgent navigate **any** desktop app from a natural-language objective via the loop:

```
NL objective → PERCEIVE (scrubbed screen) → REASON (objective + memory → ONE grounded action)
            → ACT (one action) → VERIFY (postcondition) → ACCUMULATE (working memory)
            → repeat until Done | BudgetExhausted | NoProgress | PostconditionFailed | GateDenied | Cancelled
```

Today the act path is PioneerRx-workflow-shaped and driven by structured commands. The primitives exist (perception, reasoning, actuation, safety). **We build the orchestrator ON TOP of the existing seams — we do not rebuild the primitives.**

Maps (verdicts): Perception `mostly-general-small-gap` · Reasoning `mostly-general-small-gap` · Actuation `scoped-needs-real-work` · Command+Safety `scoped-needs-real-work`.

## Architecture — ports & adapters

A new **pure** `AgenticLoopRunner` in `SuavoAgent.Core/Agentic/` depends ONLY on four injected interfaces + two pure helpers. It holds **zero** OS / UI / IPC / cloud handles, so the whole loop (control flow, budget, stuck-detection, context accumulation, termination) is unit-testable with fakes. **This pure core is what we TDD first.** Production behavior comes from thin adapters over the mapped seams.

### The four ports

```csharp
namespace SuavoAgent.Core.Agentic;

public interface IPerceiver
{
    // Synchronous on-demand scrubbed snapshot. null = capture failed (fail-closed).
    Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct);
}

public interface IReasoner
{
    // Objective + bounded memory + action whitelist → exactly ONE next action.
    // allowCloud reflects the per-run cloud sub-budget; the result reports UsedCloud
    // (consumed a round-trip) and CloudDenied (needed cloud but was over budget).
    Task<ReasonResult> ReasonNextAsync(
        AgentObjective objective, WorkingMemory memory,
        IReadOnlySet<string> allowedActions, bool allowCloud, CancellationToken ct);

    // Did the last action achieve its local postcondition? (after = settled frame)
    Task<VerifyResult> VerifyPostconditionAsync(
        AgentObjective objective, NextAction lastAction,
        PerceivedScreen before, PerceivedScreen after, bool allowCloud, CancellationToken ct);
}
// ReasonResult(NextAction Action, bool UsedCloud, bool CloudDenied=false)
// VerifyResult(PostconditionVerdict Verdict, bool UsedCloud, bool CloudDenied=false)

public interface IActuator
{
    // Execute exactly ONE action. Inherits VerbDispatcher's fail-closed 7-step flow.
    Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct);
}

public interface ISafetyGate
{
    SafetyVerdict Preflight(AgentObjective objective);          // before perceive
    SafetyVerdict GateAction(NextAction action, AgentObjective objective);  // before every act
    bool AssertScrubbed(PerceivedScreen screen);               // structural egress invariant
}
```

### Two pure helpers

- `ContextAccumulator` — immutable `WorkingMemory` builder. `Record(...)` returns a NEW `WorkingMemory` each step (never mutates), bounding history to the last N `StepRecord`s. `IsNoProgress(memory, window)` detects K consecutive steps with an identical `(screenHash, chosenAction)` pair — this is the **stuck signal that replaces blind retry** (a reasoner that keeps re-picking the same action on an unchanged screen trips it).
- `LoopBudget` — pure step-count + wall-clock deadline + **per-run cloud-call sub-budget** guard. `TryConsumeCloudCall()` decrements a per-run cloud allowance (default well under the 50-calls/pharmacy/day limit, e.g. 12); when exhausted the loop switches to local-verify-only and, if a cloud reason is then required, terminates `BudgetExhausted` rather than starving the pharmacy's other agent functions.

### The orchestrator

```csharp
public interface IAgenticLoopRunner
{
    Task<AgenticLoopResult> RunAsync(
        AgentObjective objective, AgenticLoopOptions options, CancellationToken ct);
}
```

## The loop (one iteration)

> **Codex-review resolved (2026-06-04, 5 × P1):** denial is deterministic (no implicit re-reason); verify settles before judging (no double-execute); there is **no blind retry** (NotMet re-reasons, bounded by `IsNoProgress`); cloud calls have a per-run sub-budget; multi-action decisions are rejected, not silently truncated.

1. **ct check** — `ct.IsCancellationRequested` at top of every iteration → `Cancelled` (instant abort).
2. **Preflight** — `ISafetyGate.Preflight`: kill-switch (`ActuationGate.Enabled`), pause-window (`PausedUntilUtc`, bumped by `KeyboardCategoryHook` on PMS keystroke), never-blind-on-live-PMS (`PricingExecutorMode` awareness). Deny → terminate `GateDenied` + `Escalate`. (This reads the Core-side `ActuationGate.Snapshot()` as a **fail-fast only** — the authoritative enforcement is Helper-side `ActuationGate.CheckOrReject` at act time, which closes the kill/pause-trips-mid-reason window.)
3. **Perceive** — `IPerceiver.PerceiveAsync` → `PerceivedScreen?`. null → no-perception strike (escalate after threshold). `AssertScrubbed` false → fail-closed, frame never reaches reasoning.
4. **Accumulate** — `ContextAccumulator.Record` folds the scrubbed screen into a new bounded `WorkingMemory`.
5. **Reason** — `IReasoner.ReasonNextAsync(objective, memory, allowedActions)` → exactly ONE `NextAction`. (Adapter threads a new optional `userObjective` onto `InferenceRequest` → `TieredBrain.DecideAsync`.) `Done` → terminate `Done`. **Multi-action guard:** `TieredBrain` returns `IReadOnlyList<RuleActionSpec>`; the adapter maps a decision with exactly one *actuating* action (meta `Log` entries ignored). A decision carrying **>1 actuating action** is NOT silently truncated to index 0 — it maps to `NextAction.Escalate` (fail-closed; the orchestrator drives one action per step). Costs a cloud call against the sub-budget.
6. **GateAction** — `ISafetyGate.GateAction`: action-class whitelist + `TaskAutonomyLedger.MayRunUnsupervised(taskKey, pharmacyId, EnableTaskAutonomy)`. **Deterministic outcomes** (no implicit re-reason): (a) **Allow** → act for real; (b) **AllowDryRun** (supervised mode / `options.DryRun` / earned-but-not-enabled) → act dispatched with `dryRun:true` (audited, NOT executed) then the run terminates `GateDenied`+`Escalate` for operator approval; (c) **Deny** → terminate `GateDenied`+`Escalate`. A destructive action never executes unattended unless autonomy is earned AND `EnableTaskAutonomy`.
7. **Act** — `IActuator.ActAsync` → `VerbDispatcher.DispatchAsync` (7-step fail-closed flow) → `IActuationGateway` → Helper under `ActuationGate.CheckOrReject` (rejects mid-act on kill/pause/disabled — the authoritative gate).
8. **Settle + Verify** — re-perceive in a **settle loop**: poll `PerceiveAsync` until the after-screen content-hash is stable for K consecutive reads OR a settle-deadline elapses (defeats the async-UI repaint race), THEN `IReasoner.VerifyPostconditionAsync(before, settledAfter)`. `Met` → record success, continue. `NotMet`/`Ambiguous` (Ambiguous treated as NotMet, fail-closed) → record the failed outcome and **loop back to Reason** — the reasoner re-grounds on the settled screen and chooses the next action. **There is no blind retry of the same action by the orchestrator** (non-idempotent `Type`/`PressKey`/`Click` must never be auto-re-executed). A reasoner that re-picks the same action on an unchanged screen is caught by `IsNoProgress` → stuck.
9. **Stuck-check** — `LoopBudget` (MaxSteps default 25 + wall-clock deadline + cloud-call sub-budget) + `IsNoProgress` (K=3 identical `(screenHash, chosenAction)`). Any terminal-stuck reason → emit final `NextAction.Escalate` → operator-approval / cloud-ACK path. The loop never silently spins and never blind-retries into oblivion.

## NL entry (the command seam)

New signed command in the existing dispatch switch — `HeartbeatWorker.ProcessSignedCommandAsync` (`src/SuavoAgent.Core/Workers/HeartbeatWorker.cs:728-812`), after the `update_selector` case:

```
{ command: "navigate_app",
  data: { objective: "<NL goal>", taskKey: "<stable-id>", maxSteps?, deadlineSeconds?, dryRun? } }
```

`HandleNavigateAppAsync` mirrors `HandleRunWorkflowAsync` (line 1060): dedicated `_navigationSemaphore.WaitAsync(0)` (single-threaded, fail-closed reject if busy), ephemeral `MissionCharter`, linked `_activeNavigationCts` (so `abort_navigation` cancels instantly like `HandleAbortWorkflowAsync:1227`), resolve DI `IAgenticLoopRunner`, await `RunAsync`, ACK the `AgenticLoopResult`. ECDSA + persistent-nonce verification (lines 710-726) gate the command BEFORE the handler — only the `objective` FIELD is free-form; the agent never executes unsigned input.

## Production adapters (after pure core is green)

| Port | Adapter | Wraps |
|---|---|---|
| IPerceiver | `HelperPerceiver` | Core→Helper `capture_screen` IPC → `ScreenCaptureController.CaptureAndExtractAsync` (Helper/Vision/ScreenCaptureController.cs:43); already PHI-scrubbed by `PhiScrubbingExtractor` at the factory |
| IReasoner | `TieredBrainReasoner` | `VisionContextEnricher.Enrich` + `TieredBrain.DecideAsync` (Reasoning/TieredBrain.cs:92) with new `userObjective`; maps a **single-actuating-action** `BrainDecision` → `NextAction` (lossless for that one action: full `Parameters` + `VerifyAfter`). `BrainDecision.Actions` is `IReadOnlyList`; >1 actuating action → `NextAction.Escalate` (never silently take index 0) |
| IActuator | `VerbActuator` | `VerbRegistry.Resolve` + `VerbDispatcher.DispatchAsync` (ActionGrammar/VerbDispatcher.cs:37) |
| ISafetyGate | `CompositeSafetyGate` | `ActuationGate.Snapshot()` + `TaskAutonomyLedger.MayRunUnsupervised` (Autonomy/TaskAutonomyLedger.cs:50) + `PricingExecutorMode` |

Backward-compatible primitive changes: `InferenceRequest` gains optional `string? UserObjective = null`; `TieredBrain.DecideAsync` gains optional `string? userObjective = null`; `InferencePromptBuilder` emits an `objective` key only when non-null. Existing scoped callers (`PricingBrainEvaluator`) untouched.

## Safety integration (precedence-1, every claim wired to a seam)

- **verify-postcondition** — re-perceive + `VerifyPostconditionAsync` after every act; `Ambiguous`=`NotMet`.
- **instant cancel-on-input** — `Preflight` reads `PausedUntilUtc` (bumped by `KeyboardCategoryHook`); Helper `ActuationGate.CheckOrReject` stops in-flight acts; `ct` checked per-iteration.
- **kill-switch** — `HotkeyKillSwitch` trips `Enabled=false`; `Preflight` sees it → `GateDenied`; concurrent act → `kill_switch_tripped`.
- **fail-closed** — null perceive, BrainDecision miss, deny verdict, `NotMet`, dry-run-by-default all resolve to NOT acting.
- **PHI-scrub egress** — perception scrubbed at factory; reasoner cloud path scrubs via `PhiScrubber.ScrubText`; `AssertScrubbed` structural invariant; `WorkingMemory` holds only scrubbed text + content hashes (never raw pixels).
- **never-blind-on-live-PMS** — `Preflight` is `PricingExecutorMode`-aware; refuses live `UiaFirst` blind drive unless explicitly enabled; default `navigate_app` = dry-run + supervised; first pilots sandbox-app only.
- **M3 autonomy** — `GateAction` calls `MayRunUnsupervised` before EVERY destructive action; per-task-earned AND `EnableTaskAutonomy` both required.

## TDD plan (pure-unit, fakes for all four ports — build FIRST)

1. `HappyPath_PostconditionMetOnFirstAction_TerminatesDone`
2. `StepBudgetExhausted_NeverMeetsPostcondition_EscalatesBudgetExhausted`
3. `SafetyGateDenied_OnPreflight_HaltsImmediately_NoPerceiveNoAct`
4. `SafetyGateDenied_OnGateAction_TerminatesGateDenied_NoActuation_NoReReason` *(Q1: deterministic deny)*
5. `GateActionAllowDryRun_DispatchesDryRunThenEscalates_NeverExecutesForReal` *(Q1: supervised path)*
6. `CancelMidLoop_TokenCancelledAfterFirstAct_StopsBeforeNextReason`
7. `PostconditionNotMet_ReReasons_DoesNotBlindRetrySameAction` *(Q4: no double-execute)*
8. `NonIdempotentAction_NeverAutoReExecutedByOrchestrator` *(Q4: Type/PressKey/Click never re-fired by the loop)*
9. `SettleLoop_WaitsForStableScreenHash_BeforeVerify` *(Q3: defeats async-UI repaint race)*
10. `SettleDeadline_Elapses_VerifiesOnLastFrame_NoHang` *(Q3: bounded settle)*
11. `NoProgress_SameScreenHashAndChosenAction_TerminatesNoProgress` *(stuck replaces retry)*
12. `MultiActuatingActionDecision_IsRejected_Escalates_NotTruncatedToIndex0` *(Q7)*
13. `CloudCallSubBudgetExhausted_FallsBackToLocalVerify_ThenBudgetExhausted` *(Q6: no pharmacy starvation)*
14. `PhiNeverEgressesUnscrubbed_AssertScrubbedGuardsEveryFrame`
15. `PerceiveReturnsNull_CountsAsStrike_EscalatesAfterThreshold`
16. `ContextAccumulator_IsImmutable_AndBoundsHistory`
17. `ReasonerReceivesObjectiveAndPriorActions_InWorkingMemory`

## Open questions (do NOT block pure-core TDD; resolve before production adapters)

1. **Skill selection** — a general objective has no `skillId`. Deterministic keyword router vs skill-picker pre-call?
2. **taskKey derivation** — per-objective-hash is too granular to earn an autonomy streak; per-app or per-objective-template?
3. **Cloud `/api/agent/reason` contract** — does it accept `objective` + `priorActions`, or need a versioned bump? (Verify in the Suavo cloud repo.)
4. **Postcondition authoring** — cloud reasoner each step vs one-time goal-decomposition into checkable sub-postconditions?
5. **Perceiver staleness** — does production `IPerceiver` need a freshness deadline distinct from the 1000ms rate-limit floor?
6. **Semaphore** — share the workflow semaphore (mutually exclusive with `run_workflow`) or a dedicated `_navigationSemaphore`?

## Risks (top)

- **CRITICAL** — live PioneerRx `UiaFirst` blind drive. Mitigation: `Preflight` refuses live UiaFirst unless explicitly enabled; default dry-run + supervised; sandbox-only first pilots; M3 gate.
- **MEDIUM** — `userObjective` cloud-contract drift silently degrades to scoped. Mitigation: nullable/optional default; cloud accept-and-ignore unknown keys FIRST; real-agent integration test (mocked tests miss DOA).
- **MEDIUM** — 2N cloud calls/step vs 50-calls/pharmacy/day + Vercel cost. Mitigation: prefer local `VerifyAfter` predicates; cloud-verify only when no predicate; low MaxSteps + deadline; count against rate limit, fail-closed.
