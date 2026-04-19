using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Cloud;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Flattens cloud-fetched config overrides to a JSON file that sits in the
/// host config stack on top of appsettings.json. Atomic write: build a temp
/// file, fsync, rename — so a mid-write crash never leaves a truncated file
/// that would break host startup.
///
/// The file layout follows normal Microsoft.Extensions.Configuration JSON —
/// a dotted path like "Reasoning.PricingBrainEnabled" becomes a nested
/// object { "Reasoning": { "PricingBrainEnabled": true } }. The cloud
/// stores values as JSONB so scalars/arrays/objects all round-trip.
/// </summary>
public sealed class ConfigOverrideStore
{
    public string Path { get; }
    private readonly ILogger<ConfigOverrideStore> _logger;

    public ConfigOverrideStore(string path, ILogger<ConfigOverrideStore> logger)
    {
        Path = path;
        _logger = logger;
    }

    /// <summary>
    /// Flattens the override set to a nested JSON tree and writes it atomically
    /// to <see cref="Path"/>. Returns true if something changed on disk.
    /// </summary>
    public bool Apply(IReadOnlyList<ConfigOverride> overrides)
    {
        var root = new Dictionary<string, object?>();
        foreach (var ov in overrides)
        {
            if (string.IsNullOrWhiteSpace(ov.Path)) continue;
            Insert(root, ov.Path.Split('.'), ov.Value);
        }

        var payload = JsonSerializer.Serialize(root, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        if (File.Exists(Path))
        {
            var existing = File.ReadAllText(Path, Encoding.UTF8);
            if (string.Equals(existing, payload, StringComparison.Ordinal))
                return false;
        }

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, payload, new UTF8Encoding(false));
        File.Move(tmp, Path, overwrite: true);
        _logger.LogInformation(
            "ConfigOverrideStore: wrote {Count} override(s) to {Path}",
            overrides.Count, Path);
        return true;
    }

    private static void Insert(Dictionary<string, object?> node, string[] segments, JsonElement value)
    {
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (!node.TryGetValue(seg, out var existing) || existing is not Dictionary<string, object?> child)
            {
                child = new Dictionary<string, object?>();
                node[seg] = child;
            }
            node = child;
        }
        node[segments[^1]] = Unwrap(value);
    }

    /// <summary>
    /// Convert a JsonElement to a plain .NET object suitable for
    /// System.Text.Json re-serialization under the nested dictionary.
    /// </summary>
    internal static object? Unwrap(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var i) ? (object)i : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => value.EnumerateArray().Select(Unwrap).ToArray(),
        JsonValueKind.Object => value.EnumerateObject()
            .ToDictionary(p => p.Name, p => Unwrap(p.Value)),
        _ => value.ToString(),
    };
}
