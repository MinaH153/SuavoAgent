using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SuavoAgent.Broker;

public sealed class SessionWatcher : BackgroundService
{
    private readonly ILogger<SessionWatcher> _logger;
    private readonly Dictionary<uint, HelperInfo> _helpers = new();

    private record HelperInfo(int ProcessId, uint SessionId, DateTimeOffset LaunchedAt);

    public SessionWatcher(ILogger<SessionWatcher> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session watcher started — monitoring for interactive sessions");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckActiveSessions();
                CleanupDeadHelpers();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session check error");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void CheckActiveSessions()
    {
        var activeSessionId = GetActiveConsoleSessionId();
        if (activeSessionId == 0xFFFFFFFF)
        {
            _logger.LogDebug("No active console session");
            return;
        }

        if (_helpers.ContainsKey(activeSessionId))
        {
            var info = _helpers[activeSessionId];
            try
            {
                var proc = Process.GetProcessById(info.ProcessId);
                if (!proc.HasExited) return; // Helper still running
                _logger.LogWarning("Helper PID {Pid} for session {Session} has exited",
                    info.ProcessId, activeSessionId);
                _helpers.Remove(activeSessionId);
            }
            catch
            {
                _helpers.Remove(activeSessionId);
            }
        }

        LaunchHelper(activeSessionId);
    }

    private void LaunchHelper(uint sessionId)
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "SuavoAgent.Helper.exe");
        if (!File.Exists(helperPath))
        {
            _logger.LogWarning("Helper not found at {Path}", helperPath);
            return;
        }

        try
        {
            int? pid = null;
            var args = $"--session {sessionId}";

            // Prefer CreateProcessAsUser on Windows — launches Helper in the user's
            // interactive session with their environment and desktop access.
            if (OperatingSystem.IsWindows())
            {
                pid = NativeProcess.LaunchInSession(sessionId, helperPath, args, _logger);
            }

            // Fallback: launch in current session (works for dev/testing)
            if (pid == null)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = helperPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                pid = proc?.Id;
                if (pid != null)
                    _logger.LogInformation("Launched Helper PID {Pid} for session {Session} (fallback)",
                        pid, sessionId);
            }

            if (pid != null)
                _helpers[sessionId] = new HelperInfo(pid.Value, sessionId, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Helper for session {Session}", sessionId);
        }
    }

    private void CleanupDeadHelpers()
    {
        var dead = new List<uint>();
        foreach (var (sessionId, info) in _helpers)
        {
            try
            {
                var proc = Process.GetProcessById(info.ProcessId);
                if (proc.HasExited) dead.Add(sessionId);
            }
            catch { dead.Add(sessionId); }
        }

        foreach (var id in dead)
        {
            _logger.LogInformation("Cleaning up dead Helper for session {Session}", id);
            _helpers.Remove(id);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    private static uint GetActiveConsoleSessionId()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return 0; // Non-Windows fallback
        return WTSGetActiveConsoleSessionId();
    }
}
