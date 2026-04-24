using SuavoAgent.Adapters.ComputerRx.Canary;
using SuavoAgent.Contracts.Canary;
using Xunit;

namespace SuavoAgent.Adapters.ComputerRx.Tests;

/// <summary>
/// Compile-time smoke tests. The Computer-Rx adapter is scaffolding only —
/// every meaningful method throws <see cref="NotImplementedException"/>.
/// These tests assert the types are accessible and wired to the contracts
/// project so the solution layout does not silently rot before the real
/// implementation lands.
///
/// DO NOT add behaviour tests here until Tier 1 5b kickoff per Mission memo.
/// </summary>
public sealed class SmokeTests
{
    [Fact]
    public void ComputerRxCanarySource_TypeIsAccessible()
    {
        var t = typeof(ComputerRxCanarySource);
        Assert.NotNull(t);
        Assert.Equal("SuavoAgent.Adapters.ComputerRx.Canary", t.Namespace);
    }

    [Fact]
    public void ComputerRxCanarySource_ImplementsCanaryDetectionSource()
    {
        Assert.True(typeof(ICanaryDetectionSource).IsAssignableFrom(typeof(ComputerRxCanarySource)));
    }

    [Fact]
    public void ComputerRxCanarySource_AdapterType_IsComputerRx()
    {
        var src = new ComputerRxCanarySource();
        Assert.Equal("computerrx", src.AdapterType);
    }

    [Fact]
    public void ComputerRxCanarySource_GetContractBaseline_ThrowsPendingRecon()
    {
        var src = new ComputerRxCanarySource();
        var ex = Assert.Throws<NotImplementedException>(() => src.GetContractBaseline());
        Assert.Contains("Tier 1 5b", ex.Message);
    }
}
