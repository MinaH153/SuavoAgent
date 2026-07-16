# SuavoAgent install-experience dogfood — 2026-06-26

> **ARCHIVED / DO NOT USE — historical QA evidence only.** Findings below are a
> dated snapshot and intentionally retain the old command and installer variants
> that were observed. They are not current customer instructions. The approved
> lifecycle is `docs/sales/windows-agent-lifecycle.md`.
>
> **Current native replacement (2026-07-13):** the WiX MSI now owns Programs
> and Features plus service registration; Burn and the Start menu expose the
> installed configuration-only pairing UI; MSI commit independently retires
> the exact former developer-publish Broker shortcut/process. This archived
> table remains evidence of the old path, not current open status.

Live end-to-end test of the **self-serve install experience** a pharmacy goes
through, driven on the demo box (Workstation `8DC472B9`, agent `15c16aae…`,
pharmacy = "Hillcrest Pharmacy"), via Chrome Remote Desktop. Goal: is it
intuitive, is the pipeline at 100%, and is it industry-standard — fixing issues
as we went.

## Verdict

- **Intuitive?** No — the *configured* install GUI is excellent, but everything
  around it (discovery, download, SmartScreen, uninstall) is rough.
- **Pipeline at 100%?** The **current build (v3.77) works end-to-end**:
  download → install (brain loads) → pair → **online + heartbeating + brain
  online**. The only thing short of fully-green is the **PMS/SQL connection**,
  which can't exist on a box without a real PioneerRx. With a real DB it would
  be fully healthy. (The fresh **v3.52** install — the only paren-free desktop
  installer — stayed offline; old-build artifact, now moot.)
- **Industry-standard?** Not yet. See findings.

## The journey we ran

signup-aware dashboard → `/suavoagent/download` → **SmartScreen block** →
install → **device-code pairing (pair-first)** → bind (PIC+MFA) → Welcome →
System check → **Terms & consent** → Destination → **verified-binary install +
brain load** → Success → **online**. Plus a true uninstall (`--uninstall`,
zero-residue) and a full v3.77 reinstall to restore the box.

## Findings (severity-ordered)

