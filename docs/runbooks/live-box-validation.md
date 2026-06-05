# Live-Box Validation Runbook — the general agentic loop + self-healing

The agent is code-complete and CI-proven end-to-end (real `TieredBrainReasoner` + real `VerbDispatcher`
drive an app to completion in `tests/.../Agentic/EndToEnd/`). The one remaining inch is **live LLM +
live UIA on a real app**, which is intrinsically a hardware-in-the-loop activity. This runbook makes
that session pure execution.

Box: Mina's Windows (agent `15c16aae`, Hillcrest) via Chrome Remote Desktop. CRD constraints +
techniques: see `reference-crd-remote-windows-techniques` in memory (keystroke drops, no clipboard,
type in ≤10-char chunks, verify-before-Enter).

## 0. Deploy the new binaries to the box (one-time per build)

The deployable artifacts BUILD for win-x64 (verified): `dotnet publish -c Release -r win-x64` of
`SuavoAgent.Core`, `.Broker`, `.Helper`, `.Watchdog` → the 4 `.exe` (+ `FlaUI.UIA2.dll` for the
Helper's live UIA). Two deploy paths:

- **OTA (preferred, exercises the crash-loop guard #166):** publish all 4 → upload to a SuavoAgent
  GitHub release → sign the manifest (`signUpdateManifest`) → issue an `update` command from the
  cloud. The agent downloads, swaps, enters **probation**; a healthy first heartbeat commits, a
  crash-loop auto-downgrades to `.old`. (This validates OTA self-heal as a side effect.)
- **Manual:** copy the 4 `.exe` over the install dir, regenerate `binaries.manifest`, restart the
  Broker service. Faster for a first smoke; doesn't exercise OTA.

Confirm post-deploy: `agent_version` bumped + `last_heartbeat_at` fresh; Helper SI=1 (interactive).

## 1. NL way-in — `navigate_app` (dryRun, sandbox first)

Issue from the cloud control surface (Suavo PR #1166):
`POST /api/admin/agents/{pharmacyId}/navigate` body `{ verb:"navigate_app", objective:"open notepad and type hi", taskKey:"nav.smoke", dryRun:true }`.

Watch (CRD + agent logs / `agent_actuation_audit`): perceive (scrubbed screen) → reason (Tier-1
rule miss → Tier-2/3 **live LLM** returns a grounded action) → act (dry-run, audited not executed) →
settle+verify → repeat → terminate. **Success = the loop turns and the LLM grounds an action in the
real screen** (the CI proof already covers the loop mechanics; this proves the *live LLM* inch).
Then re-run with `dryRun:false` on Notepad and confirm it actually types.
**NEVER on a live PMS** without `PricingExecutor=SqlFirst` / explicit live-actuation enable.

## 2. Watch-&-learn way-in — `replay_template`

Capture a template on the box (existing learning pipeline → `agent_workflow_templates`), then issue
`{ verb:"replay_template", taskKey:"replay.smoke", template:<the WorkflowTemplate JSON>, dryRun:true }`.
Watch: compile → replay each `CompiledStep` (click_by_signature resolves by ControlType+AutomationId
via `UiaSignatureResolver`) → check `ExpectedAfter` → `Completed`, or fail-closed at the offending
ordinal. **Success = a real learned template replays on the real app by signature.**

## 3. Self-healing cycles (god-tier proof)

- **OTA downgrade:** stage a deliberately-crashing Core build via OTA → confirm it boots, crash-loops
  past the budget, **auto-downgrades to `.old`**, and comes back online (probation flag cleared).
- **Hang detection:** force Core to deadlock (or stop its `LivenessBeaconWorker`) → confirm the
  Watchdog sees the stale beacon (>90s) and **force-cycles** Core (stop+start), without false-cycling
  a healthy/slow start.
- **force_restart:** issue `{ verb:"force_restart", reason:"validation restart" }` → confirm Core
  ACKs, exits, and the Watchdog restarts it.

## What "done" looks like

Both ways-in drive the real app to completion on the box (NL with the live LLM; replay by signature),
and all three self-heal cycles recover autonomously. At that point the agent is production-grade and
operationally validated on real apps — the moat (same brain, every install) is live.
