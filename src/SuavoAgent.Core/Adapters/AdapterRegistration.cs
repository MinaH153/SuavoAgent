using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Contracts.Adapters;

namespace SuavoAgent.Core.Adapters;

/// <summary>
/// Composition-root registration for the adapter registry. PioneerRx is the default
/// adapter; additional adapter configs (e.g. ComputerRx) are added to the array here —
/// registering a config is all it takes to extend the registry.
/// </summary>
public static class AdapterRegistration
{
    public static IServiceCollection AddAdapterRegistry(this IServiceCollection services)
        => services.AddSingleton<IAdapterRegistry>(_ => new AdapterRegistry(
            new[]
            {
                PioneerRxAdapterConfig.Create(),
            },
            defaultAdapterType: PioneerRxAdapterConfig.AdapterType));
}
