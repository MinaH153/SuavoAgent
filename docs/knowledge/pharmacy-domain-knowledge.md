# SuavoAgent Pharmacy Domain Knowledge

The domain knowledge SuavoAgent reasons from so it operates like an experienced retail/community
pharmacist — not a script following coordinates. Authored as a **retrievable corpus** (retrieval-as-
few-shot): the on-device reasoner's base system prompt stays small; the relevant section below is
injected per task. Each entry is a self-contained fact block so a single chunk carries its own context.

> Scope note. These are established pharmacy-practice facts (drug identity, pricing benchmarks,
> dispensing). Anything **jurisdiction-specific** (substitution law, controlled-substance rules) is
> flagged as "varies by state/board" — the agent must not assume one state's rule is universal.
> Money/clinical actions still route through the deterministic core + exact verification; this
> knowledge guides *navigation and judgment*, it does not by itself authorize a write.

---

## 1. Drug identity — the NDC

- **NDC = National Drug Code**, a package-specific identifier. It identifies a *labeler + product +
  package size*, NOT just "the drug." Two package sizes of the same drug are two different NDCs.
- **Three segments: Labeler – Product – Package.**
  - *Labeler* (assigned by FDA): the manufacturer/repackager/distributor.
  - *Product*: strength, dosage form, formulation.
  - *Package*: package size/type.
- **10-digit vs 11-digit.** The FDA assigns a 10-digit NDC in one of three configs: **4-4-2, 5-3-2,
  or 5-4-1**. Billing (HIPAA/NCPDP) requires an **11-digit** form (**5-4-2**), created by padding the
  short segment with a leading zero. Example: `1234-5678-90` (4-4-2) → `01234-5678-90`.
- **Normalization rule (what SuavoAgent does):** strip hyphens, pad to 11 digits by the config, keep
  a canonical 11-digit string for matching. A displayed `55111-0645-01` is already 5-4-2 → `55111064501`.
- **NDC is the join key** between a pharmacy's dispensing record, the wholesaler catalog, and pricing
  benchmarks. Match on the normalized 11-digit form; a raw display string may carry or omit hyphens.
- **Related identifiers:** *GPI* (Generic Product Identifier, 14-digit therapeutic hierarchy — same GPI
  = pharmacologically equivalent regardless of manufacturer), *RxNorm* (normalized clinical drug names),
  *GCN/GCN_SEQNO* (clinical formulation). SuavoAgent keys on NDC for pricing; GPI is how "the same drug,
  different labeler" is recognized when picking a cheaper equivalent.

## 2. Generic equivalence & substitution

- **Brand vs generic:** a generic is therapeutically equivalent to the brand — same active ingredient,
  strength, dosage form, route — and (for AB-rated products) demonstrated bioequivalence.
- **Orange Book / TE codes.** The FDA "Orange Book" assigns Therapeutic Equivalence codes. **A-rated**
  (AA, AB, …) = substitutable; **B-rated** (BX, …) = not considered equivalent. **AB** is the common
  substitutable rating. Pharmacists substitute A-rated generics to lower cost.
- **DAW (Dispense As Written) codes 0–9** on a claim signal substitution status. Key ones:
  - `0` — no product selection indicated (substitution allowed; the default).
  - `1` — **prescriber** requires brand (no substitution).
  - `2` — **patient** requested brand.
  - Others cover generic-not-available, pharmacist selection, etc.
- **Substitution law varies by state/board** (mandatory vs permissive; patient-notification rules).
  The agent must not assume a universal rule — it reads the pharmacy's configured behavior.
- **Why it matters for sourcing:** the cheapest *acquisition* for a dispensed drug is usually an
  A-rated generic from whichever labeler a wholesaler has cheapest today. The Supplier Catalog lists
  multiple labelers of the same item; the job is to pick the cheapest **per unit** among them.

## 3. Pricing benchmarks & the cost-per-unit rule (the core of the job)

