using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class UnsyncedBatchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public UnsyncedBatchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_batch_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void InsertBatch_RetrievesPending()
    {
        var payload = """{"v":1,"items":[{"rxNumber":"RX001"}]}""";
        _db.InsertUnsyncedBatch(payload);
        var pending = _db.GetPendingBatches();
        Assert.Single(pending);
        Assert.Equal(payload, pending[0].Payload);
        Assert.Equal(0, pending[0].RetryCount);
        Assert.Equal("pending", pending[0].Status);
    }

    [Fact]
    public void DeleteBatch_RemovesFromPending()
    {
        _db.InsertUnsyncedBatch("payload1");
        var pending = _db.GetPendingBatches();
        _db.DeleteBatch(pending[0].Id);
        Assert.Empty(_db.GetPendingBatches());
    }

    [Fact]
    public void IncrementRetry_UpdatesCount()
    {
        _db.InsertUnsyncedBatch("payload1");
        var batch = _db.GetPendingBatches()[0];
        _db.IncrementBatchRetry(batch.Id);
        var updated = _db.GetPendingBatches();
        Assert.Single(updated);
        Assert.Equal(1, updated[0].RetryCount);
    }

    [Fact]
    public void IncrementRetry_MovesToDeadLetterAt10()
    {
        _db.InsertUnsyncedBatch("payload1");
        var batch = _db.GetPendingBatches()[0];
        for (int i = 0; i < 10; i++)
            _db.IncrementBatchRetry(batch.Id);
        Assert.Empty(_db.GetPendingBatches());
        Assert.Equal(1, _db.GetDeadLetterCount());
    }

    [Fact]
    public void PurgeExpiredDeadLetters_RemovesOldEntries()
    {
        _db.InsertUnsyncedBatch("payload1");
        var batch = _db.GetPendingBatches()[0];
        for (int i = 0; i < 10; i++)
            _db.IncrementBatchRetry(batch.Id);
        _db.BackdateExpiresAt(batch.Id, DateTimeOffset.UtcNow.AddDays(-31));
        _db.PurgeExpiredDeadLetters();
        Assert.Equal(0, _db.GetDeadLetterCount());
    }

    [Fact]
    public void MultipleBatches_ConsecutiveFailuresDontOverwrite()
    {
        _db.InsertUnsyncedBatch("batch1");
        _db.InsertUnsyncedBatch("batch2");
        var pending = _db.GetPendingBatches();
        Assert.Equal(2, pending.Count);
        Assert.Equal("batch1", pending[0].Payload);
        Assert.Equal("batch2", pending[1].Payload);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
