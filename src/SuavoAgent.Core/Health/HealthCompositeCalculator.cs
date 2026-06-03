using System;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Pure-function composite calculator. Each probe wrapped in try/catch:
/// failed signal defaults to <c>false</c> (conservative). Off-hours
/// fallback for <c>extractionRecent</c>: if the business-hours probe
/// throws, treat as outside hours (extractionRecent → true).
///
/// See spec §4 for full error-handling semantics.
/// </summary>
public sealed class HealthCompositeCalculator
{
    private readonly IBusinessHoursProvider _hoursProvider;
    private readonly int _extractionWindowMinutes;

    public HealthCompositeCalculator(
        IBusinessHoursProvider hoursProvider,
        int extractionWindowMinutes = 30)
    {
        _hoursProvider = hoursProvider;
        _extractionWindowMinutes = extractionWindowMinutes;
    }

    public HealthCompositePayload Compute(HealthSignalsSnapshot snapshot, DateTimeOffset now)
    {
        var helperAttached    = snapshot.HelperAttached;
        var ipcConnected      = snapshot.IpcConnected;
        var schemaCanaryGreen = snapshot.SchemaCanaryGreen;
        var extractionRecent  = ComputeExtractionRecent(snapshot.LastExtractionAt, now);

        var components = new HealthCompositeComponents(
            HelperAttached:    helperAttached,
            IpcConnected:      ipcConnected,
            SchemaCanaryGreen: schemaCanaryGreen,
            ExtractionRecent:  extractionRecent);

        var allHealthy = helperAttached && ipcConnected && schemaCanaryGreen && extractionRecent;
        var status = allHealthy ? "healthy" : "heartbeating-but-unhealthy";

        return new HealthCompositePayload(status, components, now);
    }

    private bool ComputeExtractionRecent(DateTimeOffset? lastExtractionAt, DateTimeOffset now)
    {
        bool isOutsideBusinessHours;
        try
        {
            isOutsideBusinessHours = !_hoursProvider.IsInsideBusinessHours(now);
        }
        catch
        {
            isOutsideBusinessHours = true;
        }

        if (isOutsideBusinessHours)
            return true;

        if (lastExtractionAt is null)
            return false;

        return (now - lastExtractionAt.Value).TotalMinutes < _extractionWindowMinutes;
    }
}
