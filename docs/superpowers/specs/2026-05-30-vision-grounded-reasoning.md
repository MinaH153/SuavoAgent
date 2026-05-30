# W4b — Vision-Grounded Reasoning (Design Spec)

> **Status:** DRAFT — pending Codex design review before TDD.
> **Branch:** `feat/w4b-vision-grounded-reasoning`
> **Program:** W4 "Truly-smart" agent, chunk P0b — the flagship (`~/.claude/plans/distributed-churning-spark.md`).
> **Vision:** `[[suavoagent-product-vision-2026-05-01]]` — Track 5 agentic control; reasoning must ground on what's literally on screen, **app-agnostically**.

## HIPAA invariants (non-negotiable)
1. **Screen text is PHI-scrubbed at the extraction boundary (Helper)** before it becomes a `ScreenFrame` (`VisionContracts.cs:11-12,27`). This chunk consumes only `ScreenFrame`; it never touches raw pixels.
2. **Prompt budget + per-field caps preserved.** Vision adds bounded, capped fields to the Tier-2 prompt (existing <500-token discipline, `InferencePromptBuilder` Max* caps). No uncapped screen text.
3. **No screen content to cloud in shadow mode.** The shadow consumer runs Tier-1 + Tier-2 (local) only; it MUST NOT auto-escalate screen-derived context to Tier-3 (cloud Claude).
4. **Master gate `Vision.Enabled` (default OFF).** Every new path is dormant unless a pilot opts in. Shadow-only: the consumer never executes actions.

## 1. Goal
Make reasoning **ground on the live screen**: feed `ScreenFrame` OCR text + visual elements into `RuleContext` and the Tier-2 prompt, add a visual rule predicate, and prove it end-to-end with a **shadow-mode** consumer that reasons over real captures and logs the would-be decision (no actions). App-agnostic: rules/Tier-2 key off screen text, not hardcoded PioneerRx.

## 2. Confirmed current reality (verified)
- `ScreenFrame` (`Contracts/Vision/VisionContracts.cs:20`) is rich: `TextRegions` (OCR text + `Rect` bounds + confidence) + `Elements` (`VisualElement`: Role, Name, bounds, confidence). PHI-scrubbed.
- **Vision is captured then discarded:** `VisionCaptureWorker.TickAsync` (`Core/Workers/VisionCaptureWorker.cs:200-213`) sends `CaptureScreen` IPC, the Helper returns a serialized `ScreenFrame` + `storageId`, but the worker reads **only `storageId`** (`:203`) for audit — the TextRegions/Elements are dropped.
- **No production flow reasons over screen:** `RuleContext` (`Contracts/Reasoning/RuleContracts.cs:185`) carries `VisibleElements` (name set), flags, fingerprints — **no screen text, no geometry**. Its only prod construction sites are `PricingBrainEvaluator` (pricing flags, screen-empty) and `BrainStartupProbe` (synthetic). `InferencePromptBuilder.BuildUserMessage` (`Core/Reasoning/InferencePromptBuilder.cs:60`) serializes RuleContext fields only.
- `TieredBrain.DecideAsync(ctx, allowedTier2Actions, shadowMode, ct)` (`:92`) already supports `shadowMode`; Tier-2 input is `InferenceRequest { Context, EscalationReason, AllowedActions=SafeDefault, Timeout }`.

**Consequence:** the new vision fields need a *consumer* or they're scaffolding (the W4a lesson). The honest consumer = a shadow-mode reasoner fed by the existing capture worker.

