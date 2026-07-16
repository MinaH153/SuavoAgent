using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public sealed class ObservationReadinessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullyFreshLosslessObservation_IsReady()
    {
        var result = ObservationReadinessEvaluator.Evaluate(ReadyInput());

        Assert.True(result.LiveObservationReady);
        Assert.Equal("ready", result.Code);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void HistoricalCompatibleBatch_IsNotLive()
    {
        var input = ReadyInput() with
        {
            Delivery = ReadyDelivery() with
            {
                LastBatchUtc = Now.AddMinutes(-10),
                LastPmsBatchUtc = Now.AddMinutes(-10),
                LastVerifiedBatchUtc = Now.AddMinutes(-10),
                LastVerifiedPmsBatchUtc = Now.AddMinutes(-10),
            },
        };

        var result = ObservationReadinessEvaluator.Evaluate(input);

        Assert.True(result.ProtocolCompatible);
        Assert.False(result.PmsChannelLive);
        Assert.False(result.LiveObservationReady);
        Assert.Contains("pms_channel_stale", result.Blockers);
    }

    [Theory]
    [InlineData("connector_unavailable")]
    [InlineData("not_connected")]
    public void BrowserConnectorMustBeFreshAndReady(string status)
    {
        var result = ObservationReadinessEvaluator.Evaluate(ReadyInput() with
        {
            BrowserDomain = Status(status),
        });

        Assert.False(result.LiveObservationReady);
        Assert.Equal("browser_connector_not_ready", result.Blockers.Single(
            blocker => blocker == "browser_connector_not_ready"));
    }

    [Fact]
    public void AnyLossCounterFailsClosed()
    {
        var result = ObservationReadinessEvaluator.Evaluate(ReadyInput() with
        {
            Delivery = ReadyDelivery() with { SequenceGapCount = 1 },
        });

        Assert.False(result.Lossless);
        Assert.False(result.LiveObservationReady);
        Assert.Contains("delivery_loss_detected", result.Blockers);
    }

    [Fact]
    public void LegacyOnlyDeliveryCannotClaimProtocolCompatibility()
    {
        var delivery = ReadyDelivery() with
        {
            VerifiedBatchCount = 0,
            LastVerifiedBatchUtc = null,
            LastVerifiedPmsBatchUtc = null,
            LastVerifiedSystemBatchUtc = null,
            LegacyUnverifiedSeen = true,
            LastLegacyBatchUtc = Now.AddSeconds(-1),
        };

        var result = ObservationReadinessEvaluator.Evaluate(ReadyInput() with
        {
            Delivery = delivery,
        });

        Assert.False(result.ProtocolCompatible);
        Assert.False(result.PmsChannelLive);
        Assert.False(result.SystemChannelLive);
        Assert.False(result.LiveObservationReady);
        Assert.Contains("delivery_protocol_unverified", result.Blockers);
    }

    [Fact]
    public void FutureOrStaleStatusDoesNotPassFreshness()
    {
        var stale = ObservationReadinessEvaluator.Evaluate(ReadyInput() with
        {
            Print = Status("ready", Now.AddSeconds(-91)),
        });
        var future = ObservationReadinessEvaluator.Evaluate(ReadyInput() with
        {
            Print = Status("ready", Now.AddMinutes(1)),
        });

        Assert.False(stale.PrintObserverReady);
        Assert.False(future.PrintObserverReady);
    }

    private static ObservationReadinessInput ReadyInput() => new(
        Now,
        HelperAttached: true,
        ReadyDelivery(),
        Status("attached"),
        Status("ready"),
        Status("ready"),
        Status("ready"),
        Status("ready"),
        Status("ready"));

    private static AgentStateDb.BehavioralDeliveryHealth ReadyDelivery() => new(
        StreamCount: 2,
        PmsStreamCount: 1,
        SystemStreamCount: 1,
        DroppedEventCount: 0,
        SequenceGapCount: 0,
        AcceptedBatchCount: 2,
        VerifiedBatchCount: 2,
        DuplicateBatchCount: 0,
        RejectedEventCount: 0,
        LastBatchUtc: Now.AddSeconds(-1),
        LastPmsBatchUtc: Now.AddSeconds(-1),
        LastSystemBatchUtc: Now.AddSeconds(-1),
        LastVerifiedBatchUtc: Now.AddSeconds(-1),
        LastVerifiedPmsBatchUtc: Now.AddSeconds(-1),
        LastVerifiedSystemBatchUtc: Now.AddSeconds(-1),
        LegacyUnverifiedSeen: false,
        LastLegacyBatchUtc: null,
        ObservationSpoolStatus: "observation_spool_healthy",
        LastObservationSpoolStatusUtc: Now.AddSeconds(-1));

    private static AgentStateDb.ObserverStatusSnapshot Status(
        string value,
        DateTimeOffset? at = null) => new(value, at ?? Now.AddSeconds(-1));
}
