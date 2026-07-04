# FSD Eval — graded observe → learn → execute

**Why this exists.** The moat is the on-device *observe → learn → execute* loop
(`LearningWorker` → `RoutineDetector` → `WorkflowTemplateExtractor` →
`GatedTemplateExecutor`). "events=21, looks alive" is not proof. This harness runs a
**test with a known correct answer** against a live agent and **grades** how faithfully
SuavoAgent *observed*, *learned*, and *executed* it — a repeatable regression gate for the
moat, not a one-off demo.

The idea (Joshua, 2026-07-04): *make a test where you have an expected answer, then grade
how the agent observed, learned, and executed against it.*

## The rig

```
  ┌─ eval driver (this harness) ─┐        ┌─ system under test ─────────────┐
  │ Invoke-FsdEval.ps1           │  UIA   │ LIVE installed SuavoAgent        │
  │  launches sim, drives the    │ invoke │  Helper observers (UiaInteraction│
  │  known task N× (an operator  │ ─────► │   Observer: InvokePattern.Invoked│
  │  stand-in), records the      │        │   + FocusChanged) → buffer → Core│
  │  ground-truth trajectory     │        │   → RoutineDetector → templates  │
  └──────────────────────────────┘        └──────────────┬──────────────────┘
                    ▼ drives                               │ heartbeat telemetry
  ┌─ PioneerRxSim (WPF) ─────────┐                         ▼
  │ PioneerPharmacy.exe          │        ┌─ grade.mjs (dev box w/ prod read) ┐
  │  Item ▸ Rx Item ▸ Pricing    │        │  reads config_json.stats.         │
  └──────────────────────────────┘        │   template_learning Δ → scorecard │
                                           └───────────────────────────────────┘
```

The driver is an **independent operator stand-in** — it is NOT the agent's executor. It
drives the sim via UIA `InvokePattern.Invoke()` (menus) + `SelectionItemPattern.Select()`
(tabs), which fire the exact `InvokedEvent` / `FocusChanged` events the live
`UiaInteractionObserver` listens for. The live agent (v3.91.0+, `LearningMode=true`) attaches
to `PioneerPharmacy` by process name and observes, unmodified.

## Test #1 — `pricing-nav`

The sim's canonical automation surface (`tools/PioneerRxSim/MainWindow.cs`,
`EditRxItemWindow.cs`):

| # | Action | Element | Pattern | Observer event |
|---|--------|---------|---------|----------------|
| 1 | expand | `MenuItem` "Item"    | ExpandCollapse | (focus) |
| 2 | invoke | `MenuItem` "Rx Item" | Invoke         | `Invoked` |
| 3 | select | `TabItem`  "Pricing" | SelectionItem  | `FocusChanged` |

**Ground-truth trajectory:** `[Invoke MenuItem, Select TabItem]` (≥ `MinPathLength`=3 once
the expand focus event is counted). **Expected end-state:** window "Edit Rx Item" open,
`Pricing` tab selected, pricing `DataGrid` visible.

Names are HMAC-hashed at the source (PHI-safe), so grading is **structural**: step *kind* +
*ControlType* + *order*, plus hash-consistency (same element across reps → same `NameHash`).

## The rubric (each 0–100, vs. the trajectory the driver controls)

| Stage | Question | Signal | Pass bar |
|-------|----------|--------|----------|
| **Observe** | Did it *see* the task? | `interactionEventCount` Δ ≈ reps × steps; scrubbed | Δ ≥ 0.8× expected, no raw values |
| **Learn** | Did it learn the *right* routine? | `learnedRoutineCount`≥1 **and** `workflowTemplateCount`≥1 after `MinFrequency`=5 reps; on-box template steps == trajectory (v2) | routine + template form |
| **Execute** | Did it *do* it right? (v2) | `run_learned_template` result: each step `ExpectedAfter` verified, end-state = Pricing | reaches Pricing tab |

Thresholds are the **real** production ones (`RoutineDetector.MinFrequency`=5,
`MaxEdgeGap`=30s, `WorkflowTemplateThresholds.MinRoutineConfidence`≈0.6) — the eval does not
weaken them. Run `-Reps 6` to clear `MinFrequency` with margin.

