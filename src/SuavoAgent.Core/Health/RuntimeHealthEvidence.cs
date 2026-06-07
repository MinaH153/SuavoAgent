using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.Health;

public sealed record RuntimeHealthEvidencePayload(
    [property: JsonPropertyName("configSync")]
    ConfigSyncHealthPayload ConfigSync,
    [property: JsonPropertyName("crashLogs")]
    IReadOnlyList<CrashLogEvidencePayload> CrashLogs,
    [property: JsonPropertyName("cloudAuth")]
    CloudAuthHealthPayload CloudAuth,
    [property: JsonPropertyName("update")]
    UpdateHealthPayload Update,
    [property: JsonPropertyName("install")]
    InstallHealthPayload Install);

public sealed record ConfigSyncHealthPayload(
    [property: JsonPropertyName("present")]
    bool Present,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("lastAttemptAt")]
    string? LastAttemptAt,
    [property: JsonPropertyName("lastSuccessAt")]
    string? LastSuccessAt,
    [property: JsonPropertyName("consecutiveFailures")]
    int ConsecutiveFailures,
    [property: JsonPropertyName("lastErrorKind")]
    string? LastErrorKind,
    [property: JsonPropertyName("lastAppliedOverrideCount")]
    int LastAppliedOverrideCount);

public sealed record CrashLogEvidencePayload(
    [property: JsonPropertyName("component")]
    string Component,
    [property: JsonPropertyName("exists")]
    bool Exists,
    [property: JsonPropertyName("bytes")]
    long Bytes,
    [property: JsonPropertyName("lastWriteUtc")]
    string? LastWriteUtc,
    [property: JsonPropertyName("sha256Prefix")]
    string? Sha256Prefix);

public sealed record CloudAuthHealthPayload(
    [property: JsonPropertyName("present")]
    bool Present,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("lastAttemptAt")]
    string? LastAttemptAt,
    [property: JsonPropertyName("lastSuccessAt")]
    string? LastSuccessAt,
    [property: JsonPropertyName("consecutiveFailures")]
    int ConsecutiveFailures,
    [property: JsonPropertyName("lastErrorKind")]
    string? LastErrorKind,
    [property: JsonPropertyName("recoveryAttempted")]
    bool RecoveryAttempted,
    [property: JsonPropertyName("recoveryOutcome")]
    string? RecoveryOutcome,
    [property: JsonPropertyName("restartRequested")]
    bool RestartRequested);

public sealed record UpdateHealthPayload(
    [property: JsonPropertyName("present")]
    bool Present,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("targetVersion")]
    string? TargetVersion,
    [property: JsonPropertyName("lastAttemptAt")]
    string? LastAttemptAt,
    [property: JsonPropertyName("lastSuccessAt")]
    string? LastSuccessAt,
    [property: JsonPropertyName("consecutiveFailures")]
    int ConsecutiveFailures,
    [property: JsonPropertyName("lastErrorKind")]
    string? LastErrorKind,
    [property: JsonPropertyName("channel")]
    string? Channel);

public sealed record InstallHealthPayload(
    [property: JsonPropertyName("present")]
    bool Present,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("binaries")]
    IReadOnlyList<InstallBinaryEvidencePayload> Binaries,
    [property: JsonPropertyName("receiptSha256")]
    string? ReceiptSha256);

public sealed record InstallBinaryEvidencePayload(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("exists")]
    bool Exists,
    [property: JsonPropertyName("bytes")]
    long Bytes,
    [property: JsonPropertyName("sha256")]
    string? Sha256,
    [property: JsonPropertyName("sha256Prefix")]
    string? Sha256Prefix);

public static class RuntimeHealthEvidence
{
    private static readonly string[] RequiredInstallBinaries =
    {
        "SuavoAgent.Core.exe",
        "SuavoAgent.Broker.exe",
        "SuavoAgent.Helper.exe",
        "SuavoAgent.Watchdog.exe",
        "SuavoSetup.exe",
    };

    public static string ProgramDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");

    public static string ConfigSyncHealthPath(string? root = null) =>
        Path.Combine(root ?? ProgramDataRoot, "config-sync-health.json");

    public static string LegacyConfigSyncHealthPath(string? root = null) =>
        Path.Combine(root ?? ProgramDataRoot, "config sync health.json");

    public static string CloudAuthHealthPath(string? root = null) =>
        Path.Combine(root ?? ProgramDataRoot, "cloud-auth-health.json");

    public static string UpdateHealthPath(string? root = null) =>
        Path.Combine(root ?? ProgramDataRoot, "update-health.json");

    public static string LogsDirectory(string? root = null) =>
        Path.Combine(root ?? ProgramDataRoot, "logs");

