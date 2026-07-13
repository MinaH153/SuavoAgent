using System.Text.Json;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HeartbeatPricingUploadCapabilityTests
{
    [Fact]
    public void CapabilityProjection_IsExactAndVersioned()
    {
        var capability = JsonSerializer.SerializeToElement(
            HeartbeatWorker.BuildAgentCapabilities());

        Assert.Equal(JsonValueKind.Object, capability.ValueKind);
        Assert.Equal(
            new[] { "structuredPricingCommandsVersion" },
            capability.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            1,
            capability.GetProperty("structuredPricingCommandsVersion").GetInt32());
        Assert.False(capability.TryGetProperty("pricingUploadConsumerVersion", out _));
    }
}
