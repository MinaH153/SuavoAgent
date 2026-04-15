using System.Text.Json.Serialization;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Intelligence;

public sealed class FleetDataChannels
{
    private readonly AgentStateDb _db;
    private readonly ReadinessPredictor _predictor;

    public FleetDataChannels(AgentStateDb db)
    {
        _db = db;
        _predictor = new ReadinessPredictor(db);
    }

    public FleetSignals ComputeSignals(string pharmacyId)
    {
        var now = DateTimeOffset.UtcNow;
        return new FleetSignals
        {
            PharmacyId = pharmacyId,
            ComputedAt = now,
            OrderVolume = new OrderVolumeSignal { CurrentHourKey = now.ToString("yyyy-MM-ddTHH"), DayOfWeek = now.DayOfWeek.ToString() },
            PickupReadiness = ComputePickupReadiness(),
            BusinessHours = new BusinessHoursSignal { IsCurrentlyActive = true, ActiveToday = true },
            Capacity = new CapacitySignal { CurrentLoadLevel = LoadLevel.Normal, ActionVelocityVsBaseline = 1.0, ErrorRateVsBaseline = 1.0 }
        };
    }

    private PickupReadinessSignal ComputePickupReadiness()
    {
        var prediction = _predictor.PredictReadiness();
        if (prediction == null)
        {
            return new PickupReadinessSignal(); // no data yet
        }

        return new PickupReadinessSignal
        {
            AvgFillTimeMinutes = prediction.PredictedMinutes,
            EstimatedReadyIn = TimeSpan.FromMinutes(prediction.PredictedMinutes),
            ConfidencePct = prediction.ConfidencePct,
            SampleSize = prediction.SampleCount
        };
    }
}

public sealed class FleetSignals
{
    [JsonPropertyName("pharmacyId")] public string PharmacyId { get; set; } = "";
    [JsonPropertyName("computedAt")] public DateTimeOffset ComputedAt { get; set; }
    [JsonPropertyName("orderVolume")] public OrderVolumeSignal OrderVolume { get; set; } = new();
    [JsonPropertyName("pickupReadiness")] public PickupReadinessSignal PickupReadiness { get; set; } = new();
    [JsonPropertyName("businessHours")] public BusinessHoursSignal BusinessHours { get; set; } = new();
    [JsonPropertyName("capacity")] public CapacitySignal Capacity { get; set; } = new();
}

public sealed class OrderVolumeSignal
{
    [JsonPropertyName("currentHourKey")] public string CurrentHourKey { get; set; } = "";
    [JsonPropertyName("dayOfWeek")] public string DayOfWeek { get; set; } = "";
    [JsonPropertyName("predictedVolume")] public int PredictedVolume { get; set; }
    [JsonPropertyName("confidencePct")] public int ConfidencePct { get; set; }
}

public sealed class PickupReadinessSignal
{
    [JsonPropertyName("avgFillTimeMinutes")] public double AvgFillTimeMinutes { get; set; }
    [JsonPropertyName("estimatedReadyIn")] public TimeSpan? EstimatedReadyIn { get; set; }
    [JsonPropertyName("confidencePct")] public int ConfidencePct { get; set; }
    [JsonPropertyName("sampleSize")] public int SampleSize { get; set; }
}

public sealed class BusinessHoursSignal
{
    [JsonPropertyName("observedFirstActivity")] public DateTimeOffset? ObservedFirstActivity { get; set; }
    [JsonPropertyName("observedLastActivity")] public DateTimeOffset? ObservedLastActivity { get; set; }
    [JsonPropertyName("isCurrentlyActive")] public bool IsCurrentlyActive { get; set; }
    [JsonPropertyName("activeToday")] public bool ActiveToday { get; set; }
}

public sealed class CapacitySignal
{
    [JsonPropertyName("currentLoadLevel")] public LoadLevel CurrentLoadLevel { get; set; }
    [JsonPropertyName("actionVelocityVsBaseline")] public double ActionVelocityVsBaseline { get; set; }
    [JsonPropertyName("errorRateVsBaseline")] public double ErrorRateVsBaseline { get; set; }
}

public enum LoadLevel { Low, Normal, High, Overwhelmed }
