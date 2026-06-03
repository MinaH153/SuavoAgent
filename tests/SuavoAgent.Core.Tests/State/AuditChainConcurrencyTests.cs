using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Regression tests for the AppendChainedAuditEntry concurrent-writer race
/// (Codex 2026-04-26 P1 finding). Two threads reading GetLastAuditHash
/// simultaneously would compute identical prev_hash and produce a chain
/// where row N's stored prev_hash diverges from row N-1's computed hash.
/// VerifyAuditChain would then return false on a chain that should be valid.
/// </summary>
public class AuditChainConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AuditChainConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_audit_concurrency_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void ConcurrentAppends_ProduceVerifiableChain()
    {
        const int writerCount = 16;
        const int writesPerWriter = 25;
        var startGate = new ManualResetEventSlim(false);
        var tasks = new List<Task>();

        for (int w = 0; w < writerCount; w++)
        {
            int writerId = w;
            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < writesPerWriter; i++)
                {
                    _db.AppendChainedAuditEntry(new AuditEntry(
                        TaskId: $"task-{writerId}-{i}",
                        EventType: "writeback_transition",
                        FromState: "Queued",
                        ToState: "Claimed",
                        Trigger: "Claim"));
                }
            }));
        }

        startGate.Set();
        Task.WaitAll(tasks.ToArray());

        Assert.Equal(writerCount * writesPerWriter, _db.GetAuditEntryCount());
        Assert.True(_db.VerifyAuditChain(),
            "Chain must verify after concurrent appends — race in GetLastAuditHash + INSERT must be serialized.");
    }

    [Fact]
    public void ConcurrentAppends_AllReturnUniqueHashes()
    {
        const int writerCount = 8;
        const int writesPerWriter = 10;
        var startGate = new ManualResetEventSlim(false);
        var hashes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int w = 0; w < writerCount; w++)
        {
            int writerId = w;
            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < writesPerWriter; i++)
                {
                    var h = _db.AppendChainedAuditEntry(new AuditEntry(
                        TaskId: $"task-{writerId}-{i}",
                        EventType: "writeback_transition",
                        FromState: "Queued",
                        ToState: "Claimed",
                        Trigger: "Claim"));
                    hashes.Add(h);
                }
            }));
        }

        startGate.Set();
        Task.WaitAll(tasks.ToArray());

        Assert.Equal(writerCount * writesPerWriter, hashes.Count);
        // Even with identical payloads (same task-id pattern), every entry's hash
        // depends on its prev_hash so all hashes must be unique. Duplicates would
        // mean two writers saw the same prev_hash — i.e. the race fired.
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void VerifyAuditChain_DuringConcurrentWrites_NeverFalseFails()
    {
        // Codex CRITICAL: VerifyAuditChain runs unlocked on the shared connection
        // while writers mutate the chain. The verifier must never observe a
        // chain that fails to verify mid-write.
        const int writerCount = 8;
        const int writesPerWriter = 50;
        const int verifierCount = 4;
        const int verifiesPerVerifier = 100;
        var startGate = new ManualResetEventSlim(false);
        var stop = new CancellationTokenSource();
        var verifierFailures = 0;
        var tasks = new List<Task>();

        for (int w = 0; w < writerCount; w++)
        {
            int writerId = w;
            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < writesPerWriter; i++)
                {
                    _db.AppendChainedAuditEntry(new AuditEntry(
                        TaskId: $"task-{writerId}-{i}",
                        EventType: "writeback_transition",
                        FromState: "Queued",
                        ToState: "Claimed",
                        Trigger: "Claim"));
                }
            }));
        }

        for (int v = 0; v < verifierCount; v++)
        {
            tasks.Add(Task.Run(() =>
            {
                startGate.Wait();
                for (int i = 0; i < verifiesPerVerifier && !stop.IsCancellationRequested; i++)
                {
                    if (!_db.VerifyAuditChain())
                    {
                        Interlocked.Increment(ref verifierFailures);
                    }
                }
            }));
        }

        startGate.Set();
        Task.WaitAll(tasks.ToArray());

        Assert.Equal(0, verifierFailures);
    }
}
