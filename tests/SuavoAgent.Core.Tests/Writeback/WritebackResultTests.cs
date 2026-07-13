using SuavoAgent.Contracts.Writeback;
using Xunit;

namespace SuavoAgent.Core.Tests.Writeback;

public class WritebackResultTests
{
    [Fact]
    public void Succeeded_IsSuccess()
    {
        var r = WritebackResult.Succeeded(Guid.NewGuid(), "pickup");
        Assert.True(r.Success);
        Assert.Equal("success", r.Outcome);
        Assert.False(r.IsReplay);
    }

    [Fact]
    public void AlreadyAtTarget_IsSuccessAndReplay()
    {
        var r = WritebackResult.AlreadyAtTarget(Guid.NewGuid());
        Assert.True(r.Success);
        Assert.Equal("already_at_target", r.Outcome);
        Assert.True(r.IsReplay);
    }

    [Fact]
    public void VerifiedWithDrift_IsFailureWithoutRawTimestamps()
    {
        var r = WritebackResult.VerifiedWithDrift(Guid.NewGuid(), "2026-04-13", "2026-04-14");
        Assert.False(r.Success);
        Assert.Equal("post_verify_mismatch", r.Outcome);
        Assert.Equal("completed_date_mismatch", r.Details);
        Assert.Null(r.TransactionId);
    }

    [Fact]
    public void StatusConflict_IsFailure()
    {
        var r = WritebackResult.StatusConflict("Cancelled");
        Assert.False(r.Success);
        Assert.Equal("status_conflict", r.Outcome);
    }

    [Fact]
    public void ConnectionReset_IsRetryable()
    {
        var r = WritebackResult.ConnectionReset();
        Assert.False(r.Success);
        Assert.Equal("connection_reset", r.Outcome);
    }

    [Fact]
    public void PostVerifyMismatch_IsRetryable()
    {
        var r = WritebackResult.PostVerifyMismatch("wrong-guid");
        Assert.False(r.Success);
        Assert.Equal("post_verify_mismatch", r.Outcome);
    }

    [Fact]
    public void SqlError_UsesFixedPhiSafeDetail()
    {
        var r = WritebackResult.SqlError();
        Assert.False(r.Success);
        Assert.Equal("sql_operation_failed", r.Details);
    }

    [Fact]
    public void TriggerBlocked_CapturesTriggerName()
    {
        var r = WritebackResult.TriggerBlocked("trg_rx_audit");
        Assert.False(r.Success);
        Assert.Equal("trigger_blocked", r.Outcome);
        Assert.Equal("trigger_policy_blocked", r.Details);
    }
}
