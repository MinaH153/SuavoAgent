using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Health;

/// <summary>
/// PING-ONLY actuation-readiness prober. Reuses the exact pricing pre-flight logic
/// (<see cref="HelperInteractivePreflight"/>: command-pipe ping + interactive-console-session
/// re-derivation) but NEVER drives the screen — the only IPC it ever sends is
/// <c>IpcCommands.Ping</c>, which the Helper answers inline from its read loop with session
/// diagnostics and zero UIA work.
///
/// Invariants (each one adversarially load-bearing):
///   - NEVER blocks the heartbeat: runs on its own worker timer; the heartbeat only reads the
///     tracker's snapshot.
///   - NEVER queues behind a live job: the shared command pipe is single-instance and the
///     shared client serializes round-trips, so a probe during a pricing lookup would wait
///     ≤30–60s. <see cref="IIpcCommandClient.IsBusy"/> is checked first → busy ⇒ SKIP (carry
///     the last conclusive verdict + a skip reason) — a pipe in active use is not a strand,
///     and reporting "can't act" mid-job would be a false alarm at the worst moment.
///   - Bounded: hard <see cref="ProbeBudget"/> around the whole probe; a wedged wait inside
///     the client can consume at most one budget per cycle.
///   - Fail-closed: any error/timeout ⇒ not-ready snapshot, never a crash, never false-healthy.
///   - Retry-once on the strand signature: a Helper that restarted underneath a stale-connected
///     client pipe yields one spurious null response (the send fails and tears down); a fresh
///     connect+ping immediately after distinguishes "recovered already" from "truly stranded".
/// </summary>
public sealed class ActuationReadinessProbe
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Hard ceiling on one probe: must exceed connect+ping twice (the retry) with slack.</summary>
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(15);

    internal const string SkipPipeBusy = "pipe_busy";
    internal const string SkipProbeTimeoutBusy = "probe_timeout_busy";
    internal const string CodeProbeTimeout = "probe_timeout";
    internal const string CodeProbeError = "probe_error";

    private readonly IIpcCommandClient? _ipc;
    private readonly ActuationReadinessTracker _tracker;
    private readonly ILogger<ActuationReadinessProbe> _logger;

    public ActuationReadinessProbe(
        IIpcCommandClient? ipc,
        ActuationReadinessTracker tracker,
        ILogger<ActuationReadinessProbe> logger)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ipc = ipc; // null on boxes without a command pipe — every probe then reports not-ready
    }

    public Task<ActuationReadinessSnapshot> ProbeOnceAsync(CancellationToken ct) =>
        ProbeOnceAsync(DateTimeOffset.UtcNow, ct);

    internal async Task<ActuationReadinessSnapshot> ProbeOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var prev = _tracker.Current;
        ActuationReadinessSnapshot snapshot;

        if (_ipc is null)
        {
            snapshot = ConclusiveFailure(
                prev, now,
                HelperPreflightCodes.IpcNotConfigured,
                "Helper IPC client not configured on this agent",
                strandClass: false);
        }
        else if (_ipc.IsBusy)
        {
            // A command round-trip is in flight — the pipe is demonstrably in use. Skip,
            // carry the previous verdict, and stamp why. Never bump the strand streak here.
            snapshot = Skip(prev, now, SkipPipeBusy);
        }
        else
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(ProbeBudget);

                var result = await HelperInteractivePreflight
                    .CheckAsync(_ipc, ConnectTimeout, PingTimeout, budget.Token)
                    .ConfigureAwait(false);

                if (!result.Ok && result.Code == HelperPreflightCodes.PingUnanswered)
                {
                    // Strand signature — retry once on a fresh connection (the failed send
                    // already tore the stale pipe down) before declaring it conclusively dead.
                    result = await HelperInteractivePreflight
                        .CheckAsync(_ipc, ConnectTimeout, PingTimeout, budget.Token)
                        .ConfigureAwait(false);
                }

                snapshot = Map(result, prev, now);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // genuine shutdown — never swallowed into a fail-closed snapshot
            }
            catch (OperationCanceledException)
            {
                // Probe budget exhausted. If a job grabbed the client lock mid-probe this is
                // contention, not evidence — a skip. Otherwise something inside the client
                // wedged past every inner timeout: conclusive strand-class failure.
                snapshot = _ipc.IsBusy
                    ? Skip(prev, now, SkipProbeTimeoutBusy)
                    : ConclusiveFailure(
                        prev, now, CodeProbeTimeout,
                        "Actuation probe exceeded its budget — command pipe wedged", strandClass: true);
            }
            catch (Exception ex)
            {
                // Unknown failure: report not-ready (fail-closed) but do NOT count it toward
                // the self-heal streak — restarting the Helper must never thrash on a Core bug.
                _logger.LogWarning(ex, "Actuation readiness probe failed unexpectedly");
                snapshot = ConclusiveFailure(
                    prev, now, CodeProbeError,
                    "Actuation probe errored — readiness unknown, reported not-ready", strandClass: false);
            }
        }

        _tracker.Record(snapshot);
        LogTransition(prev, snapshot);
        return snapshot;
    }

    /// <summary>Pure mapping from a preflight verdict to a snapshot — unit-tested directly.</summary>
    internal static ActuationReadinessSnapshot Map(
        HelperPreflightResult result, ActuationReadinessSnapshot? prev, DateTimeOffset now)
    {
        if (result.Ok)
        {
            return new ActuationReadinessSnapshot(
                CommandPipeResponsive: true,
                IsConsoleInteractive: true,
                HelperSessionId: result.Info?.HelperSessionId ?? result.HelperSessionId,
                ActiveConsoleSessionId: result.Info?.ActiveConsoleSessionId,
                HelperPid: result.Info?.Pid,
                FailureCode: null,
                FailureReason: null,
                LastConclusiveCheckAtUtc: now,
                LastProbeAttemptAtUtc: now,
                SkippedReason: null,
                ConsecutiveStrandFailures: 0);
        }

        if (result.Code == HelperPreflightCodes.NotInteractive)
        {
            // The ping ANSWERED — the pipe works; the Helper is just in the wrong session.
            // Different failure, different owner (the Broker's session-transition kill), so the
            // strand streak resets.
            return new ActuationReadinessSnapshot(
                CommandPipeResponsive: true,
                IsConsoleInteractive: false,
                HelperSessionId: result.Info?.HelperSessionId ?? result.HelperSessionId,
                ActiveConsoleSessionId: result.Info?.ActiveConsoleSessionId,
                HelperPid: result.Info?.Pid,
                FailureCode: result.Code,
                FailureReason: result.Error,
                LastConclusiveCheckAtUtc: now,
                LastProbeAttemptAtUtc: now,
                SkippedReason: null,
                ConsecutiveStrandFailures: 0);
        }

        var strandClass = result.Code is HelperPreflightCodes.PingUnanswered
            or HelperPreflightCodes.PingBadStatus
            or HelperPreflightCodes.PingNoDiagnostics;

        return ConclusiveFailure(prev, now, result.Code ?? CodeProbeError, result.Error, strandClass);
    }

    private static ActuationReadinessSnapshot ConclusiveFailure(
        ActuationReadinessSnapshot? prev, DateTimeOffset now, string? code, string? reason, bool strandClass) =>
        new(
            CommandPipeResponsive: false,
            IsConsoleInteractive: false,
            HelperSessionId: null,
            ActiveConsoleSessionId: null,
            HelperPid: null,
            FailureCode: code,
            FailureReason: reason,
            LastConclusiveCheckAtUtc: now,
            LastProbeAttemptAtUtc: now,
            SkippedReason: null,
            ConsecutiveStrandFailures: strandClass ? (prev?.ConsecutiveStrandFailures ?? 0) + 1 : 0);

    private static ActuationReadinessSnapshot Skip(
        ActuationReadinessSnapshot? prev, DateTimeOffset now, string skipReason) =>
        prev is null
            // Nothing conclusive yet — report not-ready-unknown rather than inventing a verdict.
            ? new ActuationReadinessSnapshot(
                CommandPipeResponsive: false,
                IsConsoleInteractive: false,
                HelperSessionId: null,
                ActiveConsoleSessionId: null,
                HelperPid: null,
                FailureCode: null,
                FailureReason: null,
                LastConclusiveCheckAtUtc: null,
                LastProbeAttemptAtUtc: now,
                SkippedReason: skipReason,
                ConsecutiveStrandFailures: 0)
            // Both skip reasons (SkipPipeBusy / SkipProbeTimeoutBusy) mean the pipe is BUSY — i.e.
            // a live round-trip is holding it, so it is demonstrably WORKING. Reset the strand streak
            // (same rationale the conclusive path uses): carrying it through busy skips lets the
            // composite falsely report unhealthy in the recovery window while the pipe is in active use.
            : prev with { LastProbeAttemptAtUtc = now, SkippedReason = skipReason, ConsecutiveStrandFailures = 0 };

    private void LogTransition(ActuationReadinessSnapshot? prev, ActuationReadinessSnapshot next)
    {
        var was = prev?.Ready;
        if (was == next.Ready || next.SkippedReason is not null) return;
        if (next.Ready)
            _logger.LogInformation(
                "Actuation readiness RECOVERED — Helper pid {Pid} in session {Session} answers the command pipe",
                next.HelperPid, next.HelperSessionId);
        else
            _logger.LogWarning(
                "Actuation readiness LOST — code={Code} ({Reason})",
                next.FailureCode, next.FailureReason);
    }
}
