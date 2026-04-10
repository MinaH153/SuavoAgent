using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Sql;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public class PioneerRxSqlEngineTests
{
    [Fact]
    public void BuildDeliveryQuery_UsesRealSchema()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3);
        Assert.Contains("Prescription.RxTransaction rt", query);
        Assert.Contains("RxLocal.ActiveRx a", query);
        Assert.Contains("rt.RxID = a.RxID", query);
        Assert.DoesNotContain("dbo.", query);
        Assert.DoesNotContain("Prescription.Rx r", query);
    }

    [Fact]
    public void BuildDeliveryQuery_UsesParameterPlaceholders()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3);
        Assert.Contains("@status0", query);
        Assert.Contains("@status1", query);
        Assert.Contains("@status2", query);
        Assert.DoesNotContain("'53ce4c47", query); // no hardcoded GUIDs
    }

    [Fact]
    public void BuildDeliveryQuery_LimitsTo50()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("TOP 50", query);
    }

    [Fact]
    public void BuildDeliveryQuery_NoDateFilter()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.DoesNotContain("DATEADD", query);
    }

    [Fact]
    public void BuildDeliveryQuery_SelectsNoPhiColumns()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(3);
        Assert.DoesNotContain("PatientID", query);
        Assert.DoesNotContain("PatientName", query);
        Assert.DoesNotContain("Person.Patient", query);
    }

    [Fact]
    public void BuildDeliveryQuery_SelectsOperationalColumns()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("a.RxNumber", query);
        Assert.Contains("a.MedicationDescription", query);
        Assert.Contains("rt.DispensedQuantity", query);
        Assert.Contains("rt.DaysSupply", query);
        Assert.Contains("rt.RxTransactionStatusTypeID", query);
        Assert.Contains("a.DispensedNDC", query);
        Assert.Contains("a.PrescribedNDC", query);
        Assert.Contains("rt.RefillNumber", query);
    }

    [Fact]
    public void BuildDeliveryQuery_OrdersByDateFilledDesc()
    {
        var query = PioneerRxSqlEngine.BuildDeliveryQuery(1);
        Assert.Contains("ORDER BY rt.DateFilled DESC", query);
    }

    [Theory]
    [InlineData("PatientName")]
    [InlineData("PatientSSN")]
    [InlineData("DiagnosisCode")]
    [InlineData("PatientID")]
    [InlineData("PersonID")]
    public void IsPhiColumn_BlocksPhi(string col) => Assert.True(PioneerRxSqlEngine.IsPhiColumn(col));

    [Theory]
    [InlineData("RxNumber")]
    [InlineData("MedicationDescription")]
    [InlineData("DispensedNDC")]
    public void IsPhiColumn_AllowsOperational(string col) => Assert.False(PioneerRxSqlEngine.IsPhiColumn(col));
}
