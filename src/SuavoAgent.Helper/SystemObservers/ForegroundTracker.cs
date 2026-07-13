using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

internal sealed record ForegroundWindowContext(
    string ProcessName,
    nint WindowHandle,
    string? WindowTitle);

public sealed class ForegroundTracker : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private string? _currentProcessName;
    private nint _currentWindowHandle;
    private string? _currentTitleHash;
    private DateTimeOffset _focusStart;
    private DateTimeOffset _lastObserverNotification;
    private volatile bool _disposed;
    private Action<ForegroundWindowContext>? _onAppFocused;

    public int TransitionCount { get; private set; }

    internal void OnAppFocusChanged(Action<ForegroundWindowContext> callback) => _onAppFocused = callback;

    public ForegroundTracker(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
        _focusStart = DateTimeOffset.UtcNow;
        _lastObserverNotification = DateTimeOffset.MinValue;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.Information("ForegroundTracker started");
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try { PollForeground(); }
            catch (Exception ex)
            {
                _logger.Debug(
                    "ForegroundTracker poll error ({ExceptionType})",
                    ex.GetType().FullName);
            }
            await Task.Delay(2000, ct);
        }
    }

    private void PollForeground()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;

        string processName;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch { return; }

        var now = DateTimeOffset.UtcNow;
        string? rawTitle = null;
        string? titleHash = null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            GetWindowText(hwnd, sb, sb.Capacity);
            rawTitle = sb.ToString();
            if (!string.IsNullOrEmpty(rawTitle))
                titleHash = UiaPropertyScrubber.HmacHash(rawTitle, _pharmacySalt);
        }
        catch { }

        // Same-process tab, document, and window changes are observations too.
        // Comparing HWND + title hash fixes the prior process-only blind spot.
        if (processName == _currentProcessName
            && hwnd == _currentWindowHandle
            && string.Equals(titleHash, _currentTitleHash, StringComparison.Ordinal))
        {
            if (now - _lastObserverNotification >= TimeSpan.FromSeconds(30))
            {
                _lastObserverNotification = now;
                NotifyObservers(new ForegroundWindowContext(processName, hwnd, rawTitle));
            }
            return;
        }

        var duration = (long)(now - _focusStart).TotalMilliseconds;
        var prevProcess = _currentProcessName;

        _currentProcessName = processName;
        _currentWindowHandle = hwnd;
        _currentTitleHash = titleHash;
        _focusStart = now;
        _lastObserverNotification = now;

        if (prevProcess != null)
        {
            _buffer.Enqueue(BehavioralEvent.AppFocusChange(prevProcess, processName, titleHash, duration));
            TransitionCount++;
        }

        // Raw title remains inside Helper and is never serialized. Browser
        // observers deliberately receive no title-derived URL signal.
        NotifyObservers(new ForegroundWindowContext(processName, hwnd, rawTitle));
    }

    private void NotifyObservers(ForegroundWindowContext context)
    {
        try { _onAppFocused?.Invoke(context); }
        catch (Exception ex)
        {
            _logger.Warning(
                "ForegroundTracker observer callback failed ({ExceptionType})",
                ex.GetType().FullName);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public void Dispose() => _disposed = true;
}
