using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed class WritebackProcessor : BackgroundService
{
    private readonly ILogger<WritebackProcessor> _logger;
    private readonly AgentStateDb _stateDb;
    private readonly IpcPipeServer _pipeServer;
    private readonly Dictionary<string, WritebackStateMachine> _machines = new();

    public int ProcessIntervalSeconds { get; set; } = 30;
    public int ActiveMachineCount => _machines.Count(m => !m.Value.IsTerminal);

    public WritebackProcessor(
        ILogger<WritebackProcessor> logger,
        AgentStateDb stateDb,
        IpcPipeServer pipeServer)
    {
        _logger = logger;
        _stateDb = stateDb;
        _pipeServer = pipeServer;
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

    public void EnqueueWriteback(string taskId, string rxNumber)
    {
        if (_machines.ContainsKey(taskId))
        {
            _logger.LogDebug("Writeback {TaskId} already tracked", taskId);
            return;
        }

        var machine = new WritebackStateMachine(taskId, WritebackState.Queued, OnStateChanged);
        _machines[taskId] = machine;
        _stateDb.UpsertWritebackState(taskId, rxNumber, WritebackState.Queued, 0, null);
        _logger.LogInformation("Enqueued writeback {TaskId} for Rx {RxNumber}", taskId, rxNumber);
    }

    private async Task ProcessPendingWritebacksAsync(CancellationToken ct)
    {
        var queued = _machines
            .Where(m => m.Value.CurrentState == WritebackState.Queued)
            .ToList();

        if (queued.Count == 0) return;

        if (!_pipeServer.IsConnected)
        {
            foreach (var (taskId, machine) in queued)
            {
                if (machine.CanFire(WritebackTrigger.HelperDisconnected))
                {
                    machine.Fire(WritebackTrigger.HelperDisconnected);
                    _logger.LogDebug("Writeback {TaskId} blocked — no Helper", taskId);
                }
            }
            return;
        }

        foreach (var (taskId, machine) in queued)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                machine.Fire(WritebackTrigger.Claim);
                machine.Fire(WritebackTrigger.StartUia);

                _logger.LogInformation("Would send writeback {TaskId} to Helper via IPC", taskId);

                // Real implementation sends IPC command and awaits response:
                // machine.Fire(WritebackTrigger.WriteComplete);
                // machine.Fire(WritebackTrigger.VerifyMatch);
                // machine.Fire(WritebackTrigger.SyncComplete);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Writeback {TaskId} processing error", taskId);
                if (machine.CanFire(WritebackTrigger.SystemError))
                    machine.Fire(WritebackTrigger.SystemError);
            }
        }

        await Task.CompletedTask;
    }

    private void OnStateChanged(string taskId, WritebackState previousState, WritebackState newState, WritebackTrigger trigger)
    {
        _logger.LogInformation("Writeback {TaskId} {From} -> {To} ({Trigger})",
            taskId, previousState, newState, trigger);

        var machine = _machines.GetValueOrDefault(taskId);
        if (machine != null)
        {
            _stateDb.UpsertWritebackState(taskId, "", newState, machine.RetryCount, null);

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
