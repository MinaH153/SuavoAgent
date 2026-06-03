using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Core.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Adapters;

/// <summary>
/// AddAdapterRegistry wires the registry into DI with PioneerRx as the default adapter.
/// This is the composition-root seam that Program.cs calls.
/// </summary>
public class AdapterRegistrationTests
{
    [Fact]
    public void AddAdapterRegistry_RegistersResolvableRegistry_WithPioneerRxDefault()
    {
        using var sp = new ServiceCollection().AddAdapterRegistry().BuildServiceProvider();

        var reg = sp.GetRequiredService<IAdapterRegistry>();

        Assert.Equal("pioneerrx", reg.Default.AdapterType);
        Assert.Equal("PioneerRx", reg.Resolve("pioneerrx").DisplayName);
    }

    [Fact]
    public void AddAdapterRegistry_RegistryIsSingleton()
    {
        using var sp = new ServiceCollection().AddAdapterRegistry().BuildServiceProvider();
        Assert.Same(sp.GetRequiredService<IAdapterRegistry>(), sp.GetRequiredService<IAdapterRegistry>());
    }
}
