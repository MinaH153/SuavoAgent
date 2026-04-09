using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Sql;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public class PioneerRxSqlEngineTests
{
    [Fact]
    public void BuildReadyRxQuery_ExcludesPhiColumns()
    {
        var query = PioneerRxSqlEngine.BuildReadyRxQuery(
            new[] { "RxNumber", "DrugName", "PatientName", "NDC", "PatientDOB" },
            PioneerRxConstants.ReadyStatusValues);
        Assert.DoesNotContain("PatientName", query);
        Assert.DoesNotContain("PatientDOB", query);
        Assert.Contains("RxNumber", query);
    }

    [Fact]
    public void BuildReadyRxQuery_UsesParameterPlaceholders()
    {
        var query = PioneerRxSqlEngine.BuildReadyRxQuery(
            new[] { "RxNumber", "Status" }, new[] { "Ready", "Filled" });
        Assert.Contains("@status", query);
        Assert.DoesNotContain("'Ready'", query);
    }

    [Fact]
    public void BuildReadyRxQuery_LimitsTo50()
    {
        var query = PioneerRxSqlEngine.BuildReadyRxQuery(new[] { "RxNumber" }, new[] { "Ready" });
        Assert.Contains("TOP 50", query);
    }

    [Theory]
    [InlineData("PatientName")]
    [InlineData("PatientSSN")]
    [InlineData("DiagnosisCode")]
    public void IsPhiColumn_BlocksPhi(string col) => Assert.True(PioneerRxSqlEngine.IsPhiColumn(col));

    [Theory]
    [InlineData("RxNumber")]
    [InlineData("DrugName")]
    [InlineData("NDC")]
    public void IsPhiColumn_AllowsOperational(string col) => Assert.False(PioneerRxSqlEngine.IsPhiColumn(col));
}
