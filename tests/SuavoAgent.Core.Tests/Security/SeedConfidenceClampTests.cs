using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Security;

public class SeedConfidenceClampTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly SeedApplicator _applicator;
    private const string SessionId = "sess-1";

    public SeedConfidenceClampTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
        _applicator = new SeedApplicator(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ApplyModelSeeds_ClampsConfidenceTo06()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.99, 0.99, 20, 1.0)
        };
        var response = new SeedResponse("digest-clamp", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        _applicator.ApplyModelSeeds(SessionId, response);

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "t1:btn1:q1");
        Assert.True(match.Confidence <= 0.6, $"Expected <= 0.6, got {match.Confidence}");
    }

    [Fact]
    public void ApplyModelSeeds_ClampsNegativeToZero()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.5, 0.5, 5, -0.5)
        };
        var response = new SeedResponse("digest-neg", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        _applicator.ApplyModelSeeds(SessionId, response);

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "t1:btn1:q1");
        Assert.True(match.Confidence >= 0.0);
    }
}
