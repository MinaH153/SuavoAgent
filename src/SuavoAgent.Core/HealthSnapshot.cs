using System.Diagnostics;
using System.Text.Json;
using SuavoAgent.Core.Config;
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

    public HealthSnapshot(AgentOptions options, AgentStateDb stateDb,
        IServiceProvider sp, DateTimeOffset startTime)
    {
        _options = options;
        _stateDb = stateDb;
        _sp = sp;
        _startTime = startTime;
    }

    public JsonElement Take()
    {
        var rxWorker = _sp.GetService(typeof(RxDetectionWorker)) as RxDetectionWorker;
        var ipcServer = _sp.GetService(typeof(IpcPipeServer)) as IpcPipeServer;
        var canaryHold = _stateDb.GetCanaryHold(_options.PharmacyId ?? "", "pioneerrx");
        var wbEngine = rxWorker?.WritebackEngine;
        var learningSessionId = _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");

        var snapshot = new
        {
            agentId = _options.AgentId,
            version = _options.Version,
            pharmacyId = _options.PharmacyId,
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
            memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
            sql = new
            {
                connected = rxWorker?.IsSqlConnected ?? false,
                lastRxCount = rxWorker?.LastDetectedCount ?? 0,
                lastDetectionTime = rxWorker?.LastDetectionTime?.ToString("o")
            },
            helper = new
            {
                attached = ipcServer?.IsConnected ?? false
            },
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
            writebackEngine = new
            {
                enabled = wbEngine?.WritebackEnabled ?? false,
                triggerDetected = wbEngine?.TriggerDetected ?? false,
            },
            behavioral = learningSessionId is not null
                ? (object)new
                {
                    sessionId = learningSessionId,
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
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        var json = JsonSerializer.Serialize(snapshot);
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
