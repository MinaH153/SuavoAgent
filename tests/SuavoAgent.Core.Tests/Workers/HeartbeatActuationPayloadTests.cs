using System.Text.Json;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Pins the WIRE CONTRACT of heartbeat <c>helper.actuation</c> — the cloud's
/// parseHeartbeatBody and the health-composite recompute key off these exact camelCase names.
/// A rename here silently blinds the cockpit's "agent can't act" surface, so this test is the
/// tripwire. Also asserts the payload stays PHI-free-by-shape (fixed primitive fields only).
/// </summary>
public class HeartbeatActuationPayloadTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NullSnapshot_EmitsNull_SoOldCloudTreatsAgentAsPreProbe()
    {
        Assert.Null(HeartbeatWorker.BuildActuationPayload(null, null));
    }

    [Fact]
    public void ReadySnapshot_EmitsExactContractFields()
    {
        var snapshot = new ActuationReadinessSnapshot(
            CommandPipeResponsive: true,
            IsConsoleInteractive: true,
            HelperSessionId: 2,
            ActiveConsoleSessionId: 2,
            HelperPid: 4321,
            FailureCode: null,
            FailureReason: null,
            LastConclusiveCheckAtUtc: T0,
            LastProbeAttemptAtUtc: T0,
            SkippedReason: null,
            ConsecutiveStrandFailures: 0);
        var selfHeal = new SelfHealState(null, 0, false);

        var json = JsonSerializer.SerializeToElement(HeartbeatWorker.BuildActuationPayload(snapshot, selfHeal));

        Assert.True(json.GetProperty("ready").GetBoolean());
        Assert.True(json.GetProperty("commandPipeResponsive").GetBoolean());
        Assert.True(json.GetProperty("isConsoleInteractive").GetBoolean());
        Assert.Equal(2u, json.GetProperty("helperSessionId").GetUInt32());
        Assert.Equal(2u, json.GetProperty("activeConsoleSessionId").GetUInt32());
        Assert.Equal(4321, json.GetProperty("helperPid").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("failureCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("failureReason").ValueKind);
        Assert.Equal(T0, DateTimeOffset.Parse(json.GetProperty("lastConclusiveCheckAt").GetString()!));
        Assert.Equal(T0, DateTimeOffset.Parse(json.GetProperty("lastProbeAttemptAt").GetString()!));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lastCheckSkippedReason").ValueKind);
        Assert.Equal(0, json.GetProperty("consecutiveStrandFailures").GetInt32());

        var sh = json.GetProperty("selfHeal");
        Assert.Equal(JsonValueKind.Null, sh.GetProperty("lastAttemptAt").ValueKind);
        Assert.Equal(0, sh.GetProperty("attemptsInWindow").GetInt32());
        Assert.False(sh.GetProperty("exhausted").GetBoolean());
    }

    [Fact]
    public void StrandedSnapshot_SurfacesFailureCodeAndReason_ForTheCockpit()
    {
        var snapshot = new ActuationReadinessSnapshot(
            CommandPipeResponsive: false,
            IsConsoleInteractive: false,
            HelperSessionId: null,
            ActiveConsoleSessionId: null,
            HelperPid: null,
            FailureCode: "ping_unanswered",
            FailureReason: "Helper did not answer ping — command pipe is stranded; use restart_helper to relaunch it",
            LastConclusiveCheckAtUtc: T0,
            LastProbeAttemptAtUtc: T0,
            SkippedReason: null,
            ConsecutiveStrandFailures: 3);
        var selfHeal = new SelfHealState(T0, 1, false);

        var json = JsonSerializer.SerializeToElement(HeartbeatWorker.BuildActuationPayload(snapshot, selfHeal));

        Assert.False(json.GetProperty("ready").GetBoolean());
        Assert.Equal("ping_unanswered", json.GetProperty("failureCode").GetString());
        Assert.Contains("restart_helper", json.GetProperty("failureReason").GetString());
        Assert.Equal(3, json.GetProperty("consecutiveStrandFailures").GetInt32());
        Assert.Equal(1, json.GetProperty("selfHeal").GetProperty("attemptsInWindow").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("helperSessionId").ValueKind);
    }

    [Fact]
    public void BusySkip_KeepsVerdict_ButStampsSkipReason_SoCloudSeesStaleness()
    {
        var snapshot = new ActuationReadinessSnapshot(
            CommandPipeResponsive: true,
            IsConsoleInteractive: true,
            HelperSessionId: 1,
            ActiveConsoleSessionId: 1,
            HelperPid: 99,
            FailureCode: null,
            FailureReason: null,
            LastConclusiveCheckAtUtc: T0,
            LastProbeAttemptAtUtc: T0.AddMinutes(5),
            SkippedReason: "pipe_busy",
            ConsecutiveStrandFailures: 0);

        var json = JsonSerializer.SerializeToElement(HeartbeatWorker.BuildActuationPayload(snapshot, null));

        Assert.True(json.GetProperty("ready").GetBoolean());
        Assert.Equal("pipe_busy", json.GetProperty("lastCheckSkippedReason").GetString());
        // The conclusive timestamp is OLDER than the attempt — the cloud reads this gap as
        // "verdict is aging because a job holds the pipe", not as fresh health.
        var conclusive = DateTimeOffset.Parse(json.GetProperty("lastConclusiveCheckAt").GetString()!);
        var attempt = DateTimeOffset.Parse(json.GetProperty("lastProbeAttemptAt").GetString()!);
        Assert.True(conclusive < attempt);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("selfHeal").ValueKind);
    }
}
