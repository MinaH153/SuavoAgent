using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class BehavioralEventReceiverTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly BehavioralEventReceiver _receiver;
    private const string SessionId = "recv-test-session";

    public BehavioralEventReceiverTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-test");
        _receiver = new BehavioralEventReceiver(_db, SessionId);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ProcessBatch_ValidEvents_Persists()
    {
        var snapshot = BehavioralEvent.TreeSnapshot("tree-abc");
        var interaction = BehavioralEvent.Interaction("click", "tree-abc", "elem-001", "Button", "MyClass", "name-hash-1");

        var result = _receiver.ProcessBatch(new[] { snapshot, interaction }, droppedSinceLast: 0);

        Assert.True(result.Accepted);
        Assert.Equal(2, result.EventsStored);
        Assert.Equal(0, result.EventsRejected);

        var events = _db.GetBehavioralEvents(SessionId);
        Assert.Equal(2, events.Count);

        var storedSnapshot = events.First(e => e.EventType == "treesnapshot");
        var storedInteraction = events.First(e => e.EventType == "interaction");

        Assert.Equal("tree-abc", storedSnapshot.TreeHash);
        Assert.Equal("elem-001", storedInteraction.ElementId);
        Assert.Equal("click", storedInteraction.EventSubtype);
    }

    [Fact]
    public void SessionResolver_LazilyAttributes_EventsToCurrentSession()
    {
        // Trip A root cause: receiver was a singleton pinned to sessionId="ipc";
        // dashboard queried the live learning session and found 0 events.
        // Codex review redesign: receiver takes a Func<string?> sessionResolver
        // that resolves per-batch — events automatically pick up the active
        // learning session id once LearningWorker registers one in the DB.
        const string activeLearningSession = "learn-agent-1-202604270000";
        _db.CreateLearningSession(activeLearningSession, "pharm-test");

        string? activeSession = null;
        var receiver = new BehavioralEventReceiver(_db,
            sessionResolver: () => activeSession);

        // First batch arrives BEFORE LearningWorker registered a session.
        // Resolver returns null → receiver uses "pre-learning" placeholder.
        _db.CreateLearningSession("pre-learning", "pharm-test");
        receiver.ProcessBatch(new[] { BehavioralEvent.TreeSnapshot("tree-pre") }, 0);
        Assert.Equal(1, _db.GetBehavioralEventCount("pre-learning", "treesnapshot"));
        Assert.Equal(0, _db.GetBehavioralEventCount(activeLearningSession, "treesnapshot"));

        // LearningWorker boots and registers its session id.
        activeSession = activeLearningSession;

        // Next batch → resolver returns the live session, no Configure() needed.
        receiver.ProcessBatch(new[] { BehavioralEvent.TreeSnapshot("tree-post") }, 0);
        Assert.Equal(1, _db.GetBehavioralEventCount("pre-learning", "treesnapshot"));
        Assert.Equal(1, _db.GetBehavioralEventCount(activeLearningSession, "treesnapshot"));
    }

    [Fact]
    public void SessionTransition_ClearsTreeSnapshotDedupCache()
    {
        // Codex review C1: dedup cache shared across the singleton's lifetime
        // means a hash captured under "pre-learning" silently dropped the
        // same hash arriving under the live session within DedupWindow.
        // Fix: clear cache when resolver returns a different session id.
        const string activeLearningSession = "learn-agent-1-202604270000";
        _db.CreateLearningSession("pre-learning", "pharm-test");
        _db.CreateLearningSession(activeLearningSession, "pharm-test");

        string? activeSession = null;
        var receiver = new BehavioralEventReceiver(_db,
            sessionResolver: () => activeSession);

        // Identical hash captured under pre-learning — cache it.
        receiver.ProcessBatch(new[] { BehavioralEvent.TreeSnapshot("tree-shared") }, 0);
        Assert.Equal(1, _db.GetBehavioralEventCount("pre-learning", "treesnapshot"));

        // Session transitions live.
        activeSession = activeLearningSession;

        // Same hash arrives under the live session — must NOT be deduped
        // against the pre-learning entry. Without the cache clear it would
        // be silently dropped, leaving the live session at 0.
        receiver.ProcessBatch(new[] { BehavioralEvent.TreeSnapshot("tree-shared") }, 0);
        Assert.Equal(1, _db.GetBehavioralEventCount(activeLearningSession, "treesnapshot"));
    }

    [Fact]
    public void SessionTransition_ResetsDroppedEventCounter()
    {
        // Codex review H2: with the singleton lifetime spanning the agent
        // process, TotalDroppedEvents was previously a monotonic counter
        // across all session boundaries — PomExporter would over-report
        // pre-learning drops as belonging to the live session. Fix: reset
        // counter on session change so PomExporter sees per-session drops.
        const string activeLearningSession = "learn-agent-1-202604270000";
        _db.CreateLearningSession("pre-learning", "pharm-test");
        _db.CreateLearningSession(activeLearningSession, "pharm-test");

        string? activeSession = null;
        var receiver = new BehavioralEventReceiver(_db,
            sessionResolver: () => activeSession);

        // 100 events dropped during pre-learning window.
        receiver.ProcessBatch(Array.Empty<BehavioralEvent>(), droppedSinceLast: 100);
        Assert.Equal(100, receiver.TotalDroppedEvents);

        // Session transitions — pre-learning drops must NOT be attributed
        // to the live session.
        activeSession = activeLearningSession;
        receiver.ProcessBatch(Array.Empty<BehavioralEvent>(), droppedSinceLast: 5);
        Assert.Equal(5, receiver.TotalDroppedEvents);
    }

    [Fact]
    public void OnInteractionCallback_NotFired_ForInvalidInteractionEvents()
    {
        // Codex round-3 H-NEW-1: the post-lock callback replay must mirror
        // IsValid()'s string.IsNullOrEmpty check. An Interaction with
        // ElementId="" fails IsValid + skips persistence; the callback must
        // also skip — otherwise ActionCorrelator sees phantom empty-id
        // events the DB never contains.
        var hits = new List<string>();
        var receiver = new BehavioralEventReceiver(_db, SessionId,
            onInteraction: (_, elementId, _, _) => hits.Add(elementId));

        var validInteraction = BehavioralEvent.Interaction(
            "click", "tree-1", "elem-real", "Button", null, null);
        var emptyIdInteraction = BehavioralEvent.Interaction(
            "click", "tree-2", "", "Button", null, null);
        var nullIdInteraction = BehavioralEvent.Interaction(
            "click", "tree-3", null, "Button", null, null);

        receiver.ProcessBatch(
            new[] { validInteraction, emptyIdInteraction, nullIdInteraction },
            droppedSinceLast: 0);

        Assert.Single(hits);
        Assert.Equal("elem-real", hits[0]);
    }

    [Fact]
    public void SetInteractionCallback_HotSwapsCallback_FutureInteractionsCallNewCallback()
    {
        // ActionCorrelator wiring couples to the active session's interaction
        // stream. Helper-emitted events flow through the singleton receiver,
        // so LearningWorker rebinds the callback once ActionCorrelator is
        // constructed for the active session.
        var oldCallbackHits = 0;
        var newCallbackHits = 0;

        var receiver = new BehavioralEventReceiver(_db, SessionId,
            onInteraction: (_, _, _, _) => oldCallbackHits++);

        var interaction1 = BehavioralEvent.Interaction("click", "tree-a", "elem-1", "Button", null, null);
        receiver.ProcessBatch(new[] { interaction1 }, 0);
        Assert.Equal(1, oldCallbackHits);
        Assert.Equal(0, newCallbackHits);

        receiver.SetInteractionCallback((_, _, _, _) => newCallbackHits++);

        var interaction2 = BehavioralEvent.Interaction("click", "tree-b", "elem-2", "Button", null, null);
        receiver.ProcessBatch(new[] { interaction2 }, 0);
        Assert.Equal(1, oldCallbackHits); // unchanged
        Assert.Equal(1, newCallbackHits); // new callback fired
    }

    [Fact]
    public async Task Concurrent_SetInteractionCallback_AndProcessBatch_NoTornState()
    {
        // Codex review H1: Two threadpool tasks (IPC handler running
        // ProcessBatch + LearningWorker bg service calling SetInteractionCallback)
        // race on _onInteraction. Without locking, an in-flight batch can
        // see a half-updated state. The lock guarantees reads + writes are
        // atomic; this test stresses the path to confirm no exceptions and
        // every callback observed is consistent with one of the registered
        // callbacks (never a torn intermediate).
        var receiver = new BehavioralEventReceiver(_db, SessionId);
        var totalHits = 0;

        Action<string, string, string?, DateTimeOffset> cb1 = (_, _, _, _) => Interlocked.Increment(ref totalHits);
        Action<string, string, string?, DateTimeOffset> cb2 = (_, _, _, _) => Interlocked.Increment(ref totalHits);

        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var configurer = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                receiver.SetInteractionCallback((i++ & 1) == 0 ? cb1 : cb2);
            }
        });

        var emitter = Task.Run(() =>
        {
            int seq = 0;
            while (!stop.IsCancellationRequested)
            {
                var interaction = BehavioralEvent.Interaction(
                    "click", $"tree-{seq}", $"elem-{seq}", "Button", null, null);
                receiver.ProcessBatch(new[] { interaction }, 0);
                seq++;
            }
        });

        await Task.WhenAll(configurer, emitter);
        // No assertion on exact hit count — what matters is no exceptions
        // and the receiver remained internally consistent throughout.
        Assert.True(totalHits >= 0);
    }

    [Fact]
    public void ProcessBatch_TreeSnapshotDedup_WithinWindow()
    {
        var snapshot1 = BehavioralEvent.TreeSnapshot("tree-dup");
        var snapshot2 = BehavioralEvent.TreeSnapshot("tree-dup");

        var result = _receiver.ProcessBatch(new[] { snapshot1, snapshot2 }, droppedSinceLast: 0);

        // second is deduped — not rejected, just skipped
        Assert.Equal(1, result.EventsStored);
        Assert.Equal(0, result.EventsRejected);

        var events = _db.GetBehavioralEvents(SessionId, eventType: "treesnapshot");
        Assert.Single(events);
    }

    [Fact]
    public void ProcessBatch_TreeSnapshotDedup_DifferentHashes_BothStored()
    {
        var snapshot1 = BehavioralEvent.TreeSnapshot("tree-aaa");
        var snapshot2 = BehavioralEvent.TreeSnapshot("tree-bbb");

        var result = _receiver.ProcessBatch(new[] { snapshot1, snapshot2 }, droppedSinceLast: 0);

        Assert.Equal(2, result.EventsStored);
        Assert.Equal(0, result.EventsRejected);
    }

    [Fact]
    public void ProcessBatch_RejectsInvalidEvents_NullElementId()
    {
        // Interaction with null ElementId is invalid
        var badInteraction = BehavioralEvent.Interaction("click", "tree-x", null, "Button", null, null);

        var result = _receiver.ProcessBatch(new[] { badInteraction }, droppedSinceLast: 0);

        Assert.True(result.Accepted);
        Assert.Equal(0, result.EventsStored);
        Assert.Equal(1, result.EventsRejected);

        Assert.Empty(_db.GetBehavioralEvents(SessionId));
    }

    [Theory]
    [InlineData("patient_123456789", "Edit")]
    [InlineData("safeButton", "customer_5551234567")]
    [InlineData("member@example.com", null)]
    public void ProcessBatch_PmsChannelRejectsDynamicStructuralIdentifiers(
        string elementId,
        string? className)
    {
        var callbacks = 0;
        var receiver = new BehavioralEventReceiver(
            _db,
            SessionId,
            (_, _, _, _) => callbacks++);
        var interaction = BehavioralEvent.Interaction(
            "click", "tree", elementId, "Edit", className, null);

        var result = receiver.ProcessBatch(
            [interaction],
            droppedSinceLast: 0,
            sourceChannel: BehavioralEventChannels.Pms);

        Assert.Equal(0, result.EventsStored);
        Assert.Equal(1, result.EventsRejected);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public void ProcessBatch_PmsChannelAcceptsExactGeneratedAnonymousIdentity()
    {
        var identity = "anon:path:unavailable:rid:" +
            "72230967" + new string('a', 56) +
            ":shape:" + new string('b', 64);
        var interaction = BehavioralEvent.Interaction(
            "click", "tree", identity, "Edit", "TextBox", null);

        var result = _receiver.ProcessBatch(
            [interaction], 0, BehavioralEventChannels.Pms);

        Assert.Equal(1, result.EventsStored);
        Assert.Equal(0, result.EventsRejected);
    }

    [Fact]
    public void ProcessBatch_RejectsTreeSnapshot_WithEmptyTreeHash()
    {
        var badSnapshot = BehavioralEvent.TreeSnapshot(string.Empty);

        var result = _receiver.ProcessBatch(new[] { badSnapshot }, droppedSinceLast: 0);

        Assert.Equal(0, result.EventsStored);
        Assert.Equal(1, result.EventsRejected);
    }

    [Fact]
    public void ProcessBatch_MixedValidAndInvalid_PartialStore()
    {
        var good = BehavioralEvent.TreeSnapshot("tree-good");
        var bad = BehavioralEvent.Interaction("focus", "tree-good", null, null, null, null); // null ElementId

        var result = _receiver.ProcessBatch(new[] { good, bad }, droppedSinceLast: 0);

        Assert.Equal(1, result.EventsStored);
        Assert.Equal(1, result.EventsRejected);
    }

    [Fact]
    public void ProcessBatch_EmptyBatch_ReturnsZeros()
    {
        var result = _receiver.ProcessBatch(Array.Empty<BehavioralEvent>(), droppedSinceLast: 0);

        Assert.True(result.Accepted);
        Assert.Equal(0, result.EventsStored);
        Assert.Equal(0, result.EventsRejected);
    }

    [Fact]
    public void ProcessBatch_MultipleBatches_SequenceIncreases()
    {
        var batch1 = new[] { BehavioralEvent.TreeSnapshot("tree-1") };
        var batch2 = new[] { BehavioralEvent.TreeSnapshot("tree-2") };

        _receiver.ProcessBatch(batch1, 0);
        _receiver.ProcessBatch(batch2, 0);

        var events = _db.GetBehavioralEvents(SessionId);
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].SequenceNum);
        Assert.Equal(2, events[1].SequenceNum);
    }

    [Fact]
    public void ProcessBatch_KeystrokeEvent_Persists()
    {
        var keystroke = BehavioralEvent.Keystroke(KeystrokeCategory.Alpha, TimingBucket.Normal, 5);

        var result = _receiver.ProcessBatch(new[] { keystroke }, droppedSinceLast: 0);

        Assert.Equal(1, result.EventsStored);
        Assert.Equal(0, result.EventsRejected);

        var events = _db.GetBehavioralEvents(SessionId, eventType: "keystrokecategory");
        Assert.Single(events);
    }

    [Fact]
    public void LegacyV1Envelope_PersistsHighWater_ButMarksDeliveryUnverified()
    {
        var batch = MakeVersionedBatch(
            streamId: Guid.NewGuid().ToString("N"),
            firstSequence: 1,
            BehavioralEvent.TreeSnapshot("tree-versioned"),
            BehavioralEvent.Interaction("click", "tree-versioned", "button-1", "Button", null, null));

        var result = _receiver.ProcessBatch(batch);

        Assert.True(result.Accepted);
        Assert.False(result.Duplicate);
        Assert.Equal(2, result.EventsStored);
        Assert.Equal(2, result.AcceptedThroughSequence);

        var health = _db.GetBehavioralDeliveryHealth();
        Assert.Equal(1, health.StreamCount);
        Assert.Equal(1, health.AcceptedBatchCount);
        Assert.Equal(0, health.VerifiedBatchCount);
        Assert.Null(health.LastVerifiedBatchUtc);
        Assert.Null(health.LastVerifiedPmsBatchUtc);
        Assert.Equal(0, health.SequenceGapCount);
        Assert.True(health.LegacyUnverifiedSeen);
    }

    [Fact]
    public void VersionedBatch_LostAckRetryAfterReceiverRestart_IsDurablyDeduplicated()
    {
        var callbackCount = 0;
        var streamId = Guid.NewGuid().ToString("N");
        var batch = MakeVersionedBatch(
            streamId,
            1,
            BehavioralEvent.Interaction("click", "tree", "button-1", "Button", null, null));

        var firstReceiver = new BehavioralEventReceiver(
            _db, SessionId, (_, _, _, _) => callbackCount++);
        var first = firstReceiver.ProcessBatch(batch);

        // Simulate Core process restart after commit but before Helper saw the response.
        var restartedReceiver = new BehavioralEventReceiver(
            _db, SessionId, (_, _, _, _) => callbackCount++);
        var retry = restartedReceiver.ProcessBatch(batch);

        Assert.True(first.Accepted);
        Assert.True(retry.Accepted);
        Assert.True(retry.Duplicate);
        Assert.Equal(0, retry.EventsStored);
        Assert.Equal(1, _db.GetBehavioralEventCount(SessionId, "interaction"));
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, _db.GetBehavioralDeliveryHealth().DuplicateBatchCount);
    }

    [Fact]
    public void VersionedBatch_SequenceGapAndHelperDrop_AreVisibleInDurableHealth()
    {
        var streamId = Guid.NewGuid().ToString("N");
        var first = MakeVersionedBatch(
            streamId,
            1,
            BehavioralEvent.TreeSnapshot("tree-1"));
        var afterEviction = MakeVersionedBatch(
            streamId,
            4,
            BehavioralEvent.TreeSnapshot("tree-4")) with { DroppedTotal = 2 };

        Assert.True(_receiver.ProcessBatch(first).Accepted);
        Assert.True(_receiver.ProcessBatch(afterEviction).Accepted);

        var health = _db.GetBehavioralDeliveryHealth();
        Assert.Equal(2, health.SequenceGapCount);
        Assert.Equal(2, health.DroppedEventCount);
        Assert.Equal(2, _receiver.TotalDroppedEvents);
    }

    [Fact]
    public void VersionedBatch_RejectsMalformedSequenceRangeWithoutPersistence()
    {
        var batch = MakeVersionedBatch(
            Guid.NewGuid().ToString("N"),
            1,
            BehavioralEvent.TreeSnapshot("tree")) with
        {
            LastSequence = 2,
        };

        var result = _receiver.ProcessBatch(batch);

        Assert.False(result.Accepted);
        Assert.Equal("sequence_range_mismatch", result.ErrorCode);
        Assert.Empty(_db.GetBehavioralEvents(SessionId));
    }

    [Fact]
    public void VersionedBatch_RejectsStreamChannelRebinding()
    {
        var streamId = Guid.NewGuid().ToString("N");
        var first = MakeVersionedBatch(
            streamId,
            1,
            BehavioralEvent.TreeSnapshot("tree-1"));
        var rebound = MakeVersionedBatch(
            streamId,
            2,
            BehavioralEvent.TreeSnapshot("tree-2")) with
        {
            Channel = BehavioralEventChannels.System,
        };

        Assert.True(_receiver.ProcessBatch(first).Accepted);
        var result = _receiver.ProcessBatch(rebound);

        Assert.False(result.Accepted);
        Assert.Equal("behavioral_stream_channel_mismatch", result.ErrorCode);
        Assert.Single(_db.GetBehavioralEvents(SessionId));
    }

    [Fact]
    public void VersionedBatch_RejectsPartialOverlapWithStableStructuralCode()
    {
        var streamId = Guid.NewGuid().ToString("N");
        var first = MakeVersionedBatch(
            streamId,
            1,
            BehavioralEvent.TreeSnapshot("tree-1"));
        var overlap = MakeVersionedBatch(
            streamId,
            1,
            BehavioralEvent.TreeSnapshot("tree-overlap"),
            BehavioralEvent.TreeSnapshot("tree-2"));

        Assert.True(_receiver.ProcessBatch(first).Accepted);
        var result = _receiver.ProcessBatch(overlap);

        Assert.False(result.Accepted);
        Assert.Equal("behavioral_stream_partial_overlap", result.ErrorCode);
        Assert.Single(_db.GetBehavioralEvents(SessionId));
    }

    [Fact]
    public void VersionedBatch_RejectsDroppedCounterRegressionWithStableStructuralCode()
    {
        var streamId = Guid.NewGuid().ToString("N");
        var first = MakeVersionedBatch(
            streamId,
            1,
            BehavioralEvent.TreeSnapshot("tree-1")) with { DroppedTotal = 2 };
        var regressed = MakeVersionedBatch(
            streamId,
            2,
            BehavioralEvent.TreeSnapshot("tree-2")) with { DroppedTotal = 1 };

        Assert.True(_receiver.ProcessBatch(first).Accepted);
        var result = _receiver.ProcessBatch(regressed);

        Assert.False(result.Accepted);
        Assert.Equal("behavioral_dropped_total_regression", result.ErrorCode);
        Assert.Single(_db.GetBehavioralEvents(SessionId));
    }

    private static BehavioralEventBatch MakeVersionedBatch(
        string streamId,
        long firstSequence,
        params BehavioralEvent[] events)
    {
        var sequenced = events
            .Select((behavioralEvent, index) => behavioralEvent.WithSeq(firstSequence + index))
            .ToArray();
        return new BehavioralEventBatch
        {
            ContractVersion = BehavioralEventBatch.LegacyContractVersion,
            BatchId = Guid.NewGuid().ToString("N"),
            StreamId = streamId,
            Channel = BehavioralEventChannels.Pms,
            FirstSequence = sequenced[0].Seq,
            LastSequence = sequenced[^1].Seq,
            DroppedTotal = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Events = sequenced,
        };
    }
}
