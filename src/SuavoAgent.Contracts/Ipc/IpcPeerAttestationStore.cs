using System.Text.Json;
using System.Text;

namespace SuavoAgent.Contracts.Ipc;

public sealed record IpcPeerAttestationEntry(
    int ProcessId,
    uint SessionId,
    DateTimeOffset LaunchedAt,
    DateTimeOffset ProcessStartedAtUtc,
    string HelperSha256);

public sealed record IpcPeerAttestationDocument(
    int Version,
    string PipeNonce,
    DateTimeOffset WrittenAt,
    IReadOnlyList<IpcPeerAttestationEntry> Helpers);

public static class IpcPeerAttestationStore
{
    public const int CurrentVersion = 2;
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
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(doc, JsonOptions));
        try
        {
            using (var stream = new FileStream(
                       tmpPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmpPath, path, overwrite: true);
        }
        finally
        {
            Array.Clear(bytes);
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    public static bool ContainsHelper(
        string path,
        string pipeNonce,
        uint processId,
        uint sessionId,
        DateTimeOffset processStartedAtUtc,
        string currentHelperSha256,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (currentHelperSha256.Length != 64 ||
                currentHelperSha256.Any(character => !Uri.IsHexDigit(character)))
                return false;

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
                helper.SessionId == sessionId &&
                helper.ProcessStartedAtUtc == processStartedAtUtc &&
                helper.HelperSha256.Length == 64 &&
                string.Equals(
                    helper.HelperSha256,
                    currentHelperSha256,
                    StringComparison.OrdinalIgnoreCase) &&
                helper.LaunchedAt <= now.AddMinutes(1) &&
                helper.ProcessStartedAtUtc <= now.AddMinutes(1));
        }
        catch
        {
            return false;
        }
    }
}
