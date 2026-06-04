using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Builds the parameterized "aggregate dispensed quantity for an NDC over a window" SQL — the
/// VOLUME half of the M1 savings inputs. Reuses the install-discovered Item table (from
/// <see cref="DiscoveredPricingSchema.ItemJoin"/>) to map an NDC to <c>DispensedItemID</c>; the
/// <c>Prescription.RxTransaction</c> table + columns are canary-pinned (verified present by
/// <c>PioneerRxCanarySource</c>). Separate from the provider so the statement can be asserted in tests.
///
/// <para>Returns null when the schema has no Item join (we cannot map RxTransaction →
/// DispensedItemID → NDC without it) — the provider then yields no quantity (fail-soft).</para>
/// </summary>
public static class SqlVolumeQueryBuilder
{
    public const string NdcParameter = "@ndc";
    public const string WindowStartParameter = "@windowStart";

    // Canary-pinned (PioneerRxCanarySource): Prescription.RxTransaction.{DispensedQuantity,
    // DispensedItemID, DateFilled}. These are verified present on the live DB by the canary;
    // if PioneerRx renames them the canary fails first. Identifiers are constants, never user input.
    private const string RxSchema = "Prescription";
    private const string RxTable = "RxTransaction";
    private const string DispensedQuantityColumn = "DispensedQuantity";
    private const string DispensedItemIdColumn = "DispensedItemID";
    private const string DateFilledColumn = "DateFilled";

    /// <summary>
    /// SUM of dispensed quantity for <see cref="NdcParameter"/> over fills on/after
    /// <see cref="WindowStartParameter"/>. Aggregate per-NDC only — no per-Rx / per-patient row is
    /// selected, so the result is PHI-negative (drug volume, no identifier). Null if unmappable.
    /// </summary>
    public static string? BuildDispensedQuantityQuery(DiscoveredPricingSchema s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.ItemJoin is null) return null; // cannot map DispensedItemID → NDC without the Item table

        var rx = QualifiedIdent(RxSchema, RxTable);
        var item = QualifiedIdent(s.ItemJoin.ItemTableSchema, s.ItemJoin.ItemTable);

        return
            $"SELECT SUM(rt.{BracketIdent(DispensedQuantityColumn)}) " +
            $"FROM {rx} AS rt " +
            $"INNER JOIN {item} AS it ON rt.{BracketIdent(DispensedItemIdColumn)} = it.{BracketIdent(s.ItemJoin.ItemIdColumnInItem)} " +
            $"WHERE it.{BracketIdent(s.ItemJoin.NdcColumnInItem)} = {NdcParameter} " +
            $"AND rt.{BracketIdent(DateFilledColumn)} >= {WindowStartParameter}";
    }

    private static string QualifiedIdent(string schema, string table) =>
        $"{BracketIdent(schema)}.{BracketIdent(table)}";

    private static string BracketIdent(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
