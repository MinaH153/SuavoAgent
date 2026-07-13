namespace SuavoAgent.Watchdog;

public enum DecisionAction
{
    DoNothing,
    ObserveStartPending,
    AttemptRestart,
    EscalateRepair,
    Alert
}

public sealed record WatchdogDecision(DecisionAction Action, string Reason);

/// <summary>
/// Per-service health ledger kept across polling ticks. The engine is pure —
/// give it the current observation + previous ledger and it returns the next
/// action + updated ledger.
/// </summary>
public sealed record ServiceLedger(
    string ServiceName,
    ServiceState LastObservedState,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? UnhealthySince,
    DateTimeOffset? LastRestartAttemptAt,
    int ConsecutiveRestartFailures,
    int RepairInvocations,
    // True after a restart the SCM ACCEPTED (START_PENDING) but whose liveness we haven't confirmed.
    // The next tick counts it as a failure if the service still isn't Running — SCM "accepted" is not
    // proof the process stayed up, so a crash-loop (accept → die → accept …) is caught here.
    bool RestartPendingLiveness = false)
{
    public static ServiceLedger Initial(string name, DateTimeOffset now) =>
        new(name, ServiceState.Unknown, now, null, null, 0, 0);
}

public sealed class WatchdogDecisionEngine
{
    public TimeSpan UnhealthyGrace { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(30);
    // QA C3: cooldown between native maintenance repair escalations on a NotInstalled service, so
    // privileged repair cannot be re-run on every ~15s poll tick in a tight loop.
    public TimeSpan RepairBackoff { get; init; } = TimeSpan.FromMinutes(5);
    public int EscalateAfterConsecutiveFailures { get; init; } = 3;

    public (WatchdogDecision Decision, ServiceLedger NextLedger) Decide(
        ServiceLedger ledger,
        ServiceState observed,
        DateTimeOffset now)
    {
        var unhealthySince = ledger.UnhealthySince;
        if (observed == ServiceState.Running)
        {
            unhealthySince = null;
        }
        else if (unhealthySince is null)
        {
            unhealthySince = now;
        }

        var next = ledger with
        {
            LastObservedState = observed,
            LastObservedAt = now,
            UnhealthySince = unhealthySince
        };

        if (observed == ServiceState.Running)
        {
            // Healthy observation → reset failure counter + clear any pending-liveness attempt (it
            // succeeded) so a later crash isn't blamed on the now-confirmed restart. Repair counter persists.
            next = next with { ConsecutiveRestartFailures = 0, RestartPendingLiveness = false, LastRestartAttemptAt = null };
            return (new(DecisionAction.DoNothing, "running"), next);
        }

        if (observed == ServiceState.NotInstalled)
        {
            // QA C3: without this, NotInstalled escalates native repair on EVERY poll
            // tick (~15s) forever. Hold if a repair/restart was attempted within RepairBackoff — the
            // branch sets LastRestartAttemptAt below on each repair, so it self-throttles to one
            // maintenance run per RepairBackoff window instead of a tight loop.
            if (ledger.LastRestartAttemptAt is { } lastRepair && now - lastRepair < RepairBackoff)
            {
                return (new(DecisionAction.DoNothing,
                    $"repair backoff ({RepairBackoff.TotalMinutes}min) — service not installed"), next);
            }
            return (new(DecisionAction.EscalateRepair, "service not installed"),
                next with
                {
                    LastRestartAttemptAt = now,
                    RepairInvocations = ledger.RepairInvocations + 1,
                    ConsecutiveRestartFailures = 0
                });
        }

        if (observed == ServiceState.StartPending)
        {
            // Service is mid-start. Let Windows finish — don't race it.
            return (new(DecisionAction.ObserveStartPending, "start_pending"), next);
        }

        // Stopped / StopPending / Unknown — unhealthy.
        // A prior restart the SCM ACCEPTED (START_PENDING) that has NOT reached Running is a crash-loop
        // failure. Count it once and clear the flag — the SCM "accepted" return never reflected liveness,
        // so without this the counter stayed 0 forever and EscalateRepair was unreachable. (A REJECTED
        // start is already counted by the worker via RecordRestartResult(false), so it never sets this flag.)
        var failures = ledger.ConsecutiveRestartFailures;
        if (ledger.RestartPendingLiveness)
        {
            failures += 1;
            next = next with { ConsecutiveRestartFailures = failures, RestartPendingLiveness = false };
        }

        if (unhealthySince is not null && now - unhealthySince < UnhealthyGrace)
        {
            return (new(DecisionAction.DoNothing, $"unhealthy < grace ({UnhealthyGrace.TotalMinutes}m)"), next);
        }

        if (failures >= EscalateAfterConsecutiveFailures)
        {
            return (new(DecisionAction.EscalateRepair,
                    $"{failures} consecutive restart failures"),
                next with
                {
                    LastRestartAttemptAt = now,
                    RepairInvocations = ledger.RepairInvocations + 1,
                    ConsecutiveRestartFailures = 0,
                    RestartPendingLiveness = false
                });
        }

        if (ledger.LastRestartAttemptAt is { } lastAttempt && now - lastAttempt < RestartBackoff)
        {
            return (new(DecisionAction.DoNothing,
                $"restart backoff ({RestartBackoff.TotalSeconds}s)"), next);
        }

        return (new(DecisionAction.AttemptRestart, "attempting sc.exe start"),
            next with { LastRestartAttemptAt = now });
    }

    public ServiceLedger RecordRestartResult(ServiceLedger ledger, bool succeeded)
    {
        return succeeded
            ? ledger with { ConsecutiveRestartFailures = 0 }
            : ledger with { ConsecutiveRestartFailures = ledger.ConsecutiveRestartFailures + 1 };
    }
}
