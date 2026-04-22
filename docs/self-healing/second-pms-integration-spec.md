# Second-PMS Integration Master Spec (Tier 1 item 5b — existential)

**Status**: draft v0.1, 2026-04-22
**Author**: Claude (session 53e55d5d)
**Reviewer required**: Codex + Joshua before any code lands
**Target merge**: one non-PioneerRx pharmacy running the closed loop inside 90 days (end of July 2026)

---

## The problem (one paragraph)

On 2025-11, RedSail Technologies (owner of PioneerRx, dominant retail pharmacy PMS) acquired
RxMile, an in-house prescription delivery platform. RedSail now owns both halves of what Suavo
bundles: PMS + delivery. Suavo's "PioneerRx-native" moat, which we spent the last 4 months building,
stops being a moat the moment RedSail bundles RxMile as free-with-PioneerRx. Our structural counter
is to ship a second PMS integration and reposition as **"works with any PMS, unlike RxMile."** This
spec describes how.

The full strategic context lives in `competitor-deep-dossier-2026-04-22.md` and
`suavoagent-95pct-roadmap-22-subprojects.md` item 5b.

## Success criteria

Shipped:
1. A second PMS adapter project exists in `src/` with its own `*.Adapters.<PmsName>` and its own
   `Canary/` + `Sql/` + (optional) `Uia/` tree, mirroring `SuavoAgent.Adapters.PioneerRx/`.
2. One real pharmacy using a non-PioneerRx PMS is on SuavoAgent with a live Rx detection +
   writeback cycle.
