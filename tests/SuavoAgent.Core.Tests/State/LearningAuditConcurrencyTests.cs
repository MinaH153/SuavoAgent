using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Regression tests for the AppendLearningAudit concurrent-writer race
/// (Codex 2026-04-27 P1 follow-up). Same shape as AppendChainedAuditEntry:
/// SELECT prev_hash + INSERT must be serialized or two writers can read
/// the same prev_hash and corrupt the chain.
/// </summary>
public class LearningAuditConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public LearningAuditConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_learning_audit_concurrency_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ConcurrentLearningAudits_AllSucceed_AndChainIsIntact()
    {
        // 16 concurrent writers per session — chain integrity is verified by
        // walking the resulting rows in id order and confirming each row's
        // prev_hash equals SHA256(prev_row's chain inputs). Without lock,
        // two writers see same prev and the chain breaks.
        const int writerCount = 16;
        const int writesPerWriter = 25;
        const string sessionId = "session-1";
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
                    _db.AppendLearningAudit(sessionId,
                        observer: $"observer-{writerId}",
                        action: "scan",
                        target: $"target-{i}",
                        phiScrubbed: true);
                }
            }));
        }

        startGate.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(writerCount * writesPerWriter, _db.GetLearningAuditCount(sessionId));
        Assert.True(_db.VerifyLearningAuditChain(sessionId),
            "learning_audit chain must verify after concurrent appends. " +
            "Race in SELECT prev_hash + INSERT must be serialized.");
    }
}
