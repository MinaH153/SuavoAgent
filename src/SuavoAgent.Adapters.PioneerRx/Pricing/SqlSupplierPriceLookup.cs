using System.Data;
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
    public const string InvalidNdcCode = "pricing_ndc_invalid";

    private readonly Func<CancellationToken, Task<SqlConnection>> _connectionFactory;
    private readonly ILogger<SqlSupplierPriceLookup> _logger;
    private readonly string _query;
    private readonly IReadOnlyList<string> _eligibleStatuses;
    private readonly PricingSqlColumnShape _ndcShape;
    private readonly PricingSqlColumnShape _statusShape;

    // Per-query timeout must be short — 500 NDCs × 2s = 1000s ceiling, well under the UIA path.
    private const int CommandTimeoutSeconds = 5;

    public SqlSupplierPriceLookup(
        DiscoveredPricingSchema schema,
        Func<CancellationToken, Task<SqlConnection>> connectionFactory,
        ILogger<SqlSupplierPriceLookup> logger)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = logger;

        // Snapshot the mutable IReadOnlyList implementation before query construction so the SQL
        // placeholder count and the values bound at execution can never diverge after admission.
        var statusSnapshot = schema.AvailableStatusValues?.ToArray()
            ?? throw new InvalidOperationException(SqlPricingQueryBuilder.StatusEligibilityUnresolvedCode);
        var schemaSnapshot = schema with { AvailableStatusValues = statusSnapshot };
        _eligibleStatuses = SqlPricingQueryBuilder.GetValidatedEligibleStatuses(schemaSnapshot);
        (_ndcShape, _statusShape) = SqlPricingQueryBuilder.GetValidatedFilterShapes(
            schemaSnapshot,
            _eligibleStatuses);
        _query = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schemaSnapshot);
    }

    public async Task<SupplierPriceResult> FindCheapestSupplierAsync(
        string jobId, int rowIndex, string ndc11, CancellationToken ct)
    {
        if (!IsCanonicalNdc11(ndc11))
            return Miss(jobId, rowIndex, ndc11 ?? string.Empty, InvalidNdcCode);

        try
        {
            var conn = await _connectionFactory(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = _query;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            BindQueryParameters(cmd, ndc11, _eligibleStatuses, _ndcShape, _statusShape);

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
        catch (SqlException)
        {
            _logger.LogWarning("SqlSupplierPriceLookup: SQL operation failed");
            return Miss(jobId, rowIndex, ndc11, "SQL operation failed");
        }
        catch (Exception)
        {
            _logger.LogWarning("SqlSupplierPriceLookup: lookup failed locally");
            return Miss(jobId, rowIndex, ndc11, "Supplier lookup failed locally");
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

    private static bool IsCanonicalNdc11(string? value)
    {
        if (value is not { Length: 11 }) return false;
        foreach (var character in value)
        {
            if (character is < '0' or > '9') return false;
        }
        return true;
    }

    internal static void BindQueryParameters(
        SqlCommand command,
        string ndc11,
        IReadOnlyList<string> eligibleStatuses,
        PricingSqlColumnShape ndcShape,
        PricingSqlColumnShape statusShape)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(eligibleStatuses);

        if (!PricingSqlTypePolicy.TryAddClassificationParameter(
                command,
                SqlPricingQueryBuilder.NdcParameter,
                ndc11,
                ndcShape,
                SqlPricingQueryBuilder.MaximumNdcColumnSize))
            throw new InvalidOperationException(SqlPricingQueryBuilder.ColumnTypeUnresolvedCode);

        for (int i = 0; i < eligibleStatuses.Count; i++)
        {
            if (!PricingSqlTypePolicy.TryAddClassificationParameter(
                    command,
                    SqlPricingQueryBuilder.StatusParameterName(i),
                    eligibleStatuses[i],
                    statusShape,
                    SqlPricingQueryBuilder.MaximumStatusColumnSize))
                throw new InvalidOperationException(SqlPricingQueryBuilder.ColumnTypeUnresolvedCode);
        }
    }

    private static SupplierPriceResult Miss(string jobId, int rowIndex, string ndc, string reason) =>
        new(jobId, rowIndex, ndc, false, null, null, reason);
}
