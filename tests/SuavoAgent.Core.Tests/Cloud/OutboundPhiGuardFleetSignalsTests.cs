using System;
using System.Text.Json;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Intelligence;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// Repro for the 2026-05-31 field bug: enabling FleetLearning made HeartbeatWorker
/// attach a serialized `fleetSignals` JSON-string to the heartbeat, and OutboundPhiGuard
/// blocked the WHOLE POST ("field: fleetsignals"), silently dropping the heartbeat.
/// fleetSignals is structural telemetry — counts, an enum, ISO timestamps, a pharmacy
/// UUID — never PHI. The guard must let it through.
/// </summary>
public sealed class OutboundPhiGuardFleetSignalsTests
{
    private static readonly AgentOptions Options = new();

    // Mirror HeartbeatWorker: a ComputeSignals-shaped object, serialized, then shipped
    // as a STRING field on the heartbeat (exactly how the agent sends it).
    private static string BuildHeartbeatWithFleetSignals()
    {
        var now = DateTimeOffset.UtcNow;
        var signals = new FleetSignals
        {
            PharmacyId = "d0000000-0000-4000-b000-000000000001",
            ComputedAt = now,
            OrderVolume = new OrderVolumeSignal
            {
                CurrentHourKey = now.ToString("yyyy-MM-ddTHH"),
                DayOfWeek = now.DayOfWeek.ToString(),
            },
            PickupReadiness = new PickupReadinessSignal
            {
                AvgFillTimeMinutes = 47.3,
                EstimatedReadyIn = TimeSpan.FromMinutes(47),
                ConfidencePct = 80,
                SampleSize = 12,
            },
            BusinessHours = new BusinessHoursSignal { IsCurrentlyActive = true, ActiveToday = true },
            Capacity = new CapacitySignal
            {
                CurrentLoadLevel = LoadLevel.Normal,
                ActionVelocityVsBaseline = 1.0,
                ErrorRateVsBaseline = 1.0,
            },
        };
        var signalsJson = JsonSerializer.Serialize(signals);
        return JsonSerializer.Serialize(new { agentId = "agent-x", fleetSignals = signalsJson });
    }

    [Fact]
    public void FleetSignals_telemetry_is_not_blocked_on_the_heartbeat()
    {
        var body = BuildHeartbeatWithFleetSignals();

        // Must NOT throw: every leaf is structural (counts, enum, ISO timestamps).
        OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options);
    }

    // Isolate the two suspect leaves as plain top-level string fields, the way they
    // appear once the guard parses the nested fleetSignals blob.
    [Theory]
    [InlineData("computedAt", "2026-05-31T06:41:45.1234567+00:00")] // DateTimeOffset.UtcNow round-trip "+00:00"
    [InlineData("currentHourKey", "2026-05-31T06")]                 // now.ToString("yyyy-MM-ddTHH")
    public void Structural_timestamp_fields_are_not_blocked(string field, string value)
    {
        var body = JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string> { [field] = value });

        OutboundPhiGuard.AssertAllowed("/api/agent/heartbeat", body, Options);
    }
}
