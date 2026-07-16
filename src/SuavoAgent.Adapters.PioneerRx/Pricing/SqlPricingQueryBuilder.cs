using System.Text;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Builds the parameterized "cheapest available supplier for an NDC" SQL against the
/// <see cref="DiscoveredPricingSchema"/> resolved at install time. Separate from the SQL runner so
/// the generated statement can be asserted in tests.
///
/// Design intent (per Codex review): no string concatenation of query values; both NDC and catalog
/// eligibility statuses use parameters. The status filter is mandatory and allowlisted. Catalog/table
/// names come from sys.columns and retain defensive T-SQL identifier quoting.
/// </summary>
public static class SqlPricingQueryBuilder
{
    public const string NdcParameter = "@ndc";
    public const string StatusEligibilityUnresolvedCode = "pricing_status_eligibility_unresolved";
    public const string ColumnTypeUnresolvedCode = "pricing_sql_column_type_unresolved";
    internal const int StatusParameterSize = 16;
    internal const int MaximumNdcColumnSize = 64;
    internal const int MaximumStatusColumnSize = 64;

    /// <summary>
    /// Returns a parameterized SELECT ranking all supplier rows for the given NDC by per-unit cost ASC,
    /// taking the top row only. Caller wires <see cref="NdcParameter"/> to the 11-digit canonical NDC.
    /// </summary>
    public static string BuildCheapestSupplierQuery(DiscoveredPricingSchema s)
    {
        ArgumentNullException.ThrowIfNull(s);

        // Feature A's output contract is explicitly per-unit. A catalog pack
        // cost is not a conservative substitute: relabeling it as CostPerUnit
        // can select the wrong supplier and fabricate savings when pack sizes
        // differ. Discovery without a dedicated unit-cost column is therefore
        // an admission failure, never a query fallback.
        if (string.IsNullOrWhiteSpace(s.CostPerUnitColumn))
            throw new InvalidOperationException("pricing_cost_basis_unresolved");

        var eligibleStatuses = GetValidatedEligibleStatuses(s);
        _ = GetValidatedFilterShapes(s, eligibleStatuses);

        var catalog = QualifiedIdent(s.CatalogSchema, s.CatalogTable);
        var costExpr = $"p.{BracketIdent(s.CostColumn)}";
        var costPerUnitExpr = $"p.{BracketIdent(s.CostPerUnitColumn)}";

        var sb = new StringBuilder();
        sb.Append("SELECT TOP 1 ");
        sb.Append(SupplierSelectExpression(s.SupplierSource)).Append(" AS SupplierName, ");
        sb.Append(costExpr).Append(" AS Cost, ");
        sb.Append(costPerUnitExpr).Append(" AS CostPerUnit");
        sb.Append(", p.").Append(BracketIdent(s.StatusColumn!)).Append(" AS CatalogStatus");

        sb.Append(" FROM ").Append(catalog).Append(" AS p");

        if (s.ItemJoin != null)
        {
            var itemTable = QualifiedIdent(s.ItemJoin.ItemTableSchema, s.ItemJoin.ItemTable);
            sb.Append(" INNER JOIN ").Append(itemTable).Append(" AS i ON i.")
              .Append(BracketIdent(s.ItemJoin.ItemIdColumnInItem)).Append(" = p.")
              .Append(BracketIdent(s.ItemJoin.ItemIdColumnInCatalog));
        }

        if (s.SupplierSource.Resolution == SupplierResolution.JoinedSupplierTable)
        {
            var supTable = QualifiedIdent(s.SupplierSource.SupplierTableSchema!, s.SupplierSource.SupplierTable!);
            sb.Append(" INNER JOIN ").Append(supTable).Append(" AS sup ON sup.")
              .Append(BracketIdent(s.SupplierSource.SupplierIdColumnInSupplier!)).Append(" = p.")
              .Append(BracketIdent(s.SupplierSource.SupplierIdColumnInCatalog!));
        }

        sb.Append(" WHERE ");
        if (s.NdcColumn != null)
            sb.Append("p.").Append(BracketIdent(s.NdcColumn)).Append(" = ").Append(NdcParameter);
        else
            sb.Append("i.").Append(BracketIdent(s.ItemJoin!.NdcColumnInItem)).Append(" = ").Append(NdcParameter);

        sb.Append(" AND p.").Append(BracketIdent(s.StatusColumn!)).Append(" IN (");
        for (int i = 0; i < eligibleStatuses.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(StatusParameterName(i));
        }
        sb.Append(')');

        // Rank by the SAME quantity the savings ledger consumes. When the catalog has a dedicated
        // per-unit cost column, the cheapest-PER-UNIT supplier — not the cheapest-PACK supplier — is
        // the correct pick: ranking by pack cost while reporting that row's per-unit cost selects the
        // wrong supplier (and a wrong savings dollar figure) whenever pack sizes differ across
        // suppliers. A missing per-unit column was rejected above, before any SQL is emitted.
        sb.Append(" AND ").Append(costExpr).Append(" > 0");
        sb.Append(" AND ").Append(costPerUnitExpr).Append(" > 0");
        sb.Append(" ORDER BY ").Append(costPerUnitExpr).Append(" ASC");

        return sb.ToString();
    }

