using Microsoft.Extensions.Logging.Abstractions;
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
            NullLogger<WritebackProcessor>.Instance, _db, _pipe);
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

    public void Dispose()
    {
        _pipe.Dispose();
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
