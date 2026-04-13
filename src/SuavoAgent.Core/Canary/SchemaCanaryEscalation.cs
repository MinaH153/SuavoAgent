using SuavoAgent.Contracts.Canary;

namespace SuavoAgent.Core.Canary;

public record CanaryHoldState(
    bool IsInHold,
    CanarySeverity EffectiveSeverity,
    int BlockedCycles,
    int ConsecutiveWarnings,
    string? AcknowledgedBy)
{
    public static CanaryHoldState Clear { get; } = new(false, CanarySeverity.None, 0, 0, null);
    public bool ShouldAlertDashboard => BlockedCycles >= 3;
    public bool ShouldAlertPhone => BlockedCycles >= 12;
}

public static class SchemaCanaryEscalation
{
    private const int WarningEscalationThreshold = 3;

    public static CanaryHoldState Transition(CanaryHoldState current, CanarySeverity severity)
    {
        return severity switch
        {
            CanarySeverity.None => current.IsInHold
                ? current // stay in hold until ack
                : current with { ConsecutiveWarnings = 0 },

            CanarySeverity.Warning when !current.IsInHold =>
                current.ConsecutiveWarnings + 1 >= WarningEscalationThreshold
                    ? current with
                    {
                        IsInHold = true,
                        EffectiveSeverity = CanarySeverity.Critical,
                        BlockedCycles = 1,
                        ConsecutiveWarnings = current.ConsecutiveWarnings + 1,
                    }
                    : current with { ConsecutiveWarnings = current.ConsecutiveWarnings + 1 },

            CanarySeverity.Critical => current.IsInHold
                ? current with { BlockedCycles = current.BlockedCycles + 1 }
                : current with
                {
                    IsInHold = true,
                    EffectiveSeverity = CanarySeverity.Critical,
                    BlockedCycles = 1,
                    ConsecutiveWarnings = 0,
                },

            CanarySeverity.Warning => current with
            {
                BlockedCycles = current.BlockedCycles + 1,
                ConsecutiveWarnings = current.ConsecutiveWarnings + 1,
            },

            _ => current,
        };
    }

    public static CanaryHoldState Acknowledge(CanaryHoldState current, string acknowledgedBy)
        => CanaryHoldState.Clear with { AcknowledgedBy = acknowledgedBy };
}
