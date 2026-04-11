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
    public void BuildMetadataQuery_HasTop50Limit()
    {
        var query = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);
        Assert.Contains("TOP 50", query);
    }
}
