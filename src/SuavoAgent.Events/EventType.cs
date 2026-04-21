namespace SuavoAgent.Events;

/// <summary>
/// Canonical event type names. Every string here must appear in
/// <c>docs/self-healing/event-registry.md</c>. Adding a new type requires a
/// PR updating both this file and the registry doc.
/// </summary>
/// <remarks>
/// Uses dotted-hierarchical notation: <c>&lt;domain&gt;.&lt;sub&gt;.&lt;action&gt;</c>.
/// Stored in the audit chain as-is; cloud API validates against this
/// enumeration before insertion.
/// </remarks>
public static class EventType
{
    // ---- Chain integrity ------------------------------------------------
    public const string ChainGenesis = "chain.genesis";
    public const string ChainVerificationCompleted = "chain.verification_completed";
    public const string ChainRetentionPurge = "chain.retention_purge";
    public const string DigestUploaded = "digest.uploaded";

    // ---- Agent lifecycle ------------------------------------------------
    public const string AgentStarted = "agent.started";
    public const string AgentStopped = "agent.stopped";
    public const string AgentCrashed = "agent.crashed";

    // ---- Windows services -----------------------------------------------
    public const string ServiceRestarted = "service.restarted";
    public const string ServiceFailed = "service.failed";
    public const string ServiceHealthy = "service.healthy";

    // ---- Heartbeat + freshness ------------------------------------------
    public const string HeartbeatEmitted = "heartbeat.emitted";
    public const string HeartbeatSilentAlarm = "heartbeat.silent_alarm";

    // ---- Configuration --------------------------------------------------
    public const string ConfigOverrideApplied = "config.override_applied";
    public const string ConfigRollbackExecuted = "config.rollback_executed";

    // ---- Binary attestation ---------------------------------------------
    public const string AttestationVerified = "attestation.verified";
    public const string AttestationMismatch = "attestation.mismatch";

    // ---- Diagnosis (Phase C) --------------------------------------------
    public const string DiagnosisRequested = "diagnosis.requested";
    public const string ScoutDispatched = "scout.dispatched";
    public const string ScoutReturned = "scout.returned";
    public const string ScoutTimeout = "scout.timeout";
    public const string DiagnosisSynthesized = "diagnosis.synthesized";
    public const string HypothesisRejectedByCharter = "hypothesis.rejected_by_charter";

    // ---- Verbs (Phase D) ------------------------------------------------
    public const string VerbProposed = "verb.proposed";
    public const string VerbPolicyEvaluated = "verb.policy_evaluated";
    public const string VerbApproved = "verb.approved";
    public const string VerbRejected = "verb.rejected";
    public const string VerbSigned = "verb.signed";
    public const string VerbDispatched = "verb.dispatched";
    public const string VerbExecuted = "verb.executed";
    public const string VerbVerified = "verb.verified";
    public const string VerbFailed = "verb.failed";
    public const string VerbRolledBack = "verb.rolled_back";
    public const string VerbRollbackCaptured = "verb.rollback_captured";
    public const string GrammarVersionMismatch = "grammar.version_mismatch";

    // ---- Plans (Phase E) ------------------------------------------------
    public const string PlanDrafted = "plan.drafted";
    public const string PlanReviewed = "plan.reviewed";
    public const string PlanApproved = "plan.approved";
    public const string PlanRejected = "plan.rejected";
    public const string PlanStepExecuted = "plan.step_executed";
    public const string PlanStepFailed = "plan.step_failed";
    public const string PlanCompensated = "plan.compensated";
    public const string PlanCompleted = "plan.completed";

    // ---- Autonomy (Phase F) ---------------------------------------------
    public const string AutonomyGranted = "autonomy.granted";
    public const string AutonomyRevoked = "autonomy.revoked";
    public const string AutonomyThresholdReached = "autonomy.threshold_reached";
    public const string RetrospectiveProposedRule = "retrospective.proposed_rule";
    public const string RetrospectiveProposalApproved = "retrospective.proposal_approved";
    public const string RetrospectiveProposalRejected = "retrospective.proposal_rejected";

    // ---- Consent + BAA --------------------------------------------------
    public const string ConsentRequested = "consent.requested";
    public const string ConsentGranted = "consent.granted";
    public const string ConsentExpired = "consent.expired";
    public const string ConsentRevoked = "consent.revoked";
    public const string BaaAmendmentApplied = "baa.amendment_applied";
    public const string BaaAmendmentReverted = "baa.amendment_reverted";

    // ---- Key lifecycle --------------------------------------------------
    public const string KeyRotated = "key.rotated";
    public const string KeyRevoked = "key.revoked";
    public const string KeySuspectedCompromise = "key.suspected_compromise";

    // ---- Kill switch ----------------------------------------------------
    public const string KillSwitchTriggered = "kill_switch.triggered";
    public const string KillSwitchCleared = "kill_switch.cleared";

    // ---- Invariants -----------------------------------------------------
    public const string InvariantViolated = "invariant.violated";
    public const string InvariantViolationResolved = "invariant.violation_resolved";

    // ---- Federated mesh (Phase G) ---------------------------------------
    public const string SignatureEmitted = "signature.emitted";
    public const string SignaturePatternMatch = "signature.pattern_match";
    public const string SignaturePatternNovel = "signature.pattern_novel";
    public const string FedMeshPrivacyBudgetConsumed = "fed_mesh.privacy_budget_consumed";
    public const string FedMeshBudgetExhausted = "fed_mesh.budget_exhausted";

    // ---- Crash aggregation ----------------------------------------------
    public const string CrashLogUploaded = "crash.log_uploaded";

    /// <summary>
    /// All canonical types. Used at cloud ingest to validate incoming
    /// <c>type</c> strings. Any type not in this set is rejected.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        ChainGenesis, ChainVerificationCompleted, ChainRetentionPurge, DigestUploaded,
        AgentStarted, AgentStopped, AgentCrashed,
        ServiceRestarted, ServiceFailed, ServiceHealthy,
        HeartbeatEmitted, HeartbeatSilentAlarm,
        ConfigOverrideApplied, ConfigRollbackExecuted,
        AttestationVerified, AttestationMismatch,
        DiagnosisRequested, ScoutDispatched, ScoutReturned, ScoutTimeout,
        DiagnosisSynthesized, HypothesisRejectedByCharter,
        VerbProposed, VerbPolicyEvaluated, VerbApproved, VerbRejected,
        VerbSigned, VerbDispatched, VerbExecuted, VerbVerified,
        VerbFailed, VerbRolledBack, VerbRollbackCaptured, GrammarVersionMismatch,
        PlanDrafted, PlanReviewed, PlanApproved, PlanRejected,
        PlanStepExecuted, PlanStepFailed, PlanCompensated, PlanCompleted,
        AutonomyGranted, AutonomyRevoked, AutonomyThresholdReached,
        RetrospectiveProposedRule, RetrospectiveProposalApproved, RetrospectiveProposalRejected,
        ConsentRequested, ConsentGranted, ConsentExpired, ConsentRevoked,
        BaaAmendmentApplied, BaaAmendmentReverted,
        KeyRotated, KeyRevoked, KeySuspectedCompromise,
        KillSwitchTriggered, KillSwitchCleared,
        InvariantViolated, InvariantViolationResolved,
        SignatureEmitted, SignaturePatternMatch, SignaturePatternNovel,
        FedMeshPrivacyBudgetConsumed, FedMeshBudgetExhausted,
        CrashLogUploaded
    };

    public static bool IsKnown(string type) => All.Contains(type);
}
