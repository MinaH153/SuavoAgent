# PioneerRx Dress Rehearsal — Simulator + Full-Chain Harness

**Why this exists:** the UIA pricing path (`PricingWorkflow`, 681 lines, written from Apr 4
field screenshots) is built and hardened but has **never executed against any UI**. This tool
makes the REAL production chain drive a PioneerRx-**shaped** WPF app before it ever touches
Nadim's live box — so the first contact with reality happens on a disposable VM, not on
tenant zero.

```
┌──────────────────────────────┐    named pipe     ┌─────────────────────────────────┐
│ <stage>\Core\                │  SuavoAgent-cmd-* │ <stage>\Helper\                 │
│  SuavoAgent.Core.exe         │ ⇄ ───────────── ⇄ │  SuavoAgent.Helper.exe (REAL)   │
│  (rehearsal driver, renamed  │                   │   IpcCommandServer (REAL, incl. │
│   apphost — REAL components: │                   │    VerifyClientIsCore gate)     │
│   HelperInteractivePreflight │                   │   PricingWorkflow (REAL)        │
│   DiscoveryClient (find_file)│                   │   PioneerRxUiaEngine (REAL,     │
│   UiaFirstPricingJobExecutor │                   │    UIA2, attaches by process    │
│   PricingJobRunner           │                   │    name "PioneerPharmacy")      │
│   AgentStateDb / Excel R+W)  │                   └──────────────┬──────────────────┘
└──────────────────────────────┘                            UIA2 + SendInput
                                                    ┌──────────────▼──────────────────┐
                                                    │ <stage>\Sim\PioneerPharmacy.exe │
                                                    │  (WPF simulator — this project) │
                                                    └─────────────────────────────────┘
```

## What is REAL vs simulated vs mirrored

| Layer | Status |
|---|---|
| `HelperInteractivePreflight` (blind-run gate, console-session check) | REAL |
| File discovery (`find_file` IPC → Helper `FileLocatorService`, `--mode full`) | REAL |
| `UiaFirstPricingJobExecutor` → `PricingJobRunner` (throttle, B1 abort, resume DB) | REAL |
| `IpcCommandClient` ⇄ `IpcCommandServer` (framing, ACLs, **client process verification**) | REAL — gate passed honestly via staged rename, never relaxed |
| `SuavoAgent.Helper.exe` (attach loop, install detector, observers, resource guard) | REAL production binary |
| `PricingWorkflow` + `PioneerRxUiaEngine` (UIA2) | REAL — **unmodified**; the sim conforms to them |
| `ExcelPricingReader/Writer` + sibling writeback + `AgentStateDb` persistence | REAL |
| PioneerRx itself | **Simulated** (this WPF app; stock `DataGrid`, not DevExpress — see Fidelity limits) |
| HeartbeatWorker glue (`HandleFindAndRunPricingJobAsync` is private on the cloud-coupled worker) | **Mirrored verbatim** in the driver: preflight → discovery → `IsExcelPathSafe` → spec → executor |
| Cloud ack / `PricingJobCloudUploader` | Out of scope (next pass) |

The simulator's automation surface matches what the workflow asserts: menu **bar** →
`Item` → `Rx Item`, a top-level window titled `Edit Rx Item`, a `ControlType.Edit` Quick
Search with `HelpText="Quick Search"`, a `Pricing` tab item, and a **virtualized** grid whose
rows are `DataItem` children, cells `Custom` children with **ValuePattern** full text, and
`Header/HeaderItem` names `Supplier` / `Cost Per Unit` / `Status` (+ decoys). The grid is
sorted supplier-ascending (cheapest is never row 1) and rows arrive in timed batches to
exercise `WaitForStableRows`.

## Running it (Windows VM)

Requirements: Windows 10/11 x64 · .NET 8 SDK · **the hypervisor CONSOLE session, not RDP**
(an RDP session is not the active console; the interactive-session gate will — correctly —
refuse to drive the screen) · admin once for the PMS install marker · don't touch mouse or
keyboard during a run (the workflow sends real clicks/keys).

