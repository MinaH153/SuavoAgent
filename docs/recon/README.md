# Pricing Schema Recon (Pre-Saturday 2026-04-25)

> **ARCHIVED / DO NOT USE — pre-pilot engineering evidence only.** These
> database-discovery steps and their script predate the native agent lifecycle.
> Pharmacy staff must never be asked to run them or provide their raw output.
> Customer lifecycle actions use `docs/sales/windows-agent-lifecycle.md`.

Codex's review flagged that our `Inventory.ItemPricing` schema is **unverified** against Nadim's live
database. Shipping an unattended 500-row batch without first proving the SQL matches the Pricing tab
is the main Saturday risk. This folder is the pre-Saturday reconnaissance kit.

## What this kit does

1. Dumps `sys.tables` + `sys.columns` for the `Inventory`, `Purchasing`, `Ordering` schemas.
2. Samples 20 NDCs from `Inventory.Item` so we can see the actual stored format (5-4-2? 11-digit no hyphens?).
3. Lists every table with `Supplier` or `Vendor` in the name.

**Everything is read-only.** No UPDATE / INSERT / DELETE / DDL statements.

## Execution status

Do not run this kit on a customer workstation. The script is retained only as
historical engineering evidence. Current customer discovery and verification
must flow through the signed native Setup wizard and dashboard diagnostics in
`docs/sales/windows-agent-lifecycle.md`; a pharmacy employee must never open a
terminal, paste a command, expose a database password, or transfer raw recon
files. If the native product cannot produce the required PHI-free, signed
evidence, the field gate remains incomplete.

## Historical output interpretation

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

Historical evidence was intended to be fed into
`PricingSchemaResolver.Resolve(...)` to confirm:

1. The resolver picks the table Nadim sees in the UI (likely `Inventory.ItemPricing`).
2. The NDC format in `Inventory.Item.NDC` matches our normalizer (5-4-2 expected; 11-digit unhyphenated would require a schema-driven normalization step).
3. The supplier resolution path — denormalized column vs join to `Inventory.Supplier` — matches the UI rows Joshua can read live.
4. The `Status` column values that correspond to green rows in the UI. (If they don't match `Available`, update `PricingSchemaResolver.DefaultAvailableStatusValues`.)

## Codex tripwire

If by Thursday 2026-04-23 23:59 PT the recon has not reproduced 5/5 Nadim-picked NDCs with exact
supplier + cost parity against the live Pricing tab, **pivot to UIA-only narrow demo (≤10 NDCs)
for Saturday**. Do NOT run an unattended 500-row SQL batch.

## Files

- `nadim-pricing-schema-recon.ps1` — archived engineering artifact; not an
  approved customer procedure
- `README.md` — this file
- (optional future) `interpret-recon-output.md` — playbook for reading the JSON
