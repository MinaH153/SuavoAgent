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
    public void Configure_UpdatesSessionId_FutureWritesUseNewSession()
    {
        // Trip A root cause: BehavioralEventReceiver registered as singleton
        // with sessionId="ipc" (Program.cs:316) but heartbeat counters query
        // for the active learning session. All Helper-emitted events were
        // invisible to the dashboard. Fix: LearningWorker calls Configure()
        // when the learning session starts so subsequent events are written
        // under the correct session.
        const string preLearningSession = "pre-learning";
        const string activeLearningSession = "learn-agent-1-202604270000";
        _db.CreateLearningSession(preLearningSession, "pharm-test");
        _db.CreateLearningSession(activeLearningSession, "pharm-test");

        var receiver = new BehavioralEventReceiver(_db, preLearningSession);

        // Event before Configure → goes to pre-learning session
        receiver.ProcessBatch(new[] { BehavioralEvent.TreeSnapshot("tree-pre") }, 0);
        Assert.Equal(1, _db.GetBehavioralEventCount(preLearningSession, "treesnapshot"));
        Assert.Equal(0, _db.GetBehavioralEventCount(activeLearningSession, "treesnapshot"));

        // LearningWorker boots, calls Configure with the active session.
        receiver.Configure(activeLearningSession);

        // Event after Configure → goes to the active learning session
        receiver.ProcessBatch(new[] { BehavioralEvent.TreeSnapshot("tree-post") }, 0);
        // Pre-learning event remains attributed to its original session;
        // post-Configure event goes to the active learning session.
        Assert.Equal(1, _db.GetBehavioralEventCount(preLearningSession, "treesnapshot"));
        Assert.Equal(1, _db.GetBehavioralEventCount(activeLearningSession, "treesnapshot"));
    }

    [Fact]
    public void Configure_UpdatesOnInteractionCallback_FutureInteractionsCallNewCallback()
    {
        // ActionCorrelator wiring (LearningWorker.cs:113-115) couples to the
        // active learning session's interaction stream. Configure must hot-
        // swap both the session AND the interaction callback so newly fired
        // interactions are routed to the active correlator instance.
        var oldCallbackHits = 0;
        var newCallbackHits = 0;

        var receiver = new BehavioralEventReceiver(_db, SessionId,
            onInteraction: (_, _, _, _) => oldCallbackHits++);

        var interaction1 = BehavioralEvent.Interaction("click", "tree-a", "elem-1", "Button", null, null);
        receiver.ProcessBatch(new[] { interaction1 }, 0);
        Assert.Equal(1, oldCallbackHits);
        Assert.Equal(0, newCallbackHits);

        receiver.Configure(SessionId, onInteraction: (_, _, _, _) => newCallbackHits++);

        var interaction2 = BehavioralEvent.Interaction("click", "tree-b", "elem-2", "Button", null, null);
        receiver.ProcessBatch(new[] { interaction2 }, 0);
        Assert.Equal(1, oldCallbackHits); // unchanged
        Assert.Equal(1, newCallbackHits); // new callback fired
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
}
