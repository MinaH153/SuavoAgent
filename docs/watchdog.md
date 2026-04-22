# Watchdog service — tiered self-healing

SuavoAgent runs three Windows services. Two of them (Core, Broker) do real
work and occasionally fail. The third (Watchdog) exists to restart the
other two before a human has to get involved.

This doc explains what the Watchdog does, how to debug it, and what the
escalation tree looks like when it gives up. It is the reference companion
to `docs/runbooks/agent-heartbeat-dead.md` in the Suavo web repo, which
tells the on-call operator what to do.

## Architecture

```
┌────────────────────── Windows SCM ──────────────────────┐
│                                                         │
│   SuavoAgent.Core  ──► LocalService, restart 5/30/60s  │
│   SuavoAgent.Broker ──► NetworkService, restart 5/30/60s│
│   SuavoAgent.Watchdog ──► LocalSystem, restart 10/60/300s │
│                                                         │
└─────────────────────────────────────────────────────────┘
          ▲
          │ sc.exe query / sc.exe start
          │
┌──────── Watchdog decision engine (poll every 60s) ──────┐
│                                                         │
│  observed state (Running / Stopped / StartPending /     │
│  StopPending / NotInstalled / Unknown) + ledger         │
│  → decision (DoNothing / AttemptRestart / Escalate      │
│  Repair / Alert)                                        │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

The decision engine is a pure function (`WatchdogDecisionEngine.Decide`)
so it's trivially testable. See
`tests/SuavoAgent.Watchdog.Tests/WatchdogDecisionEngineTests.cs` for the
10-point test coverage.

## Service placements and why

| Service | Account | Failure actions | Rationale |
| --- | --- | --- | --- |
| `SuavoAgent.Core` | `NT AUTHORITY\LocalService` | 5s → 30s → 60s | Least privilege. SQL access is via `Integrated Security=true`; `LocalService` is sufficient because PioneerRx's SQL auth scheme accepts the machine account. |
| `SuavoAgent.Broker` | `NT AUTHORITY\NetworkService` | 5s → 30s → 60s | Needs `SeTcbPrivilege` (held by NetworkService when configured as a service) for `WTSQueryUserToken` + `CreateProcessAsUser` calls. `LocalSystem` was excessive. |
| `SuavoAgent.Watchdog` | `LocalSystem` | 10s → 60s → 5min | Needs SCM control of the other services and rights to invoke `bootstrap.ps1 -Repair` under arbitrary user contexts. Longer SCM recovery windows because Watchdog churn would mask real problems. |

## The three-tier self-healing tree

When Core or Broker dies, the following sequence runs without human input:

### Tier 1 — Windows SCM

SCM's own failure-action config restarts the service in 5 seconds. For ~95%
of transient crashes this is the only tier that fires. The restart is
invisible: no cloud event, no runbook.

### Tier 2 — Watchdog decision engine

If Core or Broker is `Stopped` (or `StopPending`, `Unknown`) for longer
than the **UnhealthyGrace** (5 minutes), the Watchdog calls `sc.exe start`.
This is the tier that catches:

- SCM exhausted its three-attempt budget (the 5/30/60 config).
- Service entered a stuck state SCM doesn't recognise as a failure.
- `start= delayed-auto` races during post-reboot where the boot
  dependencies aren't quite ready.

Between restart attempts the Watchdog waits **RestartBackoff** (60 s) to
avoid tight-looping a permanently broken binary.

### Tier 3 — Bootstrap repair

After **EscalateAfterConsecutiveFailures** (3) restart attempts in a row
fail, Watchdog invokes `bootstrap.ps1 -Repair`. This re-applies service
registration, ACLs, and config without touching operator data (SQL creds,
consent receipt). It's safe to run idempotently.

### Tier 4 — Alert

If Watchdog has no remediation path (bootstrap not configured, service
state reported as `NotInstalled` after a repair run), it emits a
`LogCritical` line. The install-telemetry + crash-log-upload paths
surface this to cloud; the `agent-heartbeat-dead.md` runbook takes over.

## Configuration surface

```csharp
new WatchdogOptions
{
    WatchedServices = ["SuavoAgent.Core", "SuavoAgent.Broker"],
    PollInterval = TimeSpan.FromSeconds(60),
    StartTimeout = TimeSpan.FromSeconds(90),
    RepairTimeout = TimeSpan.FromMinutes(5),
    BootstrapPath = @"C:\SuavoAgent\bootstrap.ps1",
}
```

All fields are init-only. Tunables live in `appsettings.json` under a
`Watchdog:` section if operators ever need to override; default values
above are appropriate for every pharmacy we have in-flight and all
future ones unless proven otherwise.

## Install paths — which one installs Watchdog?

All three paths install Watchdog as of 2026-04-22:

| Path | Invokes | Watchdog? |
| --- | --- | --- |
| `suavo-agent-<pharmacy>.cmd` (pharmacy self-service) | bootstrap.ps1 | Yes (line 1046–1050) |
| `install.ps1` (fleet deploy scripts) | install.ps1 | Yes (line 212, 1047) |
| Avalonia `SuavoSetup.exe` (GUI or `--console`) | ServiceInstaller.cs | Yes (as of this commit) |

Before this commit, the GUI installer path silently skipped Watchdog. The
.cmd / bootstrap path was unaffected — Nadim's 2026-04-25 pilot uses the
.cmd path. The GUI path is pharmacy #2+ territory.

## Debugging

### Is Watchdog running?

```powershell
sc.exe query SuavoAgent.Watchdog
```

Expect `STATE : 4 RUNNING`. If `STOPPED`, Windows SCM will restart it in
10 seconds via the failure-action config. If `NotInstalled`, the installer
skipped it — check install logs and reinstall with current bootstrap.

### What is Watchdog deciding?

```powershell
Get-Content "$env:PROGRAMDATA\SuavoAgent\logs\watchdog-*.log" -Tail 50
```

Every tick logs `observed=<state> action=<decision> reason=<why>`. A long
run of `action=DoNothing reason=running` means Core + Broker are healthy.
A sudden shift to `action=AttemptRestart` indicates tier-2 kicked in.
`action=EscalateRepair` means tier-3. `action=Alert` means tier-4 —
bring the [agent-heartbeat-dead.md](../../Suavo/docs/runbooks/agent-heartbeat-dead.md)
runbook up on a phone.

### Watchdog itself is dead

SCM's failure-action config restarts it. If SCM gives up after three
attempts, the agent is in a very bad state — the only recovery is a
full reinstall from the dashboard. This has never happened in the field
as of 2026-04-22.

## Gaps + future work

- **Watchdog observability via cloud** — Watchdog's decisions are local.
  A future enhancement: every tier-2+ action emits a telemetry event to
  `install_telemetry_events` so Mina can see "pharmacy X had 3 Core
  restarts this hour" in the supervisor dashboard.
- **Self-watchdog** — SCM's failure-action config is the only thing that
  restarts Watchdog. If SCM itself is compromised (malware, registry
  corruption), there's no fallback. A remote "kill-switch + reinstall"
  command is the mitigation; lives in the Mission Loop roadmap.
- **Cross-service kill-then-restart dance** — currently Core and Broker
  start independently. A future optimisation: if Core restarts, Broker
  should be reset too so its cached Core-session tokens don't linger.

## Related

- `src/SuavoAgent.Watchdog/WatchdogDecision.cs` — decision engine (pure).
- `src/SuavoAgent.Watchdog/WatchdogWorker.cs` — background service.
- `src/SuavoAgent.Setup/ServiceInstaller.cs` — GUI installer path.
- `bootstrap.ps1` lines 1042–1050 — .cmd installer path.
- `install.ps1` — fleet deploy path.
- `docs/self-healing/invariants.md` — Phase 0 spec for Mission Loop.
- Suavo web `docs/runbooks/agent-heartbeat-dead.md` — operator-facing runbook.
