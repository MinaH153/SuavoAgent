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
    public void NoHardcodedFallbackGuids()
    {
        // Fallback GUIDs were removed — status GUIDs must be discovered from live DB.
        // This test verifies the constants class no longer contains pharmacy-specific GUIDs.
        var fields = typeof(PioneerRxConstants).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.DoesNotContain(fields, f => f.Name == "FallbackStatusGuids");
    }
}
