using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Writeback;

public class WritebackProcessorIntegrationTests
{
    [Fact]
    public void AlreadyAtTarget_FromQueued_TransitionsToDone()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });
        machine.Fire(WritebackTrigger.AlreadyAtTarget);
        Assert.Equal(WritebackState.Done, machine.CurrentState);
        Assert.True(machine.IsTerminal);
    }

    [Fact]
    public void AlreadyAtTarget_FromClaimed_TransitionsToDone()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });
        machine.Fire(WritebackTrigger.Claim);
        Assert.Equal(WritebackState.Claimed, machine.CurrentState);
        machine.Fire(WritebackTrigger.AlreadyAtTarget);
        Assert.Equal(WritebackState.Done, machine.CurrentState);
    }

    [Fact]
    public void AlreadyAtTarget_FromInProgress_TransitionsToDone()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });
        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.StartUia);
        Assert.Equal(WritebackState.InProgress, machine.CurrentState);
        machine.Fire(WritebackTrigger.AlreadyAtTarget);
        Assert.Equal(WritebackState.Done, machine.CurrentState);
    }

    [Fact]
    public void FullSuccessPath_Queued_To_Done()
    {
        var transitions = new List<(WritebackState from, WritebackState to)>();
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, from, to, _) => transitions.Add((from, to)));

        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.StartUia);
        machine.Fire(WritebackTrigger.WriteComplete);
        machine.Fire(WritebackTrigger.VerifyMatch);
        machine.Fire(WritebackTrigger.SyncComplete);

        Assert.Equal(WritebackState.Done, machine.CurrentState);
        Assert.Equal(5, transitions.Count);
    }

    [Fact]
    public void SystemError_Retries_ThenManualReview()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });

        for (int i = 0; i < 3; i++)
        {
            machine.Fire(WritebackTrigger.Claim);
            machine.Fire(WritebackTrigger.SystemError);
        }

        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.SystemError);
        Assert.Equal(WritebackState.ManualReview, machine.CurrentState);
    }

    [Fact]
    public void WritebackResult_Success_Properties()
    {
        var txId = Guid.NewGuid();
        var r = WritebackResult.Succeeded(txId, "pickup");
        Assert.True(r.Success);
        Assert.Equal("success", r.Outcome);
        Assert.Equal(txId, r.TransactionId);
        Assert.Equal("pickup", r.Details);
        Assert.False(r.IsReplay);
    }

    [Fact]
    public void WritebackResult_AlreadyAtTarget_IsReplay()
    {
        var txId = Guid.NewGuid();
        var r = WritebackResult.AlreadyAtTarget(txId);
        Assert.True(r.Success);
        Assert.True(r.IsReplay);
        Assert.Equal(txId, r.TransactionId);
    }

    [Fact]
    public void VerifyMismatch_Retries_ThenManualReview()
    {
        var machine = new WritebackStateMachine("task-1", WritebackState.Queued,
            (_, _, _, _) => { });

        machine.Fire(WritebackTrigger.Claim);
        machine.Fire(WritebackTrigger.StartUia);

        // 3 verify mismatches = max verify attempts
        for (int i = 0; i < 3; i++)
        {
            machine.Fire(WritebackTrigger.WriteComplete);
            machine.Fire(WritebackTrigger.VerifyMismatch); // back to InProgress
        }

        // 4th mismatch → BusinessError → ManualReview (guard in Fire)
        machine.Fire(WritebackTrigger.WriteComplete);
        machine.Fire(WritebackTrigger.VerifyMismatch);
        Assert.Equal(WritebackState.ManualReview, machine.CurrentState);
    }
}
