# Agent Presence Layer — Design Spec

- **Date:** 2026-06-19
- **Status:** Draft for review (brainstorming → design gate)
- **Author:** Claude (co-founder) + Joshua
- **Scope:** Overall vision + 5-phase decomposition, with **Phase 1 specified in full**. Phases 2–5 are sketched; each gets its own spec → plan → implementation cycle.

---

## 1. Vision

Make SuavoAgent **visibly drive the machine like Tesla FSD**, then **switch to watching and learning when the human takes the wheel.**

When the agent acts: a persistent, beautiful cursor glides between targets, lands a soft pre-click reticle on each control, narrates a one-line "why" in a bubble, and the screen edges breathe a warm ambient glow that says *"the agent has the wheel."* When the human takes over: control hands back explicitly, the glow shifts color, the cursor parks, and the agent silently observes — learning *why* the human does what they do, feeding the trajectory harvester (the Physarum/slime-mold moat).

This is **cosmetic over a proven core**. The actuation, vision, and reasoning all exist (v3.61.0). The presence layer is the UX skin + a learning trigger on top. **Hard invariant: visuals never gate actuation** — a hidden cursor means the agent still acts, silently.

### Strategic frame
- Serves the **wedge**: a pharmacy that watches the agent work (and watches it learn from them) trusts it — the on-ramp to the Suavo delivery OS.
- Serves the **moat**: observe-mode is where human demonstrations thicken the skill-tubes. No competitor has this (see §3).
- **Brand DNA** (design-principles.md): gold `#C8A96A` = acting · sage = observing/learning · wine-red = awaiting-confirm/danger · charcoal `#0F172A` ground · **spring-only motion**.

---

## 2. Research basis

Evidence: one verified deep-research pass (26 sources, 25 claims adversarially verified, 20 confirmed/5 killed) + three product deep-dives (Claude for Chrome, OpenAI Codex, Perplexity Comet). Full citations in §9. Key verified lessons drive every decision below; **two assumptions were killed** (see §2.2).

