using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Periodic PMS tree observer. The parent Helper only locates the approved
/// process's HWND through Win32; every UIA property read and tree walk runs in
/// a killable subprocess with a hard deadline.
/// </summary>
public sealed class UiaTreeObserver
{
    private static readonly TimeSpan WalkInterval = TimeSpan.FromSeconds(60);

    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly Action<string>? _onTreeHash;
    private readonly Func<int> _expectedProcessId;
    private readonly Func<int, bool> _processTrusted;
    private readonly IPmsWindowLocator _windowLocator;
    private readonly IWindowStructureSnapshotProvider _snapshotProvider;
    private readonly object _statusLock = new();

    private long _lastWalkDurationTicks;
    private string? _lastFailureCode;
    private int _lastElementCount;
    private int _lastSnapshotTruncated;
    private int _walkActive;

    public UiaTreeObserver(
        string pharmacySalt,
        BehavioralEventBuffer buffer,
        ILogger logger,
        Action<string>? onTreeHash = null,
        Func<int>? expectedProcessId = null,
        Func<int, bool>? processTrusted = null)
        : this(
            pharmacySalt,
            buffer,
            logger,
            expectedProcessId ?? (() => -1),
            processTrusted ?? (_ => false),
            new Win32PmsWindowLocator(),
            new IsolatedWindowStructureSnapshotProvider(),
            onTreeHash)
    {
    }

    internal UiaTreeObserver(
        string pharmacySalt,
        BehavioralEventBuffer buffer,
        ILogger logger,
        Func<int> expectedProcessId,
        Func<int, bool> processTrusted,
        IPmsWindowLocator windowLocator,
        IWindowStructureSnapshotProvider snapshotProvider,
        Action<string>? onTreeHash = null)
    {
        // Tree snapshots intentionally exclude Name/text, so no salt crosses
        // the worker protocol. Keep the parameter for the established API.
        _ = pharmacySalt;
        _buffer = buffer;
        _logger = logger.ForContext<UiaTreeObserver>();
        _expectedProcessId = expectedProcessId;
        _processTrusted = processTrusted;
        _windowLocator = windowLocator;
        _snapshotProvider = snapshotProvider;
        _onTreeHash = onTreeHash;
    }

    public int LastElementCount => Volatile.Read(ref _lastElementCount);
    public bool LastSnapshotTruncated => Volatile.Read(ref _lastSnapshotTruncated) != 0;
    public string? LastFailureCode => Volatile.Read(ref _lastFailureCode);
    public TimeSpan LastWalkDuration =>
        TimeSpan.FromTicks(Volatile.Read(ref _lastWalkDurationTicks));

    public async Task RunAsync(Func<Window?> getWindow, CancellationToken ct)
    {
        _logger.Information(
            "UiaTreeObserver started (interval={IntervalSec}s, hardTimeout={TimeoutSec}s, maxElements={MaxElements})",
            WalkInterval.TotalSeconds,
            IsolatedWindowStructureSnapshotProvider.CaptureTimeout.TotalSeconds,
            FlaUiWindowStructureSnapshotProvider.MaximumElements(WindowStructureCaptureProfile.Pms));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WalkInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Window? window;
            try
            {
                // This reads only the already-attached object reference. The
                // Window is never dereferenced by this observer in the parent.
                window = getWindow();
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "UiaTreeObserver: window resolver failed ({ExceptionType})",
                    ex.GetType().FullName);
                ReportFailure("window_resolver_failed");
                continue;
            }

            if (window is null)
            {
                ReportFailure("window_unavailable");
                continue;
            }

