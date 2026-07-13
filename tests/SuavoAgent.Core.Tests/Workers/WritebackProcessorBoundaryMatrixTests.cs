using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class WritebackProcessorBoundaryMatrixTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "suavo-writeback-boundary-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly AgentStateDb _db;
    private readonly IpcPipeServer _pipe;
    private readonly WritebackProcessor _processor;

    public WritebackProcessorBoundaryMatrixTests()
    {
        _db = new AgentStateDb(_path);
        _pipe = new IpcPipeServer(
            "writeback-boundary-pipe",
            request => Task.FromResult(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                null,
                null)),
            NullLogger<IpcPipeServer>.Instance);
        _processor = Processor(receiptOnly: false);
    }

    [Theory]
    [InlineData("pickup")]
    [InlineData("complete")]
    [InlineData("PICKUP")]
    [InlineData("Complete")]
    public void Enqueue_AllowsOnlyClosedTransitionVocabularyCaseInsensitively(string transition)
    {
        _processor.EnqueueWriteback("task-valid", "123", transition: transition);

        Assert.Single(_db.GetPendingWritebacks());
        Assert.Equal(1, _processor.ActiveMachineCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("deliver")]
    [InlineData("pickup ")]
    [InlineData("complete_now")]
    public void Enqueue_RejectsTransitionOutsideClosedVocabulary(string transition)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            _processor.EnqueueWriteback("task-invalid", "123", transition: transition));

        Assert.Equal("transition", error.ParamName);
        Assert.Empty(_db.GetPendingWritebacks());
    }

    [Fact]
    public async Task ReceiptOnlyMode_LeavesQueuedWritebackUntouched()
    {
        var processor = Processor(receiptOnly: true);
        processor.EnqueueWriteback("task-receipt-only", "123");

        await ProcessPendingAsync(processor);

        Assert.Equal(
            WritebackState.Queued,
            Assert.Single(_db.GetPendingWritebacks()).State);
    }

    [Fact]
    public async Task MissingEngineAndDisconnectedHelper_BlocksInsteadOfDroppingWriteback()
    {
        _processor.EnqueueWriteback("task-no-helper", "123");

        await ProcessPendingAsync(_processor);

        var pending = Assert.Single(_db.GetPendingWritebacks());
        Assert.Equal(WritebackState.BlockedInteractive, pending.State);
        Assert.Equal(1, _processor.ActiveMachineCount);
    }

    [Fact]
    public void Recovery_RehydratesEveryDueWritebackAndPreservesEncryptedRxValue()
    {
        _db.UpsertWritebackState("task-recover-a", "123", WritebackState.Queued, 0, null);
        _db.UpsertWritebackState("task-recover-b", "", WritebackState.Queued, 1, null);
        var restarted = Processor(receiptOnly: false);

        Invoke(restarted, "RecoverPendingWritebacks");

        Assert.Equal(2, restarted.ActiveMachineCount);
        var rxNumbers = Field<Dictionary<string, string>>(restarted, "_rxNumbers");
        Assert.Equal("123", rxNumbers["task-recover-a"]);
    }

    public static TheoryData<string, WritebackState, Action<WritebackStateMachine>> OutcomeMatrix => new()
    {
        { "success", WritebackState.Done, ToInProgress },
        { "verified_with_drift", WritebackState.Done, ToInProgress },
        { "already_at_target", WritebackState.Done, _ => { } },
        { "status_conflict", WritebackState.Queued, machine => machine.Fire(WritebackTrigger.Claim) },
        { "trigger_blocked", WritebackState.Queued, machine => machine.Fire(WritebackTrigger.Claim) },
        { "connection_reset", WritebackState.Queued, machine => machine.Fire(WritebackTrigger.Claim) },
        { "sql_error", WritebackState.Queued, machine => machine.Fire(WritebackTrigger.Claim) },
        { "post_verify_mismatch", WritebackState.InProgress, ToVerifyPending },
        { "unknown_outcome", WritebackState.Queued, _ => { } },
    };

    [Theory]
    [MemberData(nameof(OutcomeMatrix))]
    public void ResultMapping_DrivesOnlyDefinedStateMachineTransition(
        string outcome,
        WritebackState expectedState,
        Action<WritebackStateMachine> prepare)
    {
        const string taskId = "task-map";
        _processor.EnqueueWriteback(taskId, "123");
        var machine = Machine(_processor, taskId);
        prepare(machine);

        Map(_processor, machine, new WritebackResult(
            outcome is "success" or "verified_with_drift" or "already_at_target",
            outcome,
            Guid.NewGuid(),
            null));

        Assert.Equal(expectedState, machine.CurrentState);
        var pending = _db.GetPendingWritebacks();
        if (expectedState is WritebackState.Done or WritebackState.ManualReview)
            Assert.Empty(pending);
        else
            Assert.Equal(expectedState, Assert.Single(pending).State);
    }

    [Theory]
    [InlineData("status_conflict")]
    [InlineData("trigger_blocked")]
    [InlineData("connection_reset")]
    [InlineData("sql_error")]
    public void RetryableFailure_PersistsRetryCountAndBoundedBackoff(string outcome)
    {
        const string taskId = "task-retry";
        _processor.EnqueueWriteback(taskId, "123");
        var machine = Machine(_processor, taskId);
        machine.Fire(WritebackTrigger.Claim);

        Map(_processor, machine, new WritebackResult(false, outcome, null, null));

        var pending = Assert.Single(_db.GetPendingWritebacks());
        Assert.Equal(1, pending.RetryCount);
        var retryAt = Field<Dictionary<string, DateTimeOffset>>(
            _processor,
            "_nextRetryAt")[taskId];
        Assert.InRange(
            retryAt,
            DateTimeOffset.UtcNow.AddSeconds(25),
            DateTimeOffset.UtcNow.AddSeconds(35));
    }

    [Fact]
    public void SixthBusinessFailure_DeadLettersAndPersistsManualReviewReason()
    {
        const string taskId = "task-dead-letter";
        _processor.EnqueueWriteback(taskId, "123");
        var machine = Machine(_processor, taskId);
        for (var attempt = 0; attempt <= WritebackStateMachine.MaxRetries; attempt++)
        {
            machine.Fire(WritebackTrigger.Claim);
            Map(_processor, machine, WritebackResult.StatusConflict("structural_status"));
        }

        Assert.Equal(WritebackState.ManualReview, machine.CurrentState);
        Assert.Empty(_db.GetPendingWritebacks());
        Assert.Equal(1, _db.GetFailedWritebackCount());
    }

    [Fact]
    public async Task EscalationHook_CompletesWithoutThrowingOrMutatingQueue()
    {
        _processor.EnqueueWriteback("task-escalation", "123");

        var task = Assert.IsAssignableFrom<Task>(Invoke(_processor, "OnEscalateAsync"));
        await task;

        Assert.Equal(WritebackState.Queued, Assert.Single(_db.GetPendingWritebacks()).State);
    }

    private WritebackProcessor Processor(bool receiptOnly) => new(
        NullLogger<WritebackProcessor>.Instance,
        _db,
        _pipe,
        Options.Create(new AgentOptions
        {
            AgentId = "writeback-boundary-agent",
            ReceiptOnlyMode = receiptOnly,
        }));

    private static async Task ProcessPendingAsync(WritebackProcessor processor)
    {
        var result = Invoke(processor, "ProcessPendingWritebacksAsync", CancellationToken.None);
        await Assert.IsAssignableFrom<Task>(result);
    }

    private static void Map(
        WritebackProcessor processor,
        WritebackStateMachine machine,
        WritebackResult result) =>
        Invoke(processor, "MapResultToStateMachine", machine, result);

    private static WritebackStateMachine Machine(WritebackProcessor processor, string taskId) =>
        Field<Dictionary<string, WritebackStateMachine>>(processor, "_machines")[taskId];

    private static T Field<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private static object? Invoke(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(target, args);
    }

    private static void ToInProgress(WritebackStateMachine machine)
    {
        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.StartUia);
    }

    private static void ToVerifyPending(WritebackStateMachine machine)
    {
        ToInProgress(machine);
        machine.Fire(WritebackTrigger.WriteComplete);
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _db.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
