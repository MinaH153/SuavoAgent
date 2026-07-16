# Feature B — Preferred-NDC-by-Insurance (the margin guardrail) — SPEC v0

> **Status:** PARTIAL / strict offline report path only. Native private-snapshot admission, the bounded
> margin-proxy engine, exact Excel reader, runner, and atomic report composition are unit-tested. No live
> PioneerRx data source, production registration, signed command, dashboard transport, or writeback exists.
> **Source of requirement:** Nadim (`New Recording 39.m4a`, primary media). Field-truth +
> both features captured in `docs/recon/nadim-pioneerrx-field-truth-and-requirements.md` (PR #233).
> **Default posture: READ-ONLY (report).** Per Nadim's own compliance line — see §4.
> **Relationship to Feature A (pricing lookup, BUILT):** Feature B is the `argmax` **mirror** of
> Feature A's `argmin(cost)`. Same cognition, same reasoning engine, second surface. Build B on A's spine.

---

## 1. What Nadim asked for (verbatim)

> "I want to run a **report** to see what's the **most optimal NDC for a certain medication**, and then
> put it under **this insurance** as the **preferred NDC** … in a way that if we try to [bill/build —
> the audio is unclear] another
> NDC for the same medication that gives us a **lesser profit**, the system would **automatically
> block** it, saying the preferred item is a different one. … every time we're using this insurance for
> whichever patients, we have to use the manufacturer/NDC that gives us the **highest profit**. … I want
> the **report to be on the dashboard of how the agent did it**."
>
> "There's a difference between **extracting information and putting**. **Putting could be a violation to
> Pioneer's** … If that's the case, then **run me a report and I will do it manually**."

The sentence after “Pioneer's” trails off in the supplied recording. Treat vendor-contract/TOS risk as
the operational interpretation to clear with counsel/vendor, not as a fully audible direct quotation.

**App in scope:** "Just Pioneer." (explicit — no other app).

---

## 2. The problem, in pharmacy terms

For a drug group, PioneerRx may present multiple manufacturer/package NDCs. SuavoAgent must never infer
that they are clinically or legally interchangeable: DAW instructions, formulary rules, package/quantity
basis, stock, controlled-substance restrictions, and pharmacist judgment define the eligible set. Within
that pharmacist-approved set, each NDC can have a different acquisition amount and expected reimbursement.
The current local formula (`reimbursement − acquisition`) is an **expected gross-margin proxy**, not net
profit; downstream fees, DIR/clawbacks, rebates, reversals, tax, and dispensing overhead are not yet inputs.
Nadim ultimately wants PioneerRx to block a lower-margin choice through its native preferred-item feature.

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
3. **Compute:** `gross_margin_proxy(ndc) = reimbursement(ndc, plan) − acquisition_cost(ndc)`.
   **Judgment:** `argmax(gross_margin_proxy)` over the already-approved candidate NDCs → the review
   candidate + its proxy delta vs. the
   runner-up. **Cross-check** (same discipline as Feature A §5): never trust a single number or a UI
   position; corroborate (e.g. SQL vs. UI; reimbursement sanity vs. AWP/MAC) before recommending.
4. **Output (read-only default):** a **report** — per (medication, plan): preferred NDC, its
   manufacturer, acquisition cost, reimbursement, gross-margin proxy, calculation limits, and delta. Plus the
   "how the agent did it" trace for the dashboard (Nadim's stated ask).
5. **(Gated, opt-in only) Write:** set the preferred item in PioneerRx for the plan — **OFF by default**
   (§4). Until enabled, the pharmacist sets it manually from the report.
6. **Learn:** every (medication, plan) decision thickens the owned corpus — the real reimbursement
   structure per plan and the NDC↔manufacturer↔margin-evidence map. Only minimum-necessary, PHI-safe,
   locally validated evidence may enter the skill bank.

---

## 4. Compliance posture — READ-ONLY by default (load-bearing)

Nadim **himself** drew the line: *extract = fine; **put** could be a PioneerRx TOS violation → "run me a
report and I'll do it manually."* So:

- **Default mode = REPORT (read-only).** The agent reads cost + reimbursement, computes the bounded
  gross-margin proxy, and **produces a review report**. The pharmacist validates eligibility and sets the PioneerRx preferred item.
  This is the v1 deliverable. It is also the highest-trust on-ramp.
- **Write mode (set-preferred-item) is a SEPARATE, default-OFF future capability.** The proposed
  `Agent:PreferredItemWriteback` flag does not exist today. It MUST NOT ship enabled until:
  1. PioneerRx **TOS / BAA position on automated writes** is explicitly cleared (legal — precedence-1);
  2. the write goes through the SAME composite safety gate + autonomy ladder + audit + instant-kill as
     every other actuating path (it writes *into* the PMS — the "putting" risk surface);
  3. it is reversible / auditable (record prior preferred item; one-click revert).
- This maps to the existing `ReceiptOnlyMode` / extract-vs-put architecture. Report mode removes the
  mutation risk, but read automation, BAA scope, data access, and clinical use still require vendor,
  security, and pharmacist approval.

---

## 5. OPEN QUESTIONS — the reimbursement data path is NOT yet mapped (the real unknown)

Feature A's Supplier Catalog field shape is known and has a local reader, but still needs live field
proof. **Feature B's
reimbursement-per-NDC-per-plan data is the genuine gap.** These must be answered on the box (the
a future signed, read-only native reconnaissance command is the required tool; customer-facing or
field-operation PowerShell is not an acceptable product path):

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

## 6. Build plan and current status (report-first)

- **B0 — Recon (OPEN):** use a signed, read-only native action to map reimbursement / third-party
  pricing tables + the preferred-item table. Answers §5.1–5.4. Output: the data contract. *(Needs the box.)*
- **B1 — Gross-margin-proxy engine (pure, testable, no IO):** `argmax(reimbursement - acquisition)` over candidate NDCs given
  `(acquisition_cost, reimbursement)` per NDC — the mirror of Feature A's selection logic, with the same
  "compute the extremum, cross-check, never trust position" discipline + the same correctness tests
  (tie handling, missing-data fail-closed, common amount basis, named/fresh provenance, bounded arithmetic,
  delta-vs-runner-up). **BUILT + unit-tested locally; not a net-profit or live capability by itself.**
- **B2 — Reader (modality-agnostic):** read acquisition + reimbursement per the B0 contract — **prefer
  SQL** (accuracy, no brittle UI assumptions); UI/UIA only where SQL can't reach (same modality thesis
  as Feature A — never gate on one modality; pick the one that sees truth). **PARTIAL:** strict exact-
  schema Excel admission/reader exists; live SQL/UIA reimbursement reader does not.
- **B3 — Report + dashboard trace:** the read-only deliverable — per (med, plan) review candidate +
  margin proxy + evidence/limits + "how the agent did it". **PARTIAL:** native admit→evaluate→atomic-report
  composition exists locally;
  no production caller, durable job/receipt, upload contract, or dashboard view exists.
- **B4 — (gated, default-OFF) Writeback:** set the PioneerRx preferred item, behind
  a not-yet-implemented capability flag, through the full safety gate + audit + revert. **NOT BUILT.**
  Ships ONLY after §4 conditions. May never ship — report mode stands alone.

---

## 7. Why this matters to the moat (in one line)

Feature A made the agent touch the pharmacy's **costs**; Feature B makes it protect their **margin per
insurance** — a second money-touching job. **One automation is a utility; two that touch the money make
SuavoAgent expensive to rip out.** Same reasoning engine, second surface, same owned-corpus flywheel —
and a read-only report ships the full value with the TOS risk deferred to a separate, gated decision.

---

## 8. Acceptance for v1 (report mode)

**Current acceptance status: not met end-to-end.** Only the local B1 and offline Excel/report unit-test
slices are green; the live source, durable execution chain, and dashboard trace are absent.

- For a real (medication, plan), the agent produces a report whose **review candidate + margin inputs are
  correct against the box** (the same "the number shown must be REAL" trust bar as Feature A — verified,
  not guessed; fail-closed when reimbursement data is missing).
- No write to PioneerRx (writeback flag OFF).
- The decision trace is available for the dashboard ("how the agent did it").
- Margin-proxy engine (B1) has correctness tests: argmax, ties, missing-data refusal, basis/provenance/
  freshness refusal, arithmetic bounds, and delta computation.
