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
    public void BusinessError_RetriesInsteadOfDeadLettering()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.BusinessError);

        // BusinessError now retries (back to Queued) instead of immediate ManualReview
        Assert.Equal(WritebackState.Queued, sm.CurrentState);
        Assert.Equal(1, sm.RetryCount);
        Assert.False(sm.IsTerminal);
    }

    [Fact]
    public void BusinessError_DeadLettersAfterMaxRetries()
    {
        var sm = Create();
        for (int i = 0; i < WritebackStateMachine.MaxRetries; i++)
        {
            sm.Fire(WritebackTrigger.Claim);
            sm.Fire(WritebackTrigger.BusinessError);
        }
        // Next BusinessError should dead-letter → ManualReview
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.BusinessError);

        Assert.Equal(WritebackState.ManualReview, sm.CurrentState);
        Assert.True(sm.IsTerminal);
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

    [Fact]
    public void VerifyMismatchLoop_ForcesManualReviewAfterMax()
    {
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);

        for (int i = 0; i < WritebackStateMachine.MaxVerifyAttempts; i++)
        {
            sm.Fire(WritebackTrigger.WriteComplete);
            sm.Fire(WritebackTrigger.VerifyMismatch);
        }

        // Next mismatch should force ManualReview
        sm.Fire(WritebackTrigger.WriteComplete);
        sm.Fire(WritebackTrigger.VerifyMismatch);

        Assert.Equal(WritebackState.ManualReview, sm.CurrentState);
        Assert.True(sm.IsTerminal);
    }

    [Fact]
    public void MaxRetries_FromClaimed_ForcesManualReview()
    {
        // SF-23: SystemError at max retries from Claimed must not throw
        var sm = Create(WritebackState.Queued);
        for (int i = 0; i < WritebackStateMachine.MaxRetries; i++)
        {
            sm.Fire(WritebackTrigger.Claim);
            sm.Fire(WritebackTrigger.SystemError);
        }
        // Next error from Claimed state
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.SystemError);

        Assert.Equal(WritebackState.ManualReview, sm.CurrentState);
    }

    [Fact]
    public void MaxRetries_FromVerifyPending_ForcesManualReview()
    {
        // SF-23: SystemError at max retries from VerifyPending must not throw
        var sm = Create();
        sm.Fire(WritebackTrigger.Claim);
        sm.Fire(WritebackTrigger.StartUia);
        sm.Fire(WritebackTrigger.WriteComplete);

        // Burn through retries from VerifyPending
        for (int i = 0; i < WritebackStateMachine.MaxRetries; i++)
        {
            sm.Fire(WritebackTrigger.SystemError);
            sm.Fire(WritebackTrigger.Claim);
            sm.Fire(WritebackTrigger.StartUia);
            sm.Fire(WritebackTrigger.WriteComplete);
        }
        // Now at max retries, fire SystemError from VerifyPending
        sm.Fire(WritebackTrigger.SystemError);

        Assert.Equal(WritebackState.ManualReview, sm.CurrentState);
    }
}
