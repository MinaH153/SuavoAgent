using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class CanaryIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public CanaryIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"canary_integ_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void FirstRun_NoBaseline_ReturnsNull()
    {
        var baseline = _db.GetCanaryBaseline("pharm-1", "pioneerrx");
        Assert.Null(baseline);
    }

    [Fact]
    public void EstablishBaseline_ThenVerify_Clean()
    {
        // Simulate establishment: create baseline from observed schema
        var objs = new[] {
            new ObservedObject("Prescription", "RxTransaction", "RxNumber", "int", null, false, true),
            new ObservedObject("Prescription", "RxTransaction", "DateFilled", "datetime", null, true, true),
        };
        var statuses = new[] {
            new ObservedStatus("Waiting for Pick up", "53ce4c47-dff2-46ac-a310-719e792239ef"),
            new ObservedStatus("Waiting for Delivery", "c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de"),
        };

        var objHash = ContractFingerprinter.HashObjects(objs);
        var statusHash = ContractFingerprinter.HashStatusMap(statuses);
        var queryHash = ContractFingerprinter.HashQuery("SELECT 1");
        var shapeHash = ContractFingerprinter.HashResultShape(new[] { ("RxNumber", "int"), ("DateFilled", "datetime") });
        var contractJson = System.Text.Json.JsonSerializer.Serialize(
            objs.Select(o => new { o.SchemaName, o.TableName, o.ColumnName, o.DataTypeName, o.IsRequired }));

        var baseline = new ContractBaseline("pioneerrx", objHash, statusHash, queryHash, shapeHash,
            ContractFingerprinter.CompositeHash(objHash, statusHash, queryHash, shapeHash),
            contractJson, 1);
        _db.UpsertCanaryBaseline("pharm-1", baseline);

        // Verify: same schema = clean
        var observed = new ObservedContract(objs.ToList(), statuses.ToList(), queryHash, shapeHash);
        var result = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.None, result.Severity);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DriftDetected_HoldPersisted_SurvivesRestart()
    {
        // Setup baseline
        var baseline = new ContractBaseline("pioneerrx", "obj1", "stat1", "qry1", "shape1",
            "composite1", "[]", 1);
        _db.UpsertCanaryBaseline("pharm-1", baseline);

        // Simulate drift hold
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "composite1");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");

        // Log incident
        _db.InsertCanaryIncident("pharm-1", "pioneerrx", "critical",
            "[\"status_map\"]", "composite1", "observed1",
            "Status GUID changed", 12);

        // Restart (close + reopen)
        _db.Dispose();
        using var db2 = new AgentStateDb(_dbPath);

        // Verify hold survived
        var hold = db2.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.NotNull(hold);
        Assert.Equal(3, hold.Value.BlockedCycles);
        Assert.Equal("critical", hold.Value.Severity);

        // Verify incident persisted
        var incidents = db2.GetOpenCanaryIncidents("pharm-1");
        Assert.Single(incidents);
        Assert.Equal(12, incidents[0].DroppedBatchRowCount);
    }

    [Fact]
    public void AcknowledgeDrift_ClearsHold()
    {
        _db.UpsertCanaryHold("pharm-1", "pioneerrx", "critical", "composite1");
        _db.IncrementCanaryHoldCycles("pharm-1", "pioneerrx");

        var hold = _db.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.NotNull(hold);

        _db.ClearCanaryHold("pharm-1", "pioneerrx");

        hold = _db.GetCanaryHold("pharm-1", "pioneerrx");
        Assert.Null(hold);
    }

    [Fact]
    public void EscalationStateMachine_FullCycle()
    {
        // Start clean
        var state = CanaryHoldState.Clear;

        // 2 warnings — no hold
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        Assert.False(state.IsInHold);
        Assert.Equal(2, state.ConsecutiveWarnings);

        // 3rd warning — escalates to Critical hold
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        Assert.True(state.IsInHold);
        Assert.Equal(CanarySeverity.Critical, state.EffectiveSeverity);

        // Continue in hold
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.Equal(3, state.BlockedCycles);
        Assert.True(state.ShouldAlertDashboard);

        // Acknowledge
        state = SchemaCanaryEscalation.Acknowledge(state, "operator-1");
        Assert.False(state.IsInHold);
        Assert.Equal("operator-1", state.AcknowledgedBy);
    }

    [Fact]
    public void ClassifierAndFingerprinter_EndToEnd()
    {
        // Build a realistic contract
        var objs = new ObservedObject[]
        {
            new("Prescription", "RxTransaction", "RxNumber", "int", null, false, true),
            new("Prescription", "RxTransaction", "StatusTypeID", "uniqueidentifier", null, false, true),
            new("Inventory", "Item", "ItemName", "nvarchar", 200, true, false), // optional
        };

        var statuses = new ObservedStatus[]
        {
            new("Waiting for Pick up", "guid-1"),
            new("Waiting for Delivery", "guid-2"),
            new("To Be Put in Bin", "guid-3"),
        };

        var objHash = ContractFingerprinter.HashObjects(objs);
        var statusHash = ContractFingerprinter.HashStatusMap(statuses);
        var queryHash = ContractFingerprinter.HashQuery("SELECT ...");
        var shapeHash = ContractFingerprinter.HashResultShape(new[] { ("RxNumber", "int") });
        var contractJson = System.Text.Json.JsonSerializer.Serialize(
            objs.Select(o => new { o.SchemaName, o.TableName, o.ColumnName, o.DataTypeName, o.IsRequired }));

        var baseline = new ContractBaseline("pioneerrx", objHash, statusHash, queryHash, shapeHash,
            ContractFingerprinter.CompositeHash(objHash, statusHash, queryHash, shapeHash),
            contractJson, 1);

        // Same schema → clean
        var observed = new ObservedContract(objs.ToList(), statuses.ToList(), queryHash, shapeHash);
        var clean = SchemaCanaryClassifier.Classify(baseline, observed);
        Assert.Equal(CanarySeverity.None, clean.Severity);

        // Status renamed → Critical
        var renamedStatuses = new ObservedStatus[]
        {
            new("Ready for Pickup", "guid-1"), // renamed!
            new("Waiting for Delivery", "guid-2"),
            new("To Be Put in Bin", "guid-3"),
        };
        var drifted = new ObservedContract(objs.ToList(), renamedStatuses.ToList(), queryHash, shapeHash);
        var critical = SchemaCanaryClassifier.Classify(baseline, drifted);
        Assert.Equal(CanarySeverity.Critical, critical.Severity);
        Assert.Contains("status_map", critical.DriftedComponents);

        // Optional table missing → Warning
        var partialObjs = objs.Where(o => o.IsRequired).ToList();
        var partialObs = new ObservedContract(partialObjs, statuses.ToList(), queryHash, shapeHash);
        var warning = SchemaCanaryClassifier.Classify(baseline, partialObs);
        Assert.Equal(CanarySeverity.Warning, warning.Severity);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
