using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Observes process lifecycle on the pharmacy machine. Uses Process.GetProcesses()
/// as the baseline (cross-platform). ETW subscription added when running as a
/// Windows service for real-time events.
///
/// Window titles are collected via Helper IPC (Session 0 cannot access user UI).
/// This observer only records process names, paths, and service status.
/// </summary>
public sealed class ProcessObserver : ILearningObserver
{
    public static readonly Dictionary<string, string> KnownPmsSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PioneerPharmacy.exe"] = "PioneerRx",
        ["QS1NexGen.exe"] = "QS/1 NexGen",
        ["NexGen.exe"] = "QS/1 NexGen",
        ["LibertyRx.exe"] = "Liberty Software",
        ["ComputerRx.exe"] = "Computer-Rx",
        ["BestRx.exe"] = "BestRx",
        ["Rx30.exe"] = "Rx30",
        ["Pharmaserv.exe"] = "McKesson Pharmaserv",
        ["FrameworkLTC.exe"] = "FrameworkLTC",
        ["ScriptPro.exe"] = "ScriptPro",
    };

    private readonly AgentStateDb _db;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _running;
    private int _eventsCollected;
    private int _phiScrubCount;
    private DateTimeOffset _lastActivity;

    public string Name => "process";
    public ObserverPhase ActivePhases => ObserverPhase.All;

    public ProcessObserver(AgentStateDb db, string pharmacySalt, ILogger<ProcessObserver> logger)
    {
        _db = db;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
    }

    public static bool IsPmsCandidate(string processName) =>
        KnownPmsSignatures.ContainsKey(processName);

    public void RecordProcess(string sessionId, string processName, string exePath,
        string? windowTitle = null)
    {
        var scrubbed = PhiScrubber.ScrubText(windowTitle);
        var titleHash = windowTitle != null ? PhiScrubber.HmacHash(windowTitle, _pharmacySalt) : null;
        var isPms = IsPmsCandidate(processName);

        if (windowTitle != null && scrubbed != windowTitle)
            _phiScrubCount++;

        _db.UpsertObservedProcess(sessionId, processName, exePath,
            windowTitleScrubbed: scrubbed, isPmsCandidate: isPms,
            windowTitleHash: titleHash);

        _db.AppendLearningAudit(sessionId, "process", "scan", processName,
            phiScrubbed: scrubbed != windowTitle);

        _eventsCollected++;
        _lastActivity = DateTimeOffset.UtcNow;
    }

    public async Task StartAsync(string sessionId, CancellationToken ct)
    {
        _running = true;
        _logger.LogInformation("ProcessObserver started for session {Session}", sessionId);

        // Initial snapshot of all running processes
        ScanCurrentProcesses(sessionId);

        // Periodic re-scan (ETW is added in a future task for real-time events)
        while (!ct.IsCancellationRequested && _running)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            ScanCurrentProcesses(sessionId);
        }
    }

    private void ScanCurrentProcesses(string sessionId)
    {
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName + ".exe";
                    var path = "";
                    try { path = proc.MainModule?.FileName ?? ""; } catch { }
                    // Window title collected via Helper IPC, not here (Session 0)
                    RecordProcess(sessionId, name, path);
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process scan failed");
        }
    }

    public Task StopAsync()
    {
        _running = false;
        return Task.CompletedTask;
    }

    public ObserverHealth CheckHealth() => new(
        Name, _running, _eventsCollected, _phiScrubCount, _lastActivity);

    public void Dispose() { _running = false; }
}
