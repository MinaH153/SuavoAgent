using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// SQL-backed <see cref="ISupplierPriceLookup"/>. Runs the query emitted by
/// <see cref="SqlPricingQueryBuilder"/> against a live PioneerRx SQL connection for each NDC.
///
/// Connection management is deliberately externalized — the caller passes a factory that yields
/// an open <see cref="SqlConnection"/>. This keeps the pooling/lifecycle decisions with the agent's
/// existing <c>PioneerRxSqlEngine</c> rather than forking its connection logic.
/// </summary>
public sealed class SqlSupplierPriceLookup : ISupplierPriceLookup
{
    private readonly DiscoveredPricingSchema _schema;
    private readonly Func<CancellationToken, Task<SqlConnection>> _connectionFactory;
    private readonly ILogger<SqlSupplierPriceLookup> _logger;
    private readonly string _query;

    // Per-query timeout must be short — 500 NDCs × 2s = 1000s ceiling, well under the UIA path.
    private const int CommandTimeoutSeconds = 5;

    public SqlSupplierPriceLookup(
        DiscoveredPricingSchema schema,
        Func<CancellationToken, Task<SqlConnection>> connectionFactory,
        ILogger<SqlSupplierPriceLookup> logger)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger;
        _query = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);
    }

    public async Task<SupplierPriceResult> FindCheapestSupplierAsync(
        string jobId, int rowIndex, string ndc11, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(ndc11);

        try
        {
            var conn = await _connectionFactory(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = _query;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.Add(new SqlParameter(SqlPricingQueryBuilder.NdcParameter, ndc11));

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return Miss(jobId, rowIndex, ndc11, "No supplier rows found");

            var supplier = reader.IsDBNull(0) ? null : reader.GetString(0);
            var cost = ReadDecimalFlexible(reader, 1);
            var costPerUnit = ReadDecimalFlexible(reader, 2);

            if (string.IsNullOrWhiteSpace(supplier) || costPerUnit is null || costPerUnit <= 0)
                return Miss(jobId, rowIndex, ndc11, "Top row had blank supplier or non-positive cost");

            return new SupplierPriceResult(
                JobId: jobId, RowIndex: rowIndex, Ndc: ndc11,
                Found: true, SupplierName: supplier,
                CostPerUnit: costPerUnit, ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "SqlSupplierPriceLookup: SQL error for NDC {Ndc}", ndc11);
            return Miss(jobId, rowIndex, ndc11, $"SQL error {ex.Number}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SqlSupplierPriceLookup: unhandled error for NDC {Ndc}", ndc11);
            return Miss(jobId, rowIndex, ndc11, ex.Message);
        }
    }

    /// <summary>
    /// PioneerRx's pricing columns arrive as money/decimal depending on schema. Reader.GetDecimal
    /// throws on type mismatch, so accept either and coerce once.
    /// </summary>
    private static decimal? ReadDecimalFlexible(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            decimal d => d,
            double d => (decimal)d,
            float f => (decimal)f,
            long l => l,
            int i => i,
            _ => null,
        };
    }

    private static SupplierPriceResult Miss(string jobId, int rowIndex, string ndc, string reason) =>
        new(jobId, rowIndex, ndc, false, null, null, reason);
}
