using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

/// <summary>
/// The agent's understanding of the PMS situation: three raw signals → one situation it can EXPLAIN.
/// A fresh install must never fail silently — it reports "PioneerRx is closed" / "database unreachable"
/// in plain language, instead of the old behavior of doing nothing when expected elements were absent.
/// </summary>
public class PmsSituationClassifierTests
{
    [Fact]
    public void NotInstalled_TrumpsEverything()
    {
        foreach (var sql in new[] { true, false })
            foreach (var helper in new[] { true, false })
            {
                var r = PmsSituationClassifier.Classify(new PmsSignals(PmsInstalled: false, sql, helper));
                Assert.Equal(PmsSituation.NotInstalled, r.Situation);
                Assert.Equal("pms_not_installed", r.Code);
                Assert.Contains("not installed", r.Explanation);
            }
    }

    [Fact]
    public void Installed_DatabaseDown_IsDatabaseUnreachable()
    {
        var r = PmsSituationClassifier.Classify(
            new PmsSignals(PmsInstalled: true, SqlConnected: false, HelperAttached: true));
        Assert.Equal(PmsSituation.DatabaseUnreachable, r.Situation);
        Assert.Equal("pms_db_unreachable", r.Code);
        Assert.Contains("database", r.Explanation);
    }

    [Fact]
    public void Installed_DatabaseUp_HelperDetached_ExplainsAppMayBeClosed()
    {
        var r = PmsSituationClassifier.Classify(
            new PmsSignals(PmsInstalled: true, SqlConnected: true, HelperAttached: false));
        Assert.Equal(PmsSituation.HelperDetached, r.Situation);
        Assert.Equal("pms_helper_detached", r.Code);
        Assert.Contains("closed", r.Explanation); // the PioneerRx window may be closed / on another desktop
    }

    [Fact]
    public void Installed_DatabaseUp_HelperAttached_IsOperational()
    {
        var r = PmsSituationClassifier.Classify(
            new PmsSignals(PmsInstalled: true, SqlConnected: true, HelperAttached: true));
        Assert.Equal(PmsSituation.Operational, r.Situation);
        Assert.Equal("pms_operational", r.Code);
    }

    [Fact]
    public void EverySituation_HasAStableCodeAndOperatorExplanation()
    {
        var signals = new[]
        {
            new PmsSignals(false, false, false),
            new PmsSignals(true, false, false),
            new PmsSignals(true, true, false),
            new PmsSignals(true, true, true),
        };
        foreach (var s in signals)
        {
            var r = PmsSituationClassifier.Classify(s);
            Assert.NotEqual(PmsSituation.Unknown, r.Situation); // never fall through to Unknown
            Assert.False(string.IsNullOrWhiteSpace(r.Code));
            Assert.False(string.IsNullOrWhiteSpace(r.Explanation));
        }
    }
}
