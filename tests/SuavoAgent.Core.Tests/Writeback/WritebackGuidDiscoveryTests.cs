using SuavoAgent.Adapters.PioneerRx;
using Xunit;

namespace SuavoAgent.Core.Tests.Writeback;

public class WritebackGuidDiscoveryTests
{
    [Fact]
    public void AllDeliveryStatusNames_Contains5Statuses()
    {
        Assert.Equal(5, PioneerRxConstants.AllDeliveryStatusNames.Count);
        Assert.Contains("Waiting for Pick up", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("Waiting for Delivery", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("To Be Put in Bin", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("Out for Delivery", PioneerRxConstants.AllDeliveryStatusNames);
        Assert.Contains("Completed", PioneerRxConstants.AllDeliveryStatusNames);
    }

    [Fact]
    public void DeliveryReadyStatusNames_StillContains3()
    {
        Assert.Equal(3, PioneerRxConstants.DeliveryReadyStatusNames.Count);
    }

    [Fact]
    public void FallbackStatusGuids_StillContains3_NoWriteTargetFallbacks()
    {
        Assert.Equal(3, PioneerRxConstants.FallbackStatusGuids.Count);
        Assert.DoesNotContain("Out for Delivery", PioneerRxConstants.FallbackStatusGuids.Keys);
        Assert.DoesNotContain("Completed", PioneerRxConstants.FallbackStatusGuids.Keys);
    }
}
