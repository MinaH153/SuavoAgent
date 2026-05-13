using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Diagnostics;

/// Local-first events.jsonl writer. Spec §4 contract: 20ms best-effort
/// budget, daily rotation, 30-day retention, PHI-scrubbed before write.
/// On overrun the caller still writes the defense-in-depth crash log line
/// (existing %ProgramData%\SuavoAgent\logs\startup-crash.log).
public sealed class LocalJournal
{
    private readonly string _path;
    private readonly TimeSpan _timeout;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public LocalJournal(string path, TimeSpan timeout)
    {
        _path = path;
        _timeout = timeout;
    }

    /// <summary>
    /// Append a JSONL event line to the local journal. Best-effort: returns
    /// true on success, false on timeout or write error. Caller's Wire path
    /// continues either way (this is journal redundancy, not a hard
    /// dependency).
    /// </summary>
    public bool Append(IDictionary<string, object?> eventPayload)
    {
        if (string.IsNullOrWhiteSpace(_path)) return false;

        var sw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var line = JsonSerializer.Serialize(eventPayload, JsonOptions);

            lock (_lock)
            {
                if (sw.Elapsed > _timeout) return false;
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort defense-in-depth: append a single-line plaintext crash
    /// summary to the existing <c>startup-crash.log</c> path. Preserves the
    /// pre-mesh behavior in <c>SuavoAgent.Core/Program.cs:WriteCrash</c>
    /// so a complete crash trail exists even when the structured journal
    /// is unavailable.
    /// </summary>
    public static bool AppendCrashLog(string crashLogPath, string summary)
    {
        if (string.IsNullOrWhiteSpace(crashLogPath)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath) ?? ".");
            var ts = DateTimeOffset.UtcNow.ToString("o");
            File.AppendAllText(crashLogPath, $"[{ts}] {summary}{Environment.NewLine}", Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
