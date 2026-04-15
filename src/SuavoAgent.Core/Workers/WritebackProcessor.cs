using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx.Sql;
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed class WritebackProcessor : BackgroundService
{
    private readonly ILogger<WritebackProcessor> _logger;
    private readonly AgentStateDb _stateDb;
    private readonly IpcPipeServer _pipeServer;
    private readonly AgentOptions _options;
    private readonly Dictionary<string, WritebackStateMachine> _machines = new();
    private readonly Dictionary<string, string> _rxNumbers = new();
    private readonly Dictionary<string, string> _transitions = new();
    private readonly Dictionary<string, DateTimeOffset?> _deliveredAts = new();

    private PioneerRxWritebackEngine? _writebackEngine;
    private string? _sessionId;

    public int ProcessIntervalSeconds { get; set; } = 30;
    public int ActiveMachineCount => _machines.Count(m => !m.Value.IsTerminal);

    public WritebackProcessor(
        ILogger<WritebackProcessor> logger,
        AgentStateDb stateDb,
        IpcPipeServer pipeServer,
        IOptions<AgentOptions> options)
    {
        _logger = logger;
        _stateDb = stateDb;
        _pipeServer = pipeServer;
        _options = options.Value;
    }

    public void SetWritebackEngine(PioneerRxWritebackEngine engine)
    {
        _writebackEngine = engine;
        _logger.LogInformation("Writeback engine attached (enabled={Enabled})", engine.WritebackEnabled);
    }

    public void SetSessionId(string sessionId)
    {
        _sessionId = sessionId;
        _logger.LogInformation("Session ID attached to WritebackProcessor: {SessionId}", sessionId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Writeback processor started");

        RecoverPendingWritebacks();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingWritebacksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Writeback processing cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(ProcessIntervalSeconds), stoppingToken);
        }

        _logger.LogInformation("Writeback processor stopped");
    }

    private void RecoverPendingWritebacks()
    {
        var pending = _stateDb.GetDueWritebacks();
        foreach (var (taskId, state, rxNumber, retryCount, _) in pending)
        {
            _logger.LogInformation("Recovering writeback {TaskId} in state {State} (retries: {Retries})",
                taskId, state, retryCount);

            var machine = new WritebackStateMachine(taskId, state, OnStateChanged, retryCount);
            _machines[taskId] = machine;
        }
        _logger.LogInformation("Recovered {Count} pending writebacks", pending.Count);
    }

    public void EnqueueWriteback(string taskId, string rxNumber, int fillNumber = 0,
        string transition = "pickup", DateTimeOffset? deliveredAt = null)
    {
        if (_machines.ContainsKey(taskId))
        {
            _logger.LogDebug("Writeback {TaskId} already tracked", taskId);
            return;
        }

        var machine = new WritebackStateMachine(taskId, WritebackState.Queued, OnStateChanged);
        _machines[taskId] = machine;
        _rxNumbers[taskId] = rxNumber;
        _transitions[taskId] = transition;
        _deliveredAts[taskId] = deliveredAt;
        _stateDb.UpsertWritebackState(taskId, rxNumber, WritebackState.Queued, 0, null);
        _logger.LogInformation("Enqueued writeback {TaskId} for Rx {RxHash} transition={Transition}",
            taskId, PhiScrubber.HmacHash(rxNumber, _options.HmacSalt ?? ""), transition);
    }

    private async Task ProcessPendingWritebacksAsync(CancellationToken ct)
    {
        if (_options.ReceiptOnlyMode)
        {
            _logger.LogDebug("ReceiptOnlyMode — skipping writeback processing");
            return;
        }

        var queued = _machines
            .Where(m => m.Value.CurrentState == WritebackState.Queued)
            .ToList();

        if (queued.Count == 0) return;

        if (_writebackEngine == null && !_pipeServer.IsConnected)
        {
            foreach (var (taskId2, machine2) in queued)
            {
                if (machine2.CanFire(WritebackTrigger.HelperDisconnected))
                {
                    machine2.Fire(WritebackTrigger.HelperDisconnected);
                    _logger.LogDebug("Writeback {TaskId} blocked — no Helper", taskId2);
                }
            }
            return;
        }

        foreach (var (taskId, machine) in queued)
        {
            if (ct.IsCancellationRequested) break;

            // If no engine available, skip (backward compatible)
            if (_writebackEngine == null || !_writebackEngine.WritebackEnabled)
            {
                _logger.LogDebug("Writeback {TaskId} — no engine or engine disabled", taskId);
                continue;
            }

            // Get Rx info from persisted state
            var state = _stateDb.GetPendingWritebacks()
                .FirstOrDefault(s => s.TaskId == taskId);
            if (state == default) continue;

            if (!int.TryParse(state.RxNumber, out var rxNumber))
            {
                _logger.LogWarning("Writeback {TaskId} — invalid RxNumber '{RxHash}'", taskId, PhiScrubber.HmacHash(state.RxNumber, _options.HmacSalt ?? ""));
                if (machine.CanFire(WritebackTrigger.BusinessError))
                    machine.Fire(WritebackTrigger.BusinessError);
                continue;
            }

            // Per-RxNumber serialization
            if (!_writebackEngine.TryAcquireRxLock(rxNumber))
            {
                _logger.LogDebug("Writeback {TaskId} — Rx {Rx} locked, skipping cycle", taskId, rxNumber);
                continue;
            }

            try
            {
                machine.Fire(WritebackTrigger.Claim);

                // Determine transition type (default to pickup for backward compatibility)
                var transition = _transitions.GetValueOrDefault(taskId, "pickup");
                var deliveredAt = _deliveredAts.GetValueOrDefault(taskId);

                // Resolve RxTransactionID using the correct transition filter
                var resolved = await _writebackEngine.ResolveTransactionIdAsync(
                    rxNumber, 0, transition, ct);

                if (resolved == null)
                {
                    _logger.LogWarning("Writeback {TaskId} — Rx {Rx} not in expected state for {Transition}",
                        taskId, rxNumber, transition);
                    if (machine.CanFire(WritebackTrigger.BusinessError))
                        machine.Fire(WritebackTrigger.BusinessError);
                    continue;
                }

                machine.Fire(WritebackTrigger.StartUia); // → InProgress

                // Route to correct writeback method based on transition
                WritebackResult result;
                if (transition == "complete")
                {
                    result = await _writebackEngine.ExecuteCompleteAsync(
                        resolved.Value.TxId, deliveredAt ?? DateTimeOffset.UtcNow, ct);
                }
                else
                {
                    result = await _writebackEngine.ExecutePickupAsync(
                        resolved.Value.TxId, resolved.Value.CurrentStatus, ct);
                }

                MapResultToStateMachine(machine, result);

                // Record feedback for correlation confidence adjustment
                if (_sessionId != null)
                {
                    try
                    {
                        FeedbackCollector.RecordWritebackOutcome(
                            _stateDb, _sessionId, machine.TaskId,
                            result.CorrelationKey ?? "",
                            result.Outcome,
                            result.UiEventTimestamp ?? DateTimeOffset.UtcNow.ToString("o"),
                            result.SqlExecutionTimestamp ?? DateTimeOffset.UtcNow.ToString("o"));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Feedback recording failed for {TaskId} — non-fatal", machine.TaskId);
                    }
                }

                _logger.LogInformation("Writeback {TaskId} result: {Outcome}", taskId, result.Outcome);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Writeback {TaskId} error", taskId);
                if (machine.CanFire(WritebackTrigger.SystemError))
                    machine.Fire(WritebackTrigger.SystemError);
            }
            finally
            {
                _writebackEngine.ReleaseRxLock(rxNumber);
            }
        }

        await Task.CompletedTask;
    }

    private void MapResultToStateMachine(WritebackStateMachine machine, WritebackResult result)
    {
        switch (result.Outcome)
        {
            case "success":
            case "verified_with_drift":
                if (machine.CanFire(WritebackTrigger.WriteComplete))
                    machine.Fire(WritebackTrigger.WriteComplete);
                if (machine.CanFire(WritebackTrigger.VerifyMatch))
                    machine.Fire(WritebackTrigger.VerifyMatch);
                if (machine.CanFire(WritebackTrigger.SyncComplete))
                    machine.Fire(WritebackTrigger.SyncComplete);
                break;

            case "already_at_target":
                if (machine.CanFire(WritebackTrigger.AlreadyAtTarget))
                    machine.Fire(WritebackTrigger.AlreadyAtTarget);
                break;

            case "status_conflict":
            case "trigger_blocked":
                if (machine.CanFire(WritebackTrigger.BusinessError))
                    machine.Fire(WritebackTrigger.BusinessError);
                break;

            case "connection_reset":
            case "sql_error":
                if (machine.CanFire(WritebackTrigger.SystemError))
                    machine.Fire(WritebackTrigger.SystemError);
                break;

            case "post_verify_mismatch":
                if (machine.CanFire(WritebackTrigger.VerifyMismatch))
                    machine.Fire(WritebackTrigger.VerifyMismatch);
                break;
        }
    }

    private void OnStateChanged(string taskId, WritebackState previousState, WritebackState newState, WritebackTrigger trigger)
    {
        _logger.LogInformation("Writeback {TaskId} {From} -> {To} ({Trigger})",
            taskId, previousState, newState, trigger);

        var machine = _machines.GetValueOrDefault(taskId);
        if (machine != null)
        {
            var rxNum = _rxNumbers.GetValueOrDefault(taskId, "");
            _stateDb.UpsertWritebackState(taskId, rxNum, newState, machine.RetryCount, null);

            // Exponential backoff: 1min, 5min, 15min
            if (trigger == WritebackTrigger.SystemError)
            {
                var delays = new[] { 60, 300, 900 };
                var retryCount = machine.RetryCount;
                if (retryCount > 0 && retryCount <= delays.Length)
                {
                    _stateDb.UpdateNextRetryAt(taskId, DateTimeOffset.UtcNow.AddSeconds(delays[retryCount - 1]));
                }
            }
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            taskId, "writeback_transition",
            previousState.ToString(), newState.ToString(), trigger.ToString()));

        if (newState is WritebackState.Done or WritebackState.ManualReview)
        {
            _logger.LogInformation("Writeback {TaskId} reached terminal state: {State}", taskId, newState);
        }
    }
}
