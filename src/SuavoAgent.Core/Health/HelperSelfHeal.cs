using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Health;

/// <summary>Result of one self-heal consideration, with the machine reason either way.</summary>
public sealed record SelfHealDecision(bool Trigger, string Reason)
{
    public static SelfHealDecision No(string reason) => new(false, reason);
    public static SelfHealDecision Yes() => new(true, "strand_streak_threshold");
}

/// <summary>Self-heal state mirrored into the heartbeat so the cloud sees the auto-recovery posture.</summary>
public sealed record SelfHealState(DateTimeOffset? LastAttemptAtUtc, int AttemptsInWindow, bool Exhausted);

/// <summary>
/// PURE self-heal policy — when may Core automatically ask the Broker to relaunch the Helper?
/// Deliberately narrow: it fires ONLY on the observed live-box failure (command pipe
/// connected/connectable but ping conclusively dead — a strand the Broker's process-liveness
/// watch is structurally blind to). It never fires on:
///   - skipped probes (busy pipe = a job is using it),
///   - pipe_unreachable (Helper down/absent — the Broker's relaunch loop already owns that;
///     if the Broker itself is dead, a sentinel nobody reads helps nothing),
///   - not_interactive (the ping works; the session-transition kill in the Broker owns that),
///   - probe_error (a Core-side bug must never thrash the Helper).
/// Bounds: K consecutive strand verdicts, a minimum spacing between attempts, and a hard cap
/// per rolling 24h after which it goes quiet (CRITICAL log + heartbeat flag) and leaves the
/// decision to a human via restart_helper.
/// </summary>
public static class HelperSelfHealPolicy
{
    /// <summary>Consecutive conclusive strand failures required (~3 probe cycles ≈ 3 minutes).</summary>
    public const int MinStrandStreak = 3;
    /// <summary>Minimum spacing between automatic restarts — no thrash.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    /// <summary>Hard cap on automatic restarts per rolling window.</summary>
    public const int MaxAttemptsPerWindow = 4;
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromHours(24);

    public static SelfHealDecision Decide(
        ActuationReadinessSnapshot snapshot,
        IReadOnlyList<DateTimeOffset> attemptsInWindow,
        DateTimeOffset now)
    {
        if (snapshot.SkippedReason is not null)
            return SelfHealDecision.No("probe_skipped");
        if (snapshot.CommandPipeResponsive)
            return SelfHealDecision.No("pipe_responsive");
        if (snapshot.ConsecutiveStrandFailures < MinStrandStreak)
            return SelfHealDecision.No("streak_below_threshold");
        if (attemptsInWindow.Count >= MaxAttemptsPerWindow)
            return SelfHealDecision.No("exhausted");
        if (attemptsInWindow.Count > 0 && now - attemptsInWindow.Max() < MinInterval)
            return SelfHealDecision.No("backoff");
        return SelfHealDecision.Yes();
    }

    /// <summary>Attempts still inside the rolling window (pure, for pruning state).</summary>
    public static IReadOnlyList<DateTimeOffset> Prune(IEnumerable<DateTimeOffset> attempts, DateTimeOffset now) =>
        attempts.Where(a => now - a < AttemptWindow).OrderBy(a => a).ToArray();
}

/// <summary>
/// Stateful wrapper around <see cref="HelperSelfHealPolicy"/>: tracks attempt history and, on
/// a Yes, performs the two audited side effects — write the <see cref="HelperRestartRequest"/>
/// sentinel and append the audit entry (both injected so the state machine is unit-testable
/// with zero filesystem). Thread-safe; replaces its attempt list immutably under a lock.
/// </summary>
public sealed class HelperSelfHealCoordinator
{
    private readonly object _gate = new();
    private readonly Action<HelperRestartRequest.Payload> _writeSentinel;
    private readonly Action<string> _audit;
    private readonly ILogger<HelperSelfHealCoordinator> _logger;
    private IReadOnlyList<DateTimeOffset> _attempts = Array.Empty<DateTimeOffset>();
    private bool _exhaustionLogged;

    public HelperSelfHealCoordinator(
        Action<HelperRestartRequest.Payload> writeSentinel,
        Action<string> audit,
        ILogger<HelperSelfHealCoordinator> logger)
    {
        _writeSentinel = writeSentinel ?? throw new ArgumentNullException(nameof(writeSentinel));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public SelfHealState Snapshot()
    {
        lock (_gate)
        {
            return new SelfHealState(
                _attempts.Count > 0 ? _attempts.Max() : null,
                _attempts.Count,
                _attempts.Count >= HelperSelfHealPolicy.MaxAttemptsPerWindow);
        }
    }

    /// <summary>
    /// Considers one conclusive probe snapshot. Returns the decision (for tests/telemetry).
    /// Side effects (sentinel + audit) happen only on Trigger=true and never throw out —
    /// a failed sentinel write is logged and the attempt is NOT recorded (so it retries).
    /// </summary>
    public SelfHealDecision Consider(ActuationReadinessSnapshot snapshot, DateTimeOffset now)
    {
        SelfHealDecision decision;
        lock (_gate)
        {
            _attempts = HelperSelfHealPolicy.Prune(_attempts, now);
            decision = HelperSelfHealPolicy.Decide(snapshot, _attempts, now);

            if (!decision.Trigger)
            {
                if (decision.Reason == "exhausted" && !_exhaustionLogged)
                {
                    _exhaustionLogged = true;
                    _logger.LogCritical(
                        "Helper self-heal EXHAUSTED ({Max} restarts in {Window}) — command pipe still stranded " +
                        "(code={Code}). Going quiet; use the restart_helper command or investigate the box.",
                        HelperSelfHealPolicy.MaxAttemptsPerWindow, HelperSelfHealPolicy.AttemptWindow,
                        snapshot.FailureCode);
                }
                return decision;
            }

            try
            {
                var reason = $"self_heal:{snapshot.FailureCode ?? "strand"}";
                _writeSentinel(new HelperRestartRequest.Payload(now, reason, "self_heal", null));
                _audit(reason);
                _attempts = _attempts.Append(now).ToArray();
                _exhaustionLogged = false;
                _logger.LogWarning(
                    "Helper self-heal TRIGGERED (attempt {N}/{Max} in window) — strand streak {Streak}, code={Code}. " +
                    "Restart sentinel written for the Broker.",
                    _attempts.Count, HelperSelfHealPolicy.MaxAttemptsPerWindow,
                    snapshot.ConsecutiveStrandFailures, snapshot.FailureCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Helper self-heal sentinel write failed — will retry next probe cycle");
                return SelfHealDecision.No("sentinel_write_failed");
            }
        }
        return decision;
    }
}
