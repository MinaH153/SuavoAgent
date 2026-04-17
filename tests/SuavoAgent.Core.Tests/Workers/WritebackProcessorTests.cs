using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public class WritebackProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly IpcPipeServer _pipe;
    private readonly WritebackProcessor _processor;

    public WritebackProcessorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_wp_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _pipe = new IpcPipeServer("SuavoWPTest", msg =>
            Task.FromResult(new IpcResponse(msg.Id, IpcStatus.Ok, msg.Command, null, null)),
            NullLogger<IpcPipeServer>.Instance);
        _processor = new WritebackProcessor(
            NullLogger<WritebackProcessor>.Instance, _db, _pipe,
            Options.Create(new AgentOptions { AgentId = "test-agent" }));
    }

    [Fact]
    public void EnqueueWriteback_PersistsToDb()
    {
        _processor.EnqueueWriteback("task-1", "RX001");

        var pending = _db.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal("task-1", pending[0].TaskId);
        Assert.Equal(WritebackState.Queued, pending[0].State);
    }

    [Fact]
    public void EnqueueWriteback_Deduplicates()
    {
        _processor.EnqueueWriteback("task-1", "RX001");
        _processor.EnqueueWriteback("task-1", "RX001");

        Assert.Equal(1, _processor.ActiveMachineCount);
    }

    [Fact]
    public void ActiveMachineCount_TracksNonTerminal()
    {
        _processor.EnqueueWriteback("task-1", "RX001");
        _processor.EnqueueWriteback("task-2", "RX002");

        Assert.Equal(2, _processor.ActiveMachineCount);
    }

    [Fact]
    public void RxNumber_Preserved_AcrossRestart()
    {
        // Enqueue and persist to DB (simulating process before crash)
        _processor.EnqueueWriteback("task-crash", "RX99999", fillNumber: 7, transition: "complete");

        // Simulate process restart: dispose current DB, open a fresh instance on same file
        _db.Dispose();
        using var db2 = new AgentStateDb(_dbPath);

        var pending = db2.GetPendingWritebacks();
        Assert.Single(pending);
        Assert.Equal("task-crash", pending[0].TaskId);
        // Actual Rx number must survive restart (stored in rx_number_enc, not just HMAC hash)
        Assert.Equal("RX99999", pending[0].RxNumber);
        Assert.Equal(WritebackState.Queued, pending[0].State);
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
