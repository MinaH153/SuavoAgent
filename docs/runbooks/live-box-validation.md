# Live-Box Validation Runbook — the general agentic loop + self-healing

> **INTERNAL HARDWARE VALIDATION — not a customer procedure.** Installation,
> update, repair, diagnostics, and removal still use the same signed native
> experience documented in `docs/sales/windows-agent-lifecycle.md`. A result that
> depends on manual binary replacement or terminal-based lifecycle control does
> not pass this runbook.

The agent is code-complete and CI-proven end-to-end (real `TieredBrainReasoner` + real `VerbDispatcher`
drive an app to completion in `tests/.../Agentic/EndToEnd/`). The one remaining inch is **live LLM +
live UIA on a real app**, which is intrinsically a hardware-in-the-loop activity. This runbook makes
that session pure execution.

Box: Mina's Windows (agent `15c16aae`, Hillcrest) via Chrome Remote Desktop. CRD constraints +
techniques: see `reference-crd-remote-windows-techniques` in memory (keystroke drops, no clipboard,
type in ≤10-char chunks, verify-before-Enter).

## 0. Install or update the exact signed canary

Use the final signed canary artifact that already passed the release gate:

- For a clean box, download `SuavoSetup.exe` from the authenticated sandbox
  dashboard and complete the graphical pairing wizard.
- For an installed box, request the target version through the dashboard's
  native OTA control and wait for its matching acknowledgement.
- Run built-in **Diagnostics** and record the installed version, service cohort,
  Helper session, maintenance-host presence, and post-update health receipt.

There is no manual deploy alternative. Copying executables, regenerating a
manifest, restarting services, or editing update state proves only that an
engineer can patch a machine; it does not prove the product.

## 0b. Tier-2 inference package

The exact Qwen/LLamaSharp runtime, model, native libraries, and hashes used by
this test must be delivered as a signed, versioned package through setup or the
dashboard. The package must select a compatible CPU variant, verify every file,
report load state in PHI-safe diagnostics, and roll back as one cohort.

Do not drop DLLs or models into ProgramData and do not hand-edit reasoning
configuration on the validation box. If the current build cannot provision its
inference package through the native product experience, the live Tier-2 test is
**BLOCKED**; record that product gap instead of side-loading it.

The grammar constraint remains a required live assertion. A forbidden-token
spike must fail closed before the model is trusted for actuation.

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

### 4a. The Broker-ETW attribution oracle (`HoneytokenAttributorMode=EtwBroker`)

The oracle that NAMES the toucher is a LocalSystem ETW Kernel-File session in the Broker
(`EtwHoneytokenFileTracer`) — the Helper runs as the non-admin user and cannot start a kernel session. On a
decoy open the Broker resolves the TRUE IRP-issuing process (not a held-handle guess) and writes a signed,
ACL-locked handoff (`%ProgramData%\SuavoAgent\honeytoken-attribution.json`); the Helper's
`EtwBrokerFileAccessAttributor` reads it. Two flags, both default off → dark: set
`"HoneytokenAttributorMode": "EtwBroker"` AND `"HoneytokenReflexEnabled": true` in `actuation.json`.

**Before flipping `EtwBroker` on any box:**
1. Confirm the Broker log shows `ETW honeytoken oracle ARMED` (NOT `BLIND: …`). A BLIND line means the decoy
   dir didn't resolve to an NT-device path — the oracle refuses to arm rather than match-all (PHI-safe), so
   attribution is off until fixed; never arm `EtwBroker` against a blind oracle.
2. The CI `windows-uia-smoke` "Live honeytoken ETW oracle smoke" step must be green on the build (proves the
   session keyword/wiring actually emits a decoy-open event end-to-end — a mis-keyword passes every headless
   test but is silent).
3. **Pure-read detection requires the next change** (the Helper driving the reflex off the handoff-doc change,
   not just the decoy FSW): until that lands, an `EtwBroker`-armed box still only reacts to write/attr touches
   of the decoy via the FSW, even though the Broker oracle correctly attributes reads in the handoff. Do not
   treat read-exfil as covered until that ships.
4. Forgery is blocked by the handoff's per-file ACL (LocalSystem/Admins write, INTERACTIVE read-only,
   inheritance stripped, fail-closed delete-on-throw). A local Administrator can still overwrite it — accepted
   (an admin can trip the box many ways).

Then run the §4 shadow → dry-run → live-arm staging with the oracle ON in shadow first (the handoff log shows
the resolved names without the gate mutating).