### 2.1 Convergent patterns we ADOPT (every comparable does these)
1. **3-tier autonomy ladder.** Claude for Chrome (ask-each / approve-plan / act-without-asking), Codex (Chat / Agent / Full-Access), Comet (you-do-it / permit-once / auto). → Our ladder: **Observe / Assist / Auto** (§6.4).
2. **Risk-gated pause on high-stakes actions.** All three pause for login/purchase/destructive ops. → We confirm on **consequence** (§2.3 #2).
3. **Always-available, deliberate STOP.** Comet stop button; Codex hardened single-Esc → **Esc Esc** double-tap after accidental cancels. → Deliberate-but-reachable hard stop (§6).
4. **Cursor motion = spring/lerp, never splines.** Splines look smooth but lag on weak hardware; throttle + interpolate, never raw coords 1:1. *(perfect-cursors, Liveblocks, Figma)*
5. **Local highlight, never full-screen dimming.** Mouseposé's screen-dim is the documented anti-pattern; click feedback = concentric rings. *(Boinx, PointerFocus)*

### 2.2 Killed assumptions (do not assume parity)
- **No shipping product has verified real-time per-step reasoning narration.** Mariner's "Transparent Reasoning sidebar" claim refuted 0-3; Claude for Chrome's pattern is *plan→execute→report*, not live narration; Comet's sidecar is summary-level and a reviewer hit a *"doom loop"* where it never said **why** it was stuck.
- **No competitor has a Tesla-FSD ambient glow or a learn-from-human observe mode.** Both confirmed **absent** from Claude for Chrome, Codex, and Comet. → These are **differentiators**, not catch-up — but unvalidated, so build them **tunable/optional** and prove in use.

### 2.3 Our differentiators (what we build that they don't)
1. **Pre-click intent reticle** — a gold ring lands on the target ~150ms *before* the click (driven off our UIA element bounds, not raw coords). Claude's screenshot-after-the-fact loop and Comet's vague "subtle animation" both lack a "show intent before acting" beat.
2. **Confirm gates on CONSEQUENCE, not capability class.** Codex gates by file/network/git category; that's wrong for a regulated desktop. We force confirm on a small set of **irreversible pharmacy actions** (change Qty/NDC, mark dispensed, submit claim) regardless of input class, with a **read-back diff** ("Qty 30 → 90 on Rx #…") in the wine-red danger state — using the existing `actuation.assert_element` read-back as the pharmacy equivalent of Codex's diff-as-evidence.
3. **Decouple "think harder" from "narrate more."** Codex conflates reasoning-effort with narration volume. Our deterministic step labels ("Clicking 7") are **always-on and free** (not LLM output, never truncated); on-device-LLM rationale is a **separately collapsible lane** (default collapsed, one-line summary, "Explain more" on demand) — avoids the i5/2-core latency tax of streaming reasoning per step.
4. **Narrate stalls/loops.** Comet's worst failure was opacity during failure. When stuck: bubble says **"Element not found, retrying 2/3"** — never silent.
5. **FSD ambient glow** as the single source of truth for "agent active" — non-PHI by construction (renders no page content), so it can stay up even when cursor/bubble are hidden.
6. **Learn-from-human observe mode** — the moat. Glow shifts to sage; agent draws nothing, narrates what it *infers*, feeds the trajectory harvester. No PHI rendered or stored.
7. **Input-lock while acting** (adopted from Comet's strongest verified idea): the agent's cursor is the only moving thing on screen; human grab instantly overrides → pause into Observe.

### 2.4 Threat model (from Comet's documented failures)
Comet executes with the user's authenticated privileges; permission prompts gated *user-visible* actions but **did not stop prompt-injection-driven sensitive actions** (Brave, Simon Willison, Zenity local-file leak, CometJacking). Our defenses, reinforced:
- **Never act on instructions parsed from on-screen content.** Actions come from signed workflows/plans only; on-screen text is data, never command.
- **Allow-list element targets per workflow** (we already have `actuation.json` app allowlists + UIA signature gating).
- **PHI/login/money actions behind a mandatory, un-auto-dismissable confirm.**
- **PHI is never rendered in any overlay** — bubble/labels render the agent's own intent strings, never echoed scraped patient data.

---

## 3. Competitive landscape (4 dims × 4 products)

| Dimension | Claude for Chrome | OpenAI Codex | Perplexity Comet | **SuavoAgent (ours)** |
|---|---|---|---|---|
| **Cursor** | Cursor moves; ref-IDs + coords; click/label indicators only in GIF export | No spatial cursor (file/diff agent) | "See where it clicks" + overlay anim; input locked while acting | **Persistent gliding cursor + pre-click gold reticle off UIA bounds** |
| **Narration** | Side panel; plan→execute→report; live per-step unconfirmed | Plan + to-do list + diffs; effort≠verbosity | Sidecar, step-by-step but summary-level; "doom loop" opacity | **Cursor-anchored 1-line bubble + collapsible step-log; deterministic always-on + LLM rationale on demand; narrates stalls** |
| **Ambient "driving"** | None (panel only) | Running/queued/idle status chrome | Input-lock + in-page overlay; no glow | **FSD edge glow (gold=act / sage=observe / wine=confirm) + always-on-top status chip** |
| **Handoff** | ask / plan / act-without-asking; site-category **refuses medical** | sandbox×approval axes; Esc-Esc stop; `--yolo` | you / once / auto; risk-gated pause; stop button | **Observe / Assist / Auto; confirm on consequence + read-back; deliberate STOP; no `--yolo` for PHI; learn-from-human observe** |

**Strategic takeaway:** Claude for Chrome **refuses to operate on medical/financial sites** — our exact job (acting *inside* the PMS) is structurally outside their product. We are not catching up; on glow, reticle, and observe-mode we are ahead by default.

---

## 4. Phase decomposition

All phases are **box-first** (the box is source of truth) and **dashboard-mirrored**. Each is independently shippable.

| Phase | What | Built on |
|---|---|---|
| **1. Persistent presence cursor + Preferences foundation** | One session-long overlay that glides target→target, lands a pre-click reticle, pulses, **never vanishes**; the prefs system + instant hide ship here | `WindowsIntentCursorRenderer` (glide/easing exists) |
| **2. Reasoning bubble (hybrid)** | Cursor-anchored 1-line bubble + collapsible macro/micro step-log; deterministic labels always-on, LLM rationale collapsible; narrates stalls; PHI-scrubbed | Tier-2 reasoner + workflow step labels |
| **3. FSD glow + mode state machine + status chip** | Non-obscuring edge glow + always-on-top status chip; `Driving`(gold) ↔ `Observing`(sage) ↔ `AwaitingConfirm`(wine) ↔ `Idle`; explicit handoff + confirm-on-consequence | `UserInputObserver`, `HotkeyKillSwitch`, `actuation.assert_element` |
| **4. Observe-mode substance (moat)** | In Observe, human actions feed the trajectory harvester — it learns from the human; bubble narrates inferred intent; no PHI stored | `UiaInteractionObserver`, harvester |
| **5. Dashboard mirror** | Throttled cursor path + mode + bubble + action over heartbeat; dashboard interpolates + re-renders the live agent view | heartbeat channel |

---

## 5. Phase 1 — detailed design

**Goal:** the gliding-cursor-on-the-calculator demo on Joshua's box, with a working preferences system and instant hide. No PioneerRx.

### 5.1 Components (high cohesion, one purpose each)
- **`PresencePreferences`** — serializable record (keys in §5.4). Loaded from `presence.json`; overridden by signed cloud sync; hot-reloadable. Pure data.
- **`PresenceController`** — singleton owning **one** persistent overlay window for the session (replaces today's spawn→fade→destroy per click). Command queue: `MoveTo(rect)` · `Reticle(rect)` · `Click` · `Park` · `Show/Hide` · `SetTone`. Reads prefs; if `CursorVisible=false` or `Enabled=false`, suppresses render but lets actuation proceed untouched. Owns overlay lifetime + the animation thread.
- **`WindowsIntentCursorRenderer` (evolved)** — keeps the proven layered-window glide/easing, but **stops destroying the window between actions** and **only repaints while animating** (idle = zero repaint — see §5.5). Adds the pre-click reticle primitive (ring lands on `rect`, then click-pulse concentric rings).
- **`PresenceHotkey` + tray item** — instant local toggle of visibility, cloud-independent (reuse `HotkeyKillSwitch` + `TrayIndicator` patterns).
- **`PresencePreferenceStore`** — load/save `presence.json`, apply signed cloud overrides, raise change events the controller subscribes to.

### 5.2 Interfaces
- `IPresenceController.MoveTo(ElementRect target, PresenceTone tone)` — glide the cursor to the target's center; called by `SendInputDriver`/UIA click path **before** the click.
- `IPresenceController.Reticle(ElementRect target)` — land the pre-click ring (~150ms spring) before `Click`.
- `IPresenceController.SetVisible(bool)` — hotkey/tray/cloud entrypoint; idempotent.

### 5.3 Data flow (box, Phase 1)
```
actuation about to click target (UIA element rect)
  → PresenceController.MoveTo(rect, tone)      // glide (spring/lerp, honoring prefs)
  → PresenceController.Reticle(rect)           // gold ring lands ~150ms pre-click
  → [real UIA/SendInput click fires]
  → PresenceController.Click()                 // concentric-ring pulse
  → cursor PARKS at rest (persistent, no repaint)
Hotkey / tray / cloud-sync → SetVisible(toggle)   // instant; actuation unaffected
```

### 5.4 Preference keys (Phase 1 ships all; later phases consume them)
`Enabled` (master) · `CursorVisible` · `BubbleVisible` · `GlowVisible` · `ObserveVisualsVisible` · `Tone`/color · `CursorSize` · `GlideSpeed`/easing · `GlowIntensity` · `BubbleVerbosity` (off / labels / labels+LLM) · `AutoObserveOnTakeover` · `TargetMonitor` (multi-mon/DPI) · `MirrorToDashboard` · `SuppressWhenSessionDisconnected`.

**Source of truth:** dashboard (cloud), synced to box over the signed heartbeat channel; `presence.json` holds last-synced + defaults so it works offline. **Instant local hide** via hotkey + tray overrides both, cloud-independent.

### 5.5 Performance (2-core box — non-negotiable)
- **Idle = no repaint.** The current renderer loops every 16ms even at rest; the persistent overlay must only run the animation loop *during a move/pulse*, then go static and stop the pump. This kills the main CPU concern of an always-on overlay.
- **Spring/lerp interpolation only** (verified); no spline pathing.
- **Renderer decision:** Phase 1 ships **GDI-persistent with idle-no-repaint** (reuse the proven `UpdateLayeredWindow` path — fastest to the demo, lowest risk). Research verified GDI `UpdateLayeredWindow` is the slow CPU-copy path and `WS_EX_NOREDIRECTIONBITMAP` + **DirectComposition** does GPU alpha with no per-frame copy. → **Phase 1.5: migrate the renderer to DirectComposition before it runs on any real pharmacy box.** Tracked as the immediate follow-up.

### 5.6 Error handling
- Overlay render failure stays **non-fatal** (existing try/catch) and **never blocks a click** — the §1 invariant.
- Preference parse failure → safe defaults: `Enabled=true`, `CursorVisible=true`, `BubbleVisible` PHI-scrubbed, `SuppressWhenSessionDisconnected=true`.
- `SuppressWhenSessionDisconnected`: no console/RDP session → suppress all rendering (no point painting to a disconnected session; also a discretion default).

### 5.7 Verification (on Joshua's box, no PioneerRx)
- Dispatch `run_workflow calc_verified` → watch the cursor **glide button-to-button** on the calculator, land the **pre-click reticle**, pulse on click, and **persist between clicks** (never vanish).
- Toggle the **hotkey** mid-run → cursor hides instantly while the agent keeps actuating (proves the cosmetic-never-gates invariant).
- CPU check: confirm near-zero CPU at rest (idle-no-repaint) and a bounded spike only during glides.

---

## 6. Out of scope / YAGNI (Phase 1)
- The reasoning bubble (Phase 2), FSD glow + mode machine (Phase 3), observe-learning (Phase 4), dashboard mirror (Phase 5).
- DirectComposition migration (Phase 1.5 — flagged, not in Phase 1).
- Any PioneerRx-specific behavior — Phase 1 validates entirely on calc/notepad.

---

## 7. Decisions (resolved 2026-06-19)
1. **Renderer path — DECIDED:** GDI-persistent-with-idle-no-repaint for Phase 1 (fastest to demo, reuses the proven path), then migrate to DirectComposition in **Phase 1.5** before any real pharmacy box.
2. **Hide default — DECIDED:** default-visible (product vision) + instant hotkey/tray hide + `SuppressWhenSessionDisconnected=true`. Pharmacies can dial to a conservative profile via the dashboard preferences.

---

## 8. Success criteria (Phase 1)
- Persistent cursor glides + reticles + pulses + parks on the calculator, driven by real actuation, on the box.
- Instant hide works and never affects actuation.
- Near-zero idle CPU.
- Preferences load from `presence.json` and hot-reload.

---

## 9. References (verified sources)

**Cursor / interpolation:** [perfect-cursors](https://github.com/steveruizok/perfect-cursors) · [Liveblocks — animating cursors](https://liveblocks.io/blog/how-to-animate-multiplayer-cursors) · [Figma multiplayer](https://www.figma.com/blog/multiplayer-editing-in-figma/) · [Building Figma cursors](https://mskelton.dev/blog/building-figma-multiplayer-cursors)
**Overlay rendering / perf:** [MSDN/Kerr — high-performance window layering (NOREDIRECTIONBITMAP + DirectComposition)](https://learn.microsoft.com/en-us/archive/msdn-magazine/2014/june/windows-with-c-high-performance-window-layering-using-the-windows-composition-engine)
**Screencast / distraction:** [Boinx Mouseposé](https://boinx.com/mousepose/) · [PointerFocus](https://www.pointerfocus.com/) · [auto-hide cursor](https://focusee.imobie.com/record-tips/auto-hide-mouse-cursor.htm)
**Handoff / RPA:** [Power Automate Desktop PiP](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip) · [UiPath PiP](https://docs.uipath.com/robot/standalone/2024.10/user-guide/picture-in-picture)
**Claude for Chrome:** [Announcement](https://claude.com/blog/claude-for-chrome) · [Getting started](https://support.claude.com/en/articles/12012173-getting-started-with-claude-for-chrome) · [Permissions guide](https://support.claude.com/en/articles/12902446-claude-in-chrome-permissions-guide) · [Prompt-injection defenses](https://www.anthropic.com/research/prompt-injection-defenses) · [Extension internals (sshh12)](https://gist.github.com/sshh12/e352c053627ccbe1636781f73d6d715b)
**OpenAI Codex:** [CLI features](https://developers.openai.com/codex/cli/features) · [Agent approvals & security](https://developers.openai.com/codex/agent-approvals-security) · [IDE features](https://developers.openai.com/codex/ide/features) · [Cloud](https://developers.openai.com/codex/cloud) · [Esc-Esc redesign](https://github.com/openai/codex/issues/14509)
**Perplexity Comet:** [Comet Assistant puts you in control](https://www.perplexity.ai/hub/blog/comet-assistant-puts-you-in-control) · [TestingCatalog hands-on](https://www.testingcatalog.com/exclusive-comrehansive-review-comet/) · injection record: [Brave](https://brave.com/blog/comet-prompt-injection/) · [Simon Willison](https://simonwillison.net/2025/Aug/25/agentic-browser-security/) · [Zenity local-file leak](https://labs.zenity.io/p/perplexedbrowser-perplexity-s-agent-browser-can-leak-your-personal-pc-local-files) · [LayerX CometJacking](https://layerxsecurity.com/blog/cometjacking-how-one-click-can-turn-perplexitys-comet-ai-browser-against-you/)
**Ambient design language:** [Tesla FSD v14 visualization](https://www.teslaoracle.com/2025/10/11/tesla-improves-fsd-v14-driving-visualization-to-indicate-autopilot-in-use-video/) · [Apple Intelligence glow (forum)](https://discussions.apple.com/thread/255820845) · [Ambient presence patterns](https://www.aiuxplayground.com/pattern/ambient-presence-displays) · [HIPAA workstation security](https://www.accountablehq.com/post/hipaa-workstation-security-requirements-best-practices-and-checklist)
