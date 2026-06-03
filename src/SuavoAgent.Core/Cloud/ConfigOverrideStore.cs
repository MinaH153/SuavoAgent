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
    private static readonly string[] BlockedExactPaths =
    {
        "Agent.ApiKey",
        "Agent.AgentId",
        "Agent.PharmacyId",
        "Agent.MachineFingerprint",
        "Agent.CloudUrl",
        "Agent.CloudCertPin",
        "Agent.SqlServer",
        "Agent.SqlDatabase",
        "Agent.SqlUser",
        "Agent.SqlPassword",
        "Agent.HmacSalt",
        "Agent.Pharmacies",
        "Agent.ReceiptOnlyMode",
        "Agent.AutoExecution.Enabled",
        "Agent.AutoExecution.WritebackEnabled",
        "AutoExecution.Enabled",
        "AutoExecution.WritebackEnabled",
    };

    private static readonly string[] BlockedPrefixes =
    {
        "Agent.Pharmacies.",
    };

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
            var path = ov.Path?.Trim();
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (IsBlockedOverridePath(path))
            {
                _logger.LogWarning(
                    "ConfigOverrideStore: rejected protected override path {Path}",
                    path);
                continue;
            }
            if (!IsSafeOverrideValue(path, ov.Value))
            {
                _logger.LogWarning(
                    "ConfigOverrideStore: rejected unsafe override value for {Path}",
                    path);
                continue;
            }

            var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) continue;
            Insert(root, segments, ov.Value);
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
        using (var fs = new FileStream(
            tmp,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough))
        using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            writer.Write(payload);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, Path, overwrite: true);
        _logger.LogInformation(
            "ConfigOverrideStore: wrote {Count} override(s) to {Path}",
            overrides.Count, Path);
        return true;
    }

    private static bool IsBlockedOverridePath(string path)
    {
        if (BlockedExactPaths.Any(blocked =>
                string.Equals(path, blocked, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return BlockedPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeOverrideValue(string path, JsonElement value)
    {
        if (NumericBounds.TryGetValue(path, out var bounds))
        {
            if (bounds.IntegerOnly)
            {
                if (!value.TryGetInt64(out var integer))
                    return false;
                return integer >= bounds.Min && integer <= bounds.Max;
            }

            if (!value.TryGetDouble(out var number))
                return false;
            return number >= bounds.Min && number <= bounds.Max;
        }

        if (BooleanOnlyPaths.Contains(path))
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;

        return true;
    }

    private readonly record struct NumericBound(double Min, double Max, bool IntegerOnly = true);

    private static readonly Dictionary<string, NumericBound> NumericBounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Agent.HeartbeatIntervalSeconds"] = new(10, 3600),
        ["Agent.HeartbeatJitterSeconds"] = new(0, 300),
        ["Agent.MaxDetectionBatchSize"] = new(1, 500),
        ["Agent.ReceiptRetentionDays"] = new(730, 3650),
        ["Vision.RetentionHours"] = new(0, 168),
        ["Vision.MaxStoredScreens"] = new(1, 5000),
        ["Vision.MinIntervalMs"] = new(250, 60000),
        ["Vision.PeriodicCapture.IntervalSeconds"] = new(5, 3600),
        ["Vision.Tesseract.MinConfidence"] = new(0, 100),
        ["Vision.Tesseract.IdleUnloadSeconds"] = new(0, 3600),
        ["Vision.Tesseract.MemoryHeadroomBytes"] = new(64L * 1024 * 1024, 4L * 1024 * 1024 * 1024),
        ["Vision.Tesseract.ExtractionTimeoutSeconds"] = new(1, 120),
    };

    private static readonly HashSet<string> BooleanOnlyPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "Agent.LearningMode",
        "Agent.SqlTrustServerCertificate",
        "Agent.TemplateLearning.Enabled",
        "Agent.TemplateLearning.RuleGeneration",
        "Agent.TemplateLearning.AutoApproveOnFingerprintMatch",
        "Agent.TestHooks.Enabled",
        "Vision.Enabled",
        "Vision.PeriodicCapture.Enabled",
        "Vision.PeriodicCapture.RequireForegroundMatch",
        "Vision.Tesseract.Enabled",
    };

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
