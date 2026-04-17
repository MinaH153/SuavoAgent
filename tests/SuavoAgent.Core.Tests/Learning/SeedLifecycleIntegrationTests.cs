using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SeedLifecycleIntegrationTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public SeedLifecycleIntegrationTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void FullCycle_PatternSeed_Apply_Confirm_GatePass()
    {
        var applicator = new SeedApplicator(_db);

        // 1. Apply pattern seeds
        var patternResponse = new SeedResponse("digest-p", 1, "pattern", new[] { "schema" }, null, null,
            new[] {
                new SeedQueryShape("qs-1", "UPDATE [Prescription].[RxTransaction] SET [StatusID]=@p WHERE [RxNumber]=@rx", new[] { "Prescription.RxTransaction" }, 0.9, 10),
                new SeedQueryShape("qs-2", "SELECT [RxNumber] FROM [Prescription].[Rx] WHERE [StatusID]=@s0", new[] { "Prescription.Rx" }, 0.8, 8),
            },
            new[] { new SeedStatusMapping("ST", "guid-1", "Completed", 15) },
            new[] { new SeedWorkflowHint("wf-1", 3, 20, true, 5) });

        var result = applicator.ApplyPatternSeeds(SessionId, patternResponse);
        Assert.Equal(4, result.ItemsApplied);

        // 2. Simulate confirmations (agent independently observes 3 of 4)
        _db.ConfirmSeedItem("digest-p", "query_shape", "qs-1", "2026-04-14T12:00:00Z");
        _db.ConfirmSeedItem("digest-p", "status_mapping", "guid-1", "2026-04-14T12:00:00Z");
        _db.ConfirmSeedItem("digest-p", "workflow_hint", "wf-1", "2026-04-14T12:00:00Z");
        // qs-2 not confirmed yet

        // 3. Evaluate gate -- 75% < 80%, should not pass
        var gate1 = new PhaseGate(_db, SessionId, "pattern", "digest-p",
            DateTimeOffset.UtcNow.AddHours(-80), canaryClean: true, unseededPatternCount: 6);
        Assert.False(gate1.Evaluate().Ready);

        // 4. Confirm last one
        _db.ConfirmSeedItem("digest-p", "query_shape", "qs-2", "2026-04-14T13:00:00Z");

        // 5. Re-evaluate -- 100% >= 80%, should pass
        var gate2 = new PhaseGate(_db, SessionId, "pattern", "digest-p",
            DateTimeOffset.UtcNow.AddHours(-80), canaryClean: true, unseededPatternCount: 6);
        Assert.True(gate2.Evaluate().Ready);
    }

    [Fact]
    public void FullCycle_ModelSeed_LocalWins_RejectDoesNotBlockGate()
    {
        var applicator = new SeedApplicator(_db);

        // Pre-existing local correlation
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");

        // Apply model seeds -- one overlaps with local
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.9, 0.95, 12, 0.6),
            new SeedCorrelation("t2:btn2:q2", "t2", "btn2", "Button", "q2", 0.85, 0.9, 10, 0.55),
        };
        var modelResponse = new SeedResponse("digest-m", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        var applyResult = applicator.ApplyModelSeeds(SessionId, modelResponse);
        Assert.Equal(1, applyResult.CorrelationsApplied);
        Assert.Equal(1, applyResult.CorrelationsSkipped);

        // Confirm the applied one
        _db.ConfirmSeedItem("digest-m", "correlation", "t2:btn2:q2", "2026-04-14T12:00:00Z");

        // Rejected item excluded from denominator -- 1/1 = 100%
        var ratio = _db.GetSeedConfirmationRatio("digest-m");
        Assert.Equal(1.0, ratio, precision: 2);

        var gate = new PhaseGate(_db, SessionId, "model", "digest-m",
            DateTimeOffset.UtcNow.AddHours(-50), canaryClean: true, unseededPatternCount: 6);
        Assert.True(gate.Evaluate().Ready);
    }

    [Fact]
    public void Idempotency_SecondApply_NoOps()
    {
        var applicator = new SeedApplicator(_db);
        var response = new SeedResponse("digest-1", 1, "pattern", new[] { "schema" }, null, null,
            new[] { new SeedQueryShape("qs-1", "SELECT [RxNumber] FROM [Prescription].[Rx] WHERE [StatusID]=@s0", new[] { "Prescription.Rx" }, 0.8, 5) },
            Array.Empty<SeedStatusMapping>(), null);

        var first = applicator.ApplyPatternSeeds(SessionId, response);
        Assert.Equal(1, first.ItemsApplied);
        Assert.False(first.AlreadyApplied);

        var second = applicator.ApplyPatternSeeds(SessionId, response);
        Assert.True(second.AlreadyApplied);
        Assert.Equal(0, second.ItemsApplied);
    }

    [Fact]
    public void SeededCorrelation_SourceProvenancePreserved()
    {
        var applicator = new SeedApplicator(_db);

        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = new SeedResponse("digest-prov", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        applicator.ApplyModelSeeds(SessionId, response);

        var source = _db.GetCorrelatedActionSource(SessionId, "t1:btn1:q1");
        Assert.Equal("seed", source.Source);
        Assert.Equal("digest-prov", source.SeedDigest);
        Assert.NotNull(source.SeededAt);
    }

    [Fact]
    public void AbortTrigger_LowConfirmation_AfterDelay()
    {
        var applicator = new SeedApplicator(_db);

        // Apply 4 pattern seeds, confirm only 1 (25% < 50% abort threshold)
        var response = new SeedResponse("digest-abort", 1, "pattern", new[] { "schema" }, null, null,
            new[] {
                new SeedQueryShape("qs-1", "SELECT [RxNumber] FROM [Prescription].[Rx] WHERE [StatusID]=@s0", new[] { "Prescription.Rx" }, 0.9, 10),
                new SeedQueryShape("qs-2", "SELECT [RxNumber] FROM [Prescription].[Rx] WHERE [StatusID]=@s1", new[] { "Prescription.Rx" }, 0.8, 8),
                new SeedQueryShape("qs-3", "SELECT [RxNumber] FROM [Prescription].[Rx] WHERE [FillDate]=@d0", new[] { "Prescription.Rx" }, 0.7, 6),
                new SeedQueryShape("qs-4", "SELECT [RxNumber] FROM [Prescription].[RxTransaction] WHERE [StatusID]=@s2", new[] { "Prescription.RxTransaction" }, 0.6, 4),
            },
            Array.Empty<SeedStatusMapping>(), null);

        applicator.ApplyPatternSeeds(SessionId, response);
        _db.ConfirmSeedItem("digest-abort", "query_shape", "qs-1", "2026-04-14T12:00:00Z");

        // After 24+ hours with < 50% confirmation -- abort
        var gate = new PhaseGate(_db, SessionId, "pattern", "digest-abort",
            DateTimeOffset.UtcNow.AddHours(-30), canaryClean: true, unseededPatternCount: 10);
        var eval = gate.Evaluate();
        Assert.True(eval.AbortAcceleration);
        Assert.False(eval.Ready);
    }
}
