# Mock PioneerRx SQL (local integration harness)

Lightweight SQL Server 2022 container + seed script so the price-shopper Tier-2 SQL stack can be
exercised end-to-end without a live pharmacy. Ships for dev-loop use; not part of any production
artifact.

## Quickstart

```bash
cd ~/Code/SuavoAgent
docker compose --project-directory ./docs/dev/mock-pioneerrx-sql up -d
# wait for healthy
sqlcmd -S localhost,51433 -U sa -P 'Suavo!MockDev1234' -C -i ./docs/dev/mock-pioneerrx-sql/seed.sql
```

Connection string for integration tests:

```
Server=localhost,51433;Database=PioneerPharmacySystem;User Id=sa;Password=Suavo!MockDev1234;TrustServerCertificate=true;Encrypt=true
```

Host port `51433` is deliberate to avoid colliding with a local SQL Server Developer install on the
standard `1433`.

## What's in the mock

- `Inventory.Item` — 6 drugs with real-looking NDCs (Omeprazole, Metformin, Lisinopril,
  Atorvastatin, Tramadol C-IV, Hydrocodone C-II).
- `Inventory.ItemPricing` — 17 supplier rows across those 6 NDCs. Multiple suppliers per NDC with
  varying `Cost`, a mix of `Available` / `Discontinued` / `Expired` statuses so
  `PricingSchemaResolver.DefaultAvailableStatusValues` has something to filter against.
- `Prescription.Rx` / `Prescription.RxTransaction` / `Prescription.RxTransactionStatusType` —
  minimum rows so `PioneerRxSqlEngine.VerifyPioneerRxSchemaAsync` accepts the DB (H-4 LAN-impostor
  guard) and the rest of the agent can boot against this container.

## Expected behaviour

After seeding:

| NDC | Cheapest supplier (expected) | Cost per unit |
|-----|------------------------------|---------------|
| 55111-0645-01 | Anda | 0.012000 |
| 00093-5124-01 | Amerisource | 0.009800 |
| 16714-0234-01 | Anda | 0.007500 |
| 50242-0041-21 | Cardinal | 0.020500 |
| 00093-0058-01 | McKesson | 0.018000 |
| 00406-0365-01 | Mckesson 340b | 0.040000 |

These are the rows a correctly-wired `SqlPricingJobRunner` + `SqlSupplierPriceLookup` should write
to the priced.xlsx sibling file.

## Why this isn't an integration test in CI yet

1. Docker-on-CI for SQL Server pulls ~1GB on cold cache; not worth it for the quick pricing feedback loop.
2. Until CRD recon confirms Nadim's live schema matches this shape, the seed is a *guess*. Promoting to
   CI before Thursday's recon risks writing green tests against a wrong schema.

Plan: once CRD recon JSON lands (Thursday), update `seed.sql` to mirror Nadim's real schema, and add a
`tests/SuavoAgent.Adapters.PioneerRx.IntegrationTests/` project with the `[Trait("category","integration")]`
gated tests. CI opt-in via `dotnet test --filter "category=integration"`.

## Teardown

```bash
docker compose --project-directory ./docs/dev/mock-pioneerrx-sql down -v
```

`-v` deletes the named volume — you'll re-seed on the next start.
