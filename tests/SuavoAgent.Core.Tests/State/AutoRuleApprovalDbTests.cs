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
        Assert.False(row.HasWriteback);
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
    public void UpsertAutoRuleApproval_SameHash_PreservesApprovedStatus()
    {
        // Simulates LearningWorker re-emitting byte-identical YAML on every tick.
        // Before fix: CASE demoted Approved back to Pending every call.
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-stable");
        _db.SetAutoRuleApprovalStatus(
            "auto.t.abc", AgentStateDb.AutoRuleStatus.Approved,
            approvedBy: "op", approvedAt: "2026-04-20T00:00:00Z");

        // Re-emit with identical hash — content unchanged.
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-stable");

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Approved, row!.Status);
        Assert.Equal("op", row.ApprovedBy);
    }

    [Fact]
    public void UpsertAutoRuleApproval_SameHash_PreservesShadowStatus()
    {
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-stable");
        _db.SetAutoRuleApprovalStatus("auto.t.abc", AgentStateDb.AutoRuleStatus.Shadow);

        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-stable");

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Shadow, row!.Status);
    }

    [Fact]
    public void UpsertAutoRuleApproval_ChangedHash_DemotesApprovedToPending()
    {
        // Safety property: a genuine YAML content change forces re-approval.
        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-v1");
        _db.SetAutoRuleApprovalStatus(
            "auto.t.abc", AgentStateDb.AutoRuleStatus.Approved,
            approvedBy: "op", approvedAt: "2026-04-20T00:00:00Z");

        _db.UpsertAutoRuleApproval("auto.t.abc", "tmpl-a", "sha-v2");

        var row = _db.GetAutoRuleApproval("auto.t.abc");
        Assert.NotNull(row);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Pending, row!.Status);
        Assert.Equal("sha-v2", row.YamlSha256);
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

    [Fact]
    public void UpsertAutoRuleApproval_DerivesWritebackRiskFromBoundWorkflowTemplate()
    {
        UpsertWorkflowTemplate("tmpl-write", hasWriteback: true);

        // The false fallback proves the persisted workflow template is the
        // authoritative safety source, not a forgetful caller.
        _db.UpsertAutoRuleApproval(
            "auto.t.write",
            "tmpl-write",
            "sha-write",
            hasWriteback: false);

        Assert.True(_db.GetAutoRuleApproval("auto.t.write")!.HasWriteback);
        Assert.True(Assert.Single(_db.GetAllAutoRuleApprovals()).HasWriteback);
    }

    [Fact]
    public void WritebackRiskChange_DemotesApprovedRuleEvenWhenYamlHashIsUnchanged()
    {
        UpsertWorkflowTemplate("tmpl-read", hasWriteback: false);
        UpsertWorkflowTemplate("tmpl-write", hasWriteback: true);
        _db.UpsertAutoRuleApproval("auto.t.risk", "tmpl-read", "sha-stable");
        _db.SetAutoRuleApprovalStatus(
            "auto.t.risk",
            AgentStateDb.AutoRuleStatus.Approved,
            approvedBy: "operator",
            approvedAt: "2026-07-10T12:00:00Z");

        _db.UpsertAutoRuleApproval("auto.t.risk", "tmpl-write", "sha-stable");

        var row = _db.GetAutoRuleApproval("auto.t.risk");
        Assert.NotNull(row);
        Assert.True(row!.HasWriteback);
        Assert.Equal(AgentStateDb.AutoRuleStatus.Pending, row.Status);
    }

    [Fact]
    public void Migration10_BackfillsExistingApprovalFromBoundWorkflowAndSurvivesRestart()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "suavo-auto-rule-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            SQLitePCL.Batteries_V2.Init();
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migrations (
                        version INTEGER PRIMARY KEY,
                        applied_at TEXT NOT NULL,
                        description TEXT NOT NULL
                    );
                    INSERT INTO schema_migrations(version, applied_at, description) VALUES
                        (1, '2026-07-01T00:00:00Z', 'legacy'),
                        (2, '2026-07-01T00:00:00Z', 'legacy'),
                        (3, '2026-07-01T00:00:00Z', 'legacy'),
                        (4, '2026-07-01T00:00:00Z', 'legacy'),
                        (5, '2026-07-01T00:00:00Z', 'legacy'),
                        (6, '2026-07-01T00:00:00Z', 'legacy'),
                        (7, '2026-07-01T00:00:00Z', 'legacy'),
                        (8, '2026-07-01T00:00:00Z', 'legacy'),
                        (9, '2026-07-01T00:00:00Z', 'legacy');
                    CREATE TABLE workflow_templates (
                        template_id TEXT PRIMARY KEY,
                        has_writeback INTEGER NOT NULL
                    );
                    INSERT INTO workflow_templates(template_id, has_writeback)
                    VALUES ('tmpl-legacy-write', 1);
                    CREATE TABLE auto_rule_approvals (
                        rule_id TEXT PRIMARY KEY,
                        template_id TEXT NOT NULL,
                        yaml_sha256 TEXT NOT NULL,
                        status TEXT NOT NULL,
                        shadow_runs INTEGER NOT NULL DEFAULT 0,
                        shadow_matches INTEGER NOT NULL DEFAULT 0,
                        shadow_mismatches INTEGER NOT NULL DEFAULT 0,
                        approved_by TEXT,
                        approved_at TEXT,
                        rejected_reason TEXT
                    );
                    INSERT INTO auto_rule_approvals(rule_id, template_id, yaml_sha256, status)
                    VALUES ('auto.legacy.write', 'tmpl-legacy-write', 'sha-legacy', 'Pending');
                    """;
                command.ExecuteNonQuery();
            }

            using (var migrated = new AgentStateDb(path))
                Assert.True(migrated.GetAutoRuleApproval("auto.legacy.write")!.HasWriteback);
            using (var restarted = new AgentStateDb(path))
                Assert.True(restarted.GetAutoRuleApproval("auto.legacy.write")!.HasWriteback);
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + "-wal"); } catch { }
            try { File.Delete(path + "-shm"); } catch { }
        }
    }

    private void UpsertWorkflowTemplate(string templateId, bool hasWriteback)
    {
        _db.UpsertWorkflowTemplate(
            templateId,
            "1.0.0",
            "learned",
            "PioneerPharmacy*",
            "[]",
            "screen-" + templateId,
            "steps-" + templateId,
            null,
            "[]",
            0.95,
            12,
            hasWriteback,
            "2026-07-10T12:00:00Z",
            "test");
    }
}
