namespace SuavoAgent.Core.Health;

/// <summary>
/// What state PioneerRx is actually in, from the agent's point of view.
///
/// The agent already collects the raw signals each heartbeat — is the PMS installed? is its database
/// reachable? is the screen helper attached? Until now those were three disconnected booleans, and a
/// freshly-installed agent that couldn't proceed failed by the ABSENCE of expected elements: silently.
/// This turns the signals into ONE situation the agent UNDERSTANDS and can EXPLAIN, so an operator who
/// just installed it sees "PioneerRx is closed" or "database unreachable" instead of nothing.
///
/// Deliberately covers only the states the agent can know reliably today. Semantic screen states
/// (at the login screen, on the wrong tab) require grounding against real PioneerRx screens and are a
/// later rung — they must not be guessed, or the agent would "understand" a situation that isn't real.
/// </summary>
public enum PmsSituation
{
    Unknown,
    NotInstalled,
    DatabaseUnreachable,
    HelperDetached,
    Operational,
}

/// <summary>The raw per-heartbeat signals the agent already has.</summary>
public readonly record struct PmsSignals(bool PmsInstalled, bool SqlConnected, bool HelperAttached);

/// <summary>The classified situation: a stable machine code + an operator-facing explanation.</summary>
public readonly record struct PmsSituationReport(PmsSituation Situation, string Code, string Explanation);

public static class PmsSituationClassifier
{
    /// <summary>Pure: the agent's understanding of the PMS situation from its current signals.</summary>
    public static PmsSituationReport Classify(PmsSignals s)
    {
        if (!s.PmsInstalled)
            return new(PmsSituation.NotInstalled, "pms_not_installed",
                "PioneerRx is not installed on this machine — there is nothing for the agent to observe here.");

        if (!s.SqlConnected)
            return new(PmsSituation.DatabaseUnreachable, "pms_db_unreachable",
                "PioneerRx is installed, but its database is unreachable — the PMS database service may be stopped, " +
                "or the agent's SQL connection needs attention. No prescriptions can be detected until it reconnects.");

        if (!s.HelperAttached)
            return new(PmsSituation.HelperDetached, "pms_helper_detached",
                "PioneerRx data is reachable and detection is running, but the agent's screen helper isn't attached — " +
                "the PioneerRx window may be closed or on another desktop. SQL-based detection still works; on-screen " +
                "actions (pricing, navigation) are unavailable until it reconnects.");

        return new(PmsSituation.Operational, "pms_operational",
            "PioneerRx is reachable and the agent is observing it — fully operational.");
    }
}
