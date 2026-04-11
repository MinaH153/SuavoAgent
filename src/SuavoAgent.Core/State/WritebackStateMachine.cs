using Stateless;

namespace SuavoAgent.Core.State;

public enum WritebackState
{
    Queued,
    BlockedInteractive,
    Claimed,
    InProgress,
    VerifyPending,
    Verified,
    Done,
    ManualReview
}

public enum WritebackTrigger
{
    Claim,
    UserActive,
    UserIdle,
    StartUia,
    WriteComplete,
    VerifyMatch,
    VerifyMismatch,
    SystemError,
    BusinessError,
    SyncComplete,
    HelperDisconnected
}

public class WritebackStateMachine
{
    private readonly StateMachine<WritebackState, WritebackTrigger> _machine;
    private readonly string _taskId;
    private readonly Action<string, WritebackState, WritebackState, WritebackTrigger> _onStateChanged;
    private int _retryCount;
    private int _verifyAttempts;

    public const int MaxRetries = 3;
    public const int MaxVerifyAttempts = 3;
    public WritebackState CurrentState => _machine.State;
    public string TaskId => _taskId;
    public int RetryCount => _retryCount;

    public WritebackStateMachine(
        string taskId,
        WritebackState initialState,
        Action<string, WritebackState, WritebackState, WritebackTrigger> onStateChanged,
        int initialRetryCount = 0)
    {
        _taskId = taskId;
        _onStateChanged = onStateChanged;
        _retryCount = initialRetryCount;

        _machine = new StateMachine<WritebackState, WritebackTrigger>(initialState);

        _machine.Configure(WritebackState.Queued)
            .Permit(WritebackTrigger.Claim, WritebackState.Claimed)
            .Permit(WritebackTrigger.HelperDisconnected, WritebackState.BlockedInteractive)
            .Permit(WritebackTrigger.UserActive, WritebackState.BlockedInteractive);

        _machine.Configure(WritebackState.BlockedInteractive)
            .Permit(WritebackTrigger.UserIdle, WritebackState.Queued);

        _machine.Configure(WritebackState.Claimed)
            .Permit(WritebackTrigger.StartUia, WritebackState.InProgress)
            .Permit(WritebackTrigger.SystemError, WritebackState.Queued)
            .Permit(WritebackTrigger.BusinessError, WritebackState.ManualReview)
            .Permit(WritebackTrigger.HelperDisconnected, WritebackState.BlockedInteractive);

        _machine.Configure(WritebackState.InProgress)
            .Permit(WritebackTrigger.WriteComplete, WritebackState.VerifyPending)
            .Permit(WritebackTrigger.SystemError, WritebackState.Queued)
            .Permit(WritebackTrigger.BusinessError, WritebackState.ManualReview)
            .Permit(WritebackTrigger.HelperDisconnected, WritebackState.BlockedInteractive);

        _machine.Configure(WritebackState.VerifyPending)
            .Permit(WritebackTrigger.VerifyMatch, WritebackState.Verified)
            .Permit(WritebackTrigger.VerifyMismatch, WritebackState.InProgress)
            .Permit(WritebackTrigger.SystemError, WritebackState.Queued)
            .Permit(WritebackTrigger.BusinessError, WritebackState.ManualReview);

        _machine.Configure(WritebackState.Verified)
            .Permit(WritebackTrigger.SyncComplete, WritebackState.Done);

        // ManualReview and Done are terminal — no outgoing triggers

        _machine.OnTransitioned(t =>
        {
            if (t.Trigger == WritebackTrigger.SystemError)
                _retryCount++;
            if (t.Trigger == WritebackTrigger.VerifyMismatch)
                _verifyAttempts++;

            _onStateChanged(_taskId, t.Source, t.Destination, t.Trigger);
        });
    }

    public bool CanFire(WritebackTrigger trigger) => _machine.CanFire(trigger);

    public void Fire(WritebackTrigger trigger)
    {
        // Guard: max retries → ManualReview (from any state that permits BusinessError)
        if (_retryCount >= MaxRetries && trigger == WritebackTrigger.SystemError)
        {
            _machine.Fire(WritebackTrigger.BusinessError);
            return;
        }

        // Guard: verify mismatch loop → ManualReview after MaxVerifyAttempts
        if (_verifyAttempts >= MaxVerifyAttempts && trigger == WritebackTrigger.VerifyMismatch)
        {
            _machine.Fire(WritebackTrigger.BusinessError);
            return;
        }

        _machine.Fire(trigger);
    }

    public bool IsTerminal => CurrentState is WritebackState.Done or WritebackState.ManualReview;
}
