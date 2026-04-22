using System.Text;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Builds the parameterized "cheapest available supplier for an NDC" SQL against the
/// <see cref="DiscoveredPricingSchema"/> resolved at install time. Separate from the SQL runner so
/// the generated statement can be asserted in tests.
///
/// Design intent (per Codex review): no string concatenation of user input; status filter is opt-in
/// and NDC matching uses an @ndc parameter. Catalog/table names come from sys.columns — trusted.
/// </summary>
public static class SqlPricingQueryBuilder
{
    public const string NdcParameter = "@ndc";

    /// <summary>
    /// Returns a parameterized SELECT ranking all supplier rows for the given NDC by cost ASC,
    /// taking the top row only. Caller wires <see cref="NdcParameter"/> to the 11-digit canonical NDC.
    /// </summary>
    public static string BuildCheapestSupplierQuery(DiscoveredPricingSchema s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var catalog = QualifiedIdent(s.CatalogSchema, s.CatalogTable);
        var costExpr = $"p.{BracketIdent(s.CostColumn)}";
        var costPerUnitExpr = s.CostPerUnitColumn != null
            ? $"p.{BracketIdent(s.CostPerUnitColumn)}"
            : costExpr; // no dedicated per-unit column → fall back to Cost

        var sb = new StringBuilder();
        sb.Append("SELECT TOP 1 ");
        sb.Append(SupplierSelectExpression(s.SupplierSource)).Append(" AS SupplierName, ");
        sb.Append(costExpr).Append(" AS Cost, ");
        sb.Append(costPerUnitExpr).Append(" AS CostPerUnit");
        if (s.StatusColumn != null)
            sb.Append(", p.").Append(BracketIdent(s.StatusColumn)).Append(" AS CatalogStatus");

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

        if (s.StatusColumn != null && s.AvailableStatusValues.Count > 0)
        {
            sb.Append(" AND p.").Append(BracketIdent(s.StatusColumn)).Append(" IN (");
            for (int i = 0; i < s.AvailableStatusValues.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('\'').Append(SqlEscape(s.AvailableStatusValues[i])).Append('\'');
            }
            sb.Append(')');
        }

        sb.Append(" AND ").Append(costExpr).Append(" > 0 ORDER BY ").Append(costExpr).Append(" ASC");

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

    /// <summary>
    /// Wraps an identifier in brackets with <c>]</c> escaping per T-SQL rules. Identifier strings
    /// come from <c>sys.columns</c>, not user input — but we still sanitise defensively.
    /// </summary>
    private static string BracketIdent(string ident) => "[" + ident.Replace("]", "]]") + "]";

    private static string SqlEscape(string literal) => literal.Replace("'", "''");
}
