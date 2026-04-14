using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Sql;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public class PhiMinimizationTests
{
    [Fact]
    public void BuildMetadataQuery_ContainsNoPersonJoin()
    {
        var query = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);
        Assert.DoesNotContain("Person", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FirstName", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phone", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Address", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RxNumber", query);
        Assert.Contains("TradeName", query);
    }

    [Fact]
    public void BuildPatientQuery_ContainsPersonJoin()
    {
        var query = PioneerRxSqlEngine.BuildPatientQuery();
        Assert.Contains("Person", query);
        Assert.Contains("FirstName", query);
        Assert.Contains("Phone", query);
        Assert.Contains("@rxNumber", query);
        Assert.Contains("TOP 1", query);
    }

    [Fact]
    public void BuildMetadataQuery_HasTop100DefaultLimit()
    {
        var query = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);
        Assert.Contains("TOP 100", query);
    }

    [Fact]
    public void BuildMetadataQuery_HasDateCutoff()
    {
        var query = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);
        Assert.Contains("@cutoff", query);
    }

    [Fact]
    public void BuildFullDeliveryQuery_ContainsPersonJoin()
    {
        // This query exists for PullPatientForRxAsync — NOT for detection.
        // Verify it does contain PHI columns (it should, by design).
        var query = PioneerRxSqlEngine.BuildFullDeliveryQuery(3);
        Assert.Contains("Person.Person", query);
        Assert.Contains("FirstName", query);
    }

    [Theory]
    [InlineData("PatientMobileNumber")]
    [InlineData("EmergencyContactPhone")]
    [InlineData("patient_email_address")]
    [InlineData("PersonAddress2")]
    [InlineData("SSNLast4")]
    [InlineData("DateOfBirthFormatted")]
    public void IsPhiColumn_CatchesNovelPhiColumns(string columnName)
    {
        Assert.True(PioneerRxConstants.IsPhiColumn(columnName));
    }

    [Theory]
    [InlineData("RxNumber")]
    [InlineData("ItemName")]
    [InlineData("StatusTypeID")]
    [InlineData("DateFilled")]
    [InlineData("DispensedQuantity")]
    public void IsPhiColumn_AllowsNonPhiColumns(string columnName)
    {
        Assert.False(PioneerRxConstants.IsPhiColumn(columnName));
    }
}
