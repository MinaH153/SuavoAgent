using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Captures a bounded structural UI Automation fingerprint for the actual
/// foreground HWND. It never substitutes a process/title hash for a UI tree.
/// </summary>
public sealed class MultiAppUiaObserver
{
    private static readonly TimeSpan PerWindowCooldown = TimeSpan.FromSeconds(30);

    private readonly BehavioralEventBuffer _buffer;
    private readonly IWindowStructureSnapshotProvider _snapshotProvider;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastCaptureByWindow = new(StringComparer.Ordinal);
    private string? _lastFailureCode;
    private int _captureActive;
    private int _snapshotCount;
    private int _failureCount;
    private int _busySkipCount;
    private string _currentStatus = "not_started";

    public int SnapshotCount => Volatile.Read(ref _snapshotCount);
    public int FailureCount => Volatile.Read(ref _failureCount);
    public int BusySkipCount => Volatile.Read(ref _busySkipCount);
    public string CurrentStatus
    {
        get { lock (_stateLock) return _currentStatus; }
    }

    public MultiAppUiaObserver(
        BehavioralEventBuffer buffer,
        string pharmacySalt,
        ILogger logger)
        : this(buffer, new IsolatedWindowStructureSnapshotProvider(), logger)
    {
        _ = pharmacySalt; // The provider intentionally excludes Name/text entirely.
    }

    internal MultiAppUiaObserver(
        BehavioralEventBuffer buffer,
        IWindowStructureSnapshotProvider snapshotProvider,
        ILogger logger)
    {
        _buffer = buffer;
        _snapshotProvider = snapshotProvider;
        _logger = logger.ForContext<MultiAppUiaObserver>();
    }

    public void OnAppFocused(string processName, nint windowHandle)
    {
        if (string.IsNullOrWhiteSpace(processName) || windowHandle == 0)
        {
            ReportFailure("invalid_window");
            return;
        }

        var key = $"{processName}:{windowHandle}";
        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastCaptureByWindow.Count >= 256)
            {
                var staleKeys = _lastCaptureByWindow
                    .Where(entry => now - entry.Value >= TimeSpan.FromMinutes(5))
                    .Select(entry => entry.Key)
                    .ToArray();
                foreach (var staleKey in staleKeys)
                    _lastCaptureByWindow.Remove(staleKey);

                if (_lastCaptureByWindow.Count >= 256)
                {
                    var oldest = _lastCaptureByWindow.MinBy(entry => entry.Value).Key;
                    _lastCaptureByWindow.Remove(oldest);
                }
            }
            if (_lastCaptureByWindow.TryGetValue(key, out var last)
                && now - last < PerWindowCooldown)
            {
                return;
            }
            _lastCaptureByWindow[key] = now;
        }

        if (Interlocked.CompareExchange(ref _captureActive, 1, 0) != 0)
        {
            ClearCooldown(key);
            Interlocked.Increment(ref _busySkipCount);
            ReportFailure("capture_busy");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var result = _snapshotProvider.Capture(windowHandle);
                if (!result.Success || string.IsNullOrEmpty(result.TreeHash))
                {
                    ClearCooldown(key);
                    ReportFailure(result.FailureCode ?? "capture_failed");
                    return;
                }

                _buffer.Enqueue(new BehavioralEvent
                {
                    Type = BehavioralEventType.TreeSnapshot,
                    Subtype = processName,
                    TreeHash = result.TreeHash,
                    ElementId = result.Truncated ? "truncated" : "complete",
                    OccurrenceCount = result.ElementCount,
                    Timestamp = DateTimeOffset.UtcNow,
                });
                var snapshotCount = Interlocked.Increment(ref _snapshotCount);

                lock (_stateLock)
                {
                    var status = _lastFailureCode is not null ? "recovered" : "ready";
                    if (_lastFailureCode is not null || snapshotCount == 1)
                        _buffer.Enqueue(BehavioralEvent.ObserverStatus("multi_app_uia", status));
                    _currentStatus = status;
                    _lastFailureCode = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "Multi-app UIA capture failed ({ExceptionType})",
                    ex.GetType().FullName);
                ClearCooldown(key);
                ReportFailure("capture_exception");
            }
            finally
            {
                Interlocked.Exchange(ref _captureActive, 0);
            }
        });
    }

    private void ClearCooldown(string key)
    {
        lock (_stateLock)
        {
            _lastCaptureByWindow.Remove(key);
        }
    }

    private void ReportFailure(string code)
    {
        Interlocked.Increment(ref _failureCount);
        lock (_stateLock)
        {
            if (string.Equals(_lastFailureCode, code, StringComparison.Ordinal)) return;
            _lastFailureCode = code;
            _currentStatus = code;
            _buffer.Enqueue(BehavioralEvent.ObserverStatus("multi_app_uia", code));
        }
    }
}
