# Feature B — Preferred-NDC-by-Insurance (the margin guardrail) — SPEC v0

> **Status:** SPEC / not built. Greenfield (no preferred-NDC/profit/reimbursement code exists yet).
> **Source of requirement:** Nadim, verbatim (`New Recording 37.m4a`, primary media). Field-truth +
> both features captured in `docs/recon/nadim-pioneerrx-field-truth-and-requirements.md` (PR #233).
> **Default posture: READ-ONLY (report).** Per Nadim's own compliance line — see §4.
> **Relationship to Feature A (pricing lookup, BUILT):** Feature B is the `argmax(profit)` **mirror** of
> Feature A's `argmin(cost)`. Same cognition, same reasoning engine, second surface. Build B on A's spine.

---

## 1. What Nadim asked for (verbatim)

> "I want to run a **report** to see what's the **most optimal NDC for a certain medication**, and then
> put it under **this insurance** as the **preferred NDC** … in a way that if we try to build another
> NDC for the same medication that gives us a **lesser profit**, the system would **automatically
> block** it, saying the preferred item is a different one. … every time we're using this insurance for
> whichever patients, we have to use the manufacturer/NDC that gives us the **highest profit**. … I want
> the **report to be on the dashboard of how the agent did it**."
>
> "There's a difference between **extracting information and putting**. **Putting could be a violation to
> Pioneer's** [TOS]. … If that's the case, then **run me a report and I will do it manually**."

**App in scope:** "Just Pioneer." (explicit — no other app).

---

## 2. The problem, in pharmacy terms

For a single generic medication, multiple **NDCs** (different manufacturers/package sizes) can fill the
same prescription. Each NDC has a different **acquisition cost** and a different **reimbursement** under
a given **insurance plan** → therefore a different **profit** (`reimbursement − acquisition_cost`). The
pharmacist wants, for each (medication, insurance) pair, to always dispense the **NDC that maximizes
profit**, and wants PioneerRx to **block** building a less-profitable NDC for that pair (PioneerRx has a
native "preferred item under an insurance" mechanism).

Today this is manual, per-drug, per-plan judgment — high-value, repetitive, error-prone: exactly the
shape SuavoAgent automates. It is the **margin** side of the business; Feature A is the **cost** side.

---

## 3. Decomposition (the agent's way of thinking — mirrors the pricing CTA)

1. **Scope:** a (medication / drug-group, insurance plan) pair, or a batch over the top-N drugs ×
   the pharmacy's active plans.
2. **Gather (per candidate NDC for that medication):**
   - **acquisition cost** — the same Supplier Catalog data Feature A already reads (cheapest available
     supplier `Cost Per Unit` per NDC), OR the item's cost-used-for-profit field
     (`Cost Used for Calculating Profit (Lowest Replacement Cost)` — observed in the field-truth).
   - **expected reimbursement under the plan** — **this is the new data path (see §5, OPEN).** It comes
     from PioneerRx's third-party/plan pricing (MAC list, contract reimbursement, or a test-claim /
     pricing estimate per NDC under the selected plan).
3. **Compute:** `profit(ndc) = reimbursement(ndc, plan) − acquisition_cost(ndc)`.
   **Judgment:** `argmax(profit)` over the candidate NDCs → the preferred NDC + the profit delta vs. the
   runner-up. **Cross-check** (same discipline as Feature A §5): never trust a single number or a UI
   position; corroborate (e.g. SQL vs. UI; reimbursement sanity vs. AWP/MAC) before recommending.
