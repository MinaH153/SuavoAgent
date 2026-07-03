# Nadim / Better Life Pharmacy — PioneerRx field-truth + the two requirements

> **Provenance:** extracted directly from PRIMARY media in `~/…/Suavo/Pioneer Nadim/` —
> `Nadim automation.m4a` (Apr 4, pricing spec), `New Recording 37.m4a` (preferred-NDC spec),
> `IMG_5917.MOV` (3-min live capture of Nadim's REAL PioneerRx at Better Life Pharmacy), and the
> `PioneerRx Pricing Screenshots` / `System Footage` stills. **Field-truth, not screenshot inference.**
> The cognition model lives in the wiki (`research/nadim-pricing-cognitive-task-analysis.md`); THIS doc
> holds **(a)** the verified UI structure and **(b)** the TWO features Nadim asked for — the second of
> which was previously **undocumented anywhere**.

**Pharmacy:** Better Life Pharmacy, 528 E Main St, El Cajon, CA 92020-4008. **Operator:** Nadim Dib.
**PMS:** PioneerRx (DELL workstation, Windows; status bar: "Nadim Dib logged on to Better Life Pharmacy").

---

## 1. Verified UI field-truth (live video + hi-res stills)

The `PricingWorkflow` selectors already match this (validated 2026-06-11 against
`src/SuavoAgent.Helper/Workflows/PricingWorkflow.cs`). Confirmed live:

- **Path:** top menu **Item → Rx Item** → window titled **`Edit Rx Item`** → **Quick Search** box
  (top-left, accepts the NDC) → **Pricing** tab → **Supplier Catalog** grid.
- **Supplier Catalog grid — FULL column order, left→right (read off the live grid, ~0:45):**
  `Linked · Inventory Group · Name · NDC · UPC · Supplier · Supplier Item Number · Manufacturer ·
  Shipping Size · Cost · Cost Per Unit · Rebate Cost · Rebate Cost Per Unit · BOH · On Order · AWP ·
  AWP Per Unit · MAC · MAC Per Unit · Status`.
  - Cost column is literally **`Cost Per Unit`** (NOT "Unit Cost") → the sim's `renamed-cost` failure
    variant is **not** the real-world risk.
  - `Status` shows **`Available`** (+ discontinued-type) → the "exclude discontinued, argmin over
    Available rows" logic is correct against reality.
  - Grid filters present: **`Include Discontinued: No`** + **`Inventory Group: Rx`** — the agent should
    assert/respect these.
- **Item pricing fields above the grid:** `WAC`, `AWP Source (Highest AWP)`, `WAC Source`, `NADAC`,
  `PAC`, `MFP`, `Last Cost Paid`, `Average Received Cost`, `Cost Used for Pricing (Greater of Last Cost
  Paid Or Lowest…)`, `Cost Used for Calculating Profit (Lowest Replacement Cost)`, `Primary Pricing
  Category`.
- **CONFIRMED IN THE WILD — the Quick Search tautology condition:** after a load the Quick Search box
  **retains the typed item text** (~0:20 shows "Fluticasone Prop 50 Mcg Spray" highlighted in the search
  field). This is the exact state that makes `VerifyLoadedNdc` pass for ANY NDC if the box isn't
  excluded → a **real** behavior on Nadim's box, not a sim artifact. Fix on `feat/rehearsal-with-pricing-fix`
  (commit a89a942). **Open:** does PioneerRx expose `HelpText="Quick Search"` on that box (the fix's
  anchor)? If not, the fix's residual reopens — verify on first contact.
- **Menu control type (MenuBar vs Menu):** only answerable by UIA-inspecting the live box (a phone photo
  can't disambiguate the automation tree). First task on real contact.
- **DevExpress tree shape:** the grid renders as a conventional sortable table visually, but whether it
  is DevExpress-custom under UIA (rows `Custom` under panels vs `DataItem` children) is the one thing
  only FlaUInspect on the box answers.

---

## 2. Feature A — Supplier pricing lookup (BUILT; canonical spec)

**Nadim, verbatim (`Nadim automation.m4a`, Apr 4):**
> "I have an Excel sheet … the **top 500 most dispensed generics** I use in my pharmacy. I need the bot
> to go to Pioneer → **Item → Rx Item**, take the **NDC** written on the Excel sheet, copy it, paste it
> in **Quick Search**, and under **Pricing** it would know **what's the cheapest supplier — it's going
> to be the one on top**. I want the Excel sheet to add a column for the **supplier name** and the
> **cost** — the cheapest price I can get it for."

Cognition + the critical **"argmin, not top-row"** correctness mandate are in
`wiki/research/nadim-pricing-cognitive-task-analysis.md` — not repeated here. The workflow + rehearsal
sim implement this faithfully; **proof on a real box is the open item**
(`tools/PioneerRxRehearsal/rehearsal.ps1`).

---

## 3. Feature B — Preferred-NDC-by-insurance + auto-block (NOT BUILT — previously undocumented)

**Nadim, verbatim (`New Recording 37.m4a`):**
> "I want to run a **report** to see what's the **most optimal NDC for a certain medication**, and then
> put it under **this insurance** as the **preferred NDC** … in a way that if we try to build another
> NDC for the same medication that gives us a **lesser profit**, the system would **automatically
> block** it, saying the preferred item is a different one. … every time we're using this insurance for
> whichever patients, we have to use the manufacturer/NDC that gives us the **highest profit**. … I want
> the **report to be on the dashboard of how the agent did it**."

**What it is** — a profit-optimization + guardrail feature, distinct from Feature A:
1. For a (medication, insurance) pair, find the **most profitable NDC/manufacturer** — a report /
   analysis over reimbursement vs. acquisition cost = **argmax(profit)**, the mirror of A's argmin(cost).
2. Set that NDC as the **"preferred item"** for that insurance in PioneerRx (PioneerRx has a native
   "preferred item under an insurance" mechanism — Nadim offered to demo it).
3. PioneerRx then **auto-blocks** a less-profitable build for that med/insurance.

**App in scope:** **"Just Pioneer."** (Nadim, explicit.)

**Status:** not built, not in any plan/wiki/Notion as of 2026-06-11. Genuinely new surface — capture it.

---

## 4. The compliance boundary — Nadim voices the moat himself (STRATEGIC)

**Nadim, verbatim (`New Recording 37.m4a`):**
> "There's a difference between **extracting information and putting**. **Putting could be a violation to
> Pioneer's** [TOS]. … If that's the case, then **run me a report and I will do it manually**."

The customer **independently states the extract-vs-put line** that is SuavoAgent's compliance posture
(read/verify = safe; PMS write-back = the risk surface; cf. `ReceiptOnlyMode`). Implications:

- **Both features support a READ-ONLY "report" mode**, not only autonomous writeback. Feature A writes
  to **Nadim's own Excel sheet** (not into PioneerRx) → low TOS risk, fine. **Feature B** sets the
  preferred item → **writes into PioneerRx** → exactly the "putting" Nadim flags → **default to
  report-only** (agent produces the most-profitable-NDC list; Nadim sets it manually) until the
  PioneerRx TOS/BAA position on automated writes is cleared.
- **Sales/trust asset:** the customer already believes in the compliance moat — reflect it in Notion
  positioning.

---

## 5. The "is it working?" requirement (UX — stated need, not polish)

Nadim: *"I want the report to be **on the dashboard** of how the agent did it."* He is explicitly asking
for **visibility into what the agent did**. This is a **customer requirement**:
- A glanceable agent-activity view on the dashboard (what it priced/found, and how).
- A clear **state indicator** read at a glance: *Learning (observing, day X)* vs *Active (doing it)* vs
  *Needs-you (couldn't read a screen / refused a write)*. Design DNA: "no ambiguous states; label where
  data came from and when."

---

## 6. How this feeds the moat (database / fleet-learning thesis)

- **Field-truth → calibrate the sim.** The rehearsal sim should mirror §1 (real column order, the
  search-box-retains-text behavior, the `Include Discontinued: No` filter). A green rehearsal then
  *means* something — the bridge from "built from screenshots" to "verified against reality."
- **Every real lookup thickens the database.** The verified-skill flywheel (harvest → bank → replay,
  shipped #227/#228) banks the *real* grid structure + the cheapest-supplier judgment as a
  PHI-certified replayable skill. Nadim's narration is the **seed spec**; the fleet compounds.
- **Two extrema, one engine.** Feature A = argmin(cost_per_unit); Feature B = argmax(profit) over
  (reimbursement − acquisition). Same "read the data, compute the extremum, cross-check, never trust
  position" cognition → one reasoning core, two surfaces. Build B on A's spine.
- **100% accuracy is a TRUST property, not a model property.** The one line shown the pharmacy must be
  REAL on every fill of an A-item. Accuracy is engineered by the pipeline: (1) **modality that sees
  truth** — prefer SQL for the cost number, vision/UIA to confirm + learn the UI (never gate on one);
  (2) **argmin over the real column, top-row only as corroboration**; (3) **Status filtering** (Available
  only); (4) **fail-closed verify** (the tautology fix — refuse rather than report a guess); (5) the
  **replay flywheel** banking only execution-verified paths. Not hoped for from the LLM.

---

## Open questions for first real-box contact (rehearsal + Nadim's box answer these)

1. Does the Quick Search box expose `HelpText="Quick Search"`? (anchors the tautology fix)
2. MenuBar vs Menu control type on Item → Rx Item?
3. Supplier Catalog: DevExpress-custom UIA tree, or conventional `DataItem`/`Custom`?
4. Are unrealized (virtualized) supplier rows readable, or is scroll-and-merge needed?
5. Is the cheapest-cost data reachable via **SQL** (preferred modality) or UI-only on this install?
6. PioneerRx TOS/BAA position on **automated writes** (gates Feature B writeback vs report-only).
