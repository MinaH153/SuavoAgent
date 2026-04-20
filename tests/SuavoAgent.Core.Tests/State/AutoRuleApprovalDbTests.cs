using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Tests for the v3.12.1.1 auto-rule-approval DB methods that support
/// cloud sync — GetAllAutoRuleApprovals() for heartbeat upload, and
/// SetAutoRuleApprovalStatus() for receiving cloud transitions.
/// </summary>
public class AutoRuleApprovalDbTests : IDisposable
{
    private readonly AgentStateDb _db;

    public AutoRuleApprovalDbTests()
    {
        _db = new AgentStateDb(":memory:");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void GetAllAutoRuleApprovals_Empty_ReturnsEmpty()
    {
        var rows = _db.GetAllAutoRuleApprovals();
        Assert.Empty(rows);
    }

    [Fact]
    public void GetAllAutoRuleApprovals_ReturnsDeterministicOrder()
    {
        // Insertion order deliberately non-alphabetical to prove the ORDER BY.
        _db.UpsertAutoRuleApproval("auto.x.zzz", "tmpl-z", "sha-z");
        _db.UpsertAutoRuleApproval("auto.x.aaa", "tmpl-a", "sha-a");
        _db.UpsertAutoRuleApproval("auto.x.mmm", "tmpl-m", "sha-m");

        var rows = _db.GetAllAutoRuleApprovals();
        Assert.Equal(3, rows.Count);
        Assert.Equal("auto.x.aaa", rows[0].RuleId);
        Assert.Equal("auto.x.mmm", rows[1].RuleId);
        Assert.Equal("auto.x.zzz", rows[2].RuleId);
    }

    [Fact]
    public void GetAllAutoRuleApprovals_PreservesAllFields()
    {
        _db.UpsertAutoRuleApproval("auto.t.12345678", "tmpl-a", "sha-abc");

        var rows = _db.GetAllAutoRuleApprovals();
        var row = Assert.Single(rows);
        Assert.Equal("auto.t.12345678", row.RuleId);
        Assert.Equal("tmpl-a", row.TemplateId);
        Assert.Equal("sha-abc", row.YamlSha256);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Pending, row.Status);
        Assert.Equal(0, row.ShadowRuns);
        Assert.Equal(0, row.ShadowMatches);
        Assert.Equal(0, row.ShadowMismatches);
    }

    [Fact]
    public void SetAutoRuleApprovalStatus_NoRow_ReturnsFalseSilently()
    {
        var ok = _db.SetAutoRuleApprovalStatus("auto.t.missing", AgentStateDb.AutoRuleStatus.Approved);
        Assert.False(ok);
    }

    [Fact]
    public void SetAutoRuleApprovalStatus_PendingToShadow_Works()
    {
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-1");
        var ok = _db.SetAutoRuleApprovalStatus("auto.t.abc", AgentStateDb.AutoRuleStatus.Shadow);
        Assert.True(ok);

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Shadow, row!.Status);
        Assert.Null(row.ApprovedBy);
        Assert.Null(row.ApprovedAt);
        Assert.Null(row.RejectedReason);
    }

    [Fact]
    public void SetAutoRuleApprovalStatus_ApprovedStampsOperatorMetadata()
    {
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-1");
        var now = "2026-04-20T12:34:56Z";
        var ok = _db.SetAutoRuleApprovalStatus(
            "auto.t.abc",
            AgentStateDb.AutoRuleStatus.Approved,
            approvedBy: "operator-uuid-123",
            approvedAt: now);
        Assert.True(ok);

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Approved, row!.Status);
        Assert.Equal("operator-uuid-123", row.ApprovedBy);
        Assert.Equal(now, row.ApprovedAt);
        Assert.Null(row.RejectedReason);
    }

    [Fact]
    public void SetAutoRuleApprovalStatus_RejectedStampsReasonOnly()
    {
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-1");
        var ok = _db.SetAutoRuleApprovalStatus(
            "auto.t.abc",
            AgentStateDb.AutoRuleStatus.Rejected,
            approvedBy: "operator",       // should NOT land in approved_by
            approvedAt: "2026-04-20T00:00:00Z",
            rejectedReason: "Too risky for autonomous execution");
        Assert.True(ok);

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Rejected, row!.Status);
        Assert.Null(row.ApprovedBy);
        Assert.Null(row.ApprovedAt);
        Assert.Equal("Too risky for autonomous execution", row.RejectedReason);
    }

    [Fact]
    public void SetAutoRuleApprovalStatus_PendingClearsOperatorMetadata()
    {
        // Approved first so approved_by / approved_at are set, then Pending.
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-1");
        _db.SetAutoRuleApprovalStatus(
            "auto.t.abc", AgentStateDb.AutoRuleStatus.Approved,
            approvedBy: "op", approvedAt: "2026-04-20T00:00:00Z");

        var ok = _db.SetAutoRuleApprovalStatus("auto.t.abc", AgentStateDb.AutoRuleStatus.Pending);
        Assert.True(ok);

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Pending, row!.Status);
        Assert.Null(row.ApprovedBy);
        Assert.Null(row.ApprovedAt);
        Assert.Null(row.RejectedReason);
    }
}
