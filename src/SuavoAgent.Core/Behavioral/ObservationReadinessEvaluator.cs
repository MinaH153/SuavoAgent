using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

public sealed record ObservationReadinessInput(
    DateTimeOffset NowUtc,
    bool HelperAttached,
    AgentStateDb.BehavioralDeliveryHealth Delivery,
    AgentStateDb.ObserverStatusSnapshot? PioneerRx,
    AgentStateDb.ObserverStatusSnapshot? SystemLiveness,
    AgentStateDb.ObserverStatusSnapshot? BrowserDomain,
    AgentStateDb.ObserverStatusSnapshot? Print,
    AgentStateDb.ObserverStatusSnapshot? UserSession,
    AgentStateDb.ObserverStatusSnapshot? MultiAppUia);

public sealed record ObservationReadinessAssessment(
    bool ProtocolCompatible,
    bool Lossless,
    bool SpoolHealthy,
    bool PmsChannelLive,
    bool SystemChannelLive,
    bool PioneerRxReady,
    bool BrowserConnectorReady,
    bool PrintObserverReady,
    bool UserSessionObserverReady,
    bool MultiAppUiaReady,
    bool LiveObservationReady,
    string Code,
    IReadOnlyList<string> Blockers);

/// <summary>
/// One truthful readiness decision for the observation subsystem. Historical
/// batches prove compatibility only; they never imply that observation is live.
/// </summary>
public static class ObservationReadinessEvaluator
{
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(90);

    public static ObservationReadinessAssessment Evaluate(ObservationReadinessInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var delivery = input.Delivery;
        var protocolCompatible = delivery.VerifiedBatchCount > 0
            && delivery.LastVerifiedBatchUtc.HasValue;
        var lossless = delivery.DroppedEventCount == 0
            && delivery.SequenceGapCount == 0
            && delivery.RejectedEventCount == 0;
        var spoolHealthy = string.Equals(
                delivery.ObservationSpoolStatus,
                "observation_spool_healthy",
                StringComparison.Ordinal)
            && IsFresh(delivery.LastObservationSpoolStatusUtc, input.NowUtc);
        var pmsChannelLive = delivery.PmsStreamCount > 0
            && IsFresh(delivery.LastVerifiedPmsBatchUtc, input.NowUtc);
        var systemChannelLive = delivery.SystemStreamCount > 0
            && IsFresh(delivery.LastVerifiedSystemBatchUtc, input.NowUtc);
        var pioneerReady = input.HelperAttached
            && HasFreshStatus(input.PioneerRx, input.NowUtc, "attached");
        var systemReady = input.HelperAttached
            && HasFreshStatus(input.SystemLiveness, input.NowUtc, "ready");
        var browserReady = HasFreshStatus(input.BrowserDomain, input.NowUtc, "ready");
        var printReady = HasFreshStatus(input.Print, input.NowUtc, "ready", "recovered");
        var sessionReady = HasFreshStatus(input.UserSession, input.NowUtc, "ready");
        var multiAppReady = HasFreshStatus(input.MultiAppUia, input.NowUtc, "ready", "recovered");

        var blockers = new List<string>();
        if (!input.HelperAttached) blockers.Add("helper_disconnected");
        if (!protocolCompatible) blockers.Add("delivery_protocol_unverified");
        if (!lossless) blockers.Add("delivery_loss_detected");
        if (!spoolHealthy) blockers.Add("observation_spool_not_healthy");
        if (!pmsChannelLive) blockers.Add("pms_channel_stale");
        if (!systemChannelLive || !systemReady) blockers.Add("system_channel_stale");
        if (!pioneerReady) blockers.Add("pioneerrx_observer_not_ready");
        if (!browserReady) blockers.Add("browser_connector_not_ready");
        if (!printReady) blockers.Add("print_observer_not_ready");
        if (!sessionReady) blockers.Add("session_observer_not_ready");
        if (!multiAppReady) blockers.Add("multi_app_uia_not_ready");

        var ready = blockers.Count == 0;
        return new ObservationReadinessAssessment(
            protocolCompatible,
            lossless,
            spoolHealthy,
            pmsChannelLive,
            systemChannelLive && systemReady,
            pioneerReady,
            browserReady,
            printReady,
            sessionReady,
            multiAppReady,
            ready,
            ready ? "ready" : blockers[0],
            blockers);
    }

    private static bool HasFreshStatus(
        AgentStateDb.ObserverStatusSnapshot? snapshot,
        DateTimeOffset nowUtc,
        params string[] acceptedStatuses) =>
        snapshot is not null
        && IsFresh(snapshot.ReceivedAtUtc, nowUtc)
        && acceptedStatuses.Contains(snapshot.Status, StringComparer.Ordinal);

    private static bool IsFresh(DateTimeOffset? timestamp, DateTimeOffset nowUtc) =>
        timestamp.HasValue
        && timestamp.Value <= nowUtc + TimeSpan.FromSeconds(5)
        && nowUtc - timestamp.Value <= FreshnessWindow;
}
