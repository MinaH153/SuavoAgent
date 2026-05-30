using SuavoAgent.Contracts.Adapters;

namespace SuavoAgent.Core.Adapters;

/// <summary>
/// Resolves the SQL catalog to connect to for a pharmacy, after its adapter is selected.
/// A pharmacy's explicit SqlDatabase wins; otherwise the resolved adapter's default catalog
/// is used. This replaces the hardcoded "PioneerPharmacySystem" fallback that would otherwise
/// shadow a non-PioneerRx adapter's catalog (Codex Q3 trap).
/// </summary>
public static class AdapterCatalog
{
    public static string Resolve(string? pharmacySqlDatabase, AdapterConfig config)
        => string.IsNullOrWhiteSpace(pharmacySqlDatabase)
            ? config.Sql.DefaultCatalog
            : pharmacySqlDatabase;
}