    private static string SupplierSelectExpression(CatalogSupplierSource src) => src.Resolution switch
    {
        SupplierResolution.Denormalized => $"p.{BracketIdent(src.NameColumnInCatalog!)}",
        SupplierResolution.JoinedSupplierTable => $"sup.{BracketIdent(src.SupplierNameColumnInSupplier!)}",
        _ => throw new InvalidOperationException($"Unknown supplier resolution {src.Resolution}"),
    };

    private static string QualifiedIdent(string schema, string table) =>
        $"{BracketIdent(schema)}.{BracketIdent(table)}";

    internal static string StatusParameterName(int index)
    {
        if (index is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(index));
        return $"@status{index}";
    }

    internal static IReadOnlyList<string> GetValidatedEligibleStatuses(DiscoveredPricingSchema schema)
    {
        if (string.IsNullOrWhiteSpace(schema.StatusColumn) ||
            schema.AvailableStatusValues is null ||
            schema.AvailableStatusValues.Count is < 1 or > 2)
        {
            throw new InvalidOperationException(StatusEligibilityUnresolvedCode);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new string[schema.AvailableStatusValues.Count];
        for (int i = 0; i < schema.AvailableStatusValues.Count; i++)
        {
            var value = schema.AvailableStatusValues[i];
            if (string.IsNullOrWhiteSpace(value) ||
                value.Length > StatusParameterSize ||
                !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                !IsKnownEligibleStatus(value) ||
                !seen.Add(value))
            {
                throw new InvalidOperationException(StatusEligibilityUnresolvedCode);
            }

            values[i] = value;
        }

        return values;
    }

    internal static (PricingSqlColumnShape Ndc, PricingSqlColumnShape Status)
        GetValidatedFilterShapes(
            DiscoveredPricingSchema schema,
            IReadOnlyList<string> eligibleStatuses)
    {
        if (!PricingSqlTypePolicy.IsExactNumeric(schema.CostColumnShape) ||
            !PricingSqlTypePolicy.IsExactNumeric(schema.CostPerUnitColumnShape))
            throw new InvalidOperationException(ColumnTypeUnresolvedCode);

        var ndcShape = schema.NdcColumn is not null
            ? schema.NdcColumnShape
            : schema.ItemJoin?.NdcColumnShape;
        if (!PricingSqlTypePolicy.TryGetBoundedTextParameter(
                ndcShape,
                minimumCharacters: 11,
                maximumCharacters: MaximumNdcColumnSize,
                out _,
                out _))
            throw new InvalidOperationException(ColumnTypeUnresolvedCode);

        var longestStatus = eligibleStatuses.Max(value => value.Length);
        if (!PricingSqlTypePolicy.TryGetBoundedTextParameter(
                schema.StatusColumnShape,
                minimumCharacters: longestStatus,
                maximumCharacters: MaximumStatusColumnSize,
                out _,
                out _))
            throw new InvalidOperationException(ColumnTypeUnresolvedCode);

        return (ndcShape!, schema.StatusColumnShape!);
    }

    private static bool IsKnownEligibleStatus(string value) =>
        value.Equals("Available", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Active", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps an identifier in brackets with <c>]</c> escaping per T-SQL rules. Identifier strings
    /// come from <c>sys.columns</c>, not user input — but we still sanitise defensively.
    /// </summary>
    private static string BracketIdent(string ident) => "[" + ident.Replace("]", "]]") + "]";
}
