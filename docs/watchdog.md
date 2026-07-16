# Watchdog and native maintenance

SuavoAgent uses Windows Service Control Manager, a dedicated Watchdog service,
and the installed `SuavoAgent.Maintenance.exe` host to recover without asking a
customer to use a terminal.

This document describes the current native architecture and its release bar. It
does not prove that a particular signed build has passed clean-Windows or
pharmacy hardware validation.

## Runtime layout

```text
Windows Service Control Manager
├── SuavoAgent.Core       LocalService   cloud, policy, and reasoning
├── SuavoAgent.Broker     LocalSystem    interactive-session supervisor
└── SuavoAgent.Watchdog   LocalSystem    health and native recovery
        └── SuavoAgent.Maintenance.exe   fixed signed repair/uninstall host

Interactive Windows session
└── SuavoAgent.Helper     de-privileged UI observation and approved actuation
```

Broker and Watchdog require narrowly scoped system authority for session and
service coordination. Helper performs desktop work with the signed-in user's
token. Core remains least-privileged. The installed directory and component
cohort must prevent a lower-privileged process from substituting the maintenance
host before Watchdog launches it as LocalSystem.

## Recovery ladder

### Tier 1 — Windows service recovery

Windows restarts a crashed required service using its configured backoff. This
handles transient process failures without customer action.

### Tier 2 — Watchdog restart

Watchdog observes Core and Broker state and Core liveness. After the configured
grace period, it asks Windows to start or cycle the unhealthy service. Attempts
are backoff-controlled so a broken component cannot create a tight loop.

### Tier 3 — native cohort repair

After repeated restart failures, Watchdog invokes the fixed adjacent
`SuavoAgent.Maintenance.exe` host. Before a privileged launch, the caller must
reject a missing, renamed, relocated, untrusted, or cohort-mismatched host.
Native maintenance then:

1. verifies its trust proof and installed cohort;
2. reasserts the required Program Files and ProgramData access controls;
3. repairs the Core, Broker, and Watchdog service registrations;
4. restarts the required services without stopping Watchdog mid-repair; and
5. reports success only when the complete required service cohort is running.

Repair does not use or persist a script. It must not delete tenant binding,
consent, operator configuration, retained audit evidence, or pharmacy data.

### Tier 4 — visible escalation

If native repair cannot establish a trusted cohort, Watchdog stops escalating
and surfaces a sanitized failure through the health path. The dashboard must
show **Needs attention** rather than silently retrying or displaying healthy.

## Dashboard repair

The supported remote path is:

1. an authorized operator requests **Repair** in the Suavo dashboard;
2. Core verifies the signed cloud command, intended agent, schema, freshness,
   and replay state;
3. the service-owned handoff is authenticated and consumed atomically;
4. Watchdog launches the trusted native maintenance host; and
5. the dashboard receives the matching acknowledgement and refreshed
   diagnostics.

The request handoff is a security boundary, not a presence flag. A build cannot
pass the release gate if a locally writable or malformed marker can trigger a
LocalSystem repair, if a request can be replayed, or if the acknowledgement is
not bound to the initiating command.

## Local repair

The supported customer path is **Windows Settings → Apps → Installed apps →
SuavoAgent → Modify/Repair**. Windows launches the installed maintenance host
registered by setup. Customers must not run service-control commands, launch a
repair executable by path, or edit the registry.

## Installation and updates

- The signed WiX Burn `SuavoAgent-Setup.exe` bundle is the only
  customer installer. Its embedded `SuavoSetup.exe` is an internal signed
  maintenance payload, not a customer entry point.
- Setup stages and verifies the native maintenance host before registering
  repair and uninstall with Windows.
- Setup is successful only when Core, Broker, and Watchdog are running. Helper
  may wait for an interactive sign-in, but that state must be explicit.
- Dashboard-driven OTA is the only customer update path. A complete signed
  cohort is staged before activation; no one replaces files in Program Files.
- A failed activation must recover to the last known-good cohort and remain
  visible in the dashboard.

## Diagnostics

Customers and first-line support use **Diagnostics** in the dashboard. The
built-in `fetch_diagnostics` command returns a PHI-safe summary of service
health, Helper attachment, version, cloud/config health, and native maintenance
presence.

Raw logs, configuration files, screenshots, prescription numbers, credentials,
and patient data are not customer support artifacts. Engineering may access
deeper evidence only under an approved, audited support procedure.

## Configuration ownership

Watchdog timing, service names, request locations, trust keys, and access-control
rules are product configuration. Customers do not edit them. A field-specific
override must be signed, schema-validated, visible in the dashboard, and
reversible.

## Release evidence required

Before calling native recovery production-ready, the exact signed build must
prove on clean Windows that:

- setup registers Core, Broker, Watchdog, native repair, and native uninstall;
- each privileged maintenance launch rejects substitution and cohort mismatch;
- automatic, dashboard, and Windows Settings repair restore all required
  services and return matching receipts;
- repair cannot deadlock by waiting for a process it must stop;
- updates and rollback cover the same signed component cohort;
- diagnostics remain PHI-safe; and
- no customer step requires a terminal, script, manual service restart, registry
  edit, or file replacement.

Until that evidence is attached to a release, the architecture is implemented
work, not a Windows-validated field claim.

## Related

- `docs/sales/windows-agent-lifecycle.md`
- `docs/hardening/release-gate.md`
- `src/SuavoAgent.Watchdog/WatchdogWorker.cs`
- `src/SuavoAgent.Watchdog/ServiceCommand.cs`
- `src/SuavoAgent.Setup/Maintenance/`
- `src/SuavoAgent.Diagnostics/Maintenance/`
