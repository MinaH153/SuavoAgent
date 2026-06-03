using System.Net;
using System.Net.Sockets;
using Sentry;

namespace SuavoAgent.Diagnostics;

/// Sentry SDK wrap — transport sink ONLY. Per Codex §8.1 RESOLVED v1.0:
/// Wire owns unhandled-exception capture; Sentry's default AppDomain and
/// UnobservedTask hooks are explicitly DISABLED to prevent double-capture
/// recursion through BeforeSend.
///
/// Per Codex §8.3 RESOLVED v1.0: Sentry .NET SDK has NO public
/// <c>SentryOptions.SendTimeout</c>. Transport bounded via
/// <c>ConfigureClient</c> (HttpClient.Timeout) + <c>CreateHttpMessageHandler</c>
/// (SocketsHttpHandler.ConnectTimeout + ConnectCallback DNS deadline) +
/// <c>MaxQueueItems</c> + <c>ShutdownTimeout</c>/<c>FlushTimeout</c>.
public sealed class SentrySink : IDisposable
{
    // Bootstrap fallbacks used ONLY when Wire.CurrentRuntime is null
    // (impossible after AttachUnhandledHooks completes successfully —
    // Wire publishes the initial RulesetRuntime via Volatile.Write BEFORE
    // constructing this sink, so production always sees the runtime).
    // Test fixtures that instantiate SentrySink directly without going
    // through Wire's init still rely on these.
    //
    // Per-call reads via CurrentScrubber()/CurrentFingerprinter() pick up
    // the latest Wire.CurrentRuntime — Comp 2.3 RESOLVED. Before this
    // refactor, SentrySink captured these refs at construction time and
    // ignored every OTA swap, defeating the whole Comp 2 ruleset OTA
    // pipeline once it goes live.
    private readonly PhiScrubber _bootstrapScrubber;
    private readonly FingerprintComputer _bootstrapFingerprinter;
    private readonly WireOptions _options;
    private readonly IDisposable? _sdkDisposable;
    private bool _initialized;
    private long _beforeSendFailedTotal;
    // Codex chunk 1 HIGH: previously named PostSuccess/PostFailed, but the
    // Sentry SDK queues asynchronously — CaptureException returns true on
    // ENQUEUE, not on HTTP POST success. During an outage the queue could
    // fill silently without any "failed" count. Renamed to make the
    // semantics explicit. True transport-result metrics require a
    // Sentry client-report hook (Phase 2 follow-up).
    private long _enqueuedTotal;
    private long _enqueueFailedTotal;

    public SentrySink(WireOptions options, PhiScrubber scrubber, FingerprintComputer fingerprinter)
    {
        _options = options;
        _bootstrapScrubber = scrubber;
        _bootstrapFingerprinter = fingerprinter;
        _sdkDisposable = TryInit();
    }

    /// <summary>
    /// Read the active <see cref="PhiScrubber"/> from <see cref="Wire.CurrentRuntime"/>,
    /// falling back to the bootstrap ref if Wire isn't initialised
    /// (test contexts). One <see cref="Volatile.Read{T}(ref T)"/> is
    /// performed inside <see cref="Wire.CurrentRuntime"/>; callers MUST
    /// invoke this helper exactly once per signal to guarantee
    /// snapshot coherence with the fingerprinter read.
    /// </summary>
    internal PhiScrubber CurrentScrubber()
        => Wire.CurrentRuntime?.Scrubber ?? _bootstrapScrubber;

    /// <summary>
    /// Read the active <see cref="FingerprintComputer"/> from
    /// <see cref="Wire.CurrentRuntime"/>, falling back to the bootstrap ref.
    /// Same one-read-per-signal contract as <see cref="CurrentScrubber"/>.
    /// </summary>
    internal FingerprintComputer CurrentFingerprinter()
        => Wire.CurrentRuntime?.Fingerprinter ?? _bootstrapFingerprinter;

