# Field Test — Install SuavoAgent on Your Rig

**Goal:** verify the full agent boots cleanly on your personal Windows machine, runs the heartbeat loop, and proves the tiered-brain wiring is live. Tier-2 local inference is optional for this test — the plumbing (install, service start, heartbeat, rule engine, IPC) matters more than the LLM itself.

---

## What you need

- Windows 10/11 x64 machine
- Admin PowerShell
- ~200 MB free disk (agent only) or ~1.5 GB free disk (agent + model for Tier-2)
- An internet connection to fetch the release
- Your Suavo cloud credentials (pharmacy-staff login for the test pharmacy)

You do **not** need:
- PioneerRx installed — the agent will log "PMS not found" and stay quiet. That's fine. The brain still boots.
- A GPU
- Existing agent state — a fresh install is expected

---

## Step 1 — Provision a test pharmacy

Log into https://suavollc.com as the fleet admin. Create a dummy pharmacy:

- Name: `Mina Test Rig`
- NPI: `0000000001` (or any unused value)
- Owner: your email

This generates an invite code. Write it down — you'll need it at install.

---

## Step 2 — Install the agent

From an **admin PowerShell** on your rig:

```powershell
irm https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/bootstrap.ps1 | iex
```

The bootstrap will:
1. Download the latest signed release (currently v3.10.2) from GitHub
2. Verify checksums + ECDSA signature
3. Install three Windows services: `SuavoAgent.Broker`, `SuavoAgent.Core`, `SuavoAgent.Helper`
4. Prompt for invite code → claim the agent to your test pharmacy
5. Start all three services

Expected outcome: three services in `services.msc` set to Automatic, all Running.

---

## Step 3 — Verify boot + brain wiring

Open `C:\ProgramData\SuavoAgent\logs\core-<date>.log` and look for these lines in order:

```
[INF] SuavoAgent.Core starting v3.10.2
[INF] RuleEngine loaded N rules across 2 skill(s): pricing-lookup, preconditions
[INF] Tier-2 LocalInference disabled (Reasoning.Enabled=false) — running rules-only
[INF] BrainStartupProbe: tier=OperatorRequired outcome=NoMatch reason="..."
[INF] Heartbeat worker stopped
[INF] Heartbeat worker started
```

Also on the dashboard: `Mina Test Rig → Agent tab` should show `Online` within 30 s.

If any of those are missing → fail-closed, send me the log.

---

## Step 4 (optional) — Enable Tier-2 local inference

This is the full LLM path. Skip if you just want to verify install.

> **Why this is a two-part opt-in:** the default installer does NOT ship the
> native `llama.dll` / `ggml.dll` binaries. Those are a vendor fingerprint
> and their presence alone is detectable by PMS vendors that inventory loaded
> modules. So enabling Tier-2 requires the operator to consciously drop BOTH
> (a) the model file and (b) the native backend binaries.

### 4a. Download a GGUF model

Download `Llama-3.2-1B-Instruct-Q4_K_M.gguf` (~770 MB) from Hugging Face:

```
https://huggingface.co/bartowski/Llama-3.2-1B-Instruct-GGUF/resolve/main/Llama-3.2-1B-Instruct-Q4_K_M.gguf
```

Save to:

```
C:\ProgramData\SuavoAgent\models\Llama-3.2-1B-Instruct-Q4_K_M.gguf
```

Compute SHA-256:

```powershell
Get-FileHash C:\ProgramData\SuavoAgent\models\Llama-3.2-1B-Instruct-Q4_K_M.gguf -Algorithm SHA256
```

Copy the hex string.

### 4b. Drop the native llama.cpp binaries

The native binaries ship as a separate "Tier-2 Inference Pack" on the GitHub
release alongside the agent. Download from:

```
https://github.com/MinaH153/SuavoAgent/releases/latest → LLamaSharp.Backend.Cpu.win-x64.zip
```

