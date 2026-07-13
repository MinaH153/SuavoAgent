using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.State;

/// <summary>
/// Machine-DPAPI protected local correlation between one hash-only cloud candidate and the
/// raw PioneerRx lookup key. The raw Rx number exists only in memory and inside a DPAPI blob;
/// JSON metadata is PHI-free. The store is bounded, TTL-pruned, atomically replaced, and keeps
/// terminal tombstones so a replayed command cannot cause a second patient lookup.
/// </summary>
internal sealed partial class RxCorrelationStore : IRxCorrelationStore
{
    internal static readonly TimeSpan DefaultObservationTtl = TimeSpan.FromHours(24);
    internal const int DefaultMaxEntries = 4096;
    internal const int MaxPatientFetchAttempts = 12;
    internal static readonly TimeSpan PatientFetchAuthorizationTtl = TimeSpan.FromHours(4);
    private const int SchemaVersion = 1;
    private const int MaxStoreBytes = 2 * 1024 * 1024;
    private const int MaxIdentityLength = 200;
    private const int MaxProtectedRxBytes = 4096;
    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    private readonly string _filePath;
    private readonly IRxCorrelationProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly int _maxEntries;
    private readonly bool _requireProductionBoundary;
    private readonly object _gate;

    internal RxCorrelationStore(
        string filePath,
        IRxCorrelationProtector protector,
        TimeProvider? timeProvider = null,
        TimeSpan? ttl = null,
        int maxEntries = DefaultMaxEntries,
        bool requireProductionBoundary = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(protector);
        if (maxEntries <= 0 || maxEntries > DefaultMaxEntries)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));

        var effectiveTtl = ttl ?? DefaultObservationTtl;
        if (effectiveTtl <= TimeSpan.Zero || effectiveTtl > DefaultObservationTtl)
            throw new ArgumentOutOfRangeException(nameof(ttl));

        _filePath = Path.GetFullPath(filePath);
        _protector = protector;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = effectiveTtl;
        _maxEntries = maxEntries;
        _requireProductionBoundary = requireProductionBoundary;
        _gate = PathGates.GetOrAdd(_filePath, static _ => new object());
    }

    [SupportedOSPlatform("windows")]
    internal static RxCorrelationStore CreateProduction()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The production Rx correlation store requires Windows DPAPI.");

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "rx-correlations.v1.json");
        return new RxCorrelationStore(
            path,
            new DpapiRxCorrelationProtector(),
            requireProductionBoundary: true);
    }

}
