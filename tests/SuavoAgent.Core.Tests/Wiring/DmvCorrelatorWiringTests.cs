using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Wiring;

public class DmvCorrelatorWiringTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public DmvCorrelatorWiringTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ProcessAndStore_WithCorrelator_CreatesCorrelation()
    {
        var correlator = new ActionCorrelator(_db, SessionId);
        var uiTime = DateTimeOffset.UtcNow;

        // Record a UI event first — correlator needs something in its sliding window
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);

        // Process a DMV observation with correlator — SQL within 0.5s of UI event
        DmvQueryObserver.ProcessAndStore(_db, SessionId,
            "UPDATE [Prescription].[RxTransaction] SET [StatusID] = @p0 WHERE [RxNumber] = @p1",
            executionCount: 1,
            lastExecutionTime: uiTime.AddSeconds(0.5).ToString("o"),
            clockOffsetMs: 0,
            correlator: correlator);

        var actions = _db.GetCorrelatedActions(SessionId);
        Assert.NotEmpty(actions);
    }

    [Fact]
    public void ProcessAndStore_WithoutCorrelator_StillStoresObservation()
    {
        DmvQueryObserver.ProcessAndStore(_db, SessionId,
            "SELECT [RxNumber], [StatusID] FROM [Prescription].[Rx] WHERE [StatusID] = @p0",
            executionCount: 5,
            lastExecutionTime: DateTimeOffset.UtcNow.ToString("o"),
            clockOffsetMs: 0,
            correlator: null);

        var obs = _db.GetDmvQueryObservations(SessionId);
        Assert.NotEmpty(obs);
    }
}
