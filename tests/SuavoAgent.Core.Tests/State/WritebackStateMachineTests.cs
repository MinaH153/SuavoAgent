using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class WritebackStateMachineTests
{
    private readonly List<(string TaskId, WritebackState State)> _transitions = new();

    private WritebackStateMachine Create(WritebackState initial = WritebackState.Queued) =>
        new("task-001", initial, (id, prev, state, trigger) => _transitions.Add((id, state)));

    [Fact]
    public void HappyPath_QueuedToDone()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);
        sm.Fire(WritebackTrigger.WriteComplete);
        sm.Fire(WritebackTrigger.VerifyMatch);
        sm.Fire(WritebackTrigger.SyncComplete);

        Assert.Equal(WritebackState.Done, sm.CurrentState);
        Assert.True(sm.IsTerminal);
        Assert.Equal(5, _transitions.Count);
    }

    [Fact]
    public void VerifyMismatch_RetriesInProgress()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);
        sm.Fire(WritebackTrigger.WriteComplete);
        sm.Fire(WritebackTrigger.VerifyMismatch); // retry

        Assert.Equal(WritebackState.InProgress, sm.CurrentState);
    }

    [Fact]
    public void SystemError_IncrementsRetryCount()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);
        sm.Fire(WritebackTrigger.SystemError); // back to Queued, retry++

        Assert.Equal(WritebackState.Queued, sm.CurrentState);
        Assert.Equal(1, sm.RetryCount);
    }

    [Fact]
    public void MaxRetries_ForcesManualReview()
    {
        var sm = Create();
        for (int i = 0; i < WritebackStateMachine.MaxRetries; i++)
        {
            sm.Fire(WritebackTrigger.Claim);
            sm.Fire(WritebackTrigger.StartUia);
            sm.Fire(WritebackTrigger.SystemError);
        }
        // Next error should force ManualReview
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);
        sm.Fire(WritebackTrigger.SystemError);

        Assert.Equal(WritebackState.ManualReview, sm.CurrentState);
        Assert.True(sm.IsTerminal);
    }

    [Fact]
    public void BusinessError_ImmediateManualReview()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);
        sm.Fire(WritebackTrigger.BusinessError);

        Assert.Equal(WritebackState.ManualReview, sm.CurrentState);
        Assert.Equal(0, sm.RetryCount);
    }

    [Fact]
    public void HelperDisconnect_BlocksInteractive()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.HelperDisconnected);
        Assert.Equal(WritebackState.BlockedInteractive, sm.CurrentState);

        sm.Fire(WritebackTrigger.UserIdle);
        Assert.Equal(WritebackState.Queued, sm.CurrentState);
    }

    [Fact]
    public void CanFire_ReturnsFalseForInvalidTransition()
    {
        var sm = Create();
        Assert.False(sm.CanFire(WritebackTrigger.WriteComplete)); // can't write from Queued
        Assert.True(sm.CanFire(WritebackTrigger.Claim));
    }

    [Fact]
    public void InitialState_IsQueued()
    {
        var sm = Create();
        Assert.Equal(WritebackState.Queued, sm.CurrentState);
        Assert.False(sm.IsTerminal);
    }
}