- **The benchmarks (know what each means):**
  - **AWP** — Average Wholesale Price. A published "sticker" list price; historically inflated,
    NOT what the pharmacy pays. Used in many reimbursement formulas.
  - **WAC** — Wholesale Acquisition Cost. Manufacturer's list price to wholesalers; closer to real,
    still before discounts/rebates.
  - **NADAC** — National Average Drug Acquisition Cost. A CMS survey of what pharmacies *actually pay*;
    a reference for true acquisition cost.
  - **MAC** — Maximum Allowable Cost. A payer's cap on reimbursement for a multi-source (generic) drug.
  - **U&C** — Usual & Customary. The cash price the pharmacy charges the public.
  - **Acquisition cost** — what the pharmacy actually pays a supplier (the number that drives profit).
- **THE RULE SuavoAgent enforces: cheapest = lowest COST PER UNIT, never lowest pack price.**
  - A supplier line shows a **pack Cost** and a **Cost Per Unit** (= pack cost ÷ units in the pack).
  - A larger pack often wins per-unit even though its pack price is higher. Example (real, Omeprazole
    DR 40 mg): Real Value Rx `$3.16 / 100 ct = $0.0316/unit`; **McKesson `$4.95 / 500 ct = $0.0099/unit`**.
    McKesson's pack costs more but is **~3.2× cheaper per unit** — it is the correct pick.
  - The grid is frequently **sorted by pack Cost ascending**, so the cheapest-per-unit line is NOT the
    top row. "Read the top row" is the classic wrong answer. **Rank by Cost Per Unit.**
- **Skip lines with no price** (blank Cost) and **exclude Discontinued/Unavailable** suppliers — a
  blank/inactive line is not a purchasable option even if some other column (e.g. AWP/unit) has a number.
- **Why sourcing is the profit lever:** for multi-source generics, reimbursement is capped (MAC/NADAC),
  so margin = reimbursement − acquisition. Lowering acquisition (cheapest per-unit supplier) is the
  most direct, compliant way to widen margin. This is the entire point of the top-500 pricing pass.
- **340B** (covered-entity discount program): 340B pricing is a *separate* ceiling for eligible
  entities and is tracked apart from retail (PioneerRx shows a `340B` tab beside `Rx`). Do NOT mix a
  340B cost into a retail sourcing decision, or vice-versa.

## 4. Wholesalers & the supplier landscape

- **Primary (full-line) wholesalers** — a pharmacy's main daily supplier under a purchasing agreement,
  usually with a generic-compliance/GCR target: **McKesson, Cardinal Health, Cencora (AmerisourceBergen)**.
