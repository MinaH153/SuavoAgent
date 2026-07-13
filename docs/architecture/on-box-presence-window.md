# On-Box Presence Window — Architecture + HIPAA Plan (B)

> **Status: PLAN ONLY. Nothing built, nothing live.** This is the deliberate, reviewed spec for the
> desktop-resident agent presence shown in Joshua's mockup (the "Pharmacy Workstation Agent — Watching"
> window with a chat panel). Decided plan-first: a net-new on-box UI with a chat surface on a PHI box is
> an architecture + HIPAA decision, not a screenshot-to-production sprint.
>
> **Relationship to the cloud cockpit (A):** A (make the existing web cockpit feel like the mockup) is
> the **this-week, shippable** path and is **Claude's lane** (Claude owns the status indicator + shipped
> the actuation foundation it consumes). B (this doc) is the **next, planned** path. A and B share ONE
> data/state model (§4) so they never diverge — the window is the on-box *face* of the same truth the
> cockpit shows in the browser.

---

## 1. What it is (and the thesis it serves)

A small, always-available desktop window on the pharmacy workstation that makes the agent a **visible
presence** — "a human behind the screen." It shows, at a glance:

- **State** (the four states, baby words): **Watching** · **Ready to help** · **Working — stop me
  anytime** · **Needs you**.
- **What it just did / is doing** ("Priced 14 of your top-500 · saved you ~$X this week").
- **A chat panel** — ask it things in plain English, on the box, answered by the on-device model.
- **Controls** — Pause, Stop (instant), and the run buttons for proven tasks (price my top-500).

This is dead-on-thesis: the moat is "specialization + a human-like presence doing real desk work."
The window is that presence made tangible — the thing a pharmacist trusts because they can SEE it.
It is **not** a replacement for the cloud cockpit; it's the local, immediate face of it.

**Why it must be planned, not sprinted:** it runs ON the PHI box, it renders agent output in a chat
surface, and it's a new attack/HIPAA surface. The value is real; the care is mandatory.

---

## 1.5 The mental model — "the dashboard is a MONITOR; the work is always on the box"

Three founder clarifications (2026-06-11) that are core architecture, not polish:

**(a) The dashboard = a monitor + remote control. The WORK always happens on the pharmacy box.**
Think of a screen + keyboard plugged into a computer: you give input, you see output — but the machine
doing the work is the box. SuavoAgent is the same. The actuation (clicking PioneerRx), the PHI, the
on-device model — all live and run ON the pharmacy workstation, always. The "dashboard" (whether the
local window OR the cloud cockpit) is just the **monitor**: it shows what the box is doing and lets you
tell it what to do. **No work ever happens in the cloud or in the window itself** — they are views and
remotes onto the on-box agent. This resolves the HIPAA story cleanly: PHI/work never leaves the box;
only the monitor's scrubbed signal does.

**(b) ALWAYS cloud-synced — "run stuff from anywhere."** The window is NOT a local-only island. The
agent's state syncs to the cloud continuously (it already does — heartbeat), and **commands can originate
from either face**: the local window OR the cloud cockpit (phone, home, another store). Two monitors, one
box. A "price my top-500" tapped from Joshua's phone and the same tap on the local window both land as the
SAME signed/gated command to the on-box agent. **DEC-6 — the local window and the cloud cockpit are
peer faces of one synced agent; neither is authoritative, the on-box agent is.** The local window adds
*immediacy* (zero-latency local view + raw-reply fidelity §3); the cloud adds *reach* (run from
anywhere). They never diverge because they render the same state model (§4) and issue the same command
path.

**(c) The HYBRID "visible takeover" mode — human watches SuavoAgent work, like Claude-in-Chrome.**
A first-class mode (not a separate product): the pharmacist watches the agent **actually do the work on
the real PioneerRx** — the cursor moves, fields fill, the supplier grid opens — in real time, with
plain-English narration ("opening Rx Item… typing the NDC… reading the cheapest supplier…"). This is the
**FSD-passenger experience**: you're in the seat, hands near the wheel, watching it drive, and you grab
control the instant you touch the mouse/keyboard (the agent already yields on operator input). It is the
**trust-building bridge** between "Watching" (does nothing) and "autopilot" (does it unattended):

