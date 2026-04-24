# `SuavoAgent.Adapters.ComputerRx.UIA`

**Populate after process + UI fingerprint recon.**

See `docs/self-healing/second-pms-integration-spec.md` §"Process + UI
fingerprint" for the open questions that gate implementation:

- Process name when running (likely `CRxPro.exe` or similar — confirm)
- Main window title pattern
- Menu structure for "new Rx", "refill", "dispense", "record delivery"
- UIA tree shape — does Computer-Rx expose control IDs, names, automation
  properties? Or is it a thin Win32 shell that blocks UIA?

UIA observer is the Tier-2 fallback per the sandwich pattern — Tier-1 SQL
preferred, Tier-2 UIA always available. Scaffolded empty so the csproj
compiles and the solution layout matches the spec.

> DO NOT IMPLEMENT until Tier 1 5b kickoff per Mission memo.
