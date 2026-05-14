using System.Diagnostics;

namespace SuavoAgent.Diagnostics;

/// Public entry point for the Diagnostic Mesh. <c>AttachUnhandledHooks</c>
/// MUST be the literal first statement of every long-running entry point's
/// Program.cs per spec §7 PR 4 wire-ordering invariant. WireOrderingTests
/// enforces this via Roslyn scan.
///
/// Runtime recursion contract (Codex §8.1 RESOLVED v1.0):
/// - AsyncLocal&lt;int&gt; WireDepth tracks nested entries across async
///   continuations.
/// - Depth 0→1 normal, 1→2 degraded local-journal-only marker,
///   ≥3 Environment.FailFast (silent drop forbidden on unhandled paths).
/// - Handler catch ordering FIXED: PhiScrubber → FingerprintComputer →
///   LocalJournal → swallow.
/// - Sentry SDK composition: Wire owns hooks; SDK default AppDomain /
///   UnobservedTask hooks are explicitly disabled in SentrySink.
public static class Wire
{
    private static readonly AsyncLocal<int> _wireDepth = new();
    // _fatalHandlerActive was previously [ThreadStatic] but was dead code —
    // only set immediately before Environment.FailFast (which terminates
    // the process synchronously) so the flag has no observable effect.
    // Removed in Codex chunk 1 LOW. The AsyncLocal depth counter alone
    // is the recursion guard.

    private static WireComponent _component;
    private static WireOptions _options = new();
    // Single immutable snapshot of (ruleset, scrubber, fingerprinter).
    // Published via Volatile.Write, read via Volatile.Read. See
    // <see cref="RulesetRuntime"/> + Codex Comp 2 chunk B HIGH RESOLVED.
    private static RulesetRuntime? _runtime;
    private static LocalJournal? _journal;
    private static EmitBudget? _budget;
    private static SentrySink? _sentry;
    private static bool _initialized;

    // Counters surfaced via heartbeat per spec §4 self-heartbeat contract.
    private static long _eventsEmittedTotal;
    private static long _wireHandlerFailedTotal;
    private static long _fpFallbackTotal;
    private static long _rulesetSwapsTotal;

    public static long EventsEmittedTotal => Interlocked.Read(ref _eventsEmittedTotal);
    public static long WireHandlerFailedTotal => Interlocked.Read(ref _wireHandlerFailedTotal);
    public static long FpFallbackTotal => Interlocked.Read(ref _fpFallbackTotal);
    public static long RulesetSwapsTotal => Interlocked.Read(ref _rulesetSwapsTotal);
    public static long RateLimitedTotal => _budget?.RateLimitedTotal ?? 0;
    /// <summary>
    /// Count of Sentry events successfully ENQUEUED into the SDK's worker
    /// queue. Per Codex chunk 1 HIGH: the SDK posts asynchronously, so
    /// enqueue success ≠ HTTP POST success. During an outage, events
    /// enqueue OK but the worker never drains. Heartbeat surfaces this
    /// as <c>mesh.sentry_enqueued_total</c> + a separate
    /// <c>mesh.sentry_enqueue_failed_total</c> for SDK-throw cases. True
    /// transport-success metrics require Sentry's client-report hook
    /// (deferred to a Phase 2 follow-up).
    /// </summary>
    public static long SentryEnqueuedTotal => _sentry?.EnqueuedTotal ?? 0;
    public static long SentryEnqueueFailedTotal => _sentry?.EnqueueFailedTotal ?? 0;
    public static long SentryBeforeSendFailedTotal => _sentry?.BeforeSendFailedTotal ?? 0;
    public static bool SentryInitialized => _sentry?.IsInitialized ?? false;
    public static WireComponent Component => _component;
    public static string RulesetVersion
        => Volatile.Read(ref _runtime)?.Ruleset.RulesetVersion ?? "ruleset-uninitialized";
    public static int RulesetVersionInt
        => Volatile.Read(ref _runtime)?.Ruleset.RulesetVersionInt ?? 0;
    /// <summary>
    /// Snapshot of the active ruleset + derived components. Readers take
    /// ONE <see cref="Volatile.Read{T}(ref T)"/> at the top of their work
    /// and never re-read mid-dispatch. Public for SentrySink / tests; do
    /// NOT cache the returned reference across signal boundaries.
    /// </summary>
    public static RulesetRuntime? CurrentRuntime => Volatile.Read(ref _runtime);

