using System.Security.Cryptography;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public sealed class ObservationLeaseDeliveryTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");

    public ObservationLeaseDeliveryTests()
    {
        _db.CreateLearningSession("session-a", "pharmacy");
        _db.CreateLearningSession("session-b", "pharmacy");
    }

    [Fact]
    public void DelayedBatchAfterSessionRollover_PersistsOnlyToOriginalLeasedSession()
    {
        var activeSession = "session-a";
        var correlationHits = 0;
        var receiver = new BehavioralEventReceiver(
            _db,
            () => activeSession,
            (_, _, _, _) => correlationHits++);
        var lease = _db.IssueObservationKeyLease(
            "session-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(15));
        var delayed = SealBatch(
            lease,
            BehavioralEvent.Interaction("click", "tree", "button-a", "Button", null, null));

        activeSession = "session-b";
        var result = receiver.ProcessBatch(delayed);

        Assert.True(result.Accepted);
        Assert.Single(_db.GetBehavioralEvents("session-a"));
        Assert.Empty(_db.GetBehavioralEvents("session-b"));
        Assert.Equal(0, correlationHits);
        var health = _db.GetBehavioralDeliveryHealth("session-a");
        Assert.Equal(1, health.VerifiedBatchCount);
        Assert.NotNull(health.LastVerifiedBatchUtc);
        Assert.NotNull(health.LastVerifiedPmsBatchUtc);
    }

    [Fact]
    public void UnknownLease_IsRejectedWithoutReceiveTimeAttribution()
    {
        var receiver = new BehavioralEventReceiver(_db, "session-b");
        var unknownLease = new ObservationKeyLease
        {
            LeaseId = "opaque-unknown-lease",
            SessionBinding = "opaque-unknown-session",
            Epoch = 99,
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            KeyMaterial = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        };

        var result = receiver.ProcessBatch(SealBatch(
            unknownLease,
            BehavioralEvent.TreeSnapshot("unknown-tree")));

        Assert.False(result.Accepted);
        Assert.Equal("observation_lease_unknown", result.ErrorCode);
        Assert.Empty(_db.GetBehavioralEvents("session-a"));
        Assert.Empty(_db.GetBehavioralEvents("session-b"));
    }

    [Fact]
    public void ExpiredLease_IsRejectedAndNeverRebound()
    {
        var receiver = new BehavioralEventReceiver(_db, "session-b");
        var lease = _db.IssueObservationKeyLease(
            "session-a",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            TimeSpan.FromMinutes(1));

        var result = receiver.ProcessBatch(SealBatch(
            lease,
            BehavioralEvent.TreeSnapshot("expired-tree")));

        Assert.False(result.Accepted);
        Assert.Equal("observation_lease_expired", result.ErrorCode);
        Assert.Empty(_db.GetBehavioralEvents("session-a"));
        Assert.Empty(_db.GetBehavioralEvents("session-b"));
    }

    [Fact]
    public void LeaseRotation_IsMonotonicOpaqueAndBothEpochsRetainTheirSessionMapping()
    {
        var now = DateTimeOffset.UtcNow;
        var first = _db.IssueObservationKeyLease("session-a", now, TimeSpan.FromMinutes(15));
        var second = _db.IssueObservationKeyLease("session-b", now.AddSeconds(1), TimeSpan.FromMinutes(15));
        var receiver = new BehavioralEventReceiver(_db, "session-b");

        var firstResult = receiver.ProcessBatch(SealBatch(
            first,
            BehavioralEvent.TreeSnapshot("first-epoch"),
            streamId: Guid.NewGuid().ToString("N")));
        var secondResult = receiver.ProcessBatch(SealBatch(
            second,
            BehavioralEvent.TreeSnapshot("second-epoch"),
            streamId: Guid.NewGuid().ToString("N")));

        Assert.True(firstResult.Accepted);
        Assert.True(secondResult.Accepted);
        Assert.True(second.Epoch > first.Epoch);
        Assert.DoesNotContain("session-a", first.SessionBinding, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session-b", second.SessionBinding, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_db.GetBehavioralEvents("session-a"));
        Assert.Single(_db.GetBehavioralEvents("session-b"));
    }

    [Fact]
    public void BatchMutationAfterLeaseSeal_FailsAuthentication()
    {
        var lease = _db.IssueObservationKeyLease(
            "session-a",
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(15));
        var sealedBatch = SealBatch(lease, BehavioralEvent.TreeSnapshot("original"));
        var tampered = sealedBatch with
        {
            Events = [BehavioralEvent.TreeSnapshot("tampered").WithSeq(1)],
        };

        var result = new BehavioralEventReceiver(_db, "session-a").ProcessBatch(tampered);

        Assert.False(result.Accepted);
        Assert.Equal("observation_batch_authentication_failed", result.ErrorCode);
        Assert.Empty(_db.GetBehavioralEvents("session-a"));
    }

    [Fact]
    public void PeriodicRefresh_ReusesHealthyEpoch_AndRotatesOnSessionOrExpiryLead()
    {
        var now = DateTimeOffset.UtcNow;
        var first = _db.GetOrIssueObservationKeyLease(
            "session-a",
            currentLeaseId: null,
            now,
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(3));

        var unchanged = _db.GetOrIssueObservationKeyLease(
            "session-a",
            first.LeaseId,
            now.AddMinutes(1),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(3));
        var sessionChanged = _db.GetOrIssueObservationKeyLease(
            "session-b",
            first.LeaseId,
            now.AddMinutes(2),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(3));
        var expiryLeadReached = _db.GetOrIssueObservationKeyLease(
            "session-a",
            first.LeaseId,
            now.AddMinutes(13),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(3));

        Assert.Equal(first.LeaseId, unchanged.LeaseId);
        Assert.Equal(first.Epoch, unchanged.Epoch);
        Assert.NotEqual(first.LeaseId, sessionChanged.LeaseId);
        Assert.NotEqual(first.SessionBinding, sessionChanged.SessionBinding);
        Assert.NotEqual(first.LeaseId, expiryLeadReached.LeaseId);
        Assert.True(expiryLeadReached.Epoch > first.Epoch);
    }

    [Fact]
    public void ObservationSpoolStatus_IsDurableAndRejectsFreeFormPayloads()
    {
        _db.RecordObservationSpoolStatus("observation_spool_healthy");

        var health = _db.GetBehavioralDeliveryHealth();

        Assert.Equal("observation_spool_healthy", health.ObservationSpoolStatus);
        Assert.NotNull(health.LastObservationSpoolStatusUtc);
        Assert.Throws<ArgumentException>(() =>
            _db.RecordObservationSpoolStatus("patient name must never be status"));
    }

    [Fact]
    public void CoreRestart_PreservesLeaseToOriginalSessionMapping()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "suavo-observation-lease-tests",
            Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            ObservationKeyLease lease;
            using (var firstCore = new AgentStateDb(path))
            {
                firstCore.CreateLearningSession("original-session", "pharmacy");
                lease = firstCore.IssueObservationKeyLease(
                    "original-session",
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromMinutes(15));
            }

            using var restartedCore = new AgentStateDb(path);
            restartedCore.CreateLearningSession("receive-time-session", "pharmacy");
            var receiver = new BehavioralEventReceiver(restartedCore, "receive-time-session");

            var result = receiver.ProcessBatch(SealBatch(
                lease,
                BehavioralEvent.TreeSnapshot("restart-delayed")));

            Assert.True(result.Accepted);
            Assert.Single(restartedCore.GetBehavioralEvents("original-session"));
            Assert.Empty(restartedCore.GetBehavioralEvents("receive-time-session"));
        }
        finally
        {
            foreach (var candidate in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                try { File.Delete(candidate); }
                catch { }
            }
        }
    }

    public void Dispose() => _db.Dispose();

    private static BehavioralEventBatch SealBatch(
        ObservationKeyLease lease,
        BehavioralEvent behavioralEvent,
        string? streamId = null)
    {
        var sequenced = behavioralEvent.WithSeq(1);
        var batch = new BehavioralEventBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            StreamId = streamId ?? Guid.NewGuid().ToString("N"),
            Channel = BehavioralEventChannels.Pms,
            FirstSequence = 1,
            LastSequence = 1,
            DroppedTotal = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Events = [sequenced],
        };
        return ObservationBatchAuthentication.Seal(batch, lease);
    }
}
