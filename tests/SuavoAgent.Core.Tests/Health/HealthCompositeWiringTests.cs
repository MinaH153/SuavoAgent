using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

/// <summary>
/// Wave 1B Track 1.4 — the composite calculator, signals provider, and
/// HeartbeatWorker emission path were all built + unit-tested, but the
/// services were never registered in Program.cs, so in production
/// HeartbeatWorker.EmitHealthComposite() short-circuited to null (see
/// HeartbeatWorkerCompositeTests.EmitHealthComposite_HealthSignalsNotWired_*).
/// The agent therefore emitted ZERO agent.health_composite events and the
/// cloud agent-health-watch alarm was blind to a silently-degraded box.
///
/// These tests pin (a) the interim default business-hours provider and
/// (b) that AddHealthComposite() registers the three services the worker
/// resolves, so the wiring can't silently regress to the blind state.
/// </summary>
public class HealthCompositeWiringTests
{
    [Theory]
    [InlineData("2026-06-01T03:00:00Z")]
    [InlineData("2026-06-01T12:00:00Z")]
    [InlineData("2026-06-01T20:30:00Z")]
    public void DefaultBusinessHoursProvider_AlwaysReportsOutsideHours(string instant)
    {
        // Interim default: reports every time as OUTSIDE business hours, so the
        // calculator's extraction_recent signal stays permissive (always true)
        // and wiring composite emission can't introduce extraction false-alarms
        // on boxes without per-pharmacy hours configured (e.g. the test box that
        // never extracts). Helper/IPC/schema-canary signals are unaffected — they
        // don't depend on business hours — so the helper-blind case is still
        // surfaced. Replace with a per-pharmacy schedule to enable extraction
        // staleness alarming.
        var provider = new DefaultBusinessHoursProvider();
        Assert.False(provider.IsInsideBusinessHours(DateTimeOffset.Parse(instant)));
    }

    [Fact]
    public void AddHealthComposite_RegistersTheServicesHeartbeatWorkerResolves()
    {
        // HeartbeatWorker (HeartbeatWorker.cs:92-93) resolves IHealthSignals +
        // HealthCompositeCalculator via GetService<>(). If either is unregistered,
        // EmitHealthComposite() returns null and the box emits no composite. This
        // pins that AddHealthComposite() registers both (+ the IBusinessHoursProvider
        // the calculator depends on).
        var services = new ServiceCollection();

        services.AddHealthComposite();

        Assert.Contains(services, d => d.ServiceType == typeof(IBusinessHoursProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(HealthCompositeCalculator));
        Assert.Contains(services, d => d.ServiceType == typeof(IHealthSignals));
    }
}