    /// <summary>
    /// Install Wire as the process's unhandled-exception capture point.
    /// MUST be the literal first statement of Program.cs (spec §7 PR 4).
    /// Idempotent: calling twice does not double-register handlers.
    /// </summary>
    public static void AttachUnhandledHooks(WireComponent component, WireOptions options)
    {
        if (_initialized) return;

        try
        {
            _component = component;
            _options = options;

            // Build the initial RulesetRuntime snapshot OFF-LOCK, then
            // publish atomically via Volatile.Write. Readers see either
            // null (uninitialised) or a fully-constructed snapshot —
            // never a half-built generation.
            var initialRuleset = RulesetV1.LoadEmbedded();
            var initialRuntime = new RulesetRuntime(
                initialRuleset,
                new PhiScrubber(initialRuleset, options.ScrubberTimeout),
                new FingerprintComputer(initialRuleset, options.FingerprintTimeout));
            Volatile.Write(ref _runtime, initialRuntime);

            _journal = new LocalJournal(options.LocalJournalPath, options.LocalJournalTimeout);
            _budget = new EmitBudget(options.EmitBudgetPerSecond);
            // SentrySink captures the INITIAL scrubber + fingerprinter.
            // Subsequent SwapRuleset calls do NOT propagate to SentrySink;
            // its scrub layer is defense-in-depth (Wire.DispatchNormal
            // pre-scrubs the message before handing it off). The bounded
            // inconsistency window (≤5min OTA poll) is documented; Comp 2.3
            // will refactor SentrySink to read fresh from Wire.CurrentRuntime
            // if operational data shows the gap matters.
            _sentry = new SentrySink(options, initialRuntime.Scrubber, initialRuntime.Fingerprinter);

            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;

            _initialized = true;
        }
        catch
        {
            // Wire init failure is non-fatal — process continues. Local
            // crash-log fallback still operates via static LocalJournal.
            _initialized = false;
        }
    }

    public static void ReportException(WireComponent component, Exception exception, string? stage = null)
    {
        ReportSignal(new WireSignal
        {
            Component = component,
            Kind = exception is System.ComponentModel.Win32Exception ? WireSignalKind.Win32 : WireSignalKind.ManagedException,
            Exception = exception,
            Stage = stage,
        });
    }

    public static void ReportInvariant(string invariantId, IDictionary<string, string>? context = null)
    {
        ReportSignal(new WireSignal
        {
            Component = _component,
            Kind = WireSignalKind.InvariantViolation,
            InvariantId = invariantId,
            ExtraTags = context?.ToDictionary(kv => kv.Key, kv => kv.Value),
        });
    }

    public static void ReportExitCode(WireComponent component, int exitCode, string? command = null)
    {
        ReportSignal(new WireSignal
        {
            Component = component,
            Kind = WireSignalKind.ExitCode,
            ExitCode = exitCode,
            Stage = command,
        });
    }

    /// <summary>
    /// Synthetic heartbeat — fires every 5min from MeshHeartbeatWorker.
    /// Exercises the full Wire path (scrub → fingerprint → journal →
    /// Sentry POST) per spec §4 self-heartbeat contract. Phase A A1
    /// silent-agent alarm pages Joshua if Queen's mesh.heartbeat absent
    /// &gt;30min.
    /// </summary>
    /// <remarks>
    /// Codex chunk 1 HIGH: previously hand-built fingerprint + manually
    /// called Sentry/journal, bypassing scrub/fingerprint compute/emit
    /// budget/crash-log fallback. Now routes through ReportSignal so the
    /// real crash pipeline is continuously verified. Heartbeat extras
    /// are attached via ExtraTags on the WireSignal so DispatchNormal's
    /// Sentry post includes them.
    /// </remarks>
    public static void Heartbeat(WireComponent component)
    {
        if (!_initialized) return;

        ReportSignal(new WireSignal
        {
            Component = component,
            Kind = WireSignalKind.Heartbeat,
            Stage = "mesh.heartbeat",
            ExtraTags = new Dictionary<string, string>
            {
                ["mesh.events_emitted_total"] = EventsEmittedTotal.ToString(),
                ["mesh.rate_limited_total"] = RateLimitedTotal.ToString(),
                ["mesh.wire_handler_failed_total"] = WireHandlerFailedTotal.ToString(),
                ["mesh.fp_fallback_total"] = FpFallbackTotal.ToString(),
                ["mesh.sentry_enqueued_total"] = SentryEnqueuedTotal.ToString(),
                ["mesh.sentry_enqueue_failed_total"] = SentryEnqueueFailedTotal.ToString(),
                ["mesh.sentry_before_send_failed_total"] = SentryBeforeSendFailedTotal.ToString(),
                ["mesh.sentry_initialized"] = SentryInitialized.ToString(),
                ["mesh.ruleset_version"] = RulesetVersion,
                ["mesh.ruleset_version_int"] = RulesetVersionInt.ToString(),
                ["mesh.ruleset_swaps_total"] = RulesetSwapsTotal.ToString(),
                ["mesh.git_sha"] = BuildContext.DefaultGitSha,
            },
        });
    }

