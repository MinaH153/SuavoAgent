using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Intelligence;

/// <summary>
/// Predicts prescription pickup readiness timing using historical observation data.
/// Uses simple statistical model (mean + stddev by day/hour) as foundation.
/// Will evolve to ML model when sufficient data accumulates (100+ samples per time bucket).
/// </summary>
public sealed class ReadinessPredictor
{
    private readonly AgentStateDb _db;
    private const int MinSamplesForPrediction = 10;
    private const int MinSamplesForHighConfidence = 50;

    public ReadinessPredictor(AgentStateDb db) => _db = db;

    /// <summary>
    /// Predicts how many minutes until a prescription entered NOW will be ready for pickup.
    /// Returns null if insufficient data for the current time bucket.
    /// </summary>
    public ReadinessPrediction? PredictReadiness(bool isControlled = false, int? queueDepth = null)
    {
        var now = DateTimeOffset.UtcNow;
        var stats = _db.GetReadinessStats((int)now.DayOfWeek, now.Hour);

        if (stats.SampleCount < MinSamplesForPrediction)
            return null; // insufficient data

        var predictedMinutes = stats.AvgMinutes;

        // Adjust for queue depth (linear scaling heuristic)
        if (queueDepth.HasValue && queueDepth.Value > 5)
        {
            var queueFactor = 1.0 + (queueDepth.Value - 5) * 0.05; // +5% per Rx over 5
            predictedMinutes *= queueFactor;
        }

        // Controlled substances take longer (verification step)
        if (isControlled)
            predictedMinutes *= 1.25; // 25% longer on average

        var confidence = stats.SampleCount >= MinSamplesForHighConfidence ? 85 :
                         stats.SampleCount >= 30 ? 70 : 50;

        return new ReadinessPrediction
        {
            PredictedMinutes = Math.Round(predictedMinutes, 1),
            ConfidencePct = confidence,
            SampleCount = stats.SampleCount,
            StdDevMinutes = Math.Round(stats.StdDevMinutes, 1),
            DayOfWeek = now.DayOfWeek.ToString(),
            HourOfDay = now.Hour,
            DispatchAtMinute = Math.Max(0, Math.Round(predictedMinutes - stats.StdDevMinutes - 5, 0))
        };
    }

    /// <summary>
    /// Returns the optimal minute to dispatch a driver for a prescription entered NOW.
    /// "Dispatch at minute 18 of a 23-minute fill" = driver arrives as bag hits shelf.
    /// </summary>
    public double? GetOptimalDispatchMinute(bool isControlled = false, int? queueDepth = null)
    {
        var prediction = PredictReadiness(isControlled, queueDepth);
        return prediction?.DispatchAtMinute;
    }
}

public sealed class ReadinessPrediction
{
    public double PredictedMinutes { get; init; }
    public int ConfidencePct { get; init; }
    public int SampleCount { get; init; }
    public double StdDevMinutes { get; init; }
    public string DayOfWeek { get; init; } = "";
    public int HourOfDay { get; init; }
    /// <summary>
    /// The minute mark at which to dispatch the driver.
    /// Calculated as: predicted - stddev - 5min buffer.
    /// Driver arrives ~5 minutes before predicted readiness.
    /// </summary>
    public double DispatchAtMinute { get; init; }
}
