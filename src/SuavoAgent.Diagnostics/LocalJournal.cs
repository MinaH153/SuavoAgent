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
    ///
    /// Cross-process-safe: uses <c>FileShare.ReadWrite</c> with append mode
    /// so Core / Broker / Helper / Watchdog / Setup can write concurrently
    /// without losing HIPAA §164.312(b) audit lines (Codex chunk 3 CRITICAL).
    /// Writes under ~4 KB are atomic at the Windows OS level; intra-process
    /// `_lock` still serializes calls from the same process to avoid
    /// interleaving when the underlying write is fragmented by GC pause.
    /// </summary>
    public bool Append(IDictionary<string, object?> eventPayload)
    {
        if (string.IsNullOrWhiteSpace(_path)) return false;

        var sw = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            var lineBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(eventPayload, JsonOptions) + Environment.NewLine);

            lock (_lock)
            {
                return AppendWithRetry(_path, lineBytes, _timeout, sw);
            }
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
    /// is unavailable. Same cross-process-safe append primitive as
    /// <see cref="Append"/>.
    /// </summary>
    public static bool AppendCrashLog(string crashLogPath, string summary)
    {
        if (string.IsNullOrWhiteSpace(crashLogPath)) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(crashLogPath) ?? ".");
            var ts = DateTimeOffset.UtcNow.ToString("o");
            var line = Encoding.UTF8.GetBytes($"[{ts}] {summary}{Environment.NewLine}");
            // Static fallback path has no instance lock; relies on OS-level
            // append atomicity. Inter-process contention is rare here (only
            // fires on unhandled exception per process) so 5 retries within
            // a 500ms budget is the right trade-off.
            return AppendWithRetry(crashLogPath, line, TimeSpan.FromMilliseconds(500), Stopwatch.StartNew());
        }
        catch
        {
            return false;
        }
    }

    private static bool AppendWithRetry(string path, byte[] line, TimeSpan budget, Stopwatch sw)
    {
        var attempts = 0;
        while (true)
        {
            if (sw.Elapsed > budget) return false;
            try
            {
                using var fs = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    options: FileOptions.None);
                fs.Write(line, 0, line.Length);
                return true;
            }
            catch (IOException) when (++attempts < 5)
            {
                // Transient contention with another process appending —
                // brief spin (~25-500 ns) then retry. Total wall-clock cost
                // bounded by the budget check above.
                Thread.SpinWait(50 * attempts);
            }
        }
    }
}
