#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using SuavoAgent.Contracts.Adapters;

namespace SuavoAgent.Core.Adapters;

/// <summary>
/// In-memory <see cref="IAdapterRegistry"/>. Normalizes AdapterType keys (trim + lower),
/// rejects duplicate keys, and enforces HIPAA invariant #1 at construction: a config with
/// an empty PHI policy cannot register (defense in depth even if <see cref="PhiPolicy.Create"/>
/// was bypassed via object initializer).
/// </summary>
public sealed class AdapterRegistry : IAdapterRegistry
{
    private readonly Dictionary<string, AdapterConfig> _byType = new(StringComparer.Ordinal);
    private readonly AdapterConfig _default;

    public AdapterRegistry(IEnumerable<AdapterConfig> configs, string defaultAdapterType)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultAdapterType);

        foreach (var cfg in configs)
        {
            ArgumentNullException.ThrowIfNull(cfg);

            if (cfg.Phi.ColumnBlocklist.Count == 0 && cfg.Phi.ColumnPatterns.Count == 0)
                throw new ArgumentException(
                    $"Adapter '{cfg.AdapterType}' has an empty PHI policy; every adapter must declare PHI columns.",
                    nameof(configs));

            var key = Normalize(cfg.AdapterType);
            if (key.Length == 0)
                throw new ArgumentException("AdapterConfig.AdapterType must be non-empty.", nameof(configs));
            if (!_byType.TryAdd(key, cfg))
                throw new ArgumentException($"Duplicate adapter type '{key}'.", nameof(configs));
        }

        if (_byType.Count == 0)
            throw new ArgumentException("At least one adapter config is required.", nameof(configs));

        if (!_byType.TryGetValue(Normalize(defaultAdapterType), out var def))
            throw new ArgumentException(
                $"Default adapter type '{defaultAdapterType}' is not among the registered configs.",
                nameof(defaultAdapterType));
        _default = def;
    }

    public IReadOnlyCollection<AdapterConfig> All => _byType.Values;

    public AdapterConfig Default => _default;

    public AdapterConfig Resolve(string adapterType)
        => TryResolve(adapterType, out var cfg)
            ? cfg
            : throw new AdapterNotRegisteredException(adapterType);

    public bool TryResolve(string adapterType, [NotNullWhen(true)] out AdapterConfig? config)
        => _byType.TryGetValue(Normalize(adapterType), out config);

    private static string Normalize(string? adapterType)
        => (adapterType ?? string.Empty).Trim().ToLowerInvariant();
}
