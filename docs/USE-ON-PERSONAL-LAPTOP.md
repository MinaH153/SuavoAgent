# Running SuavoAgent on your own laptop (for real)

The honest path from "it's built" to "I'm using it on my machine." Derived from a full repo gap analysis
(2026-06-06) — every claim here is verified against the code, not aspirational.

## The reality (read this first)

SuavoAgent is a **Windows-only, x64** product: Windows services (`sc.exe`), a LocalSystem Broker that uses
`WTSQueryUserToken`/`CreateProcessAsUser`, DPAPI, FlaUI UIA, GDI screen capture, and ETW. **It cannot run on
macOS** — the Mac *builds* the binaries (verified) but can't run them. It also can't run its reason-for-being
(driving **PioneerRx**) on a personal laptop — you don't have the PMS. So "use it on my laptop" concretely
means: **the 3 services healthy + the Helper in your interactive session + the agent ONLINE in the cloud
cockpit + it executing a real command on your machine** — proven against a normal app (Notepad/Calc), not a
pharmacy.

## THE ONE DECISION — where will it run?

| Option | Cost | Notes |
|---|---|---|
| **On-demand x64 cloud Windows** (AWS EC2 `t3.medium` / Azure `B2s`) — **recommended** | ~$0.06/hr, run only while testing | The EXACT native-x64 env CI already proves green → zero new unknowns. RDP in, run the flow. Best for "real this week." |
| **A cheap x64 mini-PC / NUC** on Windows 11 | ~$150–250 once | Best if you want it permanent + local. |
| Parallels **Windows-11-ARM** VM on the Mac | free-ish | Convenient but rides x64 emulation where the two riskiest pieces — **AVX2** (on-device LLM) and **ETW** (Broker/honeytoken) — are UNTESTED and likely fragile. Treat as a later experiment, not the first run. |

Everything below assumes **x64 Windows**.

## Steps

### 1. Seed your cloud identity (one-time; I can do this for you)
The cockpit + pairing gate on you being a Pharmacist-in-Charge of a pharmacy with a SuavoAgent entitlement.
A **sandbox** pharmacy bypasses the paid subscription gate. Run in the Supabase SQL editor (project
`zsufzmxkccznvolrlkzy`) — replace the email if needed:

```sql
-- 1) a sandbox pharmacy (bypasses the suavoagent subscription gate)
insert into pharmacy_profiles (name, address_line1, city, state, zip, is_internal_sandbox)
values ('Joshua Sandbox', '1 Test St', 'San Diego', 'CA', '92101', true)
returning id;  -- note this pharmacy_id

-- 2) make yourself the PIC of it (role='pic' is what the approve route requires)
insert into pharmacy_staff (user_id, pharmacy_id, role, is_primary)
select u.id, '<PHARMACY_ID_FROM_STEP_1>', 'pic', true
from auth.users u where u.email = 'minahenein96911@gmail.com';
```
Then confirm your account has **TOTP/MFA enrolled** (every privileged route requires it).
*(Tell me to proceed and I'll run this for you via the Supabase MCP — it's a one-time prod write I want your
explicit go on first.)*

### 2. Stand up the x64 Windows box
EC2 `t3.medium` Windows (≥8 GB RAM; Qwen3-1.7B-Q4 needs ~2 GB + headroom), on-demand. RDP in as a local
**Administrator**.

### 3. Install + pair (signed, ~30 min)
- Download the signed **`SuavoSetup.exe`** from the latest GitHub release (currently **v3.22.2**) — EV-signed,
  UAC shows "Verified publisher: MKM Technologies LLC".
- **Right-click → Run as administrator.** Use the GUI wizard — do **not** pass `--console` (the console
  installer hard-fails without PioneerRx; the GUI tolerates no-PMS).
- It auto-enters **device-code pairing** and shows an `XXXX-XXXX` code. Approve it at
  `suavollc.com/pharmacy/agent` (works now that you're the sandbox PIC). System check shows PioneerRx/SQL as
  "Self-configures"; finish the wizard → it registers + starts the 3 services.

### 4. Verify it's genuinely RUNNING (not just installed)
```powershell
Get-Service SuavoAgent.Core, SuavoAgent.Broker, SuavoAgent.Watchdog   # all Running
Get-Process SuavoAgent.Helper                                          # present...
# ...and in the INTERACTIVE session (SI=1, NOT Session 0) — that's the real success signal
```
The agent should flip **ONLINE** at `suavollc.com/pharmacy/agent` within ~2 min.

### 5. First real use — issue a command from the cockpit
At `/pharmacy/agent`, trigger **`fetch_diagnostics`** (or `show_cursor` / `repair`). Watch it go
`pending → sent → done` on the next heartbeat. **That's the moment: a paired, online, command-responsive
SuavoAgent on your own machine.** (Avoid `run_pricing` — pilot-allowlisted + PioneerRx-gated.)

### 6. (Optional, a day more) Drive a real app with the live brain
Drop the LLamaSharp 0.24 x64 native DLLs into `C:\ProgramData\SuavoAgent\native\` and a
`Qwen3-1.7B-Q4_K_M.gguf` into `models\`; set `Agent.Reasoning {Enabled:true, ModelPath, NativeLibraryPath,
ModelSha256}`; restart Core (log shows "Tier-2 LocalInference ENABLED"). Build + launch the repo's classic
Win32 harness (`tests/SuavoAgent.UiaHarness`) and issue `navigate_app` — watch perceive → reason(LLM) →
type/click → verify-by-read-back drive a real app to Done, zero pharmacy software.

## What's done vs. what needs you

- **Done (on my side):** the agent builds + ships signed; installs in no-PMS mode; device-code + install-token
  pairing both shipped; the cockpit issues allowlisted non-PMS commands; the perceive→reason→act→verify engine
  + the real Qwen3 brain are CI-proven on real Windows; safe defaults (Vision/AutoExec/Writeback OFF).
- **Needs you:** pick the run target (the ONE decision); a Windows x64 box; your go on the one-time cloud seed
  (step 1) + TOTP enrolled. Then steps 3–5 are ~30 minutes.
