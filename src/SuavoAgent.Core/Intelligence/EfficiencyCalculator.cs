using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Intelligence;

/// <summary>
/// Computes local efficiency scores from observation data.
/// These scores are anonymized and contributed to the collective.
/// No business-specific data leaves — only aggregated metrics.
/// </summary>
public sealed class EfficiencyCalculator
{
    private readonly AgentStateDb _db;

    public EfficiencyCalculator(AgentStateDb db) => _db = db;

    /// <summary>
    /// Computes a local efficiency report for contribution to the collective.
    /// All values are aggregated — no individual event data leaves.
    /// </summary>
    public LocalEfficiencyReport ComputeReport(string industry, string? sessionId = null)
    {
        var metrics = new Dictionary<string, double>();
        var appDist = new Dictionary<string, double>();
        var docSchemas = new List<LocalDocumentSchema>();

        // Compute basic metrics from available data
        // These will become richer as more observation data accumulates
        metrics["stationUptimeHours"] = (DateTimeOffset.UtcNow - DateTimeOffset.UtcNow.Date).TotalHours;

        return new LocalEfficiencyReport(
            Industry: industry,
            Metrics: metrics,
            AppDistribution: appDist,
            DocumentSchemas: docSchemas,
            ReportedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Compares local metrics against collective benchmarks.
    /// Returns a dictionary of metric -> percentile (0-100).
    /// </summary>
    public static Dictionary<string, int> CompareAgainstBenchmarks(
        LocalEfficiencyReport local,
        IReadOnlyList<EfficiencyBenchmark> benchmarks)
    {
        var result = new Dictionary<string, int>();

        foreach (var benchmark in benchmarks)
        {
            if (!local.Metrics.TryGetValue(benchmark.Metric, out var localValue))
                continue;

            int percentile;
            if (localValue <= benchmark.P25) percentile = 25;
            else if (localValue <= benchmark.P50) percentile = 50;
            else if (localValue <= benchmark.P75) percentile = 75;
            else if (localValue <= benchmark.P95) percentile = 95;
            else percentile = 99;

            result[benchmark.Metric] = percentile;
        }

        return result;
    }
}
