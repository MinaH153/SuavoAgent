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
        try
        {
            return (await GenerateVerifiedAsync(spec, topN, windowStart, ct)
                    .ConfigureAwait(false)).Rows;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<TopDispensedRow>();
        }
    }

    /// <summary>
    /// Orchestration-facing variant. It preserves fixed structural failure evidence and lets
    /// cancellation propagate so a signed command cannot be mislabeled as an empty report.
    /// </summary>
    public async Task<TopDispensedGenerationResult> GenerateVerifiedAsync(
        TopDispensedSpec spec,
        int topN,
        DateTime windowStart,
        CancellationToken ct)
    {
        string? query;
        try
        {
            query = SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(spec, _dispensedStatusNames);
        }
        catch (InvalidOperationException ex) when (
            ex.Message is SqlTopDispensedQueryBuilder.RxFilterUnresolvedCode
                or SqlTopDispensedQueryBuilder.ScheduleFilterUnresolvedCode
                or SqlTopDispensedQueryBuilder.FilterTypeUnresolvedCode)
        {
            _logger.LogWarning(
                "SqlTopDispensedGenerator: required report filter unresolved ({ReasonCode}) — yielding no worklist",
                ex.Message);
            return TopDispensedGenerationResult.Fail(ex.Message);
        }

        if (query is null || topN <= 0)
        {
            _logger.LogWarning(
                "SqlTopDispensedGenerator: cannot build query (schema unresolved, no dispensed statuses, or topN<=0) — yielding no worklist");
            return TopDispensedGenerationResult.Fail(
                topN <= 0
                    ? "top_dispensed_top_n_invalid"
                    : "top_dispensed_schema_unresolved");
        }

        try
        {
            var conn = await _connectionFactory(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = query;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            BindParameters(cmd, spec, topN, windowStart, _dispensedStatusNames);

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
            return TopDispensedGenerationResult.Success(rows);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("SqlTopDispensedGenerator failed ({ErrorType}) — yielding no worklist", ex.GetType().Name);
            return TopDispensedGenerationResult.Fail("top_dispensed_query_failed");
        }
    }

    internal static void BindParameters(
        SqlCommand command,
        TopDispensedSpec spec,
        int topN,
        DateTime windowStart,
        IReadOnlyList<string> dispensedStatusNames)
    {
        SqlTopDispensedQueryBuilder.ValidateFilterTypes(spec);
        command.Parameters.Add(SqlTopDispensedQueryBuilder.TopNParameter, System.Data.SqlDbType.Int).Value = topN;
        command.Parameters.Add(SqlTopDispensedQueryBuilder.WindowStartParameter, System.Data.SqlDbType.DateTime2).Value = windowStart;
        if (!PricingSqlTypePolicy.TryAddClassificationParameter(
                command,
                SqlTopDispensedQueryBuilder.GenericParameter,
                spec.GenericValue,
                spec.BrandGenericColumnShape,
                SqlTopDispensedQueryBuilder.MaximumClassificationSize) ||
            !PricingSqlTypePolicy.TryAddClassificationParameter(
                command,
                SqlTopDispensedQueryBuilder.RxParameter,
                spec.RxValue!,
                spec.RxOtcColumnShape,
                SqlTopDispensedQueryBuilder.MaximumClassificationSize) ||
            !PricingSqlTypePolicy.TryAddTextOrIntegerParameter(
                command,
                SqlTopDispensedQueryBuilder.NoScheduleParameter,
                spec.NoScheduleValue!,
                spec.ScheduleColumnShape,
                SqlTopDispensedQueryBuilder.MaximumClassificationSize))
            throw new InvalidOperationException(SqlTopDispensedQueryBuilder.FilterTypeUnresolvedCode);

        for (var i = 0; i < dispensedStatusNames.Count; i++)
        {
            var value = dispensedStatusNames[i];
            if (string.IsNullOrWhiteSpace(value) || value.Length > SqlTopDispensedQueryBuilder.StatusParameterSize)
                throw new InvalidOperationException(SqlTopDispensedQueryBuilder.FilterTypeUnresolvedCode);
            command.Parameters.Add(
                $"{SqlTopDispensedQueryBuilder.StatusParameterPrefix}{i}",
                System.Data.SqlDbType.NVarChar,
                SqlTopDispensedQueryBuilder.StatusParameterSize).Value = value;
        }
    }
}

public sealed record TopDispensedGenerationResult(
    bool Ok,
    IReadOnlyList<TopDispensedRow> Rows,
    string? ErrorCode)
{
    public static TopDispensedGenerationResult Success(
        IReadOnlyList<TopDispensedRow> rows) => new(true, rows, null);

    public static TopDispensedGenerationResult Fail(string errorCode) =>
        new(false, Array.Empty<TopDispensedRow>(), errorCode);
}
