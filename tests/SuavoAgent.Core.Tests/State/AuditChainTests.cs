using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AuditChainTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AuditChainTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_audit_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void AppendChainedAuditEntry_ComputesHash()
    {
        var hash = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
    }

    [Fact]
    public void AuditChain_SecondEntryLinksToFirst()
    {
        var hash1 = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        var hash2 = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyAuditChain_ValidChain_ReturnsTrue()
    {
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "InProgress", "VerifyPending", "WriteComplete"));
        Assert.True(_db.VerifyAuditChain());
    }

    [Fact]
    public void VerifyAuditChain_EmptyChain_ReturnsTrue()
    {
        Assert.True(_db.VerifyAuditChain());
    }

    [Fact]
    public void VerifyAuditChain_TamperedEntry_ReturnsFalse()
    {
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));
        _db.TamperAuditEntryForTest(1, "Queued", "ManualReview");
        Assert.False(_db.VerifyAuditChain());
    }

    [Fact]
    public void AuditChain_SeedStableAcrossReconnect()
    {
        // Per-installation seed must persist across reconnects to the same DB.
        var tempPath = Path.Combine(Path.GetTempPath(), $"suavo_seed_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var db1 = new AgentStateDb(tempPath))
            {
                db1.AppendChainedAuditEntry(new AuditEntry(
                    "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
            }
            using var db2 = new AgentStateDb(tempPath);
            Assert.True(db2.VerifyAuditChain());
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    [Fact]
    public void AppendChainedAuditEntry_PhiAccessEvent()
    {
        var hash = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "phi_access", "", "", "",
            CommandId: "cmd-123", RequesterId: "pharmacist-1", RxNumber: "RX001"));
        Assert.NotNull(hash);
        Assert.Equal(1, _db.GetAuditEntryCount());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
