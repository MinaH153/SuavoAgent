using System;
using System.Threading;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Point-in-time verdict on whether the agent's "hands" actually work: can a command cross
/// the Core→Helper command pipe AND is the Helper in the interactive console session? This is
/// the signal the live-box strand made structurally invisible: the legacy health composite
/// reads only the EVENT pipe (Helper→Core, <c>IpcPipeServer.IsConnected</c>), so a Helper
/// whose command-LISTEN path is wedged still reported <c>helper_attached=true</c> /
/// <c>status=healthy</c> while every pricing run failed its pre-flight.
///
/// PHI-free by construction: booleans, Windows session ids, a pid, static reason codes and
/// timestamps — nothing user- or patient-derived ever enters this record.
/// </summary>
/// <param name="CommandPipeResponsive">Ping round-trip over the COMMAND pipe completed within budget.</param>
/// <param name="IsConsoleInteractive">Helper session == active console session != 0, re-derived in
/// Core from the raw ids (never the Helper's self-report).</param>
/// <param name="HelperSessionId">Windows session the Helper runs in (null until a ping has answered).</param>
/// <param name="ActiveConsoleSessionId">Active physical console session at probe time.</param>
/// <param name="HelperPid">Helper process id from the ping diagnostics.</param>
/// <param name="FailureCode">Stable machine code (<see cref="SuavoAgent.Core.Pricing.HelperPreflightCodes"/>) when not ready; null when ready.</param>
/// <param name="FailureReason">Operator-facing static reason when not ready; null when ready.</param>
/// <param name="LastConclusiveCheckAtUtc">When the verdict above was actually EARNED (a probe ran
/// to completion). Stale value + recent <paramref name="LastProbeAttemptAtUtc"/> means probes are
/// being skipped (busy pipe) — the cloud must treat an old conclusive verdict as unknown, not healthy.</param>
/// <param name="LastProbeAttemptAtUtc">When the prober last TRIED (conclusive or skipped).</param>
/// <param name="SkippedReason">Why the most recent attempt was skipped ("pipe_busy", "probe_timeout_busy"),
/// null when the most recent attempt was conclusive.</param>
/// <param name="ConsecutiveStrandFailures">Consecutive CONCLUSIVE strand-class failures (pipe
/// connected but ping dead) — the self-heal trigger. Resets on any conclusive success, stays
/// untouched on skips, and deliberately excludes pipe_unreachable / not_interactive (those have
/// different owners: the Broker's relaunch loop and the session-transition kill).</param>
public sealed record ActuationReadinessSnapshot(
    bool CommandPipeResponsive,
    bool IsConsoleInteractive,
    uint? HelperSessionId,
    uint? ActiveConsoleSessionId,
    int? HelperPid,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset? LastConclusiveCheckAtUtc,
    DateTimeOffset LastProbeAttemptAtUtc,
    string? SkippedReason,
    int ConsecutiveStrandFailures)
{
    /// <summary>The single bit the cockpit renders: the agent can actuate right now.</summary>
    public bool Ready => CommandPipeResponsive && IsConsoleInteractive;
}

/// <summary>
/// Thread-safe holder for the latest <see cref="ActuationReadinessSnapshot"/>. The prober
/// writes (replaces — never mutates) and the HeartbeatWorker reads; the heartbeat path is
/// therefore a lock-free volatile read and can never block on, or be slowed by, a probe.
/// <c>Current</c> is null until the first probe completes (heartbeat emits no actuation block
/// then, which the cloud reads as "agent too old / probe not yet run").
/// </summary>
public sealed class ActuationReadinessTracker
{
    private ActuationReadinessSnapshot? _current;

    public ActuationReadinessSnapshot? Current => Volatile.Read(ref _current);

    public void Record(ActuationReadinessSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
}
