using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.ActionGrammarV1;

/// <summary>
/// Discovers <see cref="IVerb"/> implementations via reflection and
/// publishes their <c>SHA-256(manifest)</c> hashes to the dispatcher /
/// audit chain. The registry is the trust root the cloud and the local
/// dispatcher both consult before allowing a verb to execute.
///
/// Anti-CrowdStrike property: a workflow run record from the cloud
/// includes the expected manifest hash for every verb it intends to call.
/// At dispatch time the registry compares against the locally computed
/// hash; if they differ the run is REJECTED with
/// <c>verb_manifest_mismatch</c>. There is no "best effort" fallback.
/// </summary>
public sealed class VerbRegistry
{
    private readonly Dictionary<string, RegistryEntry> _byName;
    private readonly ILogger<VerbRegistry> _logger;

    public sealed record RegistryEntry(
        string Name,
        string Version,
        VerbMetadata Metadata,
        Type ImplementationType,
        string ManifestHash);

    private VerbRegistry(Dictionary<string, RegistryEntry> byName, ILogger<VerbRegistry> logger)
    {
        _byName = byName;
        _logger = logger;
    }

    public IReadOnlyCollection<RegistryEntry> All => _byName.Values;

    /// <summary>Build a registry by scanning the supplied assemblies for IVerb types.</summary>
    public static VerbRegistry Build(IEnumerable<Assembly> assemblies, ILogger<VerbRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(logger);

        var entries = new Dictionary<string, RegistryEntry>(StringComparer.Ordinal);
        foreach (var asm in assemblies.Distinct())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                logger.LogWarning(ex, "VerbRegistry: failed to load some types from {Assembly}", asm.FullName);
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IVerb).IsAssignableFrom(t)) continue;
                if (t.GetConstructor(Type.EmptyTypes) is null) continue;

                IVerb instance;
                try { instance = (IVerb)Activator.CreateInstance(t)!; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "VerbRegistry: cannot instantiate {Type} (skipping)", t.FullName);
                    continue;
                }

                var meta = instance.Metadata;
                var hash = ComputeManifestHash(meta);
                var entry = new RegistryEntry(meta.Name, meta.Version, meta, t, hash);

                if (entries.ContainsKey(meta.Name))
                {
                    logger.LogError("VerbRegistry: duplicate verb name '{Name}' from {Type} (already registered)", meta.Name, t.FullName);
                    continue;
                }
                entries[meta.Name] = entry;
            }
        }

        logger.LogInformation("VerbRegistry: registered {Count} verbs", entries.Count);
        return new VerbRegistry(entries, logger);
    }

    /// <summary>
    /// Resolve a verb by name and verify its manifest hash matches the
    /// caller's expectation. Returns null with the reason populated when
    /// the registry is missing the verb or the hash mismatches.
    /// </summary>
    public RegistryEntry? Resolve(string name, string? expectedManifestHash, out string? failureReason)
    {
        if (!_byName.TryGetValue(name, out var entry))
        {
            failureReason = $"verb '{name}' not registered";
            return null;
        }
        if (expectedManifestHash is not null && !string.Equals(expectedManifestHash, entry.ManifestHash, StringComparison.Ordinal))
        {
            failureReason = $"manifest_hash_mismatch expected={expectedManifestHash} actual={entry.ManifestHash}";
            return null;
        }
        failureReason = null;
        return entry;
    }

    public IVerb Instantiate(RegistryEntry entry, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(services);
        return (IVerb)ActivatorUtilities.CreateInstance(services, entry.ImplementationType);
    }

    /// <summary>
    /// SHA-256 of the verb's canonical manifest (sorted JSON of name, version,
    /// risk tier, BAA scope, mutation/destructive flags, max execution time,
    /// param + output schema). Stable across processes and machines.
    /// </summary>
    public static string ComputeManifestHash(VerbMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = metadata.Name,
            ["version"] = metadata.Version,
            ["risk_tier"] = metadata.RiskTier.ToString(),
            ["baa_scope"] = metadata.BaaScope switch
            {
                VerbBaaScope.None => "none",
                VerbBaaScope.AgentBaa => "agent_baa",
                VerbBaaScope.BaaAmendment a => $"baa_amendment:{a.AmendmentId}",
                VerbBaaScope.Forbidden => "forbidden",
                _ => "unknown",
            },
            ["is_mutation"] = metadata.IsMutation,
            ["is_destructive"] = metadata.IsDestructive,
            ["max_execution_ms"] = (long)metadata.MaxExecutionTime.TotalMilliseconds,
            ["params"] = metadata.Params.Parameters.Select(p => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = p.Name,
                ["clr_type"] = p.ClrType.FullName ?? p.ClrType.Name,
                ["required"] = p.Required,
            }).ToList(),
            ["output"] = metadata.Output.Fields.Select(o => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = o.Name,
                ["clr_type"] = o.ClrType.FullName ?? o.ClrType.Name,
            }).ToList(),
        };
        var json = JsonSerializer.Serialize(canonical);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
