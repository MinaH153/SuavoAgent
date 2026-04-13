using System.Text.Json;
using SuavoAgent.Contracts.Canary;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class SchemaCanaryClassifierTests
{
    private static readonly ObservedObject ReqCol1 = new("Prescription", "RxTransaction",
        "RxNumber", "int", null, false, true);
    private static readonly ObservedObject ReqCol2 = new("Prescription", "RxTransaction",
        "DateFilled", "datetime", null, true, true);
    private static readonly ObservedObject OptCol1 = new("Inventory", "Item",
        "ItemName", "nvarchar", 200, true, false);
    private static readonly ObservedStatus St1 = new("Waiting for Pick up",
        "53ce4c47-dff2-46ac-a310-719e792239ef");
    private static readonly ObservedStatus St2 = new("Waiting for Delivery",
        "c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de");

    private static ContractBaseline MakeBaseline(
        IReadOnlyList<ObservedObject>? objects = null,
        IReadOnlyList<ObservedStatus>? statuses = null)
    {
        var objs = objects ?? new[] { ReqCol1, ReqCol2, OptCol1 };
        var stats = statuses ?? new[] { St1, St2 };
        var objHash = ContractFingerprinter.HashObjects(objs);
        var statusHash = ContractFingerprinter.HashStatusMap(stats);
        var queryHash = ContractFingerprinter.HashQuery("SELECT 1");
        var shapeHash = ContractFingerprinter.HashResultShape(
            new[] { ("RxNumber", "int"), ("DateFilled", "datetime") });
        var contractJson = JsonSerializer.Serialize(objs);
        return new ContractBaseline("pioneerrx", objHash, statusHash, queryHash,
            shapeHash, ContractFingerprinter.CompositeHash(objHash, statusHash, queryHash, shapeHash),
            contractJson, 1);
    }

    private ObservedContract MakeObserved(
        IReadOnlyList<ObservedObject>? objects = null,
        IReadOnlyList<ObservedStatus>? statuses = null,
        string? queryFp = null, string? shapeFp = null)
    {
        var objs = objects ?? new[] { ReqCol1, ReqCol2, OptCol1 };
        var stats = statuses ?? new[] { St1, St2 };
        return new ObservedContract(objs, stats,
            queryFp ?? ContractFingerprinter.HashQuery("SELECT 1"),
            shapeFp ?? ContractFingerprinter.HashResultShape(
                new[] { ("RxNumber", "int"), ("DateFilled", "datetime") }));
    }

    [Fact]
    public void NoChanges_ReturnsNone()
    {
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(), MakeObserved());
        Assert.Equal(CanarySeverity.None, result.Severity);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void RequiredColumnMissing_Critical()
    {
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(objects: new[] { ReqCol2, OptCol1 })); // ReqCol1 missing
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("object", result.DriftedComponents);
    }

    [Fact]
    public void StatusDescriptionRenamed_Critical()
    {
        var renamed = St1 with { Description = "Ready for Pickup" };
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(statuses: new[] { renamed, St2 }));
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("status_map", result.DriftedComponents);
    }

    [Fact]
    public void StatusGuidChanged_Critical()
    {
        var changed = St1 with { GuidValue = "00000000-0000-0000-0000-000000000000" };
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(statuses: new[] { changed, St2 }));
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void StatusCountChanged_Critical()
    {
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(statuses: new[] { St1 })); // was 2, now 1
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void ColumnTypeChangedIncompatibly_Critical()
    {
        var retyped = ReqCol1 with { DataTypeName = "varchar" }; // int → varchar
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(objects: new[] { retyped, ReqCol2, OptCol1 }));
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void ResultShapeMismatch_Critical()
    {
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(shapeFp: ContractFingerprinter.HashResultShape(
                new[] { ("RxNumber", "int") }))); // missing DateFilled
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("result_shape", result.DriftedComponents);
    }

    [Fact]
    public void QueryFingerprintChanged_Critical()
    {
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(queryFp: ContractFingerprinter.HashQuery("SELECT 2")));
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("query", result.DriftedComponents);
    }

    [Fact]
    public void ColumnTypeWidened_Warning()
    {
        var widened = OptCol1 with { MaxLength = 500 }; // nvarchar(200) → nvarchar(500)
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(objects: new[] { ReqCol1, ReqCol2, widened }));
        Assert.Equal(CanarySeverity.Warning, result.Severity);
    }

    [Fact]
    public void NullableChanged_Warning()
    {
        var changed = ReqCol1 with { IsNullable = true };
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(objects: new[] { changed, ReqCol2, OptCol1 }));
        Assert.Equal(CanarySeverity.Warning, result.Severity);
    }

    [Fact]
    public void OptionalObjectMissing_Warning()
    {
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(objects: new[] { ReqCol1, ReqCol2 })); // OptCol1 missing
        Assert.Equal(CanarySeverity.Warning, result.Severity);
    }

    [Fact]
    public void DuplicateStatusDescriptions_Critical()
    {
        var dup = St1 with { GuidValue = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" };
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(statuses: new[] { St1, dup, St2 }));
        Assert.Equal(CanarySeverity.Critical, result.Severity);
    }

    [Fact]
    public void MultipleComponentsDrifted_ReportsAll()
    {
        var retyped = ReqCol1 with { DataTypeName = "varchar" };
        var renamedSt = St1 with { Description = "Ready" };
        var result = SchemaCanaryClassifier.Classify(MakeBaseline(),
            MakeObserved(objects: new[] { retyped, ReqCol2, OptCol1 },
                statuses: new[] { renamedSt, St2 }));
        Assert.Equal(CanarySeverity.Critical, result.Severity);
        Assert.Contains("object", result.DriftedComponents);
        Assert.Contains("status_map", result.DriftedComponents);
    }
}
