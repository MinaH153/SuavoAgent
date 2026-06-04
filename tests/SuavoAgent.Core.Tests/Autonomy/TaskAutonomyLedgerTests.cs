using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Autonomy;

public class TaskAutonomyLedgerTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");
    private const string Task = "pricing";
    private const string Pharmacy = "ph-1";

    public void Dispose() => _db.Dispose();

    [Fact]
    public void NeverRun_IsSupervised_AndMayNotRunUnsupervised()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 12);
        var s = ledger.GetState(Task, Pharmacy);
        Assert.Equal(0, s.ConsecutiveCleanRuns);
        Assert.Equal(AutonomyLevel.Supervised, s.Level);
        Assert.False(ledger.MayRunUnsupervised(Task, Pharmacy, unsupervisedExecutionEnabled: true));
    }

    [Fact]
    public void EarnsEligibility_AfterThresholdCleanRuns_PersistedAcrossInstances()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 3);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        var third = ledger.RecordRun(Task, Pharmacy, clean: true, outcome: "completed");

        Assert.Equal(3, third.ConsecutiveCleanRuns);
        Assert.Equal(3, third.TotalRuns);
        Assert.Equal(AutonomyLevel.Eligible, third.Level);

        // A fresh ledger over the same DB sees the earned standing (persistence).
        var reopened = new TaskAutonomyLedger(_db, cleanRunsThreshold: 3);
        Assert.Equal(AutonomyLevel.Eligible, reopened.GetState(Task, Pharmacy).Level);
    }

    [Fact]
    public void OneStumble_ResetsTheStreak_AndDropsBackToSupervised()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 3);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        Assert.Equal(AutonomyLevel.Eligible, ledger.GetState(Task, Pharmacy).Level);

        var afterFail = ledger.RecordRun(Task, Pharmacy, clean: false, outcome: "aborted");
        Assert.Equal(0, afterFail.ConsecutiveCleanRuns);
        Assert.Equal(4, afterFail.TotalRuns); // total still counts the failed run
        Assert.Equal(AutonomyLevel.Supervised, afterFail.Level);
    }

    [Theory]
    [InlineData(true, true)]    // eligible + enabled → the ONLY way to run unsupervised
    [InlineData(false, false)]  // eligible + NOT enabled → still supervised
    public void MayRunUnsupervised_RequiresBothEligibleAndEnabled(bool enabled, bool expected)
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 2);
        ledger.RecordRun(Task, Pharmacy, clean: true);
        ledger.RecordRun(Task, Pharmacy, clean: true); // now Eligible
        Assert.Equal(expected, ledger.MayRunUnsupervised(Task, Pharmacy, enabled));
    }

    [Fact]
    public void Eligible_ButNotEnabled_NeverRunsUnsupervised()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 1);
        ledger.RecordRun(Task, Pharmacy, clean: true); // Eligible immediately at threshold 1
        Assert.Equal(AutonomyLevel.Eligible, ledger.GetState(Task, Pharmacy).Level);
        Assert.False(ledger.MayRunUnsupervised(Task, Pharmacy, unsupervisedExecutionEnabled: false));
    }

    [Fact]
    public void Tasks_AreTrackedIndependently()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 2);
        ledger.RecordRun("pricing", Pharmacy, clean: true);
        ledger.RecordRun("pricing", Pharmacy, clean: true); // pricing Eligible
        ledger.RecordRun("writeback", Pharmacy, clean: true); // writeback only 1

        Assert.Equal(AutonomyLevel.Eligible, ledger.GetState("pricing", Pharmacy).Level);
        Assert.Equal(AutonomyLevel.Supervised, ledger.GetState("writeback", Pharmacy).Level);
    }
}