## Running it

### Prerequisite — the Helper must actually attach (sim boxes)

The moat's interaction observer only wires up **inside the Helper's PMS attach loop**, and
that loop is gated on `PioneerRxInstallDetector` — a *real* PioneerRx install (path or
registry), never process presence. A bare **sim box** (Queen) runs `PioneerPharmacy.exe`
from a rehearsal dir and satisfies neither, so the Helper logs *"PioneerRx not installed —
skipping attach polling"* and **never subscribes the interaction observer**. Result:
`interactionEventCount` is frozen and **Observe scores 0 no matter how well you drive the
sim** (found the hard way on the first on-box run, 2026-07-04 — three clean 6/6 driver runs,
all Observe 0/100, `behavioralEventCount` crept up only because the *system* observers run
unconditionally).

Fix (eval/CI boxes only): set the machine env var `SUAVOAGENT_FORCE_PMS_ATTACH=1` **before
the Helper starts**, then bounce the Helper so it re-reads it:

```powershell
[Environment]::SetEnvironmentVariable('SUAVOAGENT_FORCE_PMS_ATTACH','1','Machine')
Restart-Service SuavoAgent.Broker   # relaunches the Helper, which now enters the attach loop
```

Requires a Helper build that has `PioneerRxInstallDetector.ShouldPollForPms` (this PR). Do
**not** set it on a real pharmacy box — there `IsInstalled()` is already true. Confirm it took
by re-running `--baseline` while driving: `interactionEventCount` must move.

**On the box** (Windows, live agent installed + `LearningMode=true`, console session):

```powershell
# 1. drive the known task N times against the sim (records ground truth)
pwsh tools/FsdEval/Invoke-FsdEval.ps1 -Reps 6 -Out C:\ProgramData\SuavoAgent\fsd-eval-run.json
```

**On a dev box with prod read creds** (`SUPABASE_URL` + `SUPABASE_SERVICE_ROLE_KEY`, or
`~/Code/Suavo/.env.production.local`):

```bash
# 2. grade from heartbeat telemetry (poll until a post-run heartbeat lands)
node tools/FsdEval/grade.mjs --run fsd-eval-run.json --agent 15c16aae-fa55-49c6-9d4c-971606243b86
```

`grade.mjs` snapshots the baseline itself if `--run` has no baseline block, then waits for the
next heartbeat and scores. Output: a scorecard JSON + a printed report card.

## What v1 grades vs. v2

- **v1 (this):** Observe + Learn from prod heartbeat telemetry (counts + phase). Fully
  automatic, no on-box DB access.
- **v2 (next):** step-level structural match (needs an on-box read-only
  `dump_learning_state` command emitting routine/template steps as PHI-safe structural JSON)
  + the Execute stage (needs actuation IPC enabled + the auto-rule Approved, then
  `run_learned_template` and grade each `ExpectedAfter`).

## On-box verification points (confirm on first run)

1. The live Helper actually attaches to the launched sim — **to THIS pid**. The Helper binds
   to the FIRST PioneerPharmacy process with a UIA window and re-attaches only when that
   window dies (Helper health loop, 10s poll). A stale sim from a previous session keeps the
   observer pointed away from the eval's sim → Observe scores 0 (exactly what happened on the
   first on-box run, 2026-07-04: leftover pid from the prior day). The driver now pre-flights
   this (`-KillExisting` on eval boxes) and waits `-AttachWaitSec` (default 15s) after the sim
   window appears before driving. Attach logs at Debug and the Helper has no writable log file
   on locked-down boxes — prove attach via the interactionEventCount delta, not log lines.
2. Which of steps 1/3 the observer records — `InvokedEvent` (step 2) is certain; the expand
   (step 1) and tab-select (step 3) depend on `FocusChanged` firing. If the captured path is
   < 3 events, add an explicit interaction (e.g. click Quick Search) so the path clears
   `MinPathLength`.
3. `interactionEventCount` moves by roughly `reps × observed-steps`. Tune `-Reps` if the
   directed-follows edges do not reach `MinFrequency`.