| # | Severity | Finding | Status |
|---|----------|---------|--------|
| 1 | **Critical** | **SmartScreen BLOCKS the installer** ("unrecognized app", default "Don't run") on both v3.77 and v3.52, while the download page claims **"SmartScreen-trusted."** The cert *does* carry org identity ("MKM TECHNOLOGIES LLC, Private Organization, CA"), so it's **SmartScreen reputation** that's missing (new cert / low install volume) — not "go get EV." | OPEN. Fix the false badge copy + pursue reputation/cert path. |
| 2 | High | **Public `/suavoagent/download` exe is a pairing stub** — even on a clean machine it shows only a device-code screen (the real install GUI is gated *behind* pairing). Cancel out → developer error **"No setup.json found"** exposing `--pharmacy-id`/`--api-key` CLI jargon. | OPEN (friendlier error owed). |
| 3 | High | **No download/reinstall affordance in the dashboard cockpit** — the chat redesign (`AgentCockpitCowork`) dropped the old `InstallStateCard`. Pair page told you to "install the desktop app" with no link. | **FIXED** (branches below). |
| 4 | High | **No Windows Add/Remove Programs entry** — a pharmacy cannot uninstall via Settings → Apps. Only `SuavoSetup.exe --uninstall` (CLI) or the GUI uninstall reachable *only after pairing*. Trust/compliance smell for HIPAA software. | OPEN (register ARP entry). |
| 5 | Medium | **Off-brand download page** — light + teal, disconnected from the gold dashboard, anonymous (no "for [Pharmacy]"). | OPEN (re-skin + integrate). |
| 6 | Medium | **Three divergent installer paths** — `.bat` zero-touch token (`/api/agent/installer`), `.cmd` (`/api/agent/installer-file`), signed `.exe` (`/suavoagent/download`). Canonical undecided. | OPEN — **Joshua's product call**. Recommend: signed exe + baked token (zero-touch + trusted format). |
| 7 | Medium | **Brain load is the install long-pole** — `qwen3-1.7b` took **~4 min** on this low-end CPU ("Loading 1.7 billion neurons…"). Confirm the verify-gate timeout tolerates slow boxes; set expectation in UI. | OPEN (verify timeout / copy). |
| 8 | Low | **Consent "State" field invisible text** — the focused ComboBox face rendered black-on-black on **v3.52**; did **NOT** reproduce on v3.77 (field + dropdown readable). v3.52-specific / focus-transient. | **FIXED** (safe hardening, branch below). |
| 9 | Low | `run_uninstall` desktop helper printed **"Uninstall finished" while failing** (OneDrive `\Desktop\` vs `\OneDrive\Desktop\` path bug) — false success. | Dev-tooling, not product. |

## What's genuinely good — keep

- **Pairing security**: 8-char device-code, PIC-only + MFA, 5-min TTL, hashed at
  rest, rate-limited. Bound cleanly, no MFA re-prompt on an authed session.
- **Configured install GUI**: Welcome → System check (incl. **VC++ runtime**
  check, the vcredist preflight) → **Terms & consent** (excellent: collect /
  never-collect, state-law notices, "nothing uploads until you agree") →
  Destination → install.
- **Install integrity**: per-binary **ECDSA-P256 checksum verification**;
  v3.77 success screen confirms **Brain: qwen3-1.7b · installed and ready** and
  honest **SQL: unknown** (no false all-green).
- **Uninstall logic**: watchdog-first, kills Helper lock, removes both dirs,
  "zero residue."
- **Dashboard honesty**: reports "Heartbeating But Unhealthy" rather than faking
  green; HIPAA idle-privacy lock; friendly **"PioneerRx · Chrome · Excel"**
  multi-app framing.

## Fixes shipped (branches, NOT pushed — review/merge owed)

**Suavo (web)** — both sit *uncommitted together* in the working tree on
`fix/agent-cockpit-reinstall-affordance` (stacked off `fix/suavoagent-install-experience`):
- `step-download-agent.tsx`: "install Node.js" → "register its secure
  background services" (accurate for the .NET agent).
- `pair/page.tsx`: added "Download the installer" link (+ `rel=noopener
  noreferrer`, which also kills a `?code=` referrer leak).
- `CockpitRail.tsx` + new `src/hooks/useInstallerDownload.ts` +
  `AgentInstallHero.tsx` refactor: download/reinstall button in the cockpit's
  "Your computers" section, adaptive label (download / reinstall / add another).
  tsc PASS, Codex APPROVE, 23/23 rail tests pass.

**SuavoAgent (desktop)** — `fix/consent-state-invisible-input` (uncommitted on
that branch):
- `ConsentView.axaml` + `ConsentViewModel.cs`: State ComboBox → TextBox
  (`StateCode`, 2-char, auto-uppercase) — guaranteed contrast. Build PASS.
  *Note: the bug didn't reproduce on v3.77, so this is hardening, not a
  confirmed live-build fix.*

## Owed (not code — Joshua / build / decision)

- **#6** canonical installer-path decision.
- **#1** SmartScreen reputation / cert strategy + fix the "SmartScreen-trusted"
  badge to not assert trust the OS contradicts.
- **#4** register an ARP uninstall entry in the installer.
- **#2** friendlier "No setup.json" message (no CLI jargon for end users).
- Visual verify of the consent TextBox on a real Setup build.

## Multi-vertical note

The whole flow is **PioneerRx/pharmacy-hardcoded** (NPI verify, HIPAA BA ack,
SQL auto-discovery from `PioneerPharmacy.exe.config`). A restaurant/retail
operator **cannot onboard today**. The agent's value ("watches your system,
PioneerRx · Chrome · Excel") is vertical-agnostic — make "connect your stack" a
**pluggable adapter** and the compliance ack **conditional** so the agent is the
wedge across verticals.

## Caveats

- Couldn't launch v3.77 via terminal (filename `SuavoSetup (10).exe` —
  parentheses can't be typed through the CRD Shift-drop); used the download-tray
  click instead. v3.52 was the staged paren-free desktop installer.
- CRD harness: Shift-drop, control-key mangling, corrupted console, coordinate
  drift made terminal diagnosis unreliable — not product bugs.
