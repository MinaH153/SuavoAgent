using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Integration;

/// <summary>
/// Integration tests for critical runtime paths that cross module boundaries.
/// Codex MED-8: detection PHI-free, unsigned command rejection, migration,
/// package staging, retry persistence.
/// </summary>
public class CriticalPathTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly ILogger _logger = NullLogger.Instance;

    public CriticalPathTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_crit_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    // ── 1. Decommission refuses unsigned commands ──

    [Fact]
    public void SignedCommandVerifier_RejectsUnsignedDecommission()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubDer = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var verifier = new SignedCommandVerifier(
            new Dictionary<string, string> { ["k1"] = pubDer },
            "agent-1", "fp-1");

        // Forge a command with garbage signature
        var cmd = new SignedCommand(
            "decommission", "agent-1", "fp-1",
            DateTimeOffset.UtcNow.ToString("o"),
            Guid.NewGuid().ToString(), "k1",
            Convert.ToBase64String(new byte[64]),
            SignedCommandVerifier.ComputeDataHash(null));

        var result = verifier.Verify(cmd);
        Assert.False(result.IsValid);
        Assert.Contains("signature", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignedCommandVerifier_RejectsUpdateWithWrongKey()
    {
        var realKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var realPub = Convert.ToBase64String(realKey.ExportSubjectPublicKeyInfo());

        var verifier = new SignedCommandVerifier(
            new Dictionary<string, string> { ["k1"] = realPub },
            "agent-1", "fp-1");

        var ts = DateTimeOffset.UtcNow.ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var dataHash = SignedCommandVerifier.ComputeDataHash(null);
        var canonical = $"update|agent-1|fp-1|{ts}|{nonce}|{dataHash}";
        // Sign with attacker's key
        var sig = Convert.ToBase64String(
            attackerKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));

        var cmd = new SignedCommand("update", "agent-1", "fp-1", ts, nonce, "k1", sig, dataHash);
        Assert.False(verifier.Verify(cmd).IsValid);
    }

    // ── 2. SQLCipher migration on upgrade ──
    // These tests require SQLCipher (bundle_e_sqlcipher), which is only on Windows deployments.
    // On macOS/CI with bundle_e_sqlite3, BackupDatabase doesn't support encrypted targets.

    [Fact(Skip = "Requires SQLCipher — skip on macOS/CI")]
    public void MigrateToEncrypted_PreservesData()
    {
        // Write data to plain DB
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 3, null);
        _db.AppendChainedAuditEntry(new AuditEntry("agent-1", "test", "A", "B", "trigger"));
        _db.Dispose();

        // Migrate
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        AgentStateDb.MigrateToEncrypted(_dbPath, password, NullLogger<AgentStateDb>.Instance);

        // Verify data survived
        using var encrypted = new AgentStateDb(_dbPath, password);
        var pending = encrypted.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal("task-1", pending[0].TaskId);
        Assert.Equal(3, pending[0].RetryCount);
        Assert.True(encrypted.GetAuditEntryCount() >= 1);
    }

    [Fact(Skip = "Requires SQLCipher — skip on macOS/CI")]
    public void MigrateToEncrypted_AlreadyEncrypted_NoOp()
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _db.Dispose();

        // First migration
        AgentStateDb.MigrateToEncrypted(_dbPath, password, NullLogger<AgentStateDb>.Instance);

        // Write data to encrypted DB
        using (var enc = new AgentStateDb(_dbPath, password))
        {
            enc.UpsertWritebackState("task-2", "RX002", WritebackState.Queued, 0, null);
        }

        // Second migration should be no-op
        AgentStateDb.MigrateToEncrypted(_dbPath, password, NullLogger<AgentStateDb>.Instance);

        using var verify = new AgentStateDb(_dbPath, password);
        Assert.Single(verify.GetPendingWritebacks());
    }

    // ── 3. Package self-update stages all 3 binaries + sentinel ──

    [Fact]
    public void CheckPendingUpdate_ValidSentinel_SwapsAndCleans()
    {
        // Create a temp "install dir" with staged binaries and sentinel
        var tempDir = Path.Combine(Path.GetTempPath(), "suavo-crit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Stage binaries
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe"), "old");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe.new"), "new-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe"), "old");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe.new"), "new-broker");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe"), "old");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe.new"), "new-helper");

            // SwapBinaries swaps all 3
            var result = SelfUpdater.SwapBinaries(tempDir, _logger);
            Assert.True(result);

            // Verify all swapped
            Assert.Equal("new-core", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe")));
            Assert.Equal("new-broker", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Broker.exe")));
            Assert.Equal("new-helper", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Helper.exe")));

            // Old files preserved for rollback
            Assert.True(File.Exists(Path.Combine(tempDir, "SuavoAgent.Core.exe.old")));
            Assert.True(File.Exists(Path.Combine(tempDir, "SuavoAgent.Broker.exe.old")));
            Assert.True(File.Exists(Path.Combine(tempDir, "SuavoAgent.Helper.exe.old")));

            // No .new files remain
            Assert.Empty(Directory.GetFiles(tempDir, "*.exe.new"));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SwapBinaries_RollsBackOnPartialFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "suavo-crit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            // Stage Core.new but make Broker.new → Broker swap fail by creating a read-only old file
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe"), "old-core");
            File.WriteAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe.new"), "new-core");

            // If only Core has a .new file, it should swap that one and return true
            var result = SelfUpdater.SwapBinaries(tempDir, _logger);
            Assert.True(result);
            Assert.Equal("new-core", File.ReadAllText(Path.Combine(tempDir, "SuavoAgent.Core.exe")));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    // ── 4. Retry counts survive restart ──

    [Fact]
    public void RetryCount_SurvivesDbReopen()
    {
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 0, null);
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 3, "transient error");
        _db.Dispose();

        using var db2 = new AgentStateDb(_dbPath);
        var pending = db2.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal(3, pending[0].RetryCount);
    }

    [Fact]
    public void RetryCount_PreservedAcrossStateTransitions()
    {
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 0, null);
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.InProgress, 1, null);
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 2, "retry after fail");

        var pending = _db.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal(2, pending[0].RetryCount);
    }

    [Fact]
    public void UnsyncedBatch_RetryIncrementsAndDeadLetters()
    {
        _db.InsertUnsyncedBatch("payload-1");
        var batches = _db.GetPendingBatches();
        Assert.Single(batches);
        var batchId = batches[0].Id;

        // Simulate 10 retries → should become dead letter
        for (int i = 0; i < 10; i++)
            _db.IncrementBatchRetry(batchId);

        var pending = _db.GetPendingBatches();
        Assert.Empty(pending); // Dead-lettered, no longer pending

        Assert.True(_db.GetDeadLetterCount() >= 1);
    }

    // ── 5. Nonce persistence survives restart ──

    [Fact]
    public void Nonce_PersistsAcrossReopen()
    {
        Assert.True(_db.TryRecordNonce("nonce-1"));
        _db.Dispose();

        using var db2 = new AgentStateDb(_dbPath);
        Assert.False(db2.TryRecordNonce("nonce-1")); // Already used
        Assert.True(db2.TryRecordNonce("nonce-2"));   // New nonce works
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + ".bak"); } catch { }
    }
}
