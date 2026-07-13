using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Observes local print-server change notifications. Raw document, user, printer,
/// and machine values never leave Helper; Core receives only a daily HMAC of the
/// printer/job identity.
/// </summary>
public sealed class PrintEventObserver : IDisposable
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);
    private const int MaximumRememberedJobs = 4_096;

    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private readonly IPrintJobNotificationSource _notificationSource;
    private readonly HashSet<string> _seenJobHashes = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenJobOrder = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateGate = new();
    private int _disposed;
    private int _printEventCount;
    private int _failureCount;
    private bool _isAvailable;
    private bool _hasReportedReady;
    private string? _lastFailureCode;
    private string? _lastDegradedCode;
    private string _currentStatus = "not_started";
    private DateTimeOffset? _lastSuccessfulNotificationUtc;

    public int PrintEventCount
    {
        get { lock (_stateGate) return _printEventCount; }
    }

    public int FailureCount
    {
        get { lock (_stateGate) return _failureCount; }
    }

    public bool IsAvailable
    {
        get { lock (_stateGate) return _isAvailable; }
    }

    public DateTimeOffset? LastSuccessfulNotificationUtc
    {
        get { lock (_stateGate) return _lastSuccessfulNotificationUtc; }
    }

    public string CurrentStatus
    {
        get { lock (_stateGate) return _currentStatus; }
    }

    public PrintEventObserver(
        BehavioralEventBuffer buffer,
        string pharmacySalt,
        ILogger logger)
        : this(buffer, pharmacySalt, new WindowsPrintJobNotificationSource(), logger)
    {
    }

    internal PrintEventObserver(
        BehavioralEventBuffer buffer,
        string pharmacySalt,
        IPrintJobNotificationSource notificationSource,
        ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _notificationSource = notificationSource;
        _logger = logger.ForContext<PrintEventObserver>();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_notificationSource.IsSupported)
        {
            ReportUnavailable("unsupported_platform");
            return;
        }

        _logger.Information("PrintEventObserver started with Winspool change notifications");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var token = linkedCancellation.Token;
        var retryDelay = InitialRetryDelay;

        while (!token.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            var subscriptionBecameReady = false;
            try
            {
                await _notificationSource.ObserveAsync(
                        signal =>
                        {
                            if (signal.Kind == PrintMonitorSignalKind.Ready)
                                subscriptionBecameReady = true;
                            HandleSignal(signal);
                        },
                        token)
                    .ConfigureAwait(false);

                if (token.IsCancellationRequested) break;
                ReportUnavailable("notification_source_stopped");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (PrintSpoolerException ex)
            {
                ReportUnavailable($"winspool_{ex.NativeErrorCode}");
            }
            catch (PrintNotificationException ex)
            {
                ReportUnavailable(NormalizeFailureCode(ex.FailureCode));
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "PrintEventObserver notification source failed ({ExceptionType})",
                    ex.GetType().FullName);
                ReportUnavailable("notification_exception");
            }

            if (token.IsCancellationRequested) break;
            retryDelay = subscriptionBecameReady
                ? InitialRetryDelay
                : TimeSpan.FromMilliseconds(Math.Min(
                    retryDelay.TotalMilliseconds * 2,
                    MaximumRetryDelay.TotalMilliseconds));
            try
            {
                await Task.Delay(retryDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void HandleSignal(PrintMonitorSignal signal)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        switch (signal.Kind)
        {
            case PrintMonitorSignalKind.Ready:
                ReportReady();
                break;
            case PrintMonitorSignalKind.JobAdded when signal.Job is not null:
                RecordJob(signal.Job);
                break;
            case PrintMonitorSignalKind.PrinterFailure:
                ReportDegraded(NormalizeFailureCode(signal.FailureCode));
                break;
            default:
                ReportDegraded("notification_identity_invalid");
                break;
        }
    }

    private void RecordJob(PrintJobIdentity job)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // The raw printer name is used only inside this expression. It is never
        // included in the BehavioralEvent, status, exception, or log context.
        var jobHash = UiaPropertyScrubber.HmacHash(
            $"{job.PrinterName}|{job.JobId}",
            _pharmacySalt);

        lock (_stateGate)
        {
            _isAvailable = true;
            _lastSuccessfulNotificationUtc = DateTimeOffset.UtcNow;
            if (!_seenJobHashes.Add(jobHash)) return;
            _seenJobOrder.Enqueue(jobHash);
            while (_seenJobOrder.Count > MaximumRememberedJobs)
                _seenJobHashes.Remove(_seenJobOrder.Dequeue());
        }

        _buffer.Enqueue(BehavioralEvent.Interaction(
            subtype: "print_job",
            treeHash: null,
            elementId: "print",
            controlType: "printer",
            className: null,
            nameHash: jobHash));

        lock (_stateGate) _printEventCount++;
    }

    private void ReportReady()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        string? status = null;
        lock (_stateGate)
        {
            _isAvailable = true;
            _lastSuccessfulNotificationUtc = DateTimeOffset.UtcNow;
            _lastDegradedCode = null;
            if (_lastFailureCode is not null)
                status = "recovered";
            else if (!_hasReportedReady)
                status = "ready";
            _lastFailureCode = null;
            _hasReportedReady = true;
            _currentStatus = status ?? "ready";
        }

        if (status is not null)
            _buffer.Enqueue(BehavioralEvent.ObserverStatus("print", status));
    }

    private void ReportUnavailable(string code)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var normalizedCode = NormalizeFailureCode(code);
        var shouldReport = false;
        lock (_stateGate)
        {
            _failureCount++;
            _isAvailable = false;
            shouldReport = !string.Equals(
                _lastFailureCode,
                normalizedCode,
                StringComparison.Ordinal);
            _lastFailureCode = normalizedCode;
            _currentStatus = normalizedCode;
        }

        if (!shouldReport) return;
        _buffer.Enqueue(BehavioralEvent.ObserverStatus("print", normalizedCode));
        _logger.Warning("PrintEventObserver unavailable ({FailureCode})", normalizedCode);
    }

    private void ReportDegraded(string code)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var normalizedCode = NormalizeFailureCode(code);
        var status = $"degraded_{normalizedCode}";
        var shouldReport = false;
        lock (_stateGate)
        {
            _failureCount++;
            shouldReport = !string.Equals(
                _lastDegradedCode,
                normalizedCode,
                StringComparison.Ordinal);
            _lastDegradedCode = normalizedCode;
            _currentStatus = status;
        }

        if (!shouldReport) return;
        _buffer.Enqueue(BehavioralEvent.ObserverStatus("print", status));
        _logger.Warning("PrintEventObserver degraded ({FailureCode})", normalizedCode);
    }

    private static string NormalizeFailureCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "notification_failure";
        if (code.StartsWith("winspool_", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan("winspool_".Length), out _))
        {
            return code;
        }

        return code switch
        {
            "unsupported_platform" => code,
            "notification_source_stopped" => code,
            "notification_exception" => code,
            "notification_failure" => code,
            "notification_wait_invalid" => code,
            "notification_overflow" => code,
            "notification_refresh_incomplete" => code,
            "notification_batch_too_large" => code,
            "notification_identity_invalid" => code,
            "notification_identity_missing" => code,
            _ => "notification_failure",
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
    }
}