- **DEC-7 — visible takeover is the SAME gated actuation, just observed.** It does not create a new
  capability or bypass the autonomy ladder — it runs an already-earned task while (i) keeping the actions
  visible/foreground, (ii) narrating each step to the window, and (iii) honoring instant-stop on any human
  input. The "appears to be working like a human" is the existing actuation made *visible + narrated +
  interruptible*, not a new privilege.
- It maps to the **Working — stop me anytime** state (§4), with the narration stream + a giant Stop.
- HIPAA note: narration text is the agent's own step descriptions (structural — "typing the NDC", "read
  Supplier X"), rendered locally; it follows the same scrub-before-upload rule as chat (§3). The actions
  are on the box where PHI already lives — watching them is no new disclosure.

These three reframe the build: the window is a **synced monitor + remote** (local face) onto an on-box
agent whose work is always local, and **visible-takeover** is the headline mode that makes the presence
feel like a human teammate. None of it changes WHERE work happens (always the box) or the gate (always
earned + audited).

---

## 2. Grounding — what ALREADY exists (so this is additive, not greenfield)

Verified in-repo (2026-06-11). The window stands on a stack that's already here:

| Need | Already exists | File |
|---|---|---|
| UI framework | **Avalonia 11.2** ships in the agent today (the Setup GUI) | `src/SuavoAgent.Setup/*.csproj` |
| Desktop runtime | Helper is `net8.0` + `Microsoft.WindowsDesktop.App` (win-x64) | `src/SuavoAgent.Helper/*.csproj` |
| Process↔process comms | Named-pipe IPC **with peer attestation** (a UI client must pass it) | `IpcCommandServer.cs`, `IpcPeerVerifier.cs`, `IpcPeerAttestationStore.cs` |
| On-box chat | `chat` command → **on-device** qwen3 inference, reply **already PHI-scrubbed**, detached off the heartbeat loop | `HeartbeatWorker.HandleChatAsync` (~:1171) |
| State model | `composite_status` + witness-mode + `PanelState<T>` (the cockpit's contract) | cloud `useAgentData.ts`, `AgentHero.tsx` |
| Actuation readiness / Needs-you signal | `helper.actuation` readiness (the #1248 foundation) | (cloud + helper) |

**Decision DEC-1 — framework: Avalonia (not WPF).** The agent already ships Avalonia; reusing it means
one UI stack, the existing Fluent theme + Inter font + brand assets, and no new dependency. WPF would
fork the stack for no gain. (Avalonia also keeps a future cross-platform door open; not a goal now.)

---

## 3. The HIPAA crux — the central architectural argument (Precedence-1)

**The on-box window has a BETTER PHI posture than the cloud chat does today.** Here is the argument,
because it's the whole reason B is even approachable:

1. **The on-device model already sees the screen.** The local LLM (qwen3) runs inside the HIPAA boundary;
   reading PHI to reason is already in scope and already permitted (it never leaves the box).
2. **The existing chat reply is scrubbed BEFORE it leaves** — `HandleChatAsync` does
   `PhiScrubber.ScrubText(reply)` then acks to cloud. So the cloud only ever sees the scrubbed reply.
3. **The window renders the reply LOCALLY, on the box, to the logged-in pharmacist** — the same person
   already looking at PioneerRx full of PHI. Showing them a local answer **leaks nothing new**: it never
   crosses the network, and the viewer is already inside the trust boundary.

→ **Therefore the window may render the RAW (pre-scrub) local reply** to the pharmacist (better UX — no
`[REDACTED]` holes in answers about their own patients), **while the cloud upload stays scrubbed** exactly
as today. This is the key win: **local fidelity, zero new egress.**

**But the surface still needs these guardrails (the HIPAA review must confirm each):**

- **G1 — The window itself adds no new egress; cloud sync stays on the agent's existing scrubbed path.**
  The window talks ONLY to the local agent over the existing IPC — it has no cloud calls of its own. The
  agent CONTINUES to sync state to the cloud (so you can monitor + "run from anywhere" — clarification (b)),
  but ONLY through its current heartbeat path, which is **already PHI-scrubbed**. Inbound cloud commands
  (a "price top-500" from your phone) arrive through the **existing signed-command path** (same gate, same
  audit) — they are not a new door, just the door that's already there. So: raw PHI never leaves the box;
  the cloud sees only scrubbed state; the window is a *local face*, the cloud cockpit is a *remote face*,
  and the on-box agent is the single brain both observe. The window is never itself an egress point.
- **G2 — No local persistence of raw replies/PHI.** The chat transcript lives in memory for the session;
  it is NOT written to disk unscrubbed (no plaintext chat log). On close/lock, it's gone. (If any history
  is wanted, it persists only the scrubbed form, mirroring the upload.)
- **G3 — Screen-lock / session boundary.** The window hides/clears on workstation lock or user switch
  (PHI must not sit visible on an unattended screen). Tie to the same session signals the agent already
  watches (lock/unlock timing is already observed).
- **G4 — The chat is a VIEW, not a new actuation channel.** Typing in the window cannot make the agent
  *do* anything that doesn't already go through the full safety gate + autonomy ladder + audit. A "run my
  pricing" button issues the SAME signed/gated command path as the cloud cockpit — the window is just a
  nicer trigger, never a bypass. Free-text chat answers questions; it does not silently actuate. (This is
  the same line Feature B's reader contract draws: gather/answer ≠ put.)
- **G5 — Disclosure unchanged.** The existing employee-monitoring disclosure (`TrayIndicator`
  `GetDisclosureText`) still governs; the window surfaces it (About), and adds nothing collected.
- **G6 — Audit.** Window-initiated commands carry the same audit-chain entries as cockpit-initiated ones
  (actor = local operator), so "who told it to do what" is never ambiguous.

---

## 4. Architecture — the window is the local face of ONE shared state model

```
┌──────────────────────────── pharmacy workstation (PHI box) ─────────────────────────────┐
│                                                                                          │
│   SuavoAgent.Presence (NEW, Avalonia)            SuavoAgent.Helper / Core (RUNNING agent) │
│   ┌────────────────────────────────┐   IPC       ┌──────────────────────────────────────┐│
│   │  Watching/Ready/Working/Needs   │  (named     │ IpcCommandServer + IpcPeerVerifier   ││
│   │  "saved you $X" · activity      │  pipe,      │  - state feed (status, last actions) ││
│   │  chat panel  · Pause/Stop/Run   │ ⇄ attested ⇄│  - chat dispatch -> HandleChatAsync  ││
│   │  visible-takeover: live cursor  │  peer)      │    (on-device qwen3, local reply)    ││
│   │  + step narration (DEC-7)       │             │  - run/pause/stop -> SAME gated path  ││
│   │  (renders LOCAL reply, raw)     │             │  - actuation = ON THE BOX, always     ││
│   └────────────────────────────────┘             │  - cloud sync stays SCRUBBED (today)  ││
│            in-memory only (G2)                    └──────────────────────────────────────┘│
│   LOCAL FACE (immediacy)                                    ▲          │                    │
└────────────────────────────────────────────────────────────┼──────────┼────────────────────┘
        same signed/gated command path  (run from anywhere)   │          ▼  scrubbed heartbeat (G1)
                                                               │   ┌─────────────────────────────┐
                                              inbound commands │   │  CLOUD  (the sync hub)       │
                                              (signed, gated) ─┘   │  - stores scrubbed state     │
                                                                   │  - relays signed commands    │
                                                                   └──────────────┬──────────────┘
                                                                                  │ same state model (§4)
                                                                                  ▼
                                                            cloud cockpit (A) — REMOTE FACE (reach):
                                                            monitor + "run from anywhere" (phone/home)
```
Two faces (local window + cloud cockpit), one on-box brain. The work + PHI are ALWAYS on the box; both
faces are monitors/remotes. Cloud is the sync hub (scrubbed state in, signed commands out) — the door
that already exists, not a new one.

- **DEC-2 — New project `SuavoAgent.Presence`** (Avalonia desktop), launched by the Helper for the
  interactive session (the Helper already runs per-session and owns the foreground/actuation context).
  Kept a SEPARATE project from Setup.Gui (different lifecycle: Setup runs once; Presence runs always).
- **DEC-3 — It is an IPC CLIENT of the running agent**, and must pass `IpcPeerVerifier` like any peer
  (the new process identity is added to the attestation allowlist — a security task, reviewed). It does
  not embed the agent; it talks to the one already running (single source of truth, no split-brain).
- **DEC-4 — ONE state model, shared with the cockpit.** The four states + "what I did" + savings derive
  from the SAME fields the cloud `AgentSummary`/`composite_status`/witness-mode/`helper.actuation`
  readiness expose. The window subscribes to a local state feed over IPC; the cockpit reads them from the
  cloud. **Neither invents states.** A small shared status enum (Watching/Ready/Working/NeedsYou) maps
  from the existing composite status + actuation-readiness + learning(witness) mode — defined ONCE.
- **DEC-5 — Chat reuses `HandleChatAsync`** verbatim (on-device, detached, busy-aware). The window adds
  a local-render branch (raw reply to the pharmacist) but the cloud-ack path is byte-for-byte unchanged.

### The four states — mapping (no new truth, just a friendly face over existing signals)
| Window state | Derived from (existing signals) |
|---|---|
| **Watching** | online + in witness/learning window OR idle (observing, not acting) |
| **Ready to help** | online + actuation-ready + has ≥1 proven/approved task, not currently running |
| **Working — stop me anytime** | a gated run in progress (pricing job / replay active) |
| **Needs you** | offline, OR actuation NOT ready (`helper.actuation`), OR a run halted/escalated, OR a write refused (the "couldn't act / needs a human" signal) |

---

## 5. Scope — phased, each shippable + reviewable on its own

- **P0 — Read-only presence (no chat, no new actions).** The window shows the four states + "what I did /
  saved $X" + the existing controls (Pause/Stop already exist as commands; Run reuses the cockpit's gated
  trigger). **Zero new HIPAA surface beyond rendering already-available state locally.** This alone
  delivers most of the mockup's *feel* and is the safest first slice. *(HIPAA review: light — it renders
  state, issues already-gated commands.)*
