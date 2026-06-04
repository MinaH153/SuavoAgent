using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// SQL implementation of <see cref="IPharmacyBaselineVolumeProvider"/>: reads aggregate dispensed
/// QUANTITY per NDC from <c>Prescription.RxTransaction</c> (the VOLUME half of the M1 savings inputs)
/// over a trailing window.
///
/// <para>Baseline cost is intentionally left null here — PioneerRx's catalog models only the set of
/// supplier costs (cheapest-first), with no "what the pharmacy pays today" marker; baseline is
/// supplied by the Vision provider (reads the Pricing tab the pharmacist sees) or a ground-truthed
/// acquisition column. This provider composes with that one via the same seam.</para>
///
/// <para>Fail-soft: any SQL error, an unmappable schema (no Item join), or a NULL sum yields
/// <see cref="PharmacyBaselineVolume.None"/> — savings stays NULL, never a wrong number.</para>
/// </summary>
public sealed class SqlDispensedVolumeProvider : IPharmacyBaselineVolumeProvider
{
    private readonly Func<CancellationToken, Task<SqlConnection>> _connectionFactory;
    private readonly ILogger<SqlDispensedVolumeProvider> _logger;
    private readonly string? _query;
    private readonly int _windowDays;
    private readonly IReadOnlyList<string> _dispensedStatusNames;

    private const int CommandTimeoutSeconds = 5;

    public SqlDispensedVolumeProvider(
        DiscoveredPricingSchema schema,
        Func<CancellationToken, Task<SqlConnection>> connectionFactory,
        int windowDays,
        IReadOnlyList<string> dispensedStatusNames,
        ILogger<SqlDispensedVolumeProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _windowDays = windowDays > 0 ? windowDays : 90;
        _dispensedStatusNames = dispensedStatusNames ?? Array.Empty<string>();
        _logger = logger;
        // Null query when the schema is unmappable OR no dispensed-status filter is configured —
        // the provider then yields no quantity (fail-safe: never count voids / overcount).
        _query = SqlVolumeQueryBuilder.BuildDispensedQuantityQuery(schema, _dispensedStatusNames);
    }

    public async Task<PharmacyBaselineVolume> GetAsync(string ndc11, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ndc11) || _query is null)
            return PharmacyBaselineVolume.None;

        try
        {
            var conn = await _connectionFactory(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = _query;
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.Add(new SqlParameter(SqlVolumeQueryBuilder.NdcParameter, ndc11));
            cmd.Parameters.Add(new SqlParameter(
                SqlVolumeQueryBuilder.WindowStartParameter,
                DateTime.UtcNow.Date.AddDays(-_windowDays)));
            for (var i = 0; i < _dispensedStatusNames.Count; i++)
                cmd.Parameters.Add(new SqlParameter(
                    $"{SqlVolumeQueryBuilder.StatusParameterPrefix}{i}", _dispensedStatusNames[i]));

            var scalar = await cmd.ExecuteScalarAsync(ct);
            return new PharmacyBaselineVolume(BaselineCostPerUnit: null, Quantity: ReadDecimal(scalar));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SqlDispensedVolumeProvider: volume lookup failed for NDC {Ndc}; quantity left null", ndc11);
            return PharmacyBaselineVolume.None;
        }
    }

    private static decimal? ReadDecimal(object? value) => value switch
    {
        null or DBNull => null,
        decimal d => d,
        double db => (decimal)db,
        float f => (decimal)f,
        long l => l,
        int i => i,
        _ => null,
    };
}
