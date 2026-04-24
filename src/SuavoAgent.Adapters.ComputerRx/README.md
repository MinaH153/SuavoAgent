# `SuavoAgent.Adapters.ComputerRx`

> **DO NOT IMPLEMENT until Tier 1 5b is unblocked per Mission memo.**
> (`.claude/projects/-Users-joshuahenein/memory/MEMORY.md` →
> *Mission: Square-level Suavo ecosystem, revenue-first* — second PMS is
> deferred until revenue hums.)

This project is a scaffold. Every meaningful method throws
`NotImplementedException`. It exists so:

1. The solution layout reads as intentional ("we already know we need a
   second PMS adapter, and we know where it will live").
2. The adapter type `"computerrx"` can be referenced from install-time
   discovery and DI wiring without invoking speculative logic.
3. The recon gating tasks are colocated with the folder that will contain
   the implementation once they close.

## Recon questions — ALL must be answered before any code lands

These are lifted from
`docs/self-healing/second-pms-integration-spec.md` and pinned here so the
checklist lives alongside the files that will be written against it.

### Process + UI fingerprint
- [ ] Process name when running (likely `CRxPro.exe` or similar — confirm)
- [ ] Main window title pattern
- [ ] Menu structure for "new Rx", "refill", "dispense", "record delivery"
- [ ] UIA tree shape — exposes control IDs, names, automation properties?
      Or thin Win32 shell that blocks UIA?

### Data layer
- [ ] SQL Server backend? MSDE / SQL Express local? Or something more
      exotic (Btrieve, flat files)?
- [ ] Connection discovery path — config file? Registry key? Network
      probe? Environment variable?
- [ ] Auth — Windows integrated vs SQL auth
- [ ] Schema name(s) the vendor uses
- [ ] Core tables — what is the Rx record? What ties to a patient? What
      holds fill status?
- [ ] Writeback surface — is there a supported "mark delivered" path or do
      we have to UIA-drive it?

### Deployment model
- [ ] Is Computer-Rx installed at `C:\Program Files\...\` consistently
      across sites?
- [ ] Does it auto-start at login?
- [ ] How does it handle multi-user (one cashier at POS, pharmacist on
      back bench)?
- [ ] Does it enforce workstation-locking that would break a long-running
      agent session?

### Reverse-engineering risk
- [ ] Terms of service — does Computer-Rx's EULA prohibit SQL-level
      access by third parties?
- [ ] Is there a vendor integration API (REST/SOAP) that could avoid SQL
      entirely? Cost/access terms?

### Pharmacy acquisition
- [ ] Which Computer-Rx pharmacies can Joshua walk into in San Diego /
      LA / Phoenix?
- [ ] Is Nadim friends with any Computer-Rx-using pharmacist? (Nadim
      referral = 10x easier sell than cold.)
- [ ] What do Computer-Rx users pay per month today? Their pricing
      informs our per-delivery commission target.

### Compliance diff
- [ ] Does Computer-Rx have its own HIPAA attestation / BAA templates we
      can lean on?
- [ ] DEA chain-of-custody integration — does Computer-Rx already log
      C-II handoffs? If yes, do we parallel their log or supplement it?

## Folder layout (matches `SuavoAgent.Adapters.PioneerRx`)

```
SuavoAgent.Adapters.ComputerRx/
├── SuavoAgent.Adapters.ComputerRx.csproj
├── Canary/
│   └── ComputerRxCanarySource.cs        # ICanaryDetectionSource impl — throws
├── Sql/
│   └── README.md                        # populate after data-layer recon
├── UIA/
│   └── README.md                        # populate after UI-fingerprint recon
└── README.md                            # this file
```

## Related

- `docs/self-healing/second-pms-integration-spec.md` — the master spec
- `src/SuavoAgent.Adapters.PioneerRx/` — reference implementation to mirror
- `.claude/projects/-Users-joshuahenein/memory/second-pms-integration-spec-2026-04-22.md`
