# Chrome Remote Desktop Track 5 Smoke - 2026-05-06

Scope: real Windows workstation reached through Chrome Remote Desktop. No PHI
screens, secrets, patient records, or PioneerRx mutations were opened during
this pass.

## Verified Runtime State

- Remote user context: `queen\queen`.
- Windows services: `SuavoAgent.Broker`, `SuavoAgent.Core`, and
  `SuavoAgent.Watchdog` were running.
- Process split matched the Phase 5.6 foundation:
  - Broker, Core, Watchdog in session `0`.
  - `SuavoAgent.Helper` in interactive session `1`.
- Service accounts:
  - Broker: `LocalSystem`.
  - Core: `NT AUTHORITY\LocalService`.
  - Watchdog: `LocalSystem`.
- Helper executable path was visible at
  `C:\Program Files\Suavo\Agent\SuavoAgent.Helper.exe`.
- Program Files `appsettings.json` was denied to the normal user account. That
  is the right default for secrets; repair/config readback must go through the
  signed service path, not Helper-as-user.
- Runtime data under `C:\ProgramData\SuavoAgent` included logs, config override
  files, `core_state.db`, retry files, and watchdog/config health evidence.
- Config sync health on the box reported `status: ok`,
  `consecutiveFailures: 0`, and `lastAppliedOverrideCount: 0`.
- Watchdog health showed Broker/Core/Watchdog running.

## Field Bugs Found

1. Cloud command name drift:
   - Suavo web queues `show_cursor`.
   - Agent Core only handled `show_intent_cursor`.
   - Result before this patch: signed command verified, then dropped as
     unknown before Helper could draw the visual marker.

2. Repair command name drift:
   - Suavo web queues `repair`.
   - Agent Core only handled `repair_agent`.
   - Result before this patch: repair could appear queued/sent while the agent
     ignored it.

3. Legacy health filename:
   - The remote box had `config sync health.json`.
   - Current code expects `config-sync-health.json`.
   - Without compatibility, older installs can report config sync as missing
     even when local evidence exists.

## Remote-Control Lessons

- Chrome Remote Desktop canvas is not a DOM text field. Browser `type()` failed
  with no editable element focused even when PowerShell was visually focused.
- Browser clipboard did not reliably become the remote Windows clipboard.
  `Ctrl+V` pasted stale remote clipboard content, not the command just written
  into the local browser clipboard.
- Character key events worked, but key naming was brittle:
  - literal punctuation keys such as `.`, `,`, `\`, and `-` worked better than
    names like `PERIOD` or `MINUS`;
  - shifted punctuation had to be sent as actual key chords;
  - missing punctuation support can leave partial commands at the prompt.
- Blind text entry must be treated as unsafe. A remote agent needs focus
  verification, echo validation, prompt recovery, and typed command schemas
  before it can safely issue workstation commands.
- Accidental clicks can open unrelated user apps with unsaved state. Track 5
  must keep the rollout order as observe -> propose -> show cursor, with actual
  actuation behind explicit signed commands, owner/MFA gates, typed workflows,
  replay protection, and audit.
- Broad paste must never be used for secrets or PHI. In field support, typed
  commands should be short, non-PHI, idempotent, and confirmed by visible output
  or signed local health evidence.

## Product Implications

- `show_cursor` is the right pilot primitive: visual-only, click-through,
  no mouse movement, no typing, and no labels.
- Runtime proof cannot stop at "services running". The dashboard needs the
  composed state: service status, Helper session, IPC, config sync health,
  cloud auth, version drift, and recent command acks.
- Repair must be visible and acked. If signed command dispatch and agent command
  names drift, the UI can look successful while nothing happens on the box.
- Legacy field evidence needs tolerant readers and canonical writers. New code
  should write canonical hyphenated health filenames, but read known legacy
  spellings until the field fleet ages out.

## Continuation Smoke - CRD Windows Session

Timestamp: 2026-05-06 late session, same Remote Desktop route. Operator granted
full workstation permission. Scope stayed non-PHI: no PioneerRx patient screens,
no raw config secrets, no prescription/order payloads, and no raw log bodies.

### Input/Navigation Evidence

- Chrome Remote Desktop remained connected to `Mina's Windows`.
- PowerShell accepted direct keypresses after the terminal prompt was focused.
- Local browser clipboard writes did not reliably become the remote Windows
  clipboard. `Ctrl+V` pasted the remote clipboard, not the local Codex text.