4. **Output (read-only default):** a **report** — per (medication, plan): preferred NDC, its
   manufacturer, acquisition cost, reimbursement, profit, and the delta over the next-best. Plus the
   "how the agent did it" trace for the dashboard (Nadim's stated ask).
5. **(Gated, opt-in only) Write:** set the preferred item in PioneerRx for the plan — **OFF by default**
   (§4). Until enabled, the pharmacist sets it manually from the report.
6. **Learn:** every (medication, plan) decision thickens the owned corpus — the real reimbursement
   structure per plan, the NDC↔manufacturer↔profit map. Compounds across the fleet (different pharmacies
   share plans). Same flywheel as Feature A (harvest → bank → replay), PHI-certified.

---

## 4. Compliance posture — READ-ONLY by default (load-bearing)

Nadim **himself** drew the line: *extract = fine; **put** could be a PioneerRx TOS violation → "run me a
report and I'll do it manually."* So:

- **Default mode = REPORT (read-only).** The agent reads cost + reimbursement, computes the
  most-profitable NDC, and **produces a report**. The pharmacist sets the PioneerRx preferred item.
  This is the v1 deliverable. It is also the highest-trust on-ramp.
- **Write mode (set-preferred-item) is a SEPARATE, default-OFF capability** (`Agent:PreferredItemWriteback`,
  default false — mirror the `ReplayFirst`/flag discipline). It MUST NOT ship enabled until:
  1. PioneerRx **TOS / BAA position on automated writes** is explicitly cleared (legal — precedence-1);
  2. the write goes through the SAME composite safety gate + autonomy ladder + audit + instant-kill as
     every other actuating path (it writes *into* the PMS — the "putting" risk surface);
  3. it is reversible / auditable (record prior preferred item; one-click revert).
- This maps to the existing `ReceiptOnlyMode` / extract-vs-put architecture and the Two-Moats
  (compliance) thesis. **The report mode carries 100% of the value with ~0% of the TOS risk** — ship it
  first regardless of whether writeback is ever enabled.

---

## 5. OPEN QUESTIONS — the reimbursement data path is NOT yet mapped (the real unknown)

Feature A's data (Supplier Catalog cost) is known + read by the shipped workflow. **Feature B's
reimbursement-per-NDC-per-plan data is the genuine gap.** These must be answered on the box (the
`nadim-pricing-schema-recon.ps1` SQL recon is the tool; extend it to the third-party pricing tables):

1. **Where does per-plan reimbursement live?** PioneerRx SQL (`PioneerPharmacySystem` DB) third-party /
   MAC / contract-pricing tables? A claim-pricing estimate? A report PioneerRx already generates? →
   **map the table(s).** This decides whether Feature B is SQL-clean or requires UI-driving a pricing
   estimate per NDC.
2. **Is reimbursement deterministic pre-claim, or only known after adjudication?** If only post-claim,
   the "report" is historical/estimated, and the framing shifts to "based on recent adjudicated claims,
   NDC X paid best under plan Y." Clarify with Nadim what data he uses today to make this call manually.
3. **What is the candidate-NDC set for a medication?** PioneerRx drug-group / equivalents
   (`Brand Equivalent` linkage seen in the field-truth) — confirm the grouping the agent should iterate.
4. **PioneerRx "preferred item under an insurance"** — exact UI path + the underlying table (Nadim
   offered to demo it). Needed for BOTH the report (to read current preferred) and any future writeback.
5. **Acquisition cost source:** reuse Feature A's cheapest-supplier `Cost Per Unit`, or the item's
   `Cost Used for Calculating Profit`? (They can differ — pick the one Nadim reasons with.)
6. **PioneerRx TOS / BAA on automated writes** (gates §4 write mode; also flagged for Feature A).

---

## 6. Build plan (incremental, report-first)

- **B0 — Recon (no code):** extend `nadim-pricing-schema-recon.ps1` to map the reimbursement / third-party
  pricing tables + the preferred-item table. Answers §5.1–5.4. Output: the data contract. *(Needs the box.)*
- **B1 — Profit engine (pure, testable, no IO):** `argmax(profit)` over candidate NDCs given
  `(acquisition_cost, reimbursement)` per NDC — the mirror of Feature A's selection logic, with the same
  "compute the extremum, cross-check, never trust position" discipline + the same correctness tests
  (tie handling, missing-data fail-closed, delta-vs-runner-up). Reuses the reasoning spine.
- **B2 — Reader (modality-agnostic):** read acquisition + reimbursement per the B0 contract — **prefer
  SQL** (accuracy, no brittle UI assumptions); UI/UIA only where SQL can't reach (same modality thesis
  as Feature A — never gate on one modality; pick the one that sees truth).
- **B3 — Report + dashboard trace:** the read-only deliverable — per (med, plan) preferred NDC + profit +
  delta + "how the agent did it" (Nadim's dashboard ask; the cloud status/activity surface consumes it).
- **B4 — (gated, default-OFF) Writeback:** set the PioneerRx preferred item, behind
  `Agent:PreferredItemWriteback`, through the full safety gate + audit + revert. Ships ONLY after §4
  conditions. May never ship — report mode stands alone.

---

## 7. Why this matters to the moat (in one line)

Feature A made the agent touch the pharmacy's **costs**; Feature B makes it protect their **margin per
insurance** — a second money-touching job. **One automation is a utility; two that touch the money make
SuavoAgent expensive to rip out.** Same reasoning engine, second surface, same owned-corpus flywheel —
and a read-only report ships the full value with the TOS risk deferred to a separate, gated decision.

---

## 8. Acceptance for v1 (report mode)

- For a real (medication, plan), the agent produces a report whose **preferred NDC + profit numbers are
  correct against the box** (the same "the number shown must be REAL" trust bar as Feature A — verified,
  not guessed; fail-closed when reimbursement data is missing).
- No write to PioneerRx (writeback flag OFF).
- The decision trace is available for the dashboard ("how the agent did it").
- Profit engine (B1) has correctness tests: argmax, ties, missing-data refusal, delta computation.