    /// <summary>
    /// Atomically replace the active ruleset + rebuild derived components
    /// (scrubber, fingerprinter). Called by <c>ConfigSyncWorker</c> after
    /// a successful OTA fetch + signature verification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Codex Comp 2 chunk B HIGH RESOLVED. Builds the new
    /// <see cref="RulesetRuntime"/> OFF-LOCK to avoid lock-order hazards
    /// with emit paths re-entering Wire, then publishes via
    /// <see cref="Volatile.Write{T}(ref T, T)"/>. Concurrent readers
    /// observe the swap as a single atomic transition between two
    /// fully-constructed generations.
    /// </para>
    /// <para>
    /// Idempotent: a no-op when Wire hasn't been initialised yet
    /// (<see cref="AttachUnhandledHooks"/> hasn't run); the caller's OTA
    /// pipeline is expected to be off-by-default until init succeeds.
    /// </para>
    /// </remarks>
    public static void SwapRuleset(RulesetV1 next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (!_initialized)
        {
            // OTA tries to swap before Wire init — silent no-op. Worker
            // wraps Init in its own readiness check, but belt-and-suspenders.
            return;
        }

        var nextRuntime = new RulesetRuntime(
            next,
            new PhiScrubber(next, _options.ScrubberTimeout),
            new FingerprintComputer(next, _options.FingerprintTimeout));
        Volatile.Write(ref _runtime, nextRuntime);
        Interlocked.Increment(ref _rulesetSwapsTotal);

        // Emit POST-publication so the recursion-safe ReportSignal path
        // observes the NEW generation. Heartbeat won't fire here (only
        // every 5min from MeshHeartbeatWorker); a synthetic InvariantViolation
        // would also work but pollutes Sentry. Local journal append only.
        try
        {
            _journal?.Append(new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                ["signal_kind"] = "ruleset_swapped",
                ["component"] = _component.ToString(),
                ["ruleset_version"] = next.RulesetVersion,
                ["ruleset_version_int"] = next.RulesetVersionInt,
                ["key_id"] = next.KeyId,
                ["ruleset_swaps_total"] = Interlocked.Read(ref _rulesetSwapsTotal),
            });
        }
        catch
        {
            // Swap succeeded; journal failure is non-fatal. SwapRuleset
            // never throws past this boundary.
        }
    }

    private static void ReportSignal(WireSignal signal)
    {
        // ── Recursion contract (Codex §8.1 RESOLVED v1.0) ──
        var depth = _wireDepth.Value + 1;
        _wireDepth.Value = depth;
        try
        {
            if (depth >= 3)
            {
                // Silent drop forbidden on unhandled paths — FailFast
                // terminates the process synchronously with the original
                // exception captured.
                Environment.FailFast(
                    "SuavoAgent.Diagnostics Wire recursive failure (depth>=3)",
                    signal.Exception);
                return;
            }

            if (depth == 2)
            {
                // Handler-self-failure path: emit minimal local-journal-only
                // marker; no scrub on already-scrubbed payload, no Sentry.
                // Codex chunk 1 HIGH: even this best-effort write must be
                // guarded — Dictionary allocation can OOM, _journal.Append
                // could rarely escape its own catch on resource exhaustion.
                Interlocked.Increment(ref _wireHandlerFailedTotal);
                try
                {
                    _journal?.Append(new Dictionary<string, object?>
                    {
                        ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
                        ["signal_kind"] = "wire_handler_failed",
                        ["component"] = signal.Component.ToString(),
                        ["depth"] = 2,
                    });
                }
                catch
                {
                    // Already in degraded state; swallow.
                }
                return;
            }

            // Codex chunk 1 CRITICAL (reclassified as defense-in-depth):
            // even though every internal step has its own try/catch, an
            // outer guard ensures DispatchNormal cannot throw past the
            // recursion-safety boundary. A static-initializer race or
            // OOM during step transitions would otherwise propagate to
            // the AppDomain handler and crash the handler itself.
            try
            {
                DispatchNormal(signal);
            }
            catch
            {
                Interlocked.Increment(ref _wireHandlerFailedTotal);
                // Final swallow — depth-1 handler must not propagate.
            }
        }
        finally
        {
            _wireDepth.Value = Math.Max(0, depth - 1);
        }
    }

    /// <summary>
    /// Handler catch ordering FIXED per Codex §8.1: PhiScrubber first
    /// (fail-closed PHI safety), then FingerprintComputer, then
    /// LocalJournal, then swallow.
    /// </summary>
    private static void DispatchNormal(WireSignal signal)
    {
        // Codex Comp 2 chunk B HIGH: read the runtime ONCE per signal so
        // every dependent step (ruleset, scrubber, fingerprinter) comes
        // from the SAME generation. Re-reading mid-dispatch could observe
        // a swap-in-progress. The local `rt` is held for the duration of
        // this signal; a concurrent SwapRuleset only affects the NEXT
        // signal.
        var rt = Volatile.Read(ref _runtime);

        // 1. PhiScrubber
        string? scrubbedMessage = null;
        try
        {
            var raw = signal.Exception?.Message ?? signal.InvariantId ?? signal.Stage ?? string.Empty;
            scrubbedMessage = rt?.Scrubber.Sanitize(raw);
        }
        catch
        {
            scrubbedMessage = "[SCRUB_FAILED]";
        }

        // 2. FingerprintComputer
        string fingerprint;
        try
        {
            fingerprint = rt?.Fingerprinter.Compute(signal)
                ?? $"fp-fallback|{signal.Component}|{signal.Kind}";
            if (fingerprint.StartsWith("fp-fallback", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _fpFallbackTotal);
            }
        }
        catch
        {
            fingerprint = $"fp-fallback|{signal.Component}|{signal.Kind}";
            Interlocked.Increment(ref _fpFallbackTotal);
        }

        // 3. LocalJournal — always best-effort, never throws past this boundary
        try
        {
            var journalEntry = new Dictionary<string, object?>
            {
                ["ts"] = signal.OccurredAtUtc.ToString("o"),
                ["signal_kind"] = signal.Kind.ToString(),
                ["component"] = signal.Component.ToString(),
                ["fp_v1"] = fingerprint,
                ["exception_type"] = signal.Exception?.GetType().FullName,
                ["scrubbed_message"] = scrubbedMessage,
                ["stage"] = signal.Stage,
                ["invariant_id"] = signal.InvariantId,
                ["exit_code"] = signal.ExitCode,
                ["git_sha"] = BuildContext.DefaultGitSha,
                ["ruleset_version"] = rt?.Ruleset.RulesetVersion ?? "ruleset-uninitialized",
            };
            if (signal.ExtraTags is { Count: > 0 } extras)
            {
                journalEntry["extras"] = extras;
            }
            _journal?.Append(journalEntry);
        }
        catch { /* swallow */ }

        // 4. Local crash-log defense-in-depth (preserves pre-mesh
        //    startup-crash.log path). Heartbeats skip the crash-log to
        //    avoid filling it with non-crash markers.
        if (signal.Kind != WireSignalKind.Heartbeat &&
            !string.IsNullOrEmpty(_options.LocalCrashLogPath))
        {
            LocalJournal.AppendCrashLog(_options.LocalCrashLogPath,
                $"[{signal.Component}/{signal.Kind}] fp={fingerprint} msg={scrubbedMessage}");
        }

        Interlocked.Increment(ref _eventsEmittedTotal);

        // 5. Sentry POST — gated by EmitBudget; fire-and-forget, never
        //    awaited on the crash-handler critical path.
        if (_budget?.TryConsume(signal.Component) == true && _sentry is not null)
        {
            var sentryLevel = signal.Kind switch
            {
                WireSignalKind.Heartbeat => Sentry.SentryLevel.Info,
                WireSignalKind.InvariantViolation => Sentry.SentryLevel.Error,
                _ => Sentry.SentryLevel.Error,
            };
            if (signal.Exception is not null)
            {
                _sentry.CaptureException(
                    signal.Exception,
                    fingerprint,
                    signal.Component,
                    signal.Stage,
                    signal.ExtraTags);
            }
            else
            {
                _sentry.CaptureMessage(
                    scrubbedMessage ?? signal.InvariantId ?? "(no message)",
                    fingerprint,
                    signal.Component,
                    sentryLevel,
                    signal.ExtraTags);
            }
        }
    }

    private static void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportException(_component, ex, stage: "AppDomain.UnhandledException");
        }
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Codex chunk 1 MEDIUM: SetObserved MUST run even if ReportSignal
        // throws (it shouldn't post-fix, but belt-and-suspenders). Without
        // this, the task would re-fire UnobservedTaskException → double
        // capture or process termination.
        try
        {
            ReportSignal(new WireSignal
            {
                Component = _component,
                Kind = WireSignalKind.UnobservedTask,
                Exception = e.Exception,
                Stage = "TaskScheduler.UnobservedTaskException",
            });
        }
        finally
        {
            e.SetObserved();
        }
    }
}