- Windows CRD uses `Ctrl+C`/`Ctrl+V`, not macOS Command shortcuts.
- Shifted symbols must be sent as shifted key chords. Plain `*` arrived as `8`
  and plain `_` arrived as `-` until the input mapper used `Shift+8` and
  `Shift+-`.
- One focus error sent diagnostic text into the Suavo login email field in the
  browser tab. It was not submitted. This is field evidence that any future
  actuation path needs active-window verification before typing.

### Local Runtime Evidence

- Remote user context: `queen\queen`.
- Hostname shown by PowerShell: `minavn8`.
- Services were running:
  - `SuavoAgent.Broker`
  - `SuavoAgent.Core`
  - `SuavoAgent.Watchdog`
- Processes were running:
  - `SuavoAgent.Broker`
  - `SuavoAgent.Core`
  - `SuavoAgent.Helper`
  - `SuavoAgent.Watchdog`
- Install root discovered by field probing:
  - `C:\Program Files\Suavo\Agent`
- ProgramData root discovered:
  - `C:\ProgramData\SuavoAgent`
- ProgramData contained:
  - `download-cache\`
  - `logs\`
  - `actuation.json`
  - `bootstrap.ps1`
  - `config_overrides.json`
  - `consent_receipt.json`
  - `ipc_nonce`
  - `state.db`, `state.db-shm`, `state.db-wal`
  - `state.key`
- ProgramData did not contain:
  - `config.json`
  - `config-sync-health.json`
  - `watchdog-health.json`
- `actuation.json` was readable and showed the local actuation gate enabled.
  Treat that as local capability evidence only; cloud command receipt was not
  proven in this continuation pass.
- Installed binary product versions visible in PowerShell were `3.14.4+...`
  for Core, Broker, Helper, and Watchdog.

### Cloud Runtime Evidence

Cloud verification used service-role Supabase access locally, selecting only
non-PHI operational columns from `agent_instances`.

- `pioneer10` cloud row:
  - `agent_version`: `3.13.9`
  - `status`: `offline`
  - `health_status`: `unknown`
  - `last_heartbeat_at`: `2026-05-06T23:38:48.589Z`
  - sanitized stats: `helper.attached=false`, `sql_connected=false`,
    `pioneerrx_status=not_connected`, writeback counts `0`
- `Queen` cloud row:
  - `agent_version`: `3.14.0`
  - `status`: `offline`
  - `last_heartbeat_at`: `2026-05-06T17:21:44.859Z`

### Failure Classification

This is not "agent missing" and not "process crashed." It is a stronger
production failure mode:

- Local services and Helper are alive.
- Local binaries are newer than the cloud row reports.
- Cloud heartbeat is stale and still reports old version/config state.
- Health evidence files that should let the dashboard classify config/watchdog
  health are absent on the installed box.
- A normal user PowerShell could not restart the services:
  - `Restart-Service SuavoAgent.Broker -Force` failed.
  - `Restart-Service SuavoAgent.Watchdog -Force` failed.
  - services remained running afterward.
- UAC elevation could be requested, but an elevated PowerShell did not become a
  reliable controllable surface through CRD in this pass.

### Product Implications From Continuation

- Track 1 needs an explicit "local alive, cloud stale" classifier. Service
  liveness alone is false comfort.
- Track 1 repair cannot depend on a normal helper/user shell. It needs a
  service-owned repair path or a signed local repair bootstrap that can run with
  the required service-control rights.
- Track 4 dashboard should show version drift as two separate facts when known:
  installed local version evidence and cloud-reported heartbeat version. In this
  field pass they diverged: local `3.14.4+...`, cloud `3.13.9`.
- Track 5 remote actuation must never type into arbitrary focused windows. The
  login-field focus error is a concrete proof that observe/propose/show-cursor
  must remain the pilot mode until active-window and typed-workflow guards are
  enforced.
- The field support tool should prefer short, idempotent, non-PHI commands with
  visible echo checks. Broad paste and opaque multi-command scripts are brittle
  through CRD.
