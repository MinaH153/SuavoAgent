using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Builds the parameterized "Top N most-dispensed items over a window" SQL — the report Nadim
/// generates by hand today (Rx Binoculars → Transaction Search, Jan 1→today, Generic / Rx-not-OTC /
/// No Schedule → Top-X report → export). Emits rows shaped exactly like his sheet: Drug / Strength /
/// NDC / Total Dispensed, ranked by summed dispensed quantity descending.
///
/// <para>Same discipline as <see cref="SqlVolumeQueryBuilder"/>: the RxTransaction side is
/// canary-pinned (verified present by <c>PioneerRxCanarySource</c>); the Item-side columns come from
/// the resolved <see cref="TopDispensedSpec"/>. Aggregate per-item only (GROUP BY drug/strength/NDC) —
/// no per-Rx / per-patient row is selected, so the result is PHI-negative (drug volumes, no identifier).</para>
///
/// <para><b>Fail-closed</b> (returns null, caller yields no worklist rather than guess):</para>
/// <list type="bullet">
///   <item>required Item identifiers missing (table / id / NDC / drug-name);</item>
///   <item>the "generics only" gate is unresolved (no BrandGeneric column or value);</item>
///   <item>no dispensed-status names supplied (an unfiltered SUM would count voids/reversals and
///     rank the wrong items — the same overcount hazard the volume builder guards).</item>
/// </list>
/// The Rx-not-OTC and No-Schedule gates are applied only when their columns are resolved; absent, the
/// generics gate + the report's own scope still hold, and the omission is the caller's to log.
/// </summary>
public static class SqlTopDispensedQueryBuilder
{
    public const string TopNParameter = "@topN";
    public const string WindowStartParameter = "@windowStart";
    public const string GenericParameter = "@generic";
    public const string RxParameter = "@rxOtc";
    public const string NoScheduleParameter = "@noSchedule";
    public const string StatusParameterPrefix = "@tdstatus";

    // Canary-pinned (PioneerRxCanarySource) — identical anchors to SqlVolumeQueryBuilder.
    private const string RxSchema = "Prescription";
    private const string RxTable = "RxTransaction";
    private const string StatusTable = "RxTransactionStatusType";
    private const string DispensedQuantityColumn = "DispensedQuantity";
    private const string DispensedItemIdColumn = "DispensedItemID";
    private const string DateFilledColumn = "DateFilled";
    private const string StatusFkColumn = "RxTransactionStatusTypeID";
    private const string StatusDescriptionColumn = "Description";

    public static string? BuildTopDispensedQuery(
        TopDispensedSpec spec,
        IReadOnlyList<string> dispensedStatusNames)
    {
        if (spec is null) return null;
        if (dispensedStatusNames is not { Count: > 0 }) return null; // fail-safe: never count voids
        if (string.IsNullOrWhiteSpace(spec.ItemTable)
            || string.IsNullOrWhiteSpace(spec.ItemIdColumnInItem)
            || string.IsNullOrWhiteSpace(spec.NdcColumnInItem)
            || string.IsNullOrWhiteSpace(spec.DrugNameColumn)
            || string.IsNullOrWhiteSpace(spec.BrandGenericColumn)
            || string.IsNullOrWhiteSpace(spec.GenericValue))
            return null;

        var rx = QualifiedIdent(RxSchema, RxTable);
        var statusTbl = QualifiedIdent(RxSchema, StatusTable);
        var item = QualifiedIdent(spec.ItemTableSchema, spec.ItemTable);

        // Strength is optional in the schema; emit a stable empty literal so the output sheet keeps
        // its Drug / Strength / NDC / Total Dispensed column shape regardless.
        var strengthSelect = string.IsNullOrWhiteSpace(spec.StrengthColumn)
            ? "'' AS [Strength]"
            : $"it.{BracketIdent(spec.StrengthColumn!)} AS [Strength]";
        var strengthGroupBy = string.IsNullOrWhiteSpace(spec.StrengthColumn)
            ? null
            : $"it.{BracketIdent(spec.StrengthColumn!)}";

        var statusParams = string.Join(", ",
            Enumerable.Range(0, dispensedStatusNames.Count).Select(i => $"{StatusParameterPrefix}{i}"));

        var where = new List<string>
        {
            $"rt.{BracketIdent(DateFilledColumn)} >= {WindowStartParameter}",
            $"st.{BracketIdent(StatusDescriptionColumn)} IN ({statusParams})",
            $"it.{BracketIdent(spec.BrandGenericColumn)} = {GenericParameter}",
        };
        // Optional gates — only when the column is resolved AND a value to match is supplied.
        if (!string.IsNullOrWhiteSpace(spec.RxOtcColumn) && !string.IsNullOrWhiteSpace(spec.RxValue))
            where.Add($"it.{BracketIdent(spec.RxOtcColumn!)} = {RxParameter}");
        if (!string.IsNullOrWhiteSpace(spec.ScheduleColumn) && !string.IsNullOrWhiteSpace(spec.NoScheduleValue))
            where.Add($"it.{BracketIdent(spec.ScheduleColumn!)} = {NoScheduleParameter}");

        var groupBy = new List<string> { $"it.{BracketIdent(spec.DrugNameColumn)}" };
        if (strengthGroupBy is not null) groupBy.Add(strengthGroupBy);
        groupBy.Add($"it.{BracketIdent(spec.NdcColumnInItem)}");

        return
            $"SELECT TOP ({TopNParameter}) " +
            $"it.{BracketIdent(spec.DrugNameColumn)} AS [Drug], " +
            $"{strengthSelect}, " +
            $"it.{BracketIdent(spec.NdcColumnInItem)} AS [NDC], " +
            $"SUM(rt.{BracketIdent(DispensedQuantityColumn)}) AS [Total Dispensed] " +
            $"FROM {rx} AS rt " +
            $"INNER JOIN {item} AS it ON rt.{BracketIdent(DispensedItemIdColumn)} = it.{BracketIdent(spec.ItemIdColumnInItem)} " +
            $"INNER JOIN {statusTbl} AS st ON rt.{BracketIdent(StatusFkColumn)} = st.{BracketIdent(StatusFkColumn)} " +
            $"WHERE {string.Join(" AND ", where)} " +
            $"GROUP BY {string.Join(", ", groupBy)} " +
            $"ORDER BY SUM(rt.{BracketIdent(DispensedQuantityColumn)}) DESC";
    }

    private static string QualifiedIdent(string schema, string table) =>
        $"{BracketIdent(schema)}.{BracketIdent(table)}";

    private static string BracketIdent(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