    public bool IsInitialized => _initialized;
    public long BeforeSendFailedTotal => Interlocked.Read(ref _beforeSendFailedTotal);
    public long EnqueuedTotal => Interlocked.Read(ref _enqueuedTotal);
    public long EnqueueFailedTotal => Interlocked.Read(ref _enqueueFailedTotal);

    private IDisposable? TryInit()
    {
        if (!_options.EnableSentry) return null;
        if (string.IsNullOrWhiteSpace(_options.Dsn)) return null;

        try
        {
            var handle = SentrySdk.Init(o =>
            {
                o.Dsn = _options.Dsn;
                o.Environment = _options.Environment;
                o.Release = $"suavoagent@{BuildContext.DefaultGitSha}";

                // Wire-ownership: disable SDK default unhandled hooks.
                // Codex §8.1 RESOLVED v1.0.
                o.DisableAppDomainUnhandledExceptionCapture();
                o.DisableUnobservedTaskExceptionCapture();

                // Transport bounding (Codex §8.3 corrected — no SendTimeout
                // property exists). HttpClient.Timeout via ConfigureClient.
                o.ConfigureClient = client => client.Timeout = _options.SentryHttpTimeout;

                // Connection-level deadline so DNS / TCP failures never
                // block the EmitBudget worker queue or any synchronous
                // Wire caller. Default 250ms.
                o.CreateHttpMessageHandler = () => CreateBoundedHandler();

                // Queue depth — drop events if backlog grows. 30 is one
                // burst per component per heartbeat interval at the 10/sec
                // EmitBudget contract.
                o.MaxQueueItems = 30;

                // Codex chunk 1 MEDIUM PHI safety: disable breadcrumb
                // accumulation entirely. Sentry SDK auto-captures
                // breadcrumbs from Microsoft.Extensions.Logging when the
                // integration is wired. Any agent code logging PHI
                // (which it shouldn't, but is possible) would otherwise
                // carry to Sentry unscrubbed. v1 stance: zero breadcrumbs.
                // If future fingerprints need breadcrumb context, switch
                // to a BeforeBreadcrumb scrub hook.
                o.MaxBreadcrumbs = 0;

                // Shutdown/flush bounded so suavo-report-crash.exe finishes
                // fast. Wire's unhandled path never calls Flush.
                o.ShutdownTimeout = TimeSpan.FromMilliseconds(250);
                o.FlushTimeout = TimeSpan.FromMilliseconds(250);

                // PHI-scrub + fingerprint tag every event. BeforeSend MUST
                // NOT throw (Codex §8.1); on throw we drop and increment
                // mesh.sentry_before_send_failed_total.
                o.SetBeforeSend((evt, _) => SafeBeforeSend(evt));
            });
            _initialized = true;
            return handle;
        }
        catch
        {
            // Sentry init failure is non-fatal — local journal continues.
            _initialized = false;
            return null;
        }
    }

