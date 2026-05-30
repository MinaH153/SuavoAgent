using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Diagnostics;

/// <summary>
/// Builds the PHI-safe <c>fetch_diagnostics</c> snapshot the agent posts back to
/// the cloud so Claude/an operator can debug a box's wiring (config, SQL
/// connectivity, helper/IPC health, error-mesh) WITHOUT remote access to the
/// PHI workstation — the agent-as-bridge model.
///
/// This is a pure function of its inputs (no service-locator, no statics) so the
/// secret-exclusion + PHI-guard invariants are unit-testable. The caller
/// (<c>HeartbeatWorker.HandleFetchDiagnosticsAsync</c>) gathers the live inputs.
///
/// HARD RULE: only safe scalars + infra identifiers go in. Secrets
/// (<see cref="AgentOptions.ApiKey"/>, <see cref="AgentOptions.SqlPassword"/>,
/// <see cref="AgentOptions.HmacSalt"/>, <see cref="AgentOptions.CloudCertPin"/>)
/// are NEVER referenced here. SQL server/database/user are infrastructure
/// identifiers (not PHI) and are needed to confirm the pricing SQL is wired.
/// </summary>
public static class DiagnosticsSnapshotBuilder
{
    public const string Schema = "suavo.agent.diagnostics.v1";

    /// <summary>PHI-safe view of SQL connectivity (no password).</summary>
    public sealed record SqlDiagnostics(
        bool Configured,
        bool Connected,
        string? Server,
        string? Database,
        string? User);

    /// <summary>Diagnostic-mesh (Sentry/Wire) counters — pure numbers, no PHI.</summary>
    public sealed record WireDiagnostics(
        bool SentryInitialized,
        long EventsEmittedTotal,
        long WireHandlerFailedTotal,
        long SentryEnqueuedTotal,
        long SentryEnqueueFailedTotal,
        long SentryBeforeSendFailedTotal,
        string RulesetVersion);

    public static object Build(
        AgentOptions options,
        SqlDiagnostics sql,
        WireDiagnostics wire,
        object helper,
        object watchdog,
        object runtimeHealth,
        bool commandPipeConnected,
        long uptimeSeconds,
        int processId,
        DateTimeOffset collectedAtUtc)
    {
        return new
        {
            schema = Schema,
            collectedAtUtc = collectedAtUtc.ToString("o"),
            mutated = false,
            agent = new
            {
                version = options.Version,
                updateChannel = options.UpdateChannel,
                uptimeSeconds,
                processId,
            },
            config = new
            {
                cloudUrl = options.CloudUrl,
                agentId = options.AgentId,
                pharmacyId = options.PharmacyId,
                machineFingerprint = options.MachineFingerprint,
                learningMode = options.LearningMode,
                receiptOnlyMode = options.ReceiptOnlyMode,
                pricingExecutor = options.PricingExecutor.ToString(),
                pricingThrottleMs = options.PricingThrottleMs,
                maxDetectionBatchSize = options.MaxDetectionBatchSize,
                autoExecution = new
                {
                    enabled = options.AutoExecution.Enabled,
                    requireConfirmation = options.AutoExecution.RequireConfirmation,
                    writebackEnabled = options.AutoExecution.WritebackEnabled,
                },
                templateLearning = new
                {
                    enabled = options.TemplateLearning.Enabled,
                    mode = options.TemplateLearning.Mode,
                    ruleGeneration = options.TemplateLearning.RuleGeneration,
                },
                pharmacyCount = options.GetEffectivePharmacies().Count,
            },
            sql = new
            {
                configured = sql.Configured,
                connected = sql.Connected,
                server = sql.Server,
                database = sql.Database,
                user = sql.User,
                trustServerCertificate = options.SqlTrustServerCertificate,
            },
            helper,
            commandPipeConnected,
            errorMesh = new
            {
                sentryInitialized = wire.SentryInitialized,
                eventsEmittedTotal = wire.EventsEmittedTotal,
                wireHandlerFailedTotal = wire.WireHandlerFailedTotal,
                sentryEnqueuedTotal = wire.SentryEnqueuedTotal,
                sentryEnqueueFailedTotal = wire.SentryEnqueueFailedTotal,
                sentryBeforeSendFailedTotal = wire.SentryBeforeSendFailedTotal,
                rulesetVersion = wire.RulesetVersion,
            },
            watchdog,
            runtimeHealth,
        };
    }
}
