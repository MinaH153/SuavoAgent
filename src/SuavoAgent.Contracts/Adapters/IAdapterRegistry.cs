#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SuavoAgent.Contracts.Adapters;

/// <summary>
/// Resolves <see cref="AdapterConfig"/> by PMS-family type. Implementations normalize
/// the lookup key (trim + lower-case) and enforce the HIPAA invariant that every
/// registered adapter carries a non-empty <see cref="PhiPolicy"/>.
/// </summary>
public interface IAdapterRegistry
{
    IReadOnlyCollection<AdapterConfig> All { get; }

    /// <summary>The fallback adapter (e.g. "pioneerrx") for single-PMS / top-level config.</summary>
    AdapterConfig Default { get; }

    /// <summary>Throws <see cref="AdapterNotRegisteredException"/> when no config matches.</summary>
    AdapterConfig Resolve(string adapterType);

    bool TryResolve(string adapterType, [NotNullWhen(true)] out AdapterConfig? config);
}