    private HttpMessageHandler CreateBoundedHandler()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = _options.SentryConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        handler.ConnectCallback = async (ctx, ct) =>
        {
            // Bound DNS resolution to the same connect-timeout budget so a
            // never-returning resolver can't extend the local-first SLA.
            using var dnsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            dnsCts.CancelAfter(_options.SentryConnectTimeout);
            var ipAddresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, dnsCts.Token)
                .ConfigureAwait(false);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(ipAddresses, ctx.DnsEndPoint.Port, dnsCts.Token)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        };
        return handler;
    }

    private SentryEvent? SafeBeforeSend(SentryEvent evt)
    {
        try
        {
            // ONE read per signal — captures the active runtime's scrubber
            // for the duration of this BeforeSend. A concurrent SwapRuleset
            // only affects the NEXT event; this one stays coherent.
            var scrubber = CurrentScrubber();

            // Scrub message + exception messages.
            if (!string.IsNullOrEmpty(evt.Message?.Message))
            {
                evt.Message = new SentryMessage { Message = scrubber.Sanitize(evt.Message.Message) };
            }
            if (evt.SentryExceptions is { } exceptions)
            {
                foreach (var ex in exceptions)
                {
                    if (!string.IsNullOrEmpty(ex.Value))
                    {
                        ex.Value = scrubber.Sanitize(ex.Value);
                    }
                }
            }

            // Codex chunk 1 MEDIUM PHI safety: breadcrumb auto-capture is
            // already disabled via MaxBreadcrumbs=0 in TryInit, so no
            // breadcrumb scrub is needed here.

            // Scrub string-valued Extra entries (tags are set explicitly
            // by ApplyScope from non-PHI sources, so they are not
            // re-scrubbed here).
            foreach (var key in evt.Extra.Keys.ToList())
            {
                if (evt.Extra[key] is string s && !string.IsNullOrEmpty(s))
                {
                    evt.SetExtra(key, scrubber.Sanitize(s));
                }
            }

            return evt;
        }
        catch
        {
            Interlocked.Increment(ref _beforeSendFailedTotal);
            return null; // drop event entirely on scrub failure
        }
    }

    /// <summary>
    /// Capture an exception with fingerprint tag + bug-class tag if it
    /// matches a calibration_fingerprint. Returns false if Sentry is not
    /// initialized (caller continues with local-journal-only mode).
    /// </summary>
    public bool CaptureException(Exception exception, string fingerprint,
        WireComponent component, string? stage = null,
        IReadOnlyDictionary<string, string>? extraTags = null)
    {
        if (!_initialized) return false;
        try
        {
            // Sentry .NET 4.x: per-event scope via SentrySdk.CaptureException
            // overload that takes a scope-callback. WithScope was removed
            // in 4.x; per-event scope is the supported isolation.
            SentrySdk.CaptureException(exception, scope =>
            {
                ApplyScope(scope, fingerprint, component, stage, extraTags);
            });
            Interlocked.Increment(ref _enqueuedTotal);
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _enqueueFailedTotal);
            return false;
        }
    }

    /// <summary>
    /// Capture a non-exception invariant violation or heartbeat message.
    /// </summary>
    public bool CaptureMessage(string message, string fingerprint,
        WireComponent component, SentryLevel level = SentryLevel.Warning,
        IReadOnlyDictionary<string, string>? extraTags = null)
    {
        if (!_initialized) return false;
        try
        {
            SentrySdk.CaptureMessage(message, scope =>
            {
                ApplyScope(scope, fingerprint, component, stage: null, extraTags);
            }, level);
            Interlocked.Increment(ref _enqueuedTotal);
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _enqueueFailedTotal);
            return false;
        }
    }

    private void ApplyScope(Sentry.Scope scope, string fingerprint,
        WireComponent component, string? stage,
        IReadOnlyDictionary<string, string>? extraTags)
    {
        // Single read per signal — pairs with the corresponding CurrentScrubber()
        // read in SafeBeforeSend (they execute in separate SDK callbacks
        // so we accept the per-signal pair may straddle a swap; the
        // operational consequence is one event tagged with the previous
        // bug-class while scrubbed with the new ruleset, which is
        // acceptable for an extremely rare race during rollout).
        var fingerprinter = CurrentFingerprinter();

        scope.Fingerprint = new[] { fingerprint };
        scope.SetTag("component", component.ToString());
        scope.SetTag("fp_v1", fingerprint);
        scope.SetTag("git_sha", BuildContext.DefaultGitSha);
        scope.SetTag("ruleset_version", fingerprinter.RulesetVersion);
        if (stage is not null) scope.SetTag("stage", stage);
        var bugClass = fingerprinter.CalibrationBugClass(fingerprint);
        if (bugClass is not null) scope.SetTag("bug-class", bugClass);
        if (extraTags is not null)
        {
            foreach (var kv in extraTags) scope.SetTag(kv.Key, kv.Value);
        }
    }

    public void Dispose()
    {
        _sdkDisposable?.Dispose();
    }
}
