using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Executes the top-dispensed worklist query (<see cref="SqlTopDispensedQueryBuilder"/>) against the
/// PioneerRx DB and returns the ranked <see cref="TopDispensedRow"/>s — the SQL modality of the report
/// Nadim generates by hand (Rx Binoculars → Transaction Search → Top-X). The rows are handed to
/// <c>ExcelTop500Writer</c> to produce the same sheet his export does, which then feeds the pricing loop.
///
/// <para>PHI-negative: the query aggregates per drug/strength/NDC (GROUP BY) — no patient/Rx row is
/// selected. Fail-soft: an unbuildable query (unresolved schema / no dispensed statuses), a SQL error,
/// or cancellation yields an empty list — the caller generates nothing rather than a wrong/partial
/// worklist. Never throws to the caller.</para>
/// </summary>
public sealed class SqlTopDispensedGenerator
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _connectionFactory;
    private readonly ILogger<SqlTopDispensedGenerator> _logger;
    private readonly IReadOnlyList<string> _dispensedStatusNames;

    private const int CommandTimeoutSeconds = 30; // a Top-N GROUP BY over the fill history is heavier than a point lookup

    public SqlTopDispensedGenerator(
        Func<CancellationToken, Task<SqlConnection>> connectionFactory,
        IReadOnlyList<string> dispensedStatusNames,
        ILogger<SqlTopDispensedGenerator> logger)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _dispensedStatusNames = dispensedStatusNames ?? Array.Empty<string>();
        _logger = logger;
    }

    /// <summary>
    /// Top <paramref name="topN"/> most-dispensed generic Rx items filled on/after
    /// <paramref name="windowStart"/> (Nadim uses Jan 1 → today). Empty on any failure.
    /// </summary>
    public async Task<IReadOnlyList<TopDispensedRow>> GenerateAsync(
        TopDispensedSpec spec,
        int topN,
        DateTime windowStart,
        CancellationToken ct)
    {
        var query = SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(spec, _dispensedStatusNames);
        if (query is null || topN <= 0)
        {
            _logger.LogWarning(
                "SqlTopDispensedGenerator: cannot build query (schema unresolved, no dispensed statuses, or topN<=0) — yielding no worklist");
            return Array.Empty<TopDispensedRow>();
        }

        try
        {
            var conn = await _connectionFactory(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.Add(new SqlParameter(SqlTopDispensedQueryBuilder.TopNParameter, topN));
            cmd.Parameters.Add(new SqlParameter(SqlTopDispensedQueryBuilder.WindowStartParameter, windowStart));
            cmd.Parameters.Add(new SqlParameter(SqlTopDispensedQueryBuilder.GenericParameter, spec.GenericValue));
            if (!string.IsNullOrWhiteSpace(spec.RxOtcColumn) && !string.IsNullOrWhiteSpace(spec.RxValue))
                cmd.Parameters.Add(new SqlParameter(SqlTopDispensedQueryBuilder.RxParameter, spec.RxValue));
            if (!string.IsNullOrWhiteSpace(spec.ScheduleColumn) && !string.IsNullOrWhiteSpace(spec.NoScheduleValue))
                cmd.Parameters.Add(new SqlParameter(SqlTopDispensedQueryBuilder.NoScheduleParameter, spec.NoScheduleValue));
            for (var i = 0; i < _dispensedStatusNames.Count; i++)
                cmd.Parameters.Add(new SqlParameter(
                    $"{SqlTopDispensedQueryBuilder.StatusParameterPrefix}{i}", _dispensedStatusNames[i]));

            var rows = new List<TopDispensedRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var drug = reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "";
                var strength = reader.IsDBNull(1) ? "" : reader.GetValue(1)?.ToString() ?? "";
                var ndc = reader.IsDBNull(2) ? "" : reader.GetValue(2)?.ToString() ?? "";
                var total = reader.IsDBNull(3) ? 0m : Convert.ToDecimal(reader.GetValue(3));
                if (!string.IsNullOrWhiteSpace(ndc))
                    rows.Add(new TopDispensedRow(drug, strength, ndc, total));
            }

            _logger.LogInformation("SqlTopDispensedGenerator: generated {Count} rows (topN={TopN})", rows.Count, topN);
            return rows;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<TopDispensedRow>();
        }
        catch (Exception ex)
        {
            _logger.LogError("SqlTopDispensedGenerator failed ({ErrorType}) — yielding no worklist", ex.GetType().Name);
            return Array.Empty<TopDispensedRow>();
        }
    }
}