- **P1 — Chat panel (local render).** Add the chat surface over `HandleChatAsync`, rendering the raw
  local reply (§3). **This is the slice that needs the full HIPAA review (G1–G6).** Gate behind a flag
  (`Agent:PresenceChat`, default OFF) until reviewed + pilot-approved.
- **P1.5 — VISIBLE TAKEOVER (the headline mode, DEC-7).** The "watch it work like Claude-in-Chrome"
  experience: when a gated task runs, the window shows the **live cursor on the real PioneerRx** + a
  **step-by-step narration stream** ("opening Rx Item → typing the NDC → reading cheapest supplier") +
  a giant **Stop**, and **instant-yield on any human mouse/keyboard input** (the agent already cedes the
  foreground on operator activity — this surfaces it). It is the SAME gated actuation, just made
  visible + narrated + interruptible — no new privilege (the autonomy ladder still governs *whether* the
  task may run; this only changes *how it's shown*). The trust bridge from Watching → autopilot. Maps to
  the **Working** state. *(Review: the narration stream is structural step text, scrubbed-before-upload
  like chat; the actions are on-box where PHI already is — watching them is no new disclosure. Confirm
  the instant-stop is reliable, not best-effort — same bar as G3.)* Gate behind `Agent:PresenceTakeover`,
  default OFF, until reviewed + pilot-approved.
- **P2 — Polish + parity.** Micro-interactions, the "saved you $X this week" hero, activity feed parity
  with the cockpit, About/disclosure, lock-clear (G3). Spring animations, premium feel (the mockup bar).
- **P3 (future) — richer triggers.** Surface Feature A (price top-500) and Feature B (preferred-NDC
  report) as one-tap actions — but only through the same gated/report paths (Feature B stays report-only
  per its spec). No new autonomy is created by the window; it only triggers what's already earned.

**Out of scope (explicit):** the window NEVER becomes a new actuation or egress channel (G1/G4); it does
not persist raw PHI (G2); it does not create autonomy the ladder hasn't granted.

---

## 6. Risks + what the HIPAA / Codex review MUST scrutinize before ANY build

1. **The local-raw-render argument (§3).** The crux. A reviewer must agree that rendering the
   pre-scrub local reply to the logged-in pharmacist on the same box is zero new disclosure — and that
   G1 (no window egress) + G2 (no raw persistence) + G3 (lock-clear) hold airtight. If any doubt: P1
   renders the SCRUBBED reply (same as cloud) — worse UX, but trivially safe. Decide explicitly.
2. **IPC peer attestation for a new client (DEC-3).** Adding a UI process to the attested-peer allowlist
   widens who can talk to the agent. Confirm the new process identity is verified as strictly as Core is,
   and that a spoofed "Presence" process can't drive the agent. Security-reviewer territory.
3. **G4 — chat is not a back-door actuation.** Prove the free-text path cannot reach the actuator except
   through the existing gate. The on-device model answering "what's the cheapest supplier" is fine;
   it silently clicking is not. (Mirror the agentic loop's "talk vs act" separation.)
4. **Session/lock handling (G3).** PHI on an unattended screen is a breach. The clear-on-lock must be
   reliable, not best-effort.
5. **No-persistence (G2).** Verify no Avalonia/logging path writes the raw transcript to disk.
6. **Disclosure + employee-monitoring statutes (G5).** The new visible surface must not change what's
   collected; the existing CT/DE/NY disclosure still covers it. Confirm.
7. **Resource footprint.** An always-on Avalonia window on an 8GB no-GPU pharmacy PC — confirm it's light
   (the agent already guards resources; the window must not contend with the on-device model's RAM).
8. **Visible-takeover instant-stop (DEC-7 / P1.5) — SAFETY-CRITICAL.** The "watch it work" mode actuates
   the live PMS while a human watches. The instant-yield on ANY operator mouse/keyboard input must be
   **reliable, not best-effort** — a takeover that doesn't stop the moment the pharmacist grabs the mouse
   is a precedence-1 failure (it could fight the human on a live Rx). Confirm the existing
   foreground-yield is hard, fast, and covers takeover. Also confirm the narration stream carries only
   structural step text (no PHI values) and follows scrub-before-upload.
9. **Two-faces sync integrity (DEC-6).** With both a local window and the cloud cockpit issuing commands
   to one box, confirm: no double-execution of the same command (idempotency / single-flight), the on-box
   agent is the sole authority (a stale cloud view can't override a live local stop), and a command from
   anywhere still passes the SAME signed/gated/audited path (the cloud relays, it never originates
   un-gated authority).

---

## 7. Recommendation

- **Build A first (cloud cockpit, Claude's lane) — this week.** It delivers the mockup's *feel* + Nadim's
  "report on the dashboard" + the "run from anywhere" reach (clarification b) with no on-box HIPAA
  surface. Highest value, lowest risk, already staffed. A is the **remote face**; it ships now.
- **Then B, phased + reviewed.** The **local face**: start at **P0 (read-only presence)** — most of the
  mockup with almost no new surface — then **P1 (chat)** and **P1.5 (visible takeover — the headline
  "watch it work" mode)**, each behind a default-OFF flag and gated on a HIPAA + security pass. The
  local-raw-render win (§3) + visible-takeover (DEC-7) are what make B genuinely a *presence*, not just a
  second web tab. **Nothing on-box ships from a screenshot.**

**The model to hold onto:** the dashboard is a **monitor + remote** (local window OR cloud), the **work +
PHI are always on the box**, the cloud is the **sync hub** so you can run from anywhere, and **visible
takeover** is the human-watching-it-drive mode that builds the trust to eventually let it run unattended.
The vision is right and now fully captured. Sequence: A-now (reach), B-planned-then-built (presence) —
this doc is the plan so B is built right when its turn comes.
