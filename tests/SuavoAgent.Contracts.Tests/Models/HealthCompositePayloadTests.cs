using System;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class HealthCompositePayloadTests
{
    [Fact]
    public void Construct_HealthyPayload_AssignsAllFields()
    {
        var components = new HealthCompositeComponents(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            ExtractionRecent: true);

        var computedAt = DateTimeOffset.UtcNow;
        var payload = new HealthCompositePayload(
            Status: "healthy",
            Components: components,
            ComputedAt: computedAt);

        Assert.Equal("healthy", payload.Status);
        Assert.True(payload.Components.HelperAttached);
        Assert.True(payload.Components.IpcConnected);
        Assert.True(payload.Components.SchemaCanaryGreen);
        Assert.True(payload.Components.ExtractionRecent);
        Assert.Equal(computedAt, payload.ComputedAt);
    }

    [Fact]
    public void Construct_DegradedPayload_TracksFailingComponents()
    {
        var components = new HealthCompositeComponents(
            HelperAttached: true,
            IpcConnected: false,    // failing
            SchemaCanaryGreen: true,
            ExtractionRecent: false); // failing

        var payload = new HealthCompositePayload(
            Status: "heartbeating-but-unhealthy",
            Components: components,
            ComputedAt: DateTimeOffset.UtcNow);

        Assert.Equal("heartbeating-but-unhealthy", payload.Status);
        Assert.False(payload.Components.IpcConnected);
        Assert.False(payload.Components.ExtractionRecent);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("heartbeating-but-unhealthy")]
    [InlineData("initializing")]
    public void Status_AcceptsCanonicalValues(string status)
    {
        var payload = new HealthCompositePayload(
            Status: status,
            Components: new HealthCompositeComponents(true, true, true, true),
            ComputedAt: DateTimeOffset.UtcNow);

        Assert.Equal(status, payload.Status);
    }
}
