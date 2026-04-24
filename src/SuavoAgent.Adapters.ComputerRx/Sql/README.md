# `SuavoAgent.Adapters.ComputerRx.Sql`

**Populate after recon confirms data layer.**

See `docs/self-healing/second-pms-integration-spec.md` §"Data layer" for the
open questions that gate implementation:

- SQL Server backend? MSDE/SQL Express local? Or Btrieve / flat files?
- Connection discovery path (config file, registry key, network probe)
- Auth mode — Windows integrated vs SQL auth
- Schema names + core tables
- Writeback surface

Mirror `src/SuavoAgent.Adapters.PioneerRx/Sql/` only once recon answers the
questions above. Until then, this folder exists as a placeholder so the
csproj compiles and the solution layout reads as intentional.

> DO NOT IMPLEMENT until Tier 1 5b kickoff per Mission memo.
