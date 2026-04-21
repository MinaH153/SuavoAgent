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
    int RepairInvocations)
{
    public static ServiceLedger Initial(string name, DateTimeOffset now) =>
        new(name, ServiceState.Unknown, now, null, null, 0, 0);
}

public sealed class WatchdogDecisionEngine
{
    public TimeSpan UnhealthyGrace { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(60);
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
            // Healthy observation → reset failure counter (repair counter persists).
            next = next with { ConsecutiveRestartFailures = 0 };
            return (new(DecisionAction.DoNothing, "running"), next);
        }

        if (observed == ServiceState.NotInstalled)
        {
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
        if (unhealthySince is not null && now - unhealthySince < UnhealthyGrace)
        {
            return (new(DecisionAction.DoNothing, $"unhealthy < grace ({UnhealthyGrace.TotalMinutes}m)"), next);
        }

        if (ledger.ConsecutiveRestartFailures >= EscalateAfterConsecutiveFailures)
        {
            return (new(DecisionAction.EscalateRepair,
                    $"{ledger.ConsecutiveRestartFailures} consecutive restart failures"),
                next with
                {
                    LastRestartAttemptAt = now,
                    RepairInvocations = ledger.RepairInvocations + 1,
                    ConsecutiveRestartFailures = 0
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
