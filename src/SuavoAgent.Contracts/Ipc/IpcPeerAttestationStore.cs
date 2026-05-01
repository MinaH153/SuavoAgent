using System.Text.Json;

namespace SuavoAgent.Contracts.Ipc;

public sealed record IpcPeerAttestationEntry(
    int ProcessId,
    uint SessionId,
    DateTimeOffset LaunchedAt,
    string HelperSha256);

public sealed record IpcPeerAttestationDocument(
    int Version,
    string PipeNonce,
    DateTimeOffset WrittenAt,
    IReadOnlyList<IpcPeerAttestationEntry> Helpers);

public static class IpcPeerAttestationStore
{
    public const int CurrentVersion = 1;
    public const string FileName = "helper-attestations.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string GetDefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            FileName);

    public static void Write(
        string path,
        string pipeNonce,
        IReadOnlyList<IpcPeerAttestationEntry> helpers,
        DateTimeOffset now)
    {
        var doc = new IpcPeerAttestationDocument(
            Version: CurrentVersion,
            PipeNonce: pipeNonce,
            WrittenAt: now,
            Helpers: helpers);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmpPath, JsonSerializer.Serialize(doc, JsonOptions));
        File.Move(tmpPath, path, overwrite: true);
    }

    public static bool ContainsHelper(
        string path,
        string pipeNonce,
        uint processId,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path)) return false;

            var doc = JsonSerializer.Deserialize<IpcPeerAttestationDocument>(
                File.ReadAllText(path),
                JsonOptions);
            if (doc is null) return false;
            if (doc.Version != CurrentVersion) return false;
            if (!string.Equals(doc.PipeNonce, pipeNonce, StringComparison.Ordinal)) return false;
            if (doc.WrittenAt > now.AddMinutes(1)) return false;
            if (now - doc.WrittenAt > maxAge) return false;

            return doc.Helpers.Any(helper =>
                helper.ProcessId == processId &&
                !string.IsNullOrWhiteSpace(helper.HelperSha256) &&
                helper.LaunchedAt <= now.AddMinutes(1));
        }
        catch
        {
            return false;
        }
    }
}
