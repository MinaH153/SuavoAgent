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

