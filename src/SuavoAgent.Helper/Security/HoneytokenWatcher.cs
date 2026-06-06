using System;
using System.IO;
using Serilog;

namespace SuavoAgent.Helper.Security;

/// <summary>
/// The OS shell of the immune reflex: a <see cref="FileSystemWatcher"/> on the honeytoken directory that
/// forwards every touch to <see cref="HoneytokenReflex"/>. Deliberately thin — all logic + state live in the
/// reflex (unit-tested headless). FAIL-OPEN: if the watcher can't arm, the Helper logs a WARNING and runs on;
/// a disarmed reflex must never crash a live pharmacy's actuation Helper.
/// </summary>
public sealed class HoneytokenWatcher : IDisposable
{
    private readonly string _dir;
    private readonly HoneytokenReflex _reflex;
    private readonly ILogger _logger;
    private FileSystemWatcher? _fsw;

    public HoneytokenWatcher(string honeytokenDir, HoneytokenReflex reflex, ILogger logger)
    {
        _dir = honeytokenDir;
        _reflex = reflex;
        _logger = logger.ForContext<HoneytokenWatcher>();
    }

    public void Start()
    {
        try
        {
            Directory.CreateDirectory(_dir); // ensure the watch dir exists (Broker seeds the decoy file into it)
            var fsw = new FileSystemWatcher(_dir)
            {
                NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName
                             | NotifyFilters.Security | NotifyFilters.Size | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            fsw.Changed += OnEvent;
            fsw.Created += OnEvent;
            fsw.Renamed += OnEvent;
            fsw.Deleted += OnEvent;
            fsw.Error += (_, e) => _logger.Warning(e.GetException(), "HoneytokenWatcher FSW error");
            _fsw = fsw;
            _logger.Information("HoneytokenWatcher armed on {Dir}", _dir);
        }
        catch (Exception ex)
        {
            // FAIL-OPEN: the immune reflex is best-effort; never block the Helper from running.
            _logger.Warning(ex, "HoneytokenWatcher failed to start — immune reflex disarmed (non-fatal)");
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e) => _reflex.OnTouch(e.FullPath);

    public void Dispose()
    {
        try { _fsw?.Dispose(); } catch { /* best-effort */ }
    }
}