    public static RuntimeHealthEvidencePayload Collect(string? root = null, string? installDir = null) =>
        new(
            ReadConfigSyncHealth(ResolveConfigSyncHealthPath(root)),
            ReadCrashLogs(LogsDirectory(root)),
            ReadCloudAuthHealth(CloudAuthHealthPath(root)),
            ReadUpdateHealth(UpdateHealthPath(root)),
            ReadInstallHealth(installDir ?? AppContext.BaseDirectory));

    private static string ResolveConfigSyncHealthPath(string? root)
    {
        var canonical = ConfigSyncHealthPath(root);
        return File.Exists(canonical)
            ? canonical
            : LegacyConfigSyncHealthPath(root);
    }

    public static ConfigSyncHealthPayload ReadConfigSyncHealth(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new(
                    Present: false,
                    Status: "missing",
                    LastAttemptAt: null,
                    LastSuccessAt: null,
                    ConsecutiveFailures: 0,
                    LastErrorKind: null,
                    LastAppliedOverrideCount: 0);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new(
                Present: true,
                Status: ReadString(root, "status") ?? "unknown",
                LastAttemptAt: ReadString(root, "lastAttemptAt"),
                LastSuccessAt: ReadNullableString(root, "lastSuccessAt"),
                ConsecutiveFailures: ReadInt(root, "consecutiveFailures"),
                LastErrorKind: ReadNullableString(root, "lastErrorKind"),
                LastAppliedOverrideCount: ReadInt(root, "lastAppliedOverrideCount"));
        }
        catch
        {
            return new(
                Present: true,
                Status: "unreadable",
                LastAttemptAt: null,
                LastSuccessAt: null,
                ConsecutiveFailures: 0,
                LastErrorKind: "health_file_unreadable",
                LastAppliedOverrideCount: 0);
        }
    }

    public static IReadOnlyList<CrashLogEvidencePayload> ReadCrashLogs(string logsDir)
    {
        return new[]
        {
            ReadCrashLog("core", Path.Combine(logsDir, "startup-crash.log")),
            ReadCrashLog("broker", Path.Combine(logsDir, "broker-crash.log")),
            ReadCrashLog("watchdog", Path.Combine(logsDir, "watchdog-crash.log")),
        };
    }

    public static CloudAuthHealthPayload ReadCloudAuthHealth(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new(
                    Present: false,
                    Status: "missing",
                    LastAttemptAt: null,
                    LastSuccessAt: null,
                    ConsecutiveFailures: 0,
                    LastErrorKind: null,
                    RecoveryAttempted: false,
                    RecoveryOutcome: null,
                    RestartRequested: false);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new(
                Present: true,
                Status: ReadString(root, "status") ?? "unknown",
                LastAttemptAt: ReadString(root, "lastAttemptAt"),
                LastSuccessAt: ReadNullableString(root, "lastSuccessAt"),
                ConsecutiveFailures: ReadInt(root, "consecutiveFailures"),
                LastErrorKind: ReadNullableString(root, "lastErrorKind"),
                RecoveryAttempted: ReadBool(root, "recoveryAttempted"),
                RecoveryOutcome: ReadNullableString(root, "recoveryOutcome"),
                RestartRequested: ReadBool(root, "restartRequested"));
        }
        catch
        {
            return new(
                Present: true,
                Status: "unreadable",
                LastAttemptAt: null,
                LastSuccessAt: null,
                ConsecutiveFailures: 0,
                LastErrorKind: "health_file_unreadable",
                RecoveryAttempted: false,
                RecoveryOutcome: null,
                RestartRequested: false);
        }
    }

    public static UpdateHealthPayload ReadUpdateHealth(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new(
                    Present: false,
                    Status: "missing",
                    TargetVersion: null,
                    LastAttemptAt: null,
                    LastSuccessAt: null,
                    ConsecutiveFailures: 0,
                    LastErrorKind: null,
                    Channel: null);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new(
                Present: true,
                Status: ReadString(root, "status") ?? "unknown",
                TargetVersion: ReadNullableString(root, "targetVersion"),
                LastAttemptAt: ReadString(root, "lastAttemptAt"),
                LastSuccessAt: ReadNullableString(root, "lastSuccessAt"),
                ConsecutiveFailures: ReadInt(root, "consecutiveFailures"),
                LastErrorKind: ReadNullableString(root, "lastErrorKind"),
                Channel: ReadNullableString(root, "channel"));
        }
        catch
        {
            return new(
                Present: true,
                Status: "unreadable",
                TargetVersion: null,
                LastAttemptAt: null,
                LastSuccessAt: null,
                ConsecutiveFailures: 0,
                LastErrorKind: "health_file_unreadable",
                Channel: null);
        }
    }

    public static InstallHealthPayload ReadInstallHealth(string installDir)
    {
        var binaries = RequiredInstallBinaries
            .Select(name => ReadInstallBinary(name, Path.Combine(installDir, name)))
            .ToArray();
        var present = binaries.Any(binary => binary.Exists);
        var complete = binaries.All(binary => binary.Exists && binary.Sha256 is { Length: 64 });
        var unreadable = binaries.Any(binary => binary.Sha256Prefix == "unreadable");
        var status = unreadable
            ? "unreadable"
            : complete
                ? "ok"
                : present
                    ? "partial"
                    : "missing";
        var receiptSha256 = complete
            ? ReceiptSha256(binaries)
            : null;

        return new(
            Present: present,
            Status: status,
            Binaries: binaries,
            ReceiptSha256: receiptSha256);
    }

    private static CrashLogEvidencePayload ReadCrashLog(string component, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new(
                    Component: component,
                    Exists: false,
                    Bytes: 0,
                    LastWriteUtc: null,
                    Sha256Prefix: null);
            }

            var hash = Sha256Hex(path);
            return new(
                Component: component,
                Exists: true,
                Bytes: info.Length,
                LastWriteUtc: info.LastWriteTimeUtc.ToString("o"),
                Sha256Prefix: hash[..16]);
        }
        catch
        {
            return new(
                Component: component,
                Exists: true,
                Bytes: 0,
                LastWriteUtc: null,
                Sha256Prefix: "unreadable");
        }
    }

    private static InstallBinaryEvidencePayload ReadInstallBinary(string name, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new(
                    Name: name,
                    Exists: false,
                    Bytes: 0,
                    Sha256: null,
                    Sha256Prefix: null);
            }

            var hash = Sha256Hex(path);
            return new(
                Name: name,
                Exists: true,
                Bytes: info.Length,
                Sha256: hash,
                Sha256Prefix: hash[..16]);
        }
        catch
        {
            return new(
                Name: name,
                Exists: true,
                Bytes: 0,
                Sha256: null,
                Sha256Prefix: "unreadable");
        }
    }

    public static void WriteConfigSyncHealth(
        string path,
        string status,
        DateTimeOffset lastAttemptAt,
        DateTimeOffset? lastSuccessAt,
        int consecutiveFailures,
        string? lastErrorKind,
        int lastAppliedOverrideCount)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var payload = new
        {
            status,
            lastAttemptAt = lastAttemptAt.ToString("o"),
            lastSuccessAt = lastSuccessAt?.ToString("o"),
            consecutiveFailures,
            lastErrorKind,
            lastAppliedOverrideCount,
        };

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteCloudAuthHealth(
        string path,
        string status,
        DateTimeOffset lastAttemptAt,
        DateTimeOffset? lastSuccessAt,
        int consecutiveFailures,
        string? lastErrorKind,
        bool recoveryAttempted,
        string? recoveryOutcome,
        bool restartRequested)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var payload = new
        {
            status,
            lastAttemptAt = lastAttemptAt.ToString("o"),
            lastSuccessAt = lastSuccessAt?.ToString("o"),
            consecutiveFailures,
            lastErrorKind,
            recoveryAttempted,
            recoveryOutcome,
            restartRequested,
        };

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteUpdateHealth(
        string path,
        string status,
        string? targetVersion,
        DateTimeOffset lastAttemptAt,
        DateTimeOffset? lastSuccessAt,
        int consecutiveFailures,
        string? lastErrorKind,
        string? channel)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var payload = new
        {
            status,
            targetVersion,
            lastAttemptAt = lastAttemptAt.ToString("o"),
            lastSuccessAt = lastSuccessAt?.ToString("o"),
            consecutiveFailures,
            lastErrorKind,
            channel,
        };

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// True when a stuck <c>"applying"</c> update record should be finalized as succeeded — i.e. the
    /// agent is now running the very version the update targeted. A clean self-update writes
    /// <c>"applying"</c> then exits the process; on restart nothing flips it to success, so
    /// <c>update-health.json</c> (and the cloud rollout that reads it) stays <c>"applying"</c> forever
    /// even though the new binary is live and healthy. The first healthy heartbeat on the new binary is
    /// the milestone that closes the loop. Version comparison is prefix-normalized because the agent
    /// reports <c>"3.24.0"</c> while manifests/tags may carry <c>"v3.24.0"</c>.
    /// </summary>
    public static bool IsCompletedApplyingUpdate(UpdateHealthPayload payload, string? runningVersion)
    {
        if (payload is null ||
            !string.Equals(payload.Status, "applying", StringComparison.OrdinalIgnoreCase))
            return false;

        var target = AgentVersion.Normalize(payload.TargetVersion);
        var running = AgentVersion.Normalize(runningVersion);
        return !string.IsNullOrWhiteSpace(target)
            && !string.IsNullOrWhiteSpace(running)
            && string.Equals(target, running, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ReceiptSha256(IEnumerable<InstallBinaryEvidencePayload> binaries)
    {
        var canonical = string.Join(
            "\u001f",
            binaries.Select(binary => $"{binary.Name}:{binary.Bytes}:{binary.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static string? ReadString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadNullableString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int ReadInt(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static bool ReadBool(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.True;
    }
}
