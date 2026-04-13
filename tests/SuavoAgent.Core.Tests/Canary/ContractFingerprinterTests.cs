using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class ContractFingerprinterTests
{
    private static readonly ObservedObject Col1 = new("Prescription", "RxTransaction",
        "RxNumber", "int", null, false, true);
    private static readonly ObservedObject Col2 = new("Prescription", "RxTransaction",
        "DateFilled", "datetime", null, true, true);
    private static readonly ObservedStatus Status1 = new("Waiting for Pick up",
        "53ce4c47-dff2-46ac-a310-719e792239ef");
    private static readonly ObservedStatus Status2 = new("Waiting for Delivery",
        "c3adbbcc-76e3-4b06-a0dc-4e8b8ce0a2de");

    [Fact]
    public void ObjectFingerprint_SameInput_SameHash()
    {
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void ObjectFingerprint_DifferentOrder_SameHash()
    {
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { Col2, Col1 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void ObjectFingerprint_ColumnRenamed_DifferentHash()
    {
        var renamed = Col1 with { ColumnName = "RxNum" };
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { renamed, Col2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ObjectFingerprint_TypeChanged_DifferentHash()
    {
        var retyped = Col1 with { DataTypeName = "varchar" };
        var a = ContractFingerprinter.HashObjects(new[] { Col1, Col2 });
        var b = ContractFingerprinter.HashObjects(new[] { retyped, Col2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_SameInput_SameHash()
    {
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_DifferentOrder_SameHash()
    {
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { Status2, Status1 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_GuidChanged_DifferentHash()
    {
        var changed = Status1 with { GuidValue = "00000000-0000-0000-0000-000000000000" };
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { changed, Status2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StatusMapFingerprint_DescriptionRenamed_DifferentHash()
    {
        var renamed = Status1 with { Description = "Ready for Pickup" };
        var a = ContractFingerprinter.HashStatusMap(new[] { Status1, Status2 });
        var b = ContractFingerprinter.HashStatusMap(new[] { renamed, Status2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CompositeFingerprint_DerivedFromComponents()
    {
        var obj = ContractFingerprinter.HashObjects(new[] { Col1 });
        var status = ContractFingerprinter.HashStatusMap(new[] { Status1 });
        var query = ContractFingerprinter.HashQuery("SELECT 1");
        var shape = ContractFingerprinter.HashResultShape(new[] { ("RxNumber", "int") });
        var composite = ContractFingerprinter.CompositeHash(obj, status, query, shape);
        Assert.NotEmpty(composite);
        Assert.Equal(64, composite.Length); // SHA-256 hex
    }

    [Fact]
    public void QueryFingerprint_TemplateVsExpanded_DifferentHash()
    {
        var template = ContractFingerprinter.HashQuery("SELECT * FROM T WHERE status IN ({statusParams})");
        var expanded = ContractFingerprinter.HashQuery("SELECT * FROM T WHERE status IN (@s0, @s1)");
        Assert.NotEqual(template, expanded);
    }

    [Fact]
    public void ResultShapeFingerprint_ColumnMissing_DifferentHash()
    {
        var full = ContractFingerprinter.HashResultShape(new[]
            { ("RxNumber", "int"), ("DateFilled", "datetime") });
        var partial = ContractFingerprinter.HashResultShape(new[]
            { ("RxNumber", "int") });
        Assert.NotEqual(full, partial);
    }
}
