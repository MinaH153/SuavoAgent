# Pricing Schema Recon (Pre-Saturday 2026-04-25)

Codex's review flagged that our `Inventory.ItemPricing` schema is **unverified** against Nadim's live
database. Shipping an unattended 500-row batch without first proving the SQL matches the Pricing tab
is the main Saturday risk. This folder is the pre-Saturday reconnaissance kit.

## What this kit does

1. Dumps `sys.tables` + `sys.columns` for the `Inventory`, `Purchasing`, `Ordering` schemas.
2. Samples 20 NDCs from `Inventory.Item` so we can see the actual stored format (5-4-2? 11-digit no hyphens?).
3. Lists every table with `Supplier` or `Vendor` in the name.

**Everything is read-only.** No UPDATE / INSERT / DELETE / DDL statements.

## How to run

### Option A — Chrome Remote Desktop into Nadim's PioneerRx PC

1. Joshua connects to PIONEER10 via CRD.
2. Open PowerShell as Administrator.
3. `cd C:\SuavoAgent`
4. Copy `nadim-pricing-schema-recon.ps1` from this folder to `C:\SuavoAgent\recon-script.ps1`
   (or paste it via CRD clipboard).
5. Run: `powershell -ExecutionPolicy Bypass -File C:\SuavoAgent\recon-script.ps1`
6. Enter the SQL server hostname and password when prompted.
7. Output lands at `C:\SuavoAgent\recon\pioneer-pricing-recon-{timestamp}.json` and `.txt`.
8. Copy both files back to Joshua's machine via CRD file transfer.

### Option B — SQL Server Management Studio (fallback)

Copy the four queries out of the `.ps1` file and run each one in SSMS. Save the results to CSV/TSV
with the same four names. Then hand them to Claude in the next session.

## What to do with the output (Joshua, Thursday)

Once the JSON is on your macOS machine, run the bundled interpreter:

```bash
cd ~/Code/SuavoAgent
dotnet run --project docs/recon/InterpretRecon -- ~/Downloads/pioneer-pricing-recon-*.json
```

The tool reads the recon JSON, calls `PricingSchemaResolver.Resolve()`, prints the generated SQL,
and lists 5 NDCs to paste into SSMS for the 5/5 tripwire check against the live Pricing tab.

Exit codes:
- `0` — resolver succeeded, confidence ≥ 0.70. Ship Tier 2 after 5/5 NDC match.
- `1` — resolver failed (schema mismatch). Abort Tier 2; Saturday pivots to UIA-only narrow demo.
- `2` — resolver succeeded but confidence below tripwire. Manual review required.
- `3` — usage / I/O error.

## What Claude will do with the output

Feed the JSON into `PricingSchemaResolver.Resolve(...)` and confirm:

1. The resolver picks the table Nadim sees in the UI (likely `Inventory.ItemPricing`).
2. The NDC format in `Inventory.Item.NDC` matches our normalizer (5-4-2 expected; 11-digit unhyphenated would require a schema-driven normalization step).
3. The supplier resolution path — denormalized column vs join to `Inventory.Supplier` — matches the UI rows Joshua can read live.
4. The `Status` column values that correspond to green rows in the UI. (If they don't match `Available`, update `PricingSchemaResolver.DefaultAvailableStatusValues`.)

## Codex tripwire

If by Thursday 2026-04-23 23:59 PT the recon has not reproduced 5/5 Nadim-picked NDCs with exact
supplier + cost parity against the live Pricing tab, **pivot to UIA-only narrow demo (≤10 NDCs)
for Saturday**. Do NOT run an unattended 500-row SQL batch.

## Files

- `nadim-pricing-schema-recon.ps1` — the runnable script
- `README.md` — this file
- (optional future) `interpret-recon-output.md` — playbook for reading the JSON
