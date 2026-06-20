# Presence Mode Machine + FSD Glow — Phase 3 Design Spec

- **Date:** 2026-06-19
- **Status:** Draft
- **Parent:** `2026-06-19-agent-presence-layer-design.md` §4 Phase 3
- **Builds on:** Phase 1 (#239) + Phase 2 (#240) — `PresenceController`, the renderers, `PresencePreferenceStore`.

---

## 1. Goal

The Tesla-FSD beat: a breathing **screen-edge glow** while the agent is driving, that **flips to a distinct "watching" state when the human takes the wheel.** Plus a `PresenceMode` state machine that tints the whole presence layer (cursor + bubble + glow) by mode:

- **Driving** — agent is acting → gold glow, gold cursor/bubble.
- **Observing** — human took over (moved mouse / typed) → sage glow, agent paused (already enforced by `ActuationGate`), "watching" affect.
- **Idle** — neither for a while → glow off.

No competitor ships this (parent spec §2.2) — it's a pure differentiator. **Non-obscuring** by construction (edge gradient, never full-screen dim — the verified anti-pattern, parent spec §2.1).

## 2. Scope (Phase 3)
**In:** the Driving/Observing/Idle mode machine, the FSD edge glow, mode→tone cohesion across cursor+bubble+glow, and auto-takeover→Observing wired off `UserInputObserver`.
**Out (deferred):** `AwaitingConfirm` mode + explicit "Take Over / Resume" controls + confirm-on-consequence dialogs → **Phase 3b**; observe-mode *learning* substance (feed the trajectory harvester) → **Phase 4**; the LLM "Explain more" lane → still Phase 3-LLM.

## 3. Design

### Mode state machine (in `PresenceController`)
- Two timestamps (interlocked): `_lastAgentActivityUtc` (bumped by `MoveTo`/`Reticle`/`Click`/`Narrate`) and `_lastHumanInputUtc` (bumped by a new `OnHumanInput()`).
- A **pure** evaluator `PresenceModeLogic.Evaluate(lastAgent, lastHuman, now, drivingWindow, observeWindow)` → `PresenceMode`:
  - human input within `observeWindow` AND more recent than agent → **Observing**;
  - else agent activity within `drivingWindow` → **Driving**;
  - else **Idle**. (Unit-tested.)
- A ~1s `Timer` re-evaluates and, on change, applies the mode: drives the glow (`Show(tone)` / `Hide`) and sets the **active tone** (gold/sage) used by the cursor + bubble. Human-takeover flips Driving→Observing mid-drive on the next tick.
- `OnHumanInput()` is **cheap** (an interlocked timestamp write only) — safe to call from the low-level-hook path; the transition work happens on the Timer thread, never the hook thread.

### FSD glow renderer (`IGlowRenderer` + `WindowsGlowRenderer`)
- One full-virtual-desktop, layered, **click-through**, no-activate, topmost window.
- Paints **only the edges** — a gradient border fading inward, transparent center (non-obscuring).
- **Efficient breathing:** render the edge-gradient bitmap **once per tone** (cached); animate the "breath" by calling `UpdateLayeredWindow` with an oscillating `SourceConstantAlpha` (~0.35↔`GlowIntensity` over ~3s, ~12fps) — **no per-frame bitmap re-render**. Idle → hide (no repaint). Honors `GlowVisible` + `GlowIntensity` prefs.

### Tone cohesion
The controller derives an **active tone** from mode (Driving→`prefs.Tone` gold, Observing→`Observing` sage) and passes it to cursor reticle/glide/pulse, bubble, and glow — so the whole layer shifts color together on takeover.

### Alignment with the gate
Observing mode is the *visual* of what `ActuationGate` already does functionally: `UserInputObserver` → `NotifyUserInputDetected` pauses actuation for `UserInputPauseWindow`. Phase 3 taps the **same** `UserInputObserver` seam with a parallel cheap callback so the glow and the pause are driven by one signal.

## 4. Verification (on Joshua's box, no PioneerRx)
- `run_workflow calc_verified` → a **gold edge glow breathes** while the agent clicks; cursor + bubble are gold.
- **Move the mouse mid-run** → glow + cursor + bubble shift to **sage** ("Observing"); agent pauses (gate). Stop → after the window, back to Driving or Idle.
- Idle a while → glow fades off.
- `GlowVisible=false` → no glow (cursor + bubble unaffected); Ctrl+Alt+H still hides cursor + bubble.
- Confirm bounded CPU: glow breathes via blend-alpha only (no per-frame bitmap), idle = no repaint.

## 5. Success criteria
- Mode machine transitions Driving↔Observing↔Idle off real signals; pure evaluator unit-tested.
- FSD edge glow renders non-obscuring, breathes cheaply, tone per mode; honors prefs.
- Whole layer (cursor+bubble+glow) shifts tone together on takeover.
- Cosmetic-never-gates preserved; hook path stays fast.