- **Secondary / generic wholesalers** — shopped for cheaper generics outside the primary: e.g.
  **Anda, Keysource (a McKesson/DR Reddy's channel), Parmed, TopRx, Real Value Rx, Auburn, Capital**.
  Names in a Supplier Catalog vary by pharmacy's accounts.
- **Why multiple suppliers appear per item:** the pharmacy holds accounts with several; each lists its
  own pack size + cost for the same NDC/equivalent. The catalog is the live comparison surface.
- **Generic Compliance Rate (GCR):** primary contracts reward buying a % of generics through the
  primary; buying too much secondary can breach the contract. SuavoAgent surfaces the cheapest option;
  a human still weighs contract compliance. (The agent's job is *visibility*, not overriding contracts.)

## 5. PioneerRx operational knowledge

- **PioneerRx** is a leading community-pharmacy management system (PMS). SuavoAgent reads/drives it by
  sight + UIA; it never exfiltrates PHI.
- **Rx Item** — the master record for a dispensable product (opened via `Item → Rx Item`, then Quick
  Search by NDC). The **Edit Rx Item** window has tabs incl. **Common, Pricing, Ordering, …**.
- **Pricing tab → Supplier Catalog** — the grid of every supplier line for the item. Columns commonly
  include: **Linked, Inventory Group, Name, NDC, UPC, Supplier, Supplier Item #, Manufacturer,
  Shipping Size, Cost, Cost Per Unit, Rebate Cost, Rebate Cost Per Unit, BOH (Balance On Hand),
  On Order, AWP, AWP Per Unit, MAC, MAC Per Unit, Status.** The Supplier column sits *after* the
  identity columns (Name/NDC/UPC) and *before* the numeric columns.
- **Reading the grid like a pharmacist:** the **Supplier** is the first alphabetic cell after the item's
  NDC/UPC ids; the cost to compare is **Cost Per Unit**; **Status = Available** is required. The pricing
  panel *above* the grid (AWP Source, Max AWP, NADAC, Average Received Cost) is item-level context — it
  is NOT a supplier row and must be ignored when ranking.
- **"(Do Not Use)"** marker on an item means it must not be dispensed/priced — halt and skip.
- **Inventory Group** (Rx vs 340B vs OTC) scopes which catalog you're reading — keep the retail (Rx)
  group for retail sourcing.

## 6. Dispensing & compliance essentials

- **DEA controlled-substance schedules** (federal; states may be stricter):
  - **CII** — high abuse, accepted medical use (e.g. many opioids, stimulants). No refills; strict
    inventory/records; often e-scribe/paper-script rules.
  - **CIII–CIV** — decreasing abuse potential; limited refills (typically ≤5 in 6 months).
  - **CV** — lowest schedule (e.g. some antitussives).
  - Controlled items carry extra ordering/record rules; SuavoAgent flags rather than automates around
    them.
- **Days supply & quantity** — dispensed quantity ÷ directions = days supply; drives refill timing and
  many reimbursement edits. Quantity is per the package unit (each, mL, g).
- **Lot / expiration / NDC on the shelf** must match what's billed; substituting a different NDC changes
  the billing NDC.
- **PHI is protected (HIPAA).** Patient name, DOB, Rx number, address, and medication-tied identifiers
  are PHI. SuavoAgent scrubs PHI at the vision/extraction boundary; pricing/sourcing data (NDC,
  supplier, cost) is **not** PHI and is what the pricing task operates on.

## 7. The top-500 pricing task — playbook + the "why"

Goal: for each of a pharmacy's top-dispensed items, find the cheapest supplier **per unit** and record
it, so the pharmacy can source smarter and widen margin on capped-reimbursement generics.

Per NDC:
1. **Open `Item → Rx Item`.** *Why:* the item master is the only place with the live Supplier Catalog.
2. **Quick Search the NDC; verify the loaded item's NDC matches.** *Why:* a slow search can leave the
   previous item loaded — pricing the wrong drug is a costly silent error. Confirm identity first.
3. **Skip if "(Do Not Use)".** *Why:* not a dispensable/priceable product.
4. **Open the Pricing tab → Supplier Catalog.** *Why:* this is the comparison surface.
5. **Read every supplier line; rank by Cost Per Unit; exclude blank-cost & Discontinued/Unavailable.**
   *Why:* the cheapest acquisition is the profit lever; per-unit (not pack price) is the true comparison;
   the top row is often NOT the cheapest per unit (pack-cost sort).
6. **Confirm the number before recording it.** *Why:* an OCR/UI misread that writes a wrong cost
   corrupts a purchasing decision — the sighted read is confirmed against the exact PMS cell; on
   disagreement, fail closed (record nothing, flag for review) rather than guess.
7. **Write `Best Supplier`, `Best Cost`, and a status** back to the worklist. Status is explicit:
   `OK`, `NO_MATCH` (NDC not found), `NO_SUPPLIER_ROWS`, `MULTIPLE_MATCHES` (ambiguous — do not auto-pick).

**Traps a good pharmacist (and SuavoAgent) watches for:**
- Top row ≠ cheapest per unit (pack-cost sorting).
- A big-pack low per-unit cost the eye skips.
- Blank-cost lines that aren't real options.
- Discontinued/unavailable lines.
- 340B vs retail (Rx) inventory group mismatch.
- Wrong item loaded after a slow search.

---

*This corpus is the domain layer of SuavoAgent's intelligence. It pairs with: the deterministic
Supplier-Catalog reader (`VisionSupplierGridParser`) + exact reconciliation (`VisionExactReconciler`)
for money-safe reads, and the on-device reasoner (`InferencePromptBuilder`) for navigation. Extend it
as real runs surface new PMS layouts, supplier names, and edge cases — the learning flywheel.*
