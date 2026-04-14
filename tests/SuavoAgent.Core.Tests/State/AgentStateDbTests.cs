using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AgentStateDbTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AgentStateDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void UpsertAndRetrieve_WritebackState()
    {
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 0, null);
        var pending = _db.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal("task-1", pending[0].TaskId);
        Assert.Equal(WritebackState.Queued, pending[0].State);
    }

    [Fact]
    public void Upsert_UpdatesExistingState()
    {
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 0, null);
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.InProgress, 1, null);

        var pending = _db.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal(WritebackState.InProgress, pending[0].State);
        Assert.Equal(1, pending[0].RetryCount);
    }

    [Fact]
    public void GetPending_ExcludesDoneAndManualReview()
    {
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Done, 0, null);
        _db.UpsertWritebackState("task-2", "RX002", WritebackState.ManualReview, 2, "failed");
        _db.UpsertWritebackState("task-3", "RX003", WritebackState.Queued, 0, null);

        var pending = _db.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal("task-3", pending[0].TaskId);
    }

    [Fact]
    public void AuditEntries_AppendAndCount()
    {
        _db.AppendAuditEntry("task-1", WritebackState.Queued, WritebackState.Claimed, WritebackTrigger.Claim, null);
        _db.AppendAuditEntry("task-1", WritebackState.Claimed, WritebackState.InProgress, WritebackTrigger.StartUia, "hash1");

        Assert.Equal(2, _db.GetAuditEntryCount());
    }

    [Fact]
    public void Db_PersistsAcrossReopen()
    {
        _db.UpsertWritebackState("task-1", "RX001", WritebackState.Queued, 0, null);
        _db.Dispose();

        using var db2 = new AgentStateDb(_dbPath);
        var pending = db2.GetPendingWritebacks();
        Assert.Single(pending);
    }

    [Fact]
    public void InitSchema_SetsBusyTimeout()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-busy-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var field = typeof(AgentStateDb).GetField("_conn",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var conn = (Microsoft.Data.Sqlite.SqliteConnection)field!.GetValue(db)!;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout";
            var result = cmd.ExecuteScalar();
            Assert.NotNull(result);
            Assert.True((long)result! >= 5000, $"busy_timeout should be >= 5000, was {result}");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
