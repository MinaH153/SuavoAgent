using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class ForegroundTracker : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private string? _currentProcessName;
    private DateTimeOffset _focusStart;
    private volatile bool _disposed;
    private Action<string, string?>? _onAppFocused;

    public int TransitionCount { get; private set; }

    public void OnAppFocusChanged(Action<string, string?> callback) => _onAppFocused = callback;

    public ForegroundTracker(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
        _focusStart = DateTimeOffset.UtcNow;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.Information("ForegroundTracker started");
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try { PollForeground(); }
            catch (Exception ex) { _logger.Debug(ex, "ForegroundTracker poll error"); }
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
        try { processName = Process.GetProcessById((int)pid).ProcessName; }
        catch { return; }

        if (processName == _currentProcessName) return;

        var now = DateTimeOffset.UtcNow;
        var duration = (long)(now - _focusStart).TotalMilliseconds;
        var prevProcess = _currentProcessName;

        string? titleHash = null;
        try
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (!string.IsNullOrEmpty(title))
                titleHash = UiaPropertyScrubber.HmacHash(title, _pharmacySalt);
        }
        catch { }

        _currentProcessName = processName;
        _focusStart = now;

        if (prevProcess != null)
        {
            _buffer.Enqueue(BehavioralEvent.AppFocusChange(prevProcess, processName, titleHash, duration));
            TransitionCount++;

            // Extract domain if this is a browser (for BrowserDomainObserver)
            string? extractedDomain = null;
            try
            {
                var sb2 = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, sb2, sb2.Capacity);
                var rawTitle = sb2.ToString();
                if (BrowserDomainObserver.IsBrowserProcess(processName))
                    extractedDomain = BrowserDomainObserver.ExtractDomain(rawTitle);
            }
            catch { }

            // Notify registered observers
            try { _onAppFocused?.Invoke(processName, extractedDomain); }
            catch { } // observer errors must not crash the tracker
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public void Dispose() => _disposed = true;
}
