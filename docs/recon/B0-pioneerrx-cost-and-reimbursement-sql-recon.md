# B0 — PioneerRx SQL recon: the COST path (Feature A, priority) + the REIMBURSEMENT path (Feature B)

> **Status:** SPEC + executable queries. **Execute on the box** (Nadim's PIONEER10, read-only).
> **Priority reframe (load-bearing):** answer **Feature A's cost-SQL question FIRST.** If
> `Cost Per Unit` per NDC is readable straight from the PioneerRx DB, the entire
> DevExpress / row-virtualization / UIA-read risk surface **evaporates for the primary pricing job** —
> SQL becomes the accuracy-winning modality and the rehearsal's hardest unknowns stop mattering for
> pricing. That single answer sets pricing's accuracy ceiling. Reimbursement-per-NDC-per-plan
> (Feature B) is the **second** target in the same run.
> **Companions:** `feature-b-preferred-ndc-by-insurance-spec.md` (B0 is its first step),
> `nadim-pioneerrx-field-truth-and-requirements.md` (the UI field-truth this maps to SQL),
> `nadim-pricing-schema-recon.ps1` (the existing recon this extends — same scaffolding + safety posture).

---

## 0. Safety posture (unchanged from the existing recon — non-negotiable)

- **READ-ONLY. Every statement is a `SELECT`.** No `UPDATE/INSERT/DELETE/DDL/EXEC`.
- Output stays **local** (`C:\SuavoAgent\recon\`), JSON + raw TSV, **no cloud upload**.
- Password prompted as `SecureString`, zeroed from memory after.
- **No PHI in the output:** the recon reads **schema + cost/pricing structure + a few NDC/item
  samples** — NOT patient/claim rows. The sample queries below select item/cost/plan *structure*, never
  patient identifiers. (NDC + item name + cost are drug/inventory data, not PHI.)
- Run via `sqlcmd` (pre-installed on any PioneerRx box) or paste the queries into SSMS as fallback.

---

## 1. PRIORITY — Feature A: is `Cost Per Unit` SQL-reachable per NDC?

The shipped `PricingWorkflow` reads the **Supplier Catalog grid** via UIA (field-truth column order:
`… Supplier · … · Cost · Cost Per Unit · … · Status`). The question: **does that same data live in a
SELECT-able table, keyed by NDC/Item + Supplier, with a per-unit cost and an availability/status flag?**

### A-Q1 — Find the supplier-catalog / item-supplier cost table(s)
```sql
-- Tables likely holding per-(item, supplier) cost. Cast a wide net by name.
SELECT s.name + '.' + t.name AS qualified_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%Supplier%' OR t.name LIKE '%Vendor%'
   OR t.name LIKE '%ItemCost%' OR t.name LIKE '%Catalog%'
   OR t.name LIKE '%Cost%'     OR t.name LIKE '%Pricing%'
   OR t.name LIKE '%Acquisition%'
ORDER BY 1;
```

### A-Q2 — Find the COST and STATUS columns (the accuracy-ceiling question)
```sql
-- Any column whose name implies per-unit cost or availability/discontinued status.
SELECT s.name AS schema_name, t.name AS table_name,
       c.name AS column_name, tp.name AS data_type, c.is_nullable
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name LIKE '%CostPerUnit%' OR c.name LIKE '%UnitCost%'
   OR c.name LIKE '%Cost%'        OR c.name LIKE '%Price%'
   OR c.name LIKE '%Discontinued%'OR c.name LIKE '%Available%'
   OR c.name LIKE '%Status%'
ORDER BY t.name, c.column_id;
```

### A-Q3 — Confirm the join key (Item ↔ NDC ↔ Supplier)
```sql
-- The Item table's NDC + ItemID (the field-truth shows Item.NDC); plus FKs into the cost table.
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, tp.name AS data_type
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name IN ('NDC','ItemID','ItemNumber','SupplierID','VendorID','SupplierItemNumber')
ORDER BY t.name, c.name;
```

### A-Q4 — THE PROOF QUERY (fill table/column names from A-Q1–Q3, then run)
> This is the one that answers it. If this returns the supplier rows with a per-unit cost for a known
> NDC — **cost is SQL-reachable and pricing's accuracy ceiling is SQL, not UIA.** Template:
```sql
-- TEMPLATE — substitute the real <CostTable>, <cost col>, <status col>, <supplier col> from A-Q1–Q3.
-- Pick a real NDC from the field-truth (e.g. Fluticasone 60505-0829-01) or A-Q5 below.
SELECT TOP 50
       i.NDC, i.ItemName,
       cat.<SupplierColumn>      AS supplier,
       cat.<CostPerUnitColumn>   AS cost_per_unit,
       cat.<StatusColumn>        AS status
FROM Inventory.Item i
JOIN <CostSchema>.<CostTable> cat ON cat.ItemID = i.ItemID
WHERE i.NDC = '60505082901'   -- normalized (no hyphens) OR the box's stored format — try both
ORDER BY cat.<CostPerUnitColumn> ASC;   -- proves: is the cheapest the min? is it sorted?
```

### A-Q5 — NDC sample + stored format (so A-Q4's WHERE matches reality)
```sql
SELECT TOP 20 ItemID, ItemName, NDC FROM Inventory.Item
WHERE NDC IS NOT NULL AND LEN(NDC) > 0 ORDER BY NEWID();
-- NOTE the format: hyphenated (60505-0829-01) vs packed (60505082901) vs 10- vs 11-digit.
-- The agent's NDC normalization must match whatever this shows.
```

**What A answers (the decisions it unblocks):**
- **If cost IS SQL-reachable:** pricing's read path = SQL (deterministic, no DevExpress/virtualization
  risk, `argmin(cost_per_unit)` straight from the table). UIA becomes confirm-only + the learning lens.
  This is the best-case outcome — it raises the accuracy ceiling to ~100% for the primary job.
- **If cost is NOT cleanly SQL-reachable** (computed in-app, view-only, or cost is a derived/contract
  value not stored per supplier-row): pricing stays UIA-first and the rehearsal's DevExpress /
  virtualization answers become load-bearing. Either way we now KNOW, and the modality choice is made on
  evidence (per the modality-agnostic thesis: pick the modality that sees truth).

---

## 2. SECOND — Feature B: the reimbursement-per-NDC-per-plan path

Feature B needs `profit = reimbursement(ndc, plan) − acquisition_cost(ndc)`. Acquisition cost = §1.
**Reimbursement per plan is the genuine unknown.** Map it (structure only — NOT claim/patient rows):

### B-Q1 — Third-party / plan / MAC pricing tables
```sql
SELECT s.name + '.' + t.name AS qualified_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%ThirdParty%' OR t.name LIKE '%Plan%'   OR t.name LIKE '%Payer%'
   OR t.name LIKE '%MAC%'        OR t.name LIKE '%Reimburs%'OR t.name LIKE '%Contract%'
   OR t.name LIKE '%Insurance%'  OR t.name LIKE '%Claim%'   OR t.name LIKE '%Adjudicat%'
ORDER BY 1;
```

### B-Q2 — Reimbursement / preferred-item columns
```sql
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, tp.name AS data_type
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name LIKE '%Reimburs%' OR c.name LIKE '%Paid%'      OR c.name LIKE '%Allowed%'
   OR c.name LIKE '%MAC%'       OR c.name LIKE '%Preferred%' OR c.name LIKE '%PlanID%'
   OR c.name LIKE '%PayerID%'
ORDER BY t.name, c.column_id;
```

### B-Q3 — The "preferred item under an insurance" table (the writeback target, read-only here)
```sql
-- Find where PioneerRx stores the per-plan preferred item (Nadim's native feature). READ-ONLY:
-- we only LOCATE + read the current value; any future SET is the gated B4 writeback, not here.
SELECT s.name + '.' + t.name AS qualified_name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.name LIKE '%Preferred%' OR (t.name LIKE '%Plan%' AND t.name LIKE '%Item%')
ORDER BY 1;
```

### B-Q4 — Drug-group / equivalents (the candidate-NDC set for one medication)
```sql
-- Field-truth shows a "Brand Equivalent" linkage. Find the grouping that yields all NDCs for a drug.
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, tp.name AS data_type
FROM sys.columns c
JOIN sys.tables t  ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types tp  ON tp.user_type_id = c.user_type_id
WHERE c.name LIKE '%Equivalent%' OR c.name LIKE '%GenericCode%' OR c.name LIKE '%DrugGroup%'
   OR c.name LIKE '%GPI%'        OR c.name LIKE '%TherapeuticClass%'
ORDER BY t.name, c.column_id;
```

**What B answers:** whether reimbursement is stored per (NDC, plan) at all (→ Feature B is SQL-clean), or
only exists post-adjudication on claims (→ Feature B's report is historical/estimated — reframe with
Nadim), plus where the preferred-item lives + how to enumerate candidate NDCs for a drug.

---

## 3. How to run + what to send back

1. On Nadim's box (PIONEER10), open PowerShell. Run the **existing** `nadim-pricing-schema-recon.ps1`
   first (it already covers inventory schema + NDC samples + supplier-table discovery).
2. Then run the queries above (§1 priority, then §2). Easiest path: **append** the §1/§2 query blocks to
   the `$queries = @(…)` array in the existing script (same Name/Sql shape) so they land in the same
   timestamped JSON + raw TSV. (A code follow-up can fold these in; for first contact, SSMS paste works.)
3. Send back **only** the local JSON + TSV (no cloud upload). The cost-SQL answer (A-Q4) is the headline.

## 4. The one result that matters most

**A-Q4 returning supplier rows with a per-unit cost for a real NDC** = pricing's accuracy ceiling is SQL,
not UIA. That single fact de-risks the **primary** revenue feature more than anything else on the
first-contact list — run it first, read it first.
