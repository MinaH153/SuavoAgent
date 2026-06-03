using System.Diagnostics;
using System.Text.Json;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core;

/// <summary>
/// Produces a point-in-time health snapshot for the agent.
/// Consumed by the heartbeat payload and the get_health IPC command.
/// </summary>
public sealed class HealthSnapshot
{
    private readonly AgentOptions _options;
    private readonly AgentStateDb _stateDb;
    private readonly IServiceProvider _sp;
    private readonly DateTimeOffset _startTime;
    private readonly string? _runtimeHealthRoot;

    public HealthSnapshot(AgentOptions options, AgentStateDb stateDb,
        IServiceProvider sp, DateTimeOffset startTime, string? runtimeHealthRoot = null)
    {
        _options = options;
        _stateDb = stateDb;
        _sp = sp;
        _startTime = startTime;
        _runtimeHealthRoot = runtimeHealthRoot;
    }

    public JsonElement Take()
    {
        var rxWorker = _sp.GetService(typeof(RxDetectionWorker)) as RxDetectionWorker;
        var ipcServer = _sp.GetService(typeof(IpcPipeServer)) as IpcPipeServer;
        var canaryHold = _stateDb.GetCanaryHold(_options.PharmacyId ?? "", "pioneerrx");
        var wbEngine = rxWorker?.WritebackEngine;
        var learningSessionId = _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");
        var visionCapture = (_sp.GetService(typeof(VisionCaptureTelemetry)) as VisionCaptureTelemetry)?.Snapshot();
        var (ipcRejectionCount, lastIpcRejectReason, lastIpcRejectAt) = IpcRejectionStats.Snapshot();
        var workerHealth = _sp.GetService(typeof(WorkerHealthRegistry)) as WorkerHealthRegistry;
        var workers = (workerHealth?.Snapshot() ?? Array.Empty<WorkerHealth>())
            .Select(w => new
            {
                name = w.Name,
                restartCount = w.RestartCount,
                escalated = w.Escalated,
                lastFaultUtc = w.LastFaultUtc.ToString("o"),
            })
            .ToArray();

        var snapshot = new
        {
            agentId = _options.AgentId,
            version = _options.Version,
            pharmacyId = _options.PharmacyId,
            machineFingerprint = _options.MachineFingerprint,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
            memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
            runtimeHealth = RuntimeHealthEvidence.Collect(_runtimeHealthRoot),
            sql = new
            {
                connected = rxWorker?.IsSqlConnected ?? false,
                lastRxCount = rxWorker?.LastDetectedCount ?? 0,
                lastDetectionTime = rxWorker?.LastDetectionTime?.ToString("o")
            },
            helper = new
            {
                attached = ipcServer?.IsConnected ?? false,
                ipcRejectionCount,
                lastIpcRejectReason,
                lastIpcRejectAt = lastIpcRejectAt?.ToString("o")
            },
            // Supervised-worker liveness — restart-looping or escalated workers (the cloud's
            // signal for worker-granular remediation, vs. only detecting a fully-silent agent).
            workers,
            writeback = new
            {
                pending = _stateDb.GetPendingWritebacks().Count,
            },
            audit = new
            {
                chainValid = _stateDb.VerifyAuditChain(),
                entryCount = _stateDb.GetAuditEntryCount()
            },
            sync = new
            {
                unsyncedBatches = _stateDb.GetPendingBatches().Count,
                deadLetterCount = _stateDb.GetDeadLetterCount()
            },
            canary = new
            {
                status = canaryHold != null ? "drift_hold" : "clean",
                blockedCycles = canaryHold?.BlockedCycles ?? 0,
            },
            vision = new
            {
                enabled = _options.Vision.Enabled,
                periodicCaptureEnabled = _options.Vision.PeriodicCapture.Enabled,
                capture = visionCapture?.ToPayload(),
            },
            writebackEngine = new
            {
                receiptOnlyMode = _options.ReceiptOnlyMode,
                enabled = wbEngine?.WritebackEnabled ?? false,
                triggerDetected = wbEngine?.TriggerDetected ?? false,
            },
            behavioral = learningSessionId is not null
                ? (object)new
                {
                    sessionId = learningSessionId,
                    pmsVersionHash = (string?)null, // computed from PMS executable during discovery; wired in future pass
                    uniqueScreens = _stateDb.GetUniqueScreenCount(learningSessionId),
                    totalEvents = _stateDb.GetBehavioralEventCount(learningSessionId),
                    treeSnapshotCount = _stateDb.GetBehavioralEventCount(learningSessionId, "tree_snapshot"),
                    interactionEventCount = _stateDb.GetBehavioralEventCount(learningSessionId, "interaction"),
                    keystrokeCategoryCount = _stateDb.GetBehavioralEventCount(learningSessionId, "keystroke"),
                    correlatedActions = _stateDb.GetCorrelatedActionCount(learningSessionId),
                    writebackCandidates = _stateDb.GetWritebackCandidateCount(learningSessionId),
                    learnedRoutines = _stateDb.GetLearnedRoutineCount(learningSessionId),
                    routinesWithWriteback = _stateDb.GetRoutinesWithWritebackCount(learningSessionId),
                    dmvQueryShapes = _stateDb.GetDmvQueryObservations(learningSessionId, 10000).Count,
                    dmvWriteShapes = _stateDb.GetDmvWriteShapeCount(learningSessionId),
                    // TODO: droppedEventCount, dropRatePercent, clockOffsetMs, clockCalibrated, hasDmvAccess
                    // are runtime state held in the live Helper/DmvQueryObserver instances.
                    // Wire via heartbeat telemetry in a future pass — not accessible from HealthSnapshot today.
                    droppedEventCount = 0,
                    dropRatePercent = 0.0,
                    clockOffsetMs = 0,
                    clockCalibrated = false,
                    hasDmvAccess = false,
                }
                : (object)new
                {
                    sessionId = (string?)null,
                    pmsVersionHash = (string?)null,
                    uniqueScreens = 0,
                    totalEvents = 0,
                    treeSnapshotCount = 0,
                    interactionEventCount = 0,
                    keystrokeCategoryCount = 0,
                    correlatedActions = 0,
                    writebackCandidates = 0,
                    learnedRoutines = 0,
                    routinesWithWriteback = 0,
                    dmvQueryShapes = 0,
                    dmvWriteShapes = 0,
                    droppedEventCount = 0,
                    dropRatePercent = 0.0,
                    clockOffsetMs = 0,
                    clockCalibrated = false,
                    hasDmvAccess = false,
                },
            feedback = learningSessionId is not null
                ? (object)new
                {
                    totalEvents = _stateDb.GetFeedbackEventCount(learningSessionId),
                    pendingDirectives = _stateDb.GetPendingFeedbackEvents(learningSessionId).Count,
                    appliedInline = _stateDb.GetFeedbackEventCountByApplier(learningSessionId, "inline"),
                    appliedBatch = _stateDb.GetFeedbackEventCountByApplier(learningSessionId, "batch"),
                    suspendedPromotions = _stateDb.GetSuspendedPromotions(learningSessionId),
                    staleEscalations = _stateDb.GetExpiredStaleCorrelations(learningSessionId, FeedbackEvent.StaleTtlDays)
                        .Select(s => s.CorrelationKey).ToArray(),
                    activeOverrides = _stateDb.GetWindowOverrideCount(learningSessionId),
                }
                : (object)new
                {
                    totalEvents = 0,
                    pendingDirectives = 0,
                    appliedInline = 0,
                    appliedBatch = 0,
                    suspendedPromotions = Array.Empty<string>(),
                    staleEscalations = Array.Empty<string>(),
                    activeOverrides = 0,
                },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        var json = JsonSerializer.Serialize(snapshot);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
