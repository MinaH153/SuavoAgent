# Pricing SQL — real end-to-end runbook + proof

The whole SQL pricing modality is validated against a **live SQL Server engine** with a
**PioneerRx-shaped schema** and Nadim's real NDCs. Real PioneerRx can't be installed (proprietary
RedSail software, licensed per-pharmacy, no public installer) — this is the closest faithful stand-in:
same schema shape the agent's discovery + queries target, same correctness rules.

## What runs (production classes, not mocks)
1. `PricingSchemaDiscovery.DiscoverAsync` — resolves the catalog from `sys.columns`.
2. `SqlTopDispensedGenerator` — the top-500 worklist (Nadim's Rx Binoculars report), SQL modality.
3. `SqlSupplierPriceLookup` — cheapest supplier per NDC (argmin **Cost Per Unit**).

## Schema (Azure SQL Edge / SQL Server)
- `Inventory.Item` (ItemID, NDC, Name, Strength, BrandGeneric, RxOtc, DeaSchedule)
- `Inventory.ItemPricing` (ItemID, SupplierName, Cost, CostPerUnit, Status) — the supplier catalog
- `Prescription.RxTransaction` (DispensedItemID, DispensedQuantity, DateFilled, RxTransactionStatusTypeID)
- `Prescription.RxTransactionStatusType` (RxTransactionStatusTypeID, Description)

## How to run
```bash
# 1. SQL engine (Apple Silicon → Azure SQL Edge; a real Windows box → SQL Server/Express)
docker run -d --name suavo-pms -e ACCEPT_EULA=1 -e MSSQL_SA_PASSWORD='<pw>' \
  -p 11433:1433 mcr.microsoft.com/azure-sql-edge:latest

# 2. Point the gated test at it (or use the console runner in scratchpad):
export SUAVO_PMS_CONN="Server=localhost,11433;User Id=sa;Password=<pw>;Encrypt=True;TrustServerCertificate=True"
dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter FullyQualifiedName~PricingSqlE2ETests
```
`PricingSqlE2ETests` seeds the schema+data itself and no-ops when `SUAVO_PMS_CONN` is unset (CI-safe).

## Proven results (2026-07-02, 16/16)
- Discovery: catalog=`ItemPricing`, cost=`Cost`, per-unit=`CostPerUnit`, item-join NDC ✓
- Top-list ranked by dispensing: Fluticasone(2123) > Omeprazole20(1905) > Atorvastatin(1300) > Metformin > Omeprazole40 > Lisinopril; **brand (Lipitor), OTC (Aspirin), scheduled (Alprazolam) excluded; voided/reversed fills not counted; out-of-window fill excluded**
- Cheapest supplier: Omeprazole40 `55111064501` → **McKesson $0.0099/unit** (the 500-ct per-unit winner, NOT the pack-cost winner Real Value Rx $3.16) ✓; Fluticasone `60505082901` → **Parmed** ✓

## On the real target (Windows)
Pharmacies run Windows; the authentic run is SQL Server/Express on the box with the deployed agent
in `PricingExecutor=SqlFirst` pointed at it. Same schema + assertions; this runbook is the recipe.
