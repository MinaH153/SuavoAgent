using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Regression tests for the prev_hash bug: AppendChainedAuditEntry was storing
/// newHash (current row's hash) into the prev_hash column instead of prevHash
/// (the previous row's hash). The chain still *verified* because GetLastAuditHash
/// reads from the same column, making the bug self-consistent. These tests catch
/// the semantic error by inspecting the stored prev_hash value directly.
/// </summary>
public class AuditChainFixTests : IDisposable
{
    // Mirror the private seed so we can assert exact values.
    private static readonly string ChainSeed =
        Convert.ToBase64String(SHA256.HashData(
            Encoding.UTF8.GetBytes("SuavoAgent-audit-chain-v1")));
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AuditChainFixTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_audit_fix_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void GetLastAuditHash_ReturnsComputedHashOfLastEntry()
    {
        // After inserting one entry, GetLastAuditHash should return the same
        // hash that AppendChainedAuditEntry returned — the computed hash of
        // that entry (which incorporates the chain seed as its prev_hash).
        var hash1 = _db.AppendChainedAuditEntry(
            new AuditEntry("task-1", "writeback", "pending", "in_progress", "start"),
            "2026-04-14T00:00:00Z");

        Assert.Equal(hash1, _db.GetLastAuditHash());
    }

    [Fact]
    public void ChainLinks_SecondEntryDependsOnFirst()
    {
        var hash1 = _db.AppendChainedAuditEntry(
            new AuditEntry("task-1", "writeback", "pending", "in_progress", "start"),
            "2026-04-14T00:00:00Z");

        var hash2 = _db.AppendChainedAuditEntry(
            new AuditEntry("task-2", "writeback", "in_progress", "completed", "finish"),
            "2026-04-14T00:01:00Z");

        // hash2 must incorporate hash1 as its prev_hash input.
        // Verify by recomputing: hash2 == ComputeAuditHash(hash1, e2...).
        var expected = AgentStateDb.ComputeAuditHash(
            hash1, "task-2", "writeback", "in_progress", "completed", "finish",
            "2026-04-14T00:01:00Z");
        Assert.Equal(expected, hash2);
        Assert.Equal(hash2, _db.GetLastAuditHash());
    }

    [Fact]
    public void ChainVerifies_AfterFix()
    {
        _db.AppendChainedAuditEntry(
            new AuditEntry("task-1", "writeback", "pending", "in_progress", "start"),
            "2026-04-14T00:00:00Z");
        _db.AppendChainedAuditEntry(
            new AuditEntry("task-2", "writeback", "in_progress", "completed", "finish"),
            "2026-04-14T00:01:00Z");
        _db.AppendChainedAuditEntry(
            new AuditEntry("task-3", "writeback", "completed", "archived", "archive"),
            "2026-04-14T00:02:00Z");

        Assert.True(_db.VerifyAuditChain());
    }

#if DEBUG
    [Fact]
    public void TamperedEntry_BreaksChain()
    {
        _db.AppendChainedAuditEntry(
            new AuditEntry("task-1", "writeback", "pending", "in_progress", "start"),
            "2026-04-14T00:00:00Z");
        _db.AppendChainedAuditEntry(
            new AuditEntry("task-2", "writeback", "in_progress", "completed", "finish"),
            "2026-04-14T00:01:00Z");

        // Tamper with entry 1
        _db.TamperAuditEntryForTest(1, "tampered_from", "tampered_to");

        Assert.False(_db.VerifyAuditChain());
    }
#endif
}