3. Marketing copy on the public site + trust page + pitch deck reads "works with PioneerRx,
   Computer-Rx, [PMS #2 name], and any desktop PMS via our Universal Observer."
4. The SuavoAgent configuration picks the right adapter at install time based on discovery (process
   name, window class, or operator selection), and the rest of the platform — dashboard, fleet
   cockpit, driver app, writeback — stays entirely PMS-agnostic above the adapter layer.

## PMS selection: which one first?

### The short list

| PMS | US market share (est.) | Pharmacy type | Why it's a candidate | Why it's not |
|-----|------------------------|---------------|----------------------|--------------|
| **Computer-Rx** | ~4,000 pharmacies (independent-heavy) | Independent retail | Strong in non-PioneerRx independents. If we already own independents via Nadim, adjacent expansion. | Binary-only; no public API; likely requires same sandwich + SQL probe as PioneerRx. |
| **McKesson EnterpriseRx** | ~5,000 pharmacies (chain + hospital) | Chain retail + outpatient | Biggest-ticket customers. One McKesson win = multi-pharmacy land-and-expand. | Enterprise sales cycle 6-12 months. Hospital IT gates. Does NOT match Joshua's solo cadence. |
| **QS/1 NexGen** | ~2,000 pharmacies | Independent + LTC | Same vertical as PioneerRx (independents). Same-day-delivery-friendly. | Recent RedSail acquisition too — parent company is the same threat. Low diversification value. |
| **BestRx** | ~2,500 pharmacies | Independent | Cheap, wide footprint. Good diversification against RedSail. | Smaller engineering surface — integrations are sparse and quirky. |
| **Liberty Software** | ~1,000 pharmacies | Independent + LTC | LTC vertical opens nursing-home delivery which is a different buyer. | LTC workflow is different enough to need its own product surface. |
| **Rx30 / FrameworkLTC** | ~1,500 pharmacies | LTC | Same as Liberty — LTC surface. | Same caveat. |
| **ScriptPro** | Robotics-centric | Any chain | Not a PMS per se — Rx-dispensing automation. | Out of scope; we'd integrate alongside, not replace. |

### Recommendation: **Computer-Rx first, McKesson EnterpriseRx second.**

Reason:
- Computer-Rx shares the **architectural assumption** that made PioneerRx work: Windows desktop, local SQL Server, operator-installed single-tenant. Our sandwich pattern (UIA observer + optional SQL adapter via schema discovery) transfers without a paradigm shift.
- Computer-Rx customers are the same buyer profile as Nadim — owner-operator independent pharmacist who makes his own software decisions. Sales motion is the same one we already practice.
- McKesson EnterpriseRx stays on the roadmap as PMS #3 after Computer-Rx proves the sandwich generalizes. It's a different sales motion and will need a partnership or integration team rather than a solo-founder drop-in install.

The rest of this spec targets Computer-Rx specifically. Parallel assumptions apply to McKesson.

## Architectural principles (copied from PioneerRx, keep honest)

1. **Adapter layer only.** Everything from `Rx detection → cloud task → driver dispatch → writeback` stays adapter-agnostic. The only code that cares about Computer-Rx is under `src/SuavoAgent.Adapters.ComputerRx/`.
2. **Sandwich pattern.** UIA observer is always live (cheap, no SQL dependency). SQL adapter is opt-in after install-time probe discovers the DB. If SQL works, we prefer it for reads and writebacks; if not, UIA fallback runs.
3. **Schema canary is mandatory.** `ComputerRxCanarySource : ICanaryDetectionSource` mirrors `PioneerRxCanarySource`. Nothing ships against a PMS without a canary contract that fails closed.
4. **No hardcoded GUIDs.** Status lookup tables discovered at install time, cached per-pharmacy, re-validated on every canary tick.
5. **PHI boundary is re-enforced.** `PhiColumnBlocklist` in the new adapter must be tighter than PioneerRx, not looser. Computer-Rx's patient-data model is non-identical (confirm at install time, do not assume).
6. **Publish bundle gain is additive.** Adding Computer-Rx to the agent SKU must not break the single-file install for PioneerRx-only pharmacies. Adapter DLLs are discovered by adapter-type config, not compiled-in decision.

## What we need to know before writing code (Computer-Rx recon)

These are the open questions a research pass + a live visit (comparable to Nadim's Apr 8-10 PioneerRx deep dive) must answer before adapter implementation can start. They become the Computer-Rx equivalent of `nadim-pioneerrx-ground-truth.md` + `pioneerrx-schema-ground-truth.md`.

### Process + UI fingerprint
- [ ] Process name when running (likely `CRxPro.exe` or similar — confirm)
- [ ] Main window title pattern
- [ ] Menu structure for "new Rx", "refill", "dispense", "record delivery"
- [ ] UIA tree shape — does it expose control IDs, names, automation properties? Or is it a thin Win32 shell that blocks UIA?

### Data layer
- [ ] SQL Server backend? MSDE / SQL Express local? Or something more exotic (Btrieve, flat files)?
- [ ] Connection discovery path — config file? Registry key? Network probe? Environment variable?
- [ ] Auth — Windows integrated vs SQL auth
- [ ] Schema name(s) the vendor uses (PioneerRx uses `Prescription`, `Inventory`, etc.)
- [ ] Core tables — what is the Rx record? What ties to a patient? What holds fill status?
- [ ] Writeback surface — is there a supported "mark delivered" path or do we have to UIA-drive it?

### Deployment model
- [ ] Is Computer-Rx installed at `C:\Program Files\...\` consistently across sites?
- [ ] Does it auto-start at login?
- [ ] How does it handle multi-user (one cashier logged in at POS, pharmacist on back bench)?
- [ ] Does it enforce workstation-locking that would break a long-running agent session?

### Reverse-engineering risk
- [ ] Terms of service — does Computer-Rx's EULA prohibit SQL-level access by third parties?
- [ ] Is there a vendor integration API (REST/SOAP) that could avoid SQL entirely? Cost/access terms?

### Pharmacy acquisition
- [ ] Which Computer-Rx pharmacies can Joshua actually walk into in San Diego / LA / Phoenix?
- [ ] Is Nadim friends with any Computer-Rx-using pharmacist? (Nadim referral = 10x easier sell than cold.)
- [ ] What do Computer-Rx users pay per month today? Their pricing informs our per-delivery commission target.

### Compliance diff
- [ ] Does Computer-Rx have its own HIPAA attestation / BAA templates we can lean on?
- [ ] DEA chain-of-custody integration — does Computer-Rx already log C-II handoffs? If yes, do we parallel their log or supplement it?

## Proposed folder structure

```
src/
├── SuavoAgent.Adapters.ComputerRx/
│   ├── SuavoAgent.Adapters.ComputerRx.csproj
│   ├── ComputerRxConstants.cs          # process name, status-name strings, PHI blocklist
│   ├── Canary/
│   │   └── ComputerRxCanarySource.cs   # ICanaryDetectionSource impl
│   ├── Sql/
│   │   └── ComputerRxSqlEngine.cs      # connect + probe + read + writeback (mirrors PioneerRxSqlEngine)
│   ├── Uia/
│   │   └── ComputerRxUiaObserver.cs    # fallback UIA path
│   └── Pricing/                         # (optional — only if Computer-Rx exposes a cheapest-supplier surface)
├── SuavoAgent.Contracts/
│   └── Adapters/
│       └── AdapterType.cs               # "pioneerrx" | "computerrx" — discriminated at install/config
tests/
├── SuavoAgent.Adapters.ComputerRx.Tests/
```

## Adapter selection logic (install time)

Install-time detection layered like this:

```
if (process "PioneerPharmacy.exe" is running OR installed) → register pioneerrx adapter
else if (process <ComputerRx-name>.exe is running OR installed) → register computerrx adapter
else → operator picks from dropdown in /pharmacy/agent install flow
```

Selected adapter is persisted to `%ProgramData%\SuavoAgent\config-overrides.json`:

```json
{ "Adapter": { "Type": "computerrx" } }
```

DI composition (in `SuavoAgent.Core/Program.cs`):

```csharp
var adapterType = configuration["Adapter:Type"] ?? "pioneerrx";
services.AddAdapter(adapterType switch
{
    "pioneerrx" => typeof(PioneerRxSqlEngine),
    "computerrx" => typeof(ComputerRxSqlEngine),
    _ => throw new NotSupportedException($"Unknown PMS adapter: {adapterType}"),
});
```

This keeps the rest of Core unchanged — any component that consumes `ICanaryDetectionSource`,
`IRxDetectionAdapter`, `IWritebackAdapter` gets the right flavor by DI resolution.

## Canary contract shape for Computer-Rx (to be filled)

```
Required objects (TBD at recon):
  [Schema].[Table].[Column]  — e.g., dbo.Prescription.RxID
  [Schema].[Table].[Column]  — e.g., dbo.Patient.PatientID
  [Schema].[Table].[Column]  — e.g., dbo.Status.StatusName

Query template (TBD):
  SELECT TOP 50 ... FROM dbo.Prescription rx JOIN dbo.Status s ON ... WHERE ...

Expected result shape (TBD):
  RxNumber int, DateFilled datetime, TradeName nvarchar, NDC nvarchar, StatusGuid guid
```

Fill these in after Computer-Rx recon visit; shape contract identically to PioneerRx so canary
classifier logic doesn't branch per adapter.

## Test strategy

Mirror PioneerRx tests exactly:
- `ComputerRxCanarySourceTests` — covers contract baseline, preflight, detection-with-canary,
  connection-reset guard.
- `ComputerRxSqlEngineTests` — parameterized query builds, NDC column presence, schema fingerprint.
- `ComputerRxUiaObserverTests` — UIA tree probe against a recorded sample (use `.uiatree.json`
  fixtures as for PioneerRx).

Integration tests opt-in via `[Trait("category", "integration")]` requiring a mock Computer-Rx
SQL + UIA fixture (we do not have one yet — ship with the adapter).

## Timeline (6-9 weeks, solo)

| Phase | Weeks | Deliverable |
|-------|-------|-------------|
| **Recon** | 1-2 | Computer-Rx ground-truth memos + pilot pharmacy identified + BAA signed with first pharmacist |
| **Adapter scaffolding** | 3-4 | Empty project structure + config-driven DI + unit-test skeletons all green |
| **Canary contract** | 4-5 | ComputerRxCanarySource implemented against recon data + tests + local mock |
| **SQL reads + detection** | 5-6 | SQL engine connects + detects Rx + classifies status + tests |
| **UIA fallback** | 6-7 | Observer captures Rx events + a crude writeback path for pharmacies with no SQL access |
| **Live pharmacy install + writeback** | 7-8 | One Computer-Rx pharmacy installed + first live Rx detection + dashboard shows it |
| **Parity hardening + writeback** | 8-9 | Writeback verified on 10 consecutive Rx + schema canary + feedback loop + ship |

## Risks — ranked

1. **Computer-Rx's data layer is not SQL Server.** If it's Btrieve or some proprietary flat-file format, the SQL adapter path is impossible and the whole plan collapses into UIA-only, which is 10x slower + more brittle. **Unknown until recon. Highest risk.**
2. **Vendor terms of service prohibit third-party SQL access.** We risk Computer-Rx blacklisting the agent. Mitigation: prefer vendor-sanctioned integration if one exists; else get the pharmacist's written authorization under HIPAA §164.504.
3. **No Computer-Rx pharmacy in reach.** Joshua can't walk into a willing pharmacist's back-room to observe. Mitigation: activate the 2-3 pharmacist relationships that came through Nadim; if none pan out, shift to McKesson EnterpriseRx first.
4. **The sandwich pattern doesn't generalize.** PioneerRx may have been unusually well-shaped for our approach. If Computer-Rx's UIA is thin or hostile (WinForms vs WPF, or Win32-only), observer performance will degrade. **Hedge**: have a UIA-first demo planned for the first install; SQL is icing, not load-bearing.
5. **Recon takes longer than recon for PioneerRx.** That took ~2 weeks (Apr 8-10 at Nadim's + follow-ups). Computer-Rx recon could take 4+ weeks if no friendly pharmacy is reachable. **Hedge**: budget 2 weeks then reassess; if no recon access by end of week 2, fall back to marketing positioning + roadmap promise until access opens.

## What this spec does NOT commit to yet

- The exact Computer-Rx process name, SQL schema names, or writeback path — all TBD at recon.
- A specific pilot pharmacy — TBD.
- McKesson EnterpriseRx timeline — this spec covers Computer-Rx only.
- The universal observer / pack system that would abstract "any PMS" behind YAML configs
  (see `docs/superpowers/specs/2026-04-*-vertical-pack-*.md` + existing `VerticalPackTests`) —
  that is the direction, but shipping Computer-Rx first proves the second instance concretely
  before committing to full plug-in-ability.

## Decisions Joshua needs to make this week

1. **Which pharmacist** to approach for the Computer-Rx recon. Nadim's network first.
2. **Recon budget** — time (2 weeks vs 4 weeks before re-pivot to McKesson) and money (travel,
   honorarium for the pharmacist's time, any software licenses for parallel testing).
3. **Communicate externally now or after.** Do we update the website + pitch deck + fundraising
   narrative to "multi-PMS" *now* (before code ships) to claim the positioning, or wait until
   the first Computer-Rx install is live?
4. **Sequencing against Saturday pilot.** This spec cannot start until Saturday (Apr 25) is
   finished, so formally week 1 of the 6-9 week plan is **week-of-2026-04-27** at the earliest.
   That puts first Computer-Rx pilot at 2026-06-08 at the earliest. Is that acceptable?

## Related

- [Product Reality Check](../../../.claude/projects/-Users-joshuahenein/memory/suavoagent-product-reality-check-2026-04-22.md)
- [Competitor Deep Dossier — RedSail/RxMile bombshell](../../../.claude/projects/-Users-joshuahenein/memory/competitor-deep-dossier-2026-04-22.md)
- [95% Roadmap Tier 1 item 5b](../../../.claude/projects/-Users-joshuahenein/memory/suavoagent-95pct-roadmap-22-subprojects.md)
- [SuavoAgent Universal Vision](../../../.claude/projects/-Users-joshuahenein/memory/suavoagent-universal-vision.md)
- `src/SuavoAgent.Adapters.PioneerRx/` — reference implementation
- `docs/superpowers/specs/2026-04-13-schema-canary-design.md` — canary contract shape