```powershell
git checkout feat/pioneerrx-rehearsal-sim
pwsh tools/PioneerRxRehearsal/rehearsal.ps1 -CreatePmsMarker     # first run, elevated
pwsh tools/PioneerRxRehearsal/rehearsal.ps1                      # quick faithful run (~3 min)
pwsh tools/PioneerRxRehearsal/rehearsal.ps1 -Mode full           # + REAL Desktop file discovery
pwsh tools/PioneerRxRehearsal/rehearsal.ps1 -Variant virtual-depth
```

The PMS marker (`C:\Program Files (x86)\New Tech Computer Systems\PioneerRx\PioneerPharmacy.exe`,
a text file) satisfies the real Helper's `PioneerRxInstallDetector` so its attach loop runs;
the engine then attaches to the **sim** because the engine resolves by process name.
Cleanup: delete the marker dir + `.rehearsal-stage\` + the Desktop workbook.

Exit codes: `0` conformance pass · `2` conformance fail (**a real workflow bug if the sim is
faithful — exactly what we want found here**) · `3` chain/setup failure · `64` bad args.
`PROBE` rows never affect the exit code; they print graded `FINDING` lines.

## Variant matrix — what each run proves

| Variant | Surface change | Expected outcome |
|---|---|---|
| `faithful` (default) | None — the asserted surface | All MustMatch rows `OK`/correct: min-across-rows (not row 1), cheaper-but-Discontinued excluded, full supplier name via ValuePattern despite truncated cell Name, sub-cent precision, all-discontinued → explicit error, unparseable NDC → `ERROR: Invalid NDC:` |
| `renamed-cost` | `Cost Per Unit` → `Unit Cost` | Every row fails **closed**: `ERROR: Pricing grid schema not recognized…` — proves no ordinal fallback, no wrong-column writeback |
| `slow-grid` | Row batches at 2.2 s / 3.4 s | **Probe:** the 1.2 s inter-batch gap exceeds the ~500 ms stable-row quiet window → likely partial-set minimum returned as `OK`. Finding: quiet window too short for slow grids |
| `glacial-grid` | Rows at 6.5 s (> 5 s `GridLoadTimeout`) | `ERROR: Pricing grid has no rows` — timeout fails closed, no phantom price |
| `wpf-menu` | Stock WPF `Menu` (UIA `ControlType.Menu`) instead of `MenuBar` | Every row: `ERROR: Could not open Item → Rx Item menu` — the control-type finding (below) |
| `currency-cells` | Costs render `$0.3719` | Every row: `ERROR: No usable supplier rows…` — `TryParseCost` is InvariantCulture; `$` never parses. Verify on the real box whether the grid renders currency symbols |
| `virtual-depth` | 40 suppliers, true cheapest ~25 rows below the realized window | **The UIA2 virtualization answer** (below). PASS = unrealized rows readable; FAIL (realized-subset supplier returned) = virtualization blindness |

Sim-behavior switches (independent of variant): `-ClearSearchAfterLoad` (Quick Search box
cleared on successful load) and `-NoPersistLastItem` (reopened Edit Rx Item starts blank).
Defaults are the **adversarial** choices: text retained + last item persisted.

## Findings already established from code (verify on VM, fix before Nadim)

1. **CRITICAL — `VerifyLoadedNdc` is tautological when the Quick Search box retains the
   typed text.** The scan enumerates `Edit` controls FIRST; the search box itself contains
   the just-typed NDC, so verification passes for ANY NDC — including no-matches (stale grid
   from the persisted previous item gets priced and written back as `OK`) and Do-Not-Use
   items (the `(Do Not Use)` guard in the item-name `Text` element is never reached).
   The no-match and DNU probe rows demonstrate both. Fix candidate: exclude the search box
   (or all focusable `Edit`s) from the verify scan, or require the match in a non-input element.
2. **MenuBar vs Menu control type.** The workflow asserts `ControlType.MenuBar`; a WPF-native
   menu reports `ControlType.Menu`. Whether real PioneerRx reports MenuBar (Win32/WinForms/
   DevExpress bar) or Menu (WPF) is unknowable from screenshots — first task on any real
   PioneerRx contact: inspect with FlaUInspect/Accessibility Insights. `wpf-menu` shows the
   failure mode; fix candidate: try MenuBar then Menu.
3. **Stable-row quiet window (2×250 ms) is shorter than realistic lazy-load gaps** —
   `slow-grid` demonstrates a partial-minimum returned as success.
4. **`$`-prefixed costs don't parse** under InvariantCulture (`currency-cells`).
5. **Cosmetic:** "No usable supplier rows in Pricing tab" lands as `ERROR:` text rather than
   the `NO_SUPPLIER_ROWS` marker (`MarkerFor` substring miss).

## UIA2 vs UIA3 — the verdict

**UIA2 (`UIA2Automation` / managed `System.Windows.Automation`) DOES resolve a WPF
grid structurally.** WPF is natively a UIA provider (managed UIA was built for it): window,
menu, tabs, `DataGrid` → `DataItem` rows → `Custom` cells, `Header/HeaderItem`, and
ValuePattern full-text cells all resolve through UIA2 — the sim runs prove the whole chain on
exactly that stack, so "UIA2 can't see WPF/DevExpress" is NOT the risk.

The real exposure is **row virtualization + read strategy**, not the client library:
- Under both UIA2 and UIA3, virtualized (unrealized) rows either don't appear or appear
  without readable cells. `PricingWorkflow` reads `FindAllChildren(DataItem)` once stable —
  it never scrolls and never uses `ItemContainerPattern`/`VirtualizedItemPattern` (the
  realize-on-demand mechanism, which is UIA3-flavored; FlaUI.UIA2's support is partial).
  `virtual-depth` measures the blast radius empirically: if it FAILS, the workflow needs a
  scroll-and-merge loop (UIA2-compatible) or a move to FlaUI.UIA3 + ItemContainerPattern
  before any long supplier list can be trusted.
- **DevExpress caveat:** real PioneerRx is DevExpress-styled; DevExpress WPF grids expose a
  CUSTOM automation tree (rows sometimes `Custom` under intermediate panels rather than
  `DataItem` children, header/cell alignment differs). The sim deliberately presents the
  surface the workflow asserts; whether DevExpress matches it can only be answered on a real
  PioneerRx install. The workflow's failure-path candidate scans (`ScanCandidates` →
  ControlType/AutomationId/ClassName telemetry) are the designed way to learn the true tree
  on first real contact.

**Bottom line:** stay on UIA2 for the pilot; the virtualization read strategy — not the UIA
version — is the thing to fix if `virtual-depth` fails, and DevExpress tree shape is the one
question only Nadim's box (or any PioneerRx demo install) can answer.

## Fidelity limits + next pass

- Sim is a stock WPF `DataGrid`, not DevExpress `GridControl` (tree-shape caveat above);
  cell-Name truncation is emulated via `AutomationProperties.Name`.
- Real `SuavoAgent.Helper.exe` runs, but Core-the-service does not: the driver composes the
  executor in-process (Helper's main `--pipe` has no listener; its salt fetch degrades
  gracefully by design). Broker/Watchdog/cloud acks are out of scope here.
- Not yet exercised: crash-resume mid-job, selector patches (M2b), 500-row endurance +
  `MaxConsecutiveIpcFailuresBeforeAbort`, `run_pricing_job` (vs `find_and_run`) ack shapes,
  concurrent operator mouse traffic.
- `tests/SuavoAgent.UiaHarness` (buttons-only WinForms rig) is untouched — it serves the
  actuation/loop tests; this tool owns the pricing surface.

Projects here are deliberately **not** in `SuavoAgent.sln` (same precedent as the UIA
harness): the WPF sim needs the WindowsDesktop SDK. `PioneerRxSim.Shared` and
`PioneerRxRehearsal` build anywhere (`dotnet build` on macOS/Linux works); `PioneerRxSim`
itself builds on Windows.
