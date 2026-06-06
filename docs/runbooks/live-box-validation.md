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

## 0b. Tier-2 native libs — Qwen3 on LLamaSharp 0.24.0 (⚠ DLL set changed 3→5)

The runtime was bumped **0.19.0 → 0.24.0** (first Qwen3-capable llama.cpp `ceda28ef`). The native-lib set
the operator drops at `ReasoningOptions.NativeLibraryPath` (e.g. `C:\ProgramData\SuavoAgent\native\`) grew
from **3 DLLs** to **4** for text-only — `llama.dll`'s load-time deps were split (ggml → ggml + ggml-base +
ggml-cpu). The folder MUST contain ALL of:

```
ggml.dll   ggml-base.dll   ggml-cpu.dll   llama.dll
```

(`llava_shared.dll` is **optional** — only needed for the future multimodal/vision path; text-only Qwen3
loads without it.) Source them from the `LLamaSharp.Backend.Cpu` **0.24.0** nupkg (`runtimes/win-x64/native/avx2/`
— or the `noavx/` folder for an old CPU without AVX2), or hand-compile llama.cpp at exactly commit `ceda28ef`.
If any required one is missing, model load fails — `LLamaLocalInference` now logs `missing required native
libs [ggml-cpu.dll]` (it names the missing file). Model file: **Qwen3-1.7B Q4_K_M** (`qwen3-1.7b` ModelId → thinking-off template)
for the RAM-light efficiency path, or **Qwen3-4B-Instruct-2507** (`qwen3-4b-instruct-2507`) for the quality
path. Set `Agent.Reasoning.ModelId` / `ModelPath` via `agent_config_overrides`. **Grammar is still assumed a
no-op on win-x64** — verify with a forbidden-token spike before trusting it; the template + `ProposalParser`
remain the JSON guarantee.

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

## 4. Arming the honeytoken immune reflex (precedence-1 — staged, never blind)

The reflex (decoy file → corroborate → graduated ladder → local apoptosis + cloud compromise heartbeat
→ fleet revoke) ships **dark** (`ActuationConfig.HoneytokenReflexEnabled = false`). Never-brick is now
**structural**: the corroborator latches the kill switch ONLY for a resolved interactive shell
(powershell/cmd/wscript/…); every other toucher — unknown, unresolvable, or resolved-but-not-shell —
can only ever reach **reversible Degrade**, no matter how often it repeats. So the worst case of a
mis-armed reflex is "actuation goes read-only until the next Helper restart + a cloud alarm," never a
brick. Arming is still staged because, until a real attributor lands, every touch is `unknown` → Degrade
→ a NOISY (but safe) stream of alarms.

**DO NOT flip `HoneytokenReflexEnabled=true` until every step below passes. Kill-criterion at every
stage: a benign process resolving to a `SensitiveDenylist` shell name → halt and investigate (that is
the only path to a latched kill).**

1. **Shadow / observe-only** (flag still FALSE): deploy with the attributor wired but the orchestrator
   in log-only mode; for a FULL business day across a scheduled backup + a manual full AV scan + a
   search-index pass + cloud sync, log every decoy touch as `(resolvedProcessName, exePath,
   would-be-level)`. The gate never mutates.
2. **Triage the shadow log:** every benign system toucher must resolve to a `SystemAllowlist` name
   (→Observe) OR to `unknown`/a non-shell name (→Degrade). Any site-specific EDR/backup image that
   resolves to a *named non-shell* is fine (Degrade is reversible) but should be ADDED to
   `SystemAllowlist` (HoneytokenCorroborator.cs) to cut noise. **No benign process may resolve to a
   `SensitiveDenylist` shell name** — if one does, stop and investigate (that is the only apoptosis path).
3. **Adversarial check:** from an interactive `powershell`, read+hold the decoy → expect a resolved
   `powershell` → Apoptosis (the genuine signal). A fast read-close → `unknown` → Degrade (safe). Copy a
   benign script into the install dir and touch the decoy → Observe (install-dir trust).
4. **Dry-run arm:** flip `HoneytokenReflexEnabled=true` while actuation is still in the locked
   first-2-weeks `DryRun` default — a false Degrade is invisible to actuation but the self-compromise
   heartbeat surfaces it in the cloud. Watch for any Degrade not tied to a deliberate test touch.
5. **Live arm:** only after a clean shadow day (1) + a clean dry-run week (4) with zero unattributed
   Degrades. **Rollback = a single config flip `HoneytokenReflexEnabled=false`** via the OTA config path
   (no redeploy); and because every gap is Degrade-only, even a bad flip self-heals on Helper restart.

**Known Layer-1 limitations to validate against, not surprises:** (a) `NtfsDisableLastAccessUpdate` is
on by default since Win8, so a pure READ of the decoy may fire NO FSW event — Layer 1 reliably catches
write/rename/delete/attribute changes, not silent reads (reliable read detection is the Broker-ETW /
SACL follow-up). (b) The `SystemAllowlist` is name-only (spoofable: malware named `MsMpEng` is Observed
— a detection miss, never a privilege gain); a future attributor that reads the holder's signed exe path
should tighten Observe to system-path + Microsoft-publisher.