            WalkTree(window);
        }

        _logger.Information("UiaTreeObserver stopped");
    }

    /// <summary>
    /// Preserves the established API but deliberately does not dereference the
    /// FlaUI Window. HWND resolution is Win32-only and PID-bound.
    /// </summary>
    public void WalkTree(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        CaptureExpectedWindow();
    }

    internal void CaptureExpectedWindow()
    {
        if (Interlocked.CompareExchange(ref _walkActive, 1, 0) != 0)
        {
            ReportFailure("capture_busy");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            PublishCapture(CaptureIsolated(), stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "UiaTreeObserver: isolated capture failed ({ExceptionType})",
                ex.GetType().FullName);
            ReportFailure("capture_exception");
        }
        finally
        {
            stopwatch.Stop();
            Volatile.Write(ref _lastWalkDurationTicks, stopwatch.Elapsed.Ticks);
            Interlocked.Exchange(ref _walkActive, 0);
        }
    }

    internal void PublishCapture(UiaTreeCapture capture, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (!capture.Success
            || capture.ElementCount <= 0
            || string.IsNullOrWhiteSpace(capture.TreeHash))
        {
            ReportFailure(capture.FailureCode ?? "empty_tree");
            return;
        }

        Volatile.Write(ref _lastElementCount, capture.ElementCount);
        Volatile.Write(ref _lastSnapshotTruncated, capture.Truncated ? 1 : 0);

        _buffer.Enqueue(new BehavioralEvent
        {
            Type = BehavioralEventType.TreeSnapshot,
            Subtype = "pioneerrx",
            TreeHash = capture.TreeHash,
            ElementId = capture.Truncated ? "truncated" : "complete",
            OccurrenceCount = capture.ElementCount,
            Timestamp = DateTimeOffset.UtcNow,
        });
        _onTreeHash?.Invoke(capture.TreeHash);

        lock (_statusLock)
        {
            if (_lastFailureCode is not null)
                _buffer.Enqueue(BehavioralEvent.ObserverStatus("uia_tree", "recovered"));
            _lastFailureCode = null;
        }

        _logger.Debug(
            "UiaTreeObserver: isolated capture returned {Count} elements in {Ms}ms (truncated={Truncated}, hash={Hash})",
            capture.ElementCount,
            duration.TotalMilliseconds,
            capture.Truncated,
            capture.TreeHash[..Math.Min(8, capture.TreeHash.Length)]);
    }

    private UiaTreeCapture CaptureIsolated()
    {
        int expectedProcessId;
        try { expectedProcessId = _expectedProcessId(); }
        catch { return Failure("expected_process_unavailable"); }
        if (expectedProcessId <= 0)
            return Failure("expected_process_unavailable");
        if (!IsProcessTrusted(expectedProcessId))
            return Failure("process_authority_denied");

        if (!_windowLocator.TryLocate(expectedProcessId, out var windowHandle)
            || windowHandle == 0)
        {
            return Failure("window_handle_unavailable");
        }

        var snapshot = _snapshotProvider.Capture(
            windowHandle,
            expectedProcessId,
            WindowStructureCaptureProfile.Pms);
        if (!snapshot.Success)
            return Failure(snapshot.FailureCode ?? "capture_failed");
        if (!IsProcessTrusted(expectedProcessId))
            return Failure("process_authority_denied");
        if (snapshot.WindowHandle != windowHandle.ToInt64()
            || snapshot.ProcessId != expectedProcessId)
        {
            return Failure("worker_identity_mismatch");
        }

        return new UiaTreeCapture(
            true,
            snapshot.TreeHash,
            snapshot.ElementCount,
            snapshot.Truncated,
            null);
    }

    private static UiaTreeCapture Failure(string code) =>
        new(false, null, 0, false, code);

    private bool IsProcessTrusted(int processId)
    {
        try { return _processTrusted(processId); }
        catch { return false; }
    }

    private void ReportFailure(string code)
    {
        lock (_statusLock)
        {
            if (string.Equals(_lastFailureCode, code, StringComparison.Ordinal)) return;
            _lastFailureCode = code;
            _buffer.Enqueue(BehavioralEvent.ObserverStatus("uia_tree", code));
        }
    }
}

internal interface IPmsWindowLocator
{
    bool TryLocate(int expectedProcessId, out nint windowHandle);
}

/// <summary>
/// Finds a visible, unowned top-level HWND for the already-approved PID.
/// It never reads titles or invokes UI Automation.
/// </summary>
internal sealed class Win32PmsWindowLocator : IPmsWindowLocator
{
    private const uint GetWindowOwner = 4;

    public bool TryLocate(int expectedProcessId, out nint windowHandle)
    {
        windowHandle = 0;
        if (!OperatingSystem.IsWindows() || expectedProcessId <= 0) return false;

        nint candidate = 0;
        _ = EnumWindows((handle, parameter) =>
        {
            _ = parameter;
            GetWindowThreadProcessId(handle, out var processId);
            if (processId != (uint)expectedProcessId
                || !IsWindowVisible(handle)
                || GetWindow(handle, GetWindowOwner) != 0)
            {
                return true;
            }

            candidate = handle;
            return false;
        }, 0);

        windowHandle = candidate;
        return candidate != 0;
    }

    private delegate bool EnumWindowsCallback(nint windowHandle, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}

internal sealed record UiaTreeCapture(
    bool Success,
    string? TreeHash,
    int ElementCount,
    bool Truncated,
    string? FailureCode);
