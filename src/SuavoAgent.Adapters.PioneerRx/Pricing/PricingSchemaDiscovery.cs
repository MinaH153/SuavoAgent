using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Resolves the live PioneerRx pricing-catalog schema at install time by reading
/// <c>sys.tables</c>/<c>sys.columns</c> restricted to the Inventory schema, then handing the dump
/// to <see cref="PricingSchemaResolver"/>. Kept thin so the hard logic stays in a unit-testable
/// pure-C# layer.
/// </summary>
public sealed class PricingSchemaDiscovery
{
    private readonly ILogger<PricingSchemaDiscovery> _logger;

    // Constrain discovery to Inventory + Purchasing + Ordering schemas — anywhere an install could
    // reasonably place a supplier-catalog table. Broader schema sweeps risk matching Rx / Person
    // columns that look Cost-ish but aren't purchasing data.
    private static readonly string[] CandidateSchemas = { "Inventory", "Purchasing", "Ordering" };

    public PricingSchemaDiscovery(ILogger<PricingSchemaDiscovery> logger)
    {
        _logger = logger;
    }

    public async Task<PricingDiscoveryOutcome> DiscoverAsync(SqlConnection conn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (conn.State != System.Data.ConnectionState.Open)
            return PricingDiscoveryOutcome.Fail("SqlConnection is not open");

        List<InventoryColumnInfo> columns;
        try
        {
            columns = await FetchColumnsAsync(conn, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PricingSchemaDiscovery: metadata query failed");
            return PricingDiscoveryOutcome.Fail($"Metadata query failed: {ex.Message}");
        }

        _logger.LogInformation(
            "PricingSchemaDiscovery: observed {Count} columns across {Schemas}",
            columns.Count, string.Join(",", CandidateSchemas));

        var outcome = PricingSchemaResolver.Resolve(columns);
        if (outcome.Ok && outcome.Schema is { } schema)
        {
            _logger.LogInformation(
                "PricingSchemaDiscovery: resolved {Schema}.{Table} (confidence {Confidence:F2})",
                schema.CatalogSchema, schema.CatalogTable, schema.ConfidenceScore);
        }
        else
        {
            _logger.LogWarning("PricingSchemaDiscovery: resolution failed — {Reason}", outcome.Reason);
        }
        return outcome;
    }

    private static async Task<List<InventoryColumnInfo>> FetchColumnsAsync(
        SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT s.name AS SchemaName, t.name AS TableName, c.name AS ColumnName,
       tp.name AS DataTypeName, c.is_nullable AS IsNullable
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
JOIN sys.types tp ON tp.user_type_id = c.user_type_id
WHERE s.name IN (@s0, @s1, @s2)
ORDER BY s.name, t.name, c.column_id;";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = System.Data.CommandType.Text;
        cmd.Parameters.Add(new SqlParameter("@s0", CandidateSchemas[0]));
        cmd.Parameters.Add(new SqlParameter("@s1", CandidateSchemas[1]));
        cmd.Parameters.Add(new SqlParameter("@s2", CandidateSchemas[2]));

        var rows = new List<InventoryColumnInfo>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new InventoryColumnInfo(
                SchemaName: reader.GetString(0),
                TableName: reader.GetString(1),
                ColumnName: reader.GetString(2),
                DataType: reader.GetString(3),
                IsNullable: reader.GetBoolean(4)));
        }
        return rows;
    }
}
