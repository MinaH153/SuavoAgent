using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public sealed class BehavioralBatchIpcProcessorTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");
    private readonly BehavioralEventReceiver _receiver;

    public BehavioralBatchIpcProcessorTests()
    {
        _db.CreateLearningSession("session", "pharmacy");
        _receiver = new BehavioralEventReceiver(_db, "session");
    }

    [Fact]
    public void LegacyV1Envelope_ReturnsExactDurableAcknowledgement()
    {
        var batch = MakeBatch(BehavioralEventChannels.Pms, 1, 2);
        var request = Request(IpcCommands.BehavioralEvents, batch);

        var response = BehavioralBatchIpcProcessor.Process(
            request,
            BehavioralEventChannels.Pms,
            _receiver,
            new EventRateLimiter());

        Assert.Equal(IpcStatus.Ok, response.Status);
        var ack = JsonSerializer.Deserialize<BehavioralEventBatchAck>(response.Data!.Value.GetRawText());
        Assert.NotNull(ack);
        Assert.Equal(batch.BatchId, ack!.BatchId);
        Assert.Equal(batch.StreamId, ack.StreamId);
        Assert.Equal(2, ack.AcceptedThroughSequence);
        Assert.Equal(2, ack.EventsStored);
    }

    [Fact]
    public void LeasedV2Envelope_ReturnsExactDurableAcknowledgement()
    {
        var lease = _db.IssueObservationKeyLease(
            "session",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(15));
        var batch = ObservationBatchAuthentication.Seal(
            MakeBatch(BehavioralEventChannels.Pms, 1, 2),
            lease);

        var response = BehavioralBatchIpcProcessor.Process(
            Request(IpcCommands.BehavioralEvents, batch),
            BehavioralEventChannels.Pms,
            _receiver,
            new EventRateLimiter());

        Assert.Equal(IpcStatus.Ok, response.Status);
        var ack = JsonSerializer.Deserialize<BehavioralEventBatchAck>(response.Data!.Value.GetRawText());
        Assert.NotNull(ack);
        Assert.Equal(BehavioralEventBatch.CurrentContractVersion, ack!.ContractVersion);
        Assert.Equal(batch.BatchId, ack.BatchId);
        Assert.Equal(batch.StreamId, ack.StreamId);
        Assert.Equal(batch.LastSequence, ack.AcceptedThroughSequence);
    }

    [Fact]
    public void CommandChannelMismatch_IsRejectedWithoutPersistence()
    {
        var request = Request(
            IpcCommands.SystemEvents,
            MakeBatch(BehavioralEventChannels.Pms, 1, 1));

        var response = BehavioralBatchIpcProcessor.Process(
            request,
            BehavioralEventChannels.System,
            _receiver,
            new EventRateLimiter());

        Assert.Equal(IpcStatus.BadRequest, response.Status);
        Assert.Equal("channel_command_mismatch", response.Error!.Code);
        Assert.Empty(_db.GetBehavioralEvents("session"));
    }

    [Fact]
    public void OversizedEnvelope_IsRejected_NotSilentlyTruncated()
    {
        var batch = MakeBatch(
            BehavioralEventChannels.Pms,
            1,
            BehavioralEventBatch.MaximumEventCount + 1);
        var request = Request(IpcCommands.BehavioralEvents, batch);

        var response = BehavioralBatchIpcProcessor.Process(
            request,
            BehavioralEventChannels.Pms,
            _receiver,
            new EventRateLimiter(1000));

        Assert.Equal(IpcStatus.BadRequest, response.Status);
        Assert.Equal("batch_too_large", response.Error!.Code);
        Assert.Empty(_db.GetBehavioralEvents("session"));
    }

    [Fact]
    public void RateLimit_ChargesEachEventAndSignalsRetry()
    {
        var batch = MakeBatch(BehavioralEventChannels.Pms, 1, 3);
        var request = Request(IpcCommands.BehavioralEvents, batch);
        var limiter = new EventRateLimiter(maxEventsPerSecond: 2);

        var response = BehavioralBatchIpcProcessor.Process(
            request,
            BehavioralEventChannels.Pms,
            _receiver,
            limiter);

        Assert.Equal(IpcStatus.BadRequest, response.Status);
        Assert.Equal("rate_limited", response.Error!.Code);
        Assert.True(response.Error.Retryable);
        Assert.Equal(3, limiter.DroppedTotal);
    }

    [Fact]
    public void LegacyArray_IsAcceptedButDurablyMarksUnverifiedDelivery()
    {
        var legacy = new[] { BehavioralEvent.TreeSnapshot("legacy-tree") };
        var request = new IpcRequest(
            Guid.NewGuid().ToString("N"),
            IpcCommands.BehavioralEvents,
            1,
            JsonSerializer.SerializeToElement(legacy));

        var response = BehavioralBatchIpcProcessor.Process(
            request,
            BehavioralEventChannels.Pms,
            _receiver,
            new EventRateLimiter());

        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.True(_db.GetBehavioralDeliveryHealth().LegacyUnverifiedSeen);
    }

    [Fact]
    public void SystemChannel_IsPersistedForTelemetry_ButCannotFeedPmsCorrelation()
    {
        var correlationHits = 0;
        var receiver = new BehavioralEventReceiver(
            _db,
            "session",
            (_, _, _, _) => correlationHits++);
        var batch = MakeBatch(BehavioralEventChannels.System, 1, 1);

        var response = BehavioralBatchIpcProcessor.Process(
            Request(IpcCommands.SystemEvents, batch),
            BehavioralEventChannels.System,
            receiver,
            new EventRateLimiter());

        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.Equal(0, correlationHits);
        Assert.Single(_db.GetBehavioralEvents("session", sourceChannel: BehavioralEventChannels.System));
        Assert.Empty(_db.GetBehavioralEvents("session", sourceChannel: BehavioralEventChannels.Pms));
    }

    public void Dispose() => _db.Dispose();

    private static IpcRequest Request(string command, BehavioralEventBatch batch) =>
        new(
            Guid.NewGuid().ToString("N"),
            command,
            1,
            JsonSerializer.SerializeToElement(batch));

    private static BehavioralEventBatch MakeBatch(string channel, long firstSequence, int count)
    {
        var events = Enumerable.Range(0, count)
            .Select(index => BehavioralEvent.Interaction(
                    "click",
                    "tree",
                    $"element-{index}",
                    "Button",
                    null,
                    null)
                .WithSeq(firstSequence + index))
            .ToArray();
        return new BehavioralEventBatch
        {
            ContractVersion = BehavioralEventBatch.LegacyContractVersion,
            BatchId = Guid.NewGuid().ToString("N"),
            StreamId = Guid.NewGuid().ToString("N"),
            Channel = channel,
            FirstSequence = events[0].Seq,
            LastSequence = events[^1].Seq,
            DroppedTotal = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Events = events,
        };
    }
}
