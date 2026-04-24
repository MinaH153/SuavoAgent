using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class MissionCharterValidationTests
{
    private static MissionCharter BuildValid(
        IReadOnlyList<MissionObjective>? objectivesOverride = null,
        MissionPriorityOrdering? orderingOverride = null,
        MissionToleranceThresholds? toleranceOverride = null,
        IReadOnlyList<MissionConstraint>? constraintsOverride = null,
        int version = 1,
        string pharmacyId = "pharm-alpha",
        Guid? charterId = null)
    {
        var objectives = objectivesOverride ?? new List<MissionObjective>
        {
            new("keep-agent-alive", "Heartbeat continuity", 100),
            new("detect-ready-rx", "Detect ready Rx within SLA", 80),
        };
        var ordering = orderingOverride ?? new MissionPriorityOrdering(
            objectives.Select(o => o.Id).ToList());
        var tolerance = toleranceOverride ?? new MissionToleranceThresholds(120, 3, 0.90);
        var constraints = constraintsOverride ?? new List<MissionConstraint>
        {
            new("no-unsigned-verbs", "policy", "verb.signature != null", "Action grammar v1."),
        };
        return new MissionCharter(
            CharterId: charterId ?? Guid.NewGuid(),
            PharmacyId: pharmacyId,
            Version: version,
            EffectiveFrom: DateTimeOffset.UtcNow,
            Objectives: objectives,
            Constraints: constraints,
            PriorityOrdering: ordering,
            Tolerance: tolerance,
            SignedByOperator: "test",
            SignedAt: DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ValidCharter_DoesNotThrow()
    {
        var charter = BuildValid();
        MissionCharterLoader.ValidateCharter(charter);
    }

    [Fact]
    public void EmptyCharterId_Throws()
    {
        var charter = BuildValid(charterId: Guid.Empty);
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("CHARTER_ID_EMPTY", ex.ValidationRuleId);
    }

    [Fact]
    public void EmptyPharmacyId_Throws()
    {
        var charter = BuildValid(pharmacyId: "   ");
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("PHARMACY_ID_EMPTY", ex.ValidationRuleId);
    }

    [Fact]
    public void NonPositiveVersion_Throws()
    {
        var charter = BuildValid(version: 0);
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("VERSION_NON_POSITIVE", ex.ValidationRuleId);
    }

    [Fact]
    public void EmptyObjectives_Throws()
    {
        var charter = BuildValid(
            objectivesOverride: new List<MissionObjective>(),
            orderingOverride: new MissionPriorityOrdering(new List<string>()));
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("OBJECTIVES_EMPTY", ex.ValidationRuleId);
    }

    [Fact]
    public void DuplicateObjectiveId_Throws()
    {
        var objectives = new List<MissionObjective>
        {
            new("dup", "First", 10),
            new("dup", "Second", 20),
        };
        var charter = BuildValid(
            objectivesOverride: objectives,
            orderingOverride: new MissionPriorityOrdering(new List<string> { "dup", "dup" }));
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("OBJECTIVE_ID_DUPLICATE", ex.ValidationRuleId);
    }

    [Fact]
    public void PriorityOrderingMismatch_Throws()
    {
        var charter = BuildValid(
            orderingOverride: new MissionPriorityOrdering(
                new List<string> { "keep-agent-alive", "unknown-objective" }));
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("PRIORITY_ORDERING_PERMUTATION", ex.ValidationRuleId);
    }

    [Fact]
    public void PriorityOrderingCardinalityMismatch_Throws()
    {
        var charter = BuildValid(
            orderingOverride: new MissionPriorityOrdering(
                new List<string> { "keep-agent-alive" })); // missing second objective
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("PRIORITY_ORDERING_CARDINALITY", ex.ValidationRuleId);
    }

    [Fact]
    public void NegativeToleranceMaxDowntime_Throws()
    {
        var charter = BuildValid(
            toleranceOverride: new MissionToleranceThresholds(-1, 3, 0.90));
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("TOLERANCE_NEGATIVE", ex.ValidationRuleId);
    }

    [Fact]
    public void NegativeToleranceMinCacheHitRate_Throws()
    {
        var charter = BuildValid(
            toleranceOverride: new MissionToleranceThresholds(120, 3, -0.01));
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("TOLERANCE_NEGATIVE", ex.ValidationRuleId);
    }

    [Fact]
    public void DuplicateConstraintId_Throws()
    {
        var constraints = new List<MissionConstraint>
        {
            new("c1", "policy", "expr1", "why1"),
            new("c1", "policy", "expr2", "why2"),
        };
        var charter = BuildValid(constraintsOverride: constraints);
        var ex = Assert.Throws<MissionCharterInvalidException>(() =>
            MissionCharterLoader.ValidateCharter(charter));
        Assert.Equal("CONSTRAINT_ID_DUPLICATE", ex.ValidationRuleId);
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsValidatedDefault()
    {
        var loader = new MissionCharterLoader();
        var charter = await loader.LoadAsync("pharm-beta", CancellationToken.None);

        Assert.Equal("pharm-beta", charter.PharmacyId);
        Assert.NotEqual(Guid.Empty, charter.CharterId);
        Assert.Equal(1, charter.Version);
        Assert.NotEmpty(charter.Objectives);
        // LoadAsync validated already; a second ValidateCharter call MUST NOT throw either.
        MissionCharterLoader.ValidateCharter(charter);
    }

    [Fact]
    public async Task LoadAsync_EmptyPharmacyId_Throws()
    {
        var loader = new MissionCharterLoader();
        await Assert.ThrowsAsync<ArgumentException>(
            () => loader.LoadAsync("", CancellationToken.None));
    }
}
