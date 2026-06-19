# Presence Reasoning Bubble — Phase 2 Design Spec

- **Date:** 2026-06-19
- **Status:** Draft for review
- **Parent:** `2026-06-19-agent-presence-layer-design.md` §4 Phase 2
- **Builds on:** Phase 1 (`feat/presence-cursor-phase1`, PR #239) — `PresenceController`, `WindowsPresenceRenderer`, `PresencePreferenceStore`.

---

## 1. Goal

A **cursor-anchored reasoning bubble** that narrates what the agent is doing, one line at a time, as it acts — the "thinking out loud" beat none of Claude-in-Chrome / Codex / Comet ship co-located with the cursor (parent spec §2.3).

Per the locked **hybrid** decision: **deterministic step labels are always-on and free** (not LLM output → no latency, never truncated); the on-device-LLM rationale is a **separate, collapsible, on-demand lane** (parent spec §2.3 #3, the Codex "decouple effort from narration" lesson). This phase ships the deterministic half end-to-end and lays the seam for the LLM half.

---

## 2. Scope decision (the one real Phase 2 fork)

Narration label sources, cheapest → richest:
1. **Helper-side action labels** — `click_by_label` etc. already carry `Label` (e.g. "7", "Verify Rx") **into the Helper**, where the bubble renders. Zero cross-process work. → **Phase 2 (this).**
2. **Core workflow-step `Description`** — richer ("Sum the order total"), but lives in `WorkflowExecutor` (Core) behind a Core→Helper IPC hop (`presence.narrate`). → **Phase 2.5.**
3. **On-device-LLM rationale** — `ILocalInference.ChatAsync` (Core, ~seconds on a 2-core box). Fills the "Explain more" lane on demand. → **Phase 3 with the mode machine.**

**Phase 2 = source (1): a self-contained Helper-side deterministic bubble.** It gives the visible win immediately (a bubble narrating every click on the calc demo) with no Core changes and no new latency. Sources (2)/(3) slot into the same bubble component later. This is staging, not scope-cutting — the bubble *component* is built once; only its text *source* gets richer.

---

## 3. Design

### Components (new, Helper-side)
- **`BubbleNarration`** (record) — `{ string Text, string Tone }`. A small composer turns an action into one line: `Compose(actionKind, label)` → e.g. `"Clicking 7"`, `"Typing into Quick Search"`. Pure, unit-tested.
- **`IBubbleRenderer`** + **`WindowsBubbleRenderer`** — a SECOND Win32 layered, click-through, topmost, no-activate window (sibling of the cursor overlay, sized for text). Draws a **rounded "card"** (charcoal glass, gold left-accent, cream text) with one line. Same discipline as the cursor renderer: command queue on an STA thread, **idle = no repaint**, auto-fade after a dwell, follows the cursor.
- **`PresenceController.Narrate(actionKind, label)`** (new method) — **PHI-vets first** (`PhiPatternGuard.ContainsPotentialPhi`): if the label trips a PHI pattern, render the **verb/action kind only** (e.g. "Clicking…"), never the raw text. Then drives the bubble renderer, anchored a fixed offset from the cursor's current rest point. Gated by `prefs.BubbleVisible` + the same `Active` gate as the cursor (cosmetic-never-gates).
- Bubble **follows the cursor**: on `MoveTo`, re-anchor the current bubble to the new position so it trails the glide.

### Narration seam (Helper)
The Helper's actuation handler deserializes `ClickByLabelRequest` (has `.Label`). Emit `presenceController.Narrate("click", req.Label)` there — one call per action, where both the action kind and its label are in scope, **before** the click fires (intent before action). Verb-name fallback when no label.

### PHI invariant (hard)
- Bubble text is composed from action kind + a **PHI-vetted** label. `ContainsPotentialPhi == true` → drop the label, render action-kind only.
- The composer never echoes screen-scraped content (only the command's own intent label).
- This reuses the exact guard `SendInputDriver` already applies to typed text (`PhiPatternGuard`).

### Stall narration (parent spec §2.3 #4)
When an actuation retries/fails (SendInputDriver already logs retries / "giving up"), emit a stall line — **"Element not found, retrying 2/3"** — so the bubble is never silent during failure (Comet's documented worst failure). Sourced from the retry path; tone = `Confirm` (wine) on give-up.

### Preferences (already in Phase 1)
`BubbleVisible` (on/off) and `BubbleVerbosity` (off / labels / labels+llm) already exist in `PresencePreferences`. Phase 2 honors `BubbleVisible` + treats `labels` verbosity as "deterministic only" (the default); `labels+llm` reserved for Phase 3.

---

## 4. Out of scope (Phase 2)
- Core workflow-step `Description` narration + `presence.narrate` IPC command → Phase 2.5.
- On-device-LLM rationale / "Explain more" expansion → Phase 3.
- The collapsible step-LOG rail (multi-line history) → Phase 3 (with the dashboard mirror).
- DirectComposition (the bubble uses the same GDI approach as the cursor; migrates with it in Phase 1.5).

---

## 5. Verification (on Joshua's box, no PioneerRx)
- `run_workflow calc_verified` → each click shows a bubble ("Clicking 7", "Clicking +", "Clicking =") that **trails the gliding cursor** and fades.
- Bubble honors `BubbleVisible=false` (no bubble; cursor still glides; agent still acts).
- Ctrl+Alt+H hide suppresses cursor + bubble together.
- Feed a label containing a PHI pattern (e.g. an SSN-shaped string) → bubble shows the action kind only, never the digits.

---

## 6. Success criteria
- Deterministic bubble narrates each Helper actuation, PHI-vetted, anchored to + trailing the cursor, idle-no-repaint.
- Zero Core changes; zero new actuation latency; cosmetic-never-gates preserved.
- Unit-tested: the composer, the PHI-vet branch, the controller gating.
