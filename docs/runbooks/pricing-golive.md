# Pricing Go-Live Runbook — the supervised last mile

The full pricing chain is **proven to work live end-to-end** against a running app with production code,
graded correct (the `pricing-rehearsal.yml` CI harness, `wpf-menu` variant: menu opened → NDC searched →
Pricing tab read → cheapest written correctly). What remains before **unattended** pricing on a pharmacy's
**real** PioneerRx is one supervised session — this runbook makes it fast and low-risk.

Hard rules (never relaxed):
- **Never blind-run** automation on a live pharmacy's PMS. Every step below is watched by a human until
  the supervised dry-run passes.
- **Fail-closed money safety** stays on: the sighted read is confirmed against the exact PMS cell; on
  any disagreement SuavoAgent writes nothing and flags the row. Never override this to "just get a number."
- Do this on a **canary/agreed box first** (Hillcrest, agent `15c16aae`) before Nadim's box.

---

## 0. Preconditions

- [ ] Agent installed + online on the target box (`/admin/agents` shows it healthy).
- [ ] PioneerRx open and logged in, on a real item so the Supplier Catalog has data.
- [ ] Vision provisioned if using the sighted read: `set_vision_config` applied (writes `vision.json`,
      SHA-pinned Tesseract into ProgramData, `BUILTIN\Users` RX grant). Confirm the Helper log shows
      `Vision pricing reader ENABLED`. (UIA-only pricing also works and needs no vision.)
- [ ] A small test worklist `.xlsx` on the box with a `NDC` column and ~5–10 real top-dispensed NDCs.

## 1. First-minutes — resolve the ONE real unknown: the menu control type

Real PioneerRx's menu control type is not knowable from screenshots. The workflow is now menu-toolkit-
agnostic (finds the bar via MenuBar-or-Menu, finds "Item" window-wide, opens via Expand→Invoke→click),
but verify the actual surface once:

- [ ] Run **Accessibility Insights for Windows** (or FlaUInspect) on the PioneerRx main window.
- [ ] Confirm: the top bar exposes a **MenuItem "Item"**, and under it a **"Rx Item"** entry. Note the
      control type of the bar (MenuBar? Menu? a DevExpress custom type?) and whether "Item" is a walkable
      descendant of the bar or only of the window.
- [ ] If "Item"/"Rx Item" are present and named as expected → proceed. If the names differ, capture the
      exact names/automation-ids; they feed a learned-selector patch (no code change needed).

## 2. Supervised dry-run — ONE NDC, watched

- [ ] Trigger a single-NDC `pricing_lookup` (or a 1-row worklist) with a human watching the screen.
- [ ] Watch SuavoAgent: open Item → Rx Item → paste the NDC into Quick Search → Pricing tab opens →
      the Supplier Catalog is read.
- [ ] **Eyeball the answer against the screen**: the supplier SuavoAgent picked must be the row with the
      lowest **Cost Per Unit** (NOT necessarily the top row — a bigger pack can win per unit). Confirm the
      written supplier + cost match that row exactly.
- [ ] Confirm the Edit Rx Item window closed cleanly afterward (PioneerRx left usable).

Abort criteria: wrong item loaded, wrong supplier/cost written, PMS left in a bad state, or any
unexpected dialog. If any → stop, capture the Helper log (`C:\ProgramData\SuavoAgent\logs\helper\`),
fix, re-run the CI rehearsal, then retry.

## 3. Supervised small batch — 5–10 NDCs, watched

- [ ] Run the small test worklist. Watch a few rows; let the rest complete.
- [ ] Open the produced `*-priced-*.xlsx`: every found row has `OK` + a supplier + a per-unit cost;
      not-found rows carry an explicit marker (`NO_MATCH`, `NO_SUPPLIER_ROWS`, `MULTIPLE_MATCHES`).
- [ ] Spot-check 3 rows by eye against PioneerRx. All must match.
- [ ] Confirm the reconciler behaved: any row where the sighted read and the exact cell disagreed is
      flagged, not silently written.

## 4. Scale — the full top-500, attended-then-unattended

- [ ] With the dry-run + small batch clean, run the real top-500 worklist **attended for the first ~25
      rows** (watch the throttle, PMS responsiveness, memory).
- [ ] If stable, let it complete. Review the filled sheet's status column: OK vs the explicit markers.
- [ ] Only after a clean supervised full run does pricing graduate to **unattended/scheduled** — this is
      the definition of 100%.

## 5. Safety + rollback

- Throttle is configurable (`throttle-ms`); raise it if PioneerRx lags.
- The writer defaults to **Sibling** mode (`*-priced-*.xlsx`) — the source worklist is never mutated.
- To abort a run: stop the job from the cockpit; PioneerRx is left on whatever item was last opened
  (close the Edit Rx Item window if open). No data is written to PioneerRx — SuavoAgent only reads it.
- Regression guard: any workflow change re-runs the CI rehearsal (`gh workflow run pricing-rehearsal.yml`)
  before it reaches a real box.

---

### Why this is the whole remaining gap

Everything upstream is proven: the sighted read is correct on real PioneerRx screenshots, the UIA-drive
chain runs live and grades correct, the reader→reconcile→writer produces the filled sheet, and navigation
is menu-toolkit-agnostic. The only thing a simulator cannot certify is the **real** PioneerRx's exact
menu/grid automation surface and timing — which §1–§3 resolve in one supervised sitting. After that,
pricing is unattended-ready.
