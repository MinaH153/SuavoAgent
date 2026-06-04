using System.Collections.Generic;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Builds the parameterized "aggregate dispensed quantity for an NDC over a window" SQL — the
/// VOLUME half of the M1 savings inputs. Reuses the install-discovered Item table (from
/// <see cref="DiscoveredPricingSchema.ItemJoin"/>) to map an NDC to <c>DispensedItemID</c>; the
/// <c>Prescription.RxTransaction</c> table + columns are canary-pinned (verified present by
/// <c>PioneerRxCanarySource</c>). Separate from the provider so the statement can be asserted in tests.
///
/// <para><b>Fail-safe void/status filter (Codex blocker).</b> An unfiltered SUM would count voided,
/// reversed, transferred and otherwise non-final transactions and INFLATE the volume — a wrong
/// savings number. The exact "this counts as a real dispense" status set is pharmacy-specific and
/// must be ground-truthed on the live box, so this builder REQUIRES the caller to supply the
/// dispensed-status descriptions; with none, it returns null (the provider then yields no quantity)
/// rather than guess and overcount.</para>
///
/// <para>Returns null when the schema has no Item join (cannot map NDC → DispensedItemID) OR no
/// dispensed-status names are supplied.</para>
/// </summary>
public static class SqlVolumeQueryBuilder
{
    public const string NdcParameter = "@ndc";
    public const string WindowStartParameter = "@windowStart";
    public const string StatusParameterPrefix = "@vstatus";

    // Canary-pinned (PioneerRxCanarySource): Prescription.RxTransaction.{DispensedQuantity,
    // DispensedItemID, DateFilled, RxTransactionStatusTypeID} + Prescription.RxTransactionStatusType
    // .Description. Verified present on the live DB by the canary; identifiers are constants.
    private const string RxSchema = "Prescription";
    private const string RxTable = "RxTransaction";
    private const string StatusTable = "RxTransactionStatusType";
    private const string DispensedQuantityColumn = "DispensedQuantity";
    private const string DispensedItemIdColumn = "DispensedItemID";
    private const string DateFilledColumn = "DateFilled";
    private const string StatusFkColumn = "RxTransactionStatusTypeID";
    private const string StatusDescriptionColumn = "Description";

    /// <summary>
    /// SUM of dispensed quantity for <see cref="NdcParameter"/> over fills on/after
    /// <see cref="WindowStartParameter"/> whose transaction status is one of
    /// <paramref name="dispensedStatusNames"/> (bound by the caller to
    /// <see cref="StatusParameterPrefix"/>0..N). Aggregate per-NDC only — no per-Rx / per-patient row
    /// is selected, so the result is PHI-negative (drug volume, no identifier). Null if unmappable or
    /// no statuses supplied.
    /// </summary>
    public static string? BuildDispensedQuantityQuery(
        DiscoveredPricingSchema s,
        IReadOnlyList<string> dispensedStatusNames)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.ItemJoin is null) return null;                 // cannot map DispensedItemID → NDC
        if (dispensedStatusNames is not { Count: > 0 }) return null; // fail-safe: never count voids

        var rx = QualifiedIdent(RxSchema, RxTable);
        var statusTbl = QualifiedIdent(RxSchema, StatusTable);
        var item = QualifiedIdent(s.ItemJoin.ItemTableSchema, s.ItemJoin.ItemTable);

        var statusParams = string.Join(", ",
            Enumerable.Range(0, dispensedStatusNames.Count).Select(i => $"{StatusParameterPrefix}{i}"));

        return
            $"SELECT SUM(rt.{BracketIdent(DispensedQuantityColumn)}) " +
            $"FROM {rx} AS rt " +
            $"INNER JOIN {item} AS it ON rt.{BracketIdent(DispensedItemIdColumn)} = it.{BracketIdent(s.ItemJoin.ItemIdColumnInItem)} " +
            $"INNER JOIN {statusTbl} AS st ON rt.{BracketIdent(StatusFkColumn)} = st.{BracketIdent(StatusFkColumn)} " +
            $"WHERE it.{BracketIdent(s.ItemJoin.NdcColumnInItem)} = {NdcParameter} " +
            $"AND rt.{BracketIdent(DateFilledColumn)} >= {WindowStartParameter} " +
            $"AND st.{BracketIdent(StatusDescriptionColumn)} IN ({statusParams})";
    }

    private static string QualifiedIdent(string schema, string table) =>
        $"{BracketIdent(schema)}.{BracketIdent(table)}";

    private static string BracketIdent(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