## 3. Non-goals / scope boundary
- **NOT** executing actions. Shadow-mode only (observe + log the would-be `BrainDecision`). Autonomous action is a later chunk gated on pilot evidence.
- **NOT** changing the Helper / vision extraction / IPC contract. We consume the `ScreenFrame` the Helper already returns.
- **NOT** Tier-3 cloud on screen content (invariant #3).
- **Visual predicates** start with `TextPresent` only (highest-value, simplest). Spatial predicates ("text near element", "control at (x,y)") are a fast-follow once `TextPresent` proves the pattern.
- No behavior change when `Vision.Enabled=false` (default) — pinned by tests.

## 4. Design

### 4.1 `RuleContext` gains vision (back-compat, default empty) — `Contracts/Reasoning/RuleContracts.cs`
```csharp
// add `using SuavoAgent.Contracts.Vision;`
public sealed record RuleContext
{
    // ...existing fields unchanged...
    /// <summary>PHI-scrubbed OCR text regions from the current screen. Empty when vision is off.</summary>
    public IReadOnlyList<TextRegion> ScreenText { get; init; } = Array.Empty<TextRegion>();
    /// <summary>PHI-scrubbed visual UI elements (role, name, bounds). Empty when vision is off.</summary>
    public IReadOnlyList<VisualElement> ScreenElements { get; init; } = Array.Empty<VisualElement>();
}
```
Existing construction sites/tests compile unchanged (defaults).

### 4.2 Enrich seam — `Core/Reasoning/VisionContextEnricher.cs` (pure, testable)
```csharp
public static class VisionContextEnricher
{
    /// Merge a ScreenFrame into a RuleContext: screen text + elements, and union element
    /// Names into VisibleElements (so existing name-based predicates also benefit).
    public static RuleContext Enrich(RuleContext ctx, ScreenFrame frame);
}
```
This is the single point where vision meets reasoning — keeps `RuleContext` a dumb record.

### 4.3 Tier-2 grounding — `InferencePromptBuilder.BuildUserMessage`
Add to the serialized `state` (capped, PHI already scrubbed, budget-disciplined):
- `screen_text`: top-N `TextRegions` by confidence then area, each `Text` truncated (`MaxScreenTextRegions=16`, `MaxTextLen=120`).
- `screen_elements`: top-N `Elements` as `{role,name}` (`MaxScreenElements=24`, name truncated).
This is the core grounding: the local LLM now sees what's on screen, not just UIA names.

### 4.4 Visual predicate `TextPresent` — `RuleContracts.RulePredicate` + `RuleEngine.PredicateMatches`
```csharp
// RulePredicate: add
public IReadOnlyList<string> TextPresent { get; init; } = Array.Empty<string>();
```
RuleEngine: a predicate's `TextPresent` is satisfied when EVERY listed substring appears (case-insensitive) in some `RuleContext.ScreenText[i].Text`. Empty = no constraint (back-compat). Lets a rule fire on **screen content** app-agnostically.

### 4.5 Shadow consumer — `Core/Reasoning/VisionGroundedShadowReasoner.cs` + wire into `VisionCaptureWorker`
- New `VisionGroundedShadowReasoner.ObserveAsync(ScreenFrame frame, string skillId, ct)`: builds `RuleContext` via `VisionContextEnricher.Enrich(seed, frame)`, calls `TieredBrain.DecideAsync(ctx, allowedTier2Actions: null /*SafeDefault*/, shadowMode: true, ct)`, **never executes**, logs the `BrainDecision` (tier, would-be action, confidence) + records a `vision_shadow_decision` audit entry. Local-only: pass a TieredBrain whose Tier-3 is `NullCloudReasoning` for this path (invariant #3), or gate escalation off in shadow.
- `VisionCaptureWorker`: when `response.Status==200` and `Vision.ShadowReasoning.Enabled` (new sub-flag, default OFF), deserialize the `ScreenFrame` from `response.Data` (already returned by the Helper) and hand it to the reasoner. Capture/audit path otherwise unchanged.

### 4.6 Config — `VisionOptions`
Add `VisionShadowReasoningOptions ShadowReasoning { get; set; } = new()` with `bool Enabled = false`. Master `Vision.Enabled` still required.

## 5. Behavior-preservation & safety
- `Vision.Enabled=false` (default) → `VisionCaptureWorker` early-returns as today; reasoner never constructed. RuleContext vision fields default empty → `InferencePromptBuilder`/`RuleEngine` emit identical output (pinned by tests). **Zero prod impact.**
- Shadow consumer executes nothing and never sends screen content to cloud.

## 6. Test strategy (xUnit; match existing Reasoning tests)
1. `VisionContextEnricherTests` — Enrich populates ScreenText/ScreenElements; unions element Names into VisibleElements; empty frame → unchanged context.
2. `InferencePromptBuilderVisionTests` — with screen data, prompt includes `screen_text`/`screen_elements` capped + truncated; **with empty vision, prompt is byte-identical to today** (back-compat snapshot).
3. `RuleEngineTextPresentTests` — rule with `TextPresent` matches only when all substrings present (case-insensitive); empty `TextPresent` unchanged.
4. `RulePredicateTextPresentTests` (Contracts) — YAML/record round-trip + default empty.
5. `VisionGroundedShadowReasonerTests` — given a ScreenFrame, calls DecideAsync with shadowMode=true, executes no action, logs decision; never invokes cloud.
6. `VisionCaptureWorkerShadowTests` — ShadowReasoning.Enabled gates the reasoner call; disabled → reasoner not invoked (existing capture tests stay green).
7. Full `dotnet test` + solution build green; `Vision.Enabled=false` regression-clean.

## 7. Build sequence (TDD units)
1. `RuleContext` vision fields (Contracts) + `VisionContextEnricher`. Tests 1.
2. `InferencePromptBuilder` screen serialization + caps. Test 2 (incl. back-compat snapshot).
3. `RulePredicate.TextPresent` + `RuleEngine.PredicateMatches`. Tests 3,4.
4. `VisionGroundedShadowReasoner` (local-only, shadow). Test 5.
5. `VisionOptions.ShadowReasoning` + `VisionCaptureWorker` deserialize+observe gate. Test 6.
6. Full build + test; Codex chunk review before merge.

## 8. Risks / questions for Codex (YES/NO)
1. **Consumer real, not theater?** Is the shadow-mode reasoner fed by the existing capture worker a genuine consumer that proves vision-grounded reasoning end-to-end (vs. scaffolding)?
2. **PHI/cloud boundary:** Is routing the (already-scrubbed) ScreenFrame into Tier-2 local + the prompt safe given invariants 1–3, and is "no Tier-3 on screen content in shadow" correctly enforceable (NullCloudReasoning on the shadow path)?
3. **Back-compat:** Will defaulting RuleContext vision fields empty + capping keep `Vision.Enabled=false` byte-identical, and is the prompt-budget cap (16 text / 24 elements) sane for the <500-token discipline?
4. **Seam correctness:** Is `VisionContextEnricher.Enrich` (pure merge) the right seam, or should the worker build the RuleContext directly?
5. **TextPresent semantics:** all-substrings-present (case-insensitive) — correct first visual predicate, or should it be any-of / regex?

## 9. Codex review log
**R1 — APPROVE-WITH-CHANGES.** Q1 (theater) **resolved by verification**: the `CaptureScreen` IPC response carries the full `ScreenFrame` — `IpcMessage.cs:35` contract `{ storageId, frame: ScreenFrame }` + `IpcCommandServer.cs:372` returns `frame = result.Frame`; the worker simply ignores `frame` today → the shadow consumer is a genuine end-to-end consumer. Changes accepted: **Q2** the shadow path constructs `TieredBrain` with `NullCloudReasoning` (Tier-3 escalation at `TieredBrain.cs:183` is NOT suppressed by `shadowMode`, so cloud must be disabled by construction — invariant #3); **Q3** `InferencePromptBuilder` **omits** `screen_text`/`screen_elements` keys when empty (don't serialize empty arrays) → `Vision.Enabled=false` byte-identical; **Q4/Q5** pure enricher seam + all-substrings `TextPresent` confirmed.