Extract to:

```
C:\ProgramData\SuavoAgent\native\
├── llama.dll
├── ggml.dll
└── llava_shared.dll   (optional, only if you want vision models)
```

These DLLs are a distinctive fingerprint for any PMS vendor that inspects
loaded modules. Keeping them in a separate path (not `C:\Program Files\`)
means they only appear on pharmacies that explicitly opted into Tier-2.

### 4c. Edit agent config

Open `C:\Program Files\SuavoAgent\Core\appsettings.json` in an admin editor. Add the `Reasoning` section:

```json
{
  "Agent": { ... },
  "Reasoning": {
    "Enabled": true,
    "ModelPath": "C:\\ProgramData\\SuavoAgent\\models\\Llama-3.2-1B-Instruct-Q4_K_M.gguf",
    "ModelSha256": "<paste hash from step 4a>",
    "ModelId": "llama-3.2-1b-q4_k_m",
    "NativeLibraryPath": "C:\\ProgramData\\SuavoAgent\\native",
    "ContextSize": 2048,
    "MaxOutputTokens": 400,
    "IdleUnloadSeconds": 60,
    "AutoExecuteTier2Destructive": false
  }
}
```

> **AutoExecuteTier2Destructive defaults to false.** This means any destructive
> proposal (Click, Type, PressKey) from Tier-2 goes to the operator approval
> queue even at model-reported confidence 1.0 — model self-confidence is not a
> deterministic trust signal yet. Leave false until we have pattern-miner
> calibration (Week 4).

### 4d. Restart the Core service

```powershell
Restart-Service SuavoAgent.Core
```

Check the log:

```
[INF] Tier-2 LocalInference ENABLED — model 'llama-3.2-1b-q4_k_m' at C:\...
[INF] BrainStartupProbe: tier=... outcome=... reason="..."
```

If the probe line shows `tier=LocalInference` or `tier=Rules`, Tier-2 is wired. If it shows `tier=OperatorRequired reason="Local inference not ready"`, the model file couldn't load — check path + hash.

---

## Step 5 — Field test the signed command pipeline (no PioneerRx required)

From the dashboard, `Mina Test Rig → Agent → Commands` tab, click **Send Test Command**. This queues a `run_pricing_job` with a dummy Excel path.

The agent should:
1. Receive the signed command in the next heartbeat (~30 s)
2. Validate the Excel path (will fail because file doesn't exist)
3. Ack the cloud with `status=failed, error="ExcelPath missing"`

Dashboard status goes from `pending` → `sent` → `failed` with the error surfaced. This proves:
- Heartbeat roundtrip ✓
- Signed command verification ✓
- Path validation ✓
- Agent → cloud ack loop ✓

---

## Step 6 — Uninstall when done

```powershell
C:\Program Files\SuavoAgent\Setup\SuavoSetup.exe --uninstall
```

Cleans services, binaries, and state. Does NOT remove `C:\ProgramData\SuavoAgent\models\*.gguf` — those are yours; delete manually if you want.

---

## Rollback if anything goes wrong

The bootstrap writes a backup of the prior install (if any) to `C:\ProgramData\SuavoAgent\.backup\`. Restore with:

```powershell
C:\Program Files\SuavoAgent\Setup\SuavoSetup.exe --rollback
```

---

## What to send me afterward

- `C:\ProgramData\SuavoAgent\logs\core-*.log` (latest day)
- `C:\ProgramData\SuavoAgent\logs\helper-*.log` (latest day)
- Screenshot of the dashboard `Mina Test Rig → Agent` page
- One line on how it felt to install (install time, any confusing step)

---

## Known limitations this test does NOT exercise

- UIA pricing workflow (needs PioneerRx)
- Delivery writeback
- Real Tier-2 inference against a real pharmacy state
- Background model download (operator drops file manually)
- Week-3 vision pipeline
- Week-4 cloud Claude fallback
