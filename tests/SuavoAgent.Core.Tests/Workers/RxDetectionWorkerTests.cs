using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public class RxDetectionWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _stateDb;

    public RxDetectionWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_rxworker_test_{Guid.NewGuid():N}.db");
        _stateDb = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void InitialState_NotConnected()
    {
        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var options = Options.Create(new AgentOptions());
        var worker = new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance,
            NullLoggerFactory.Instance,
            options, _stateDb, sp);

        Assert.False(worker.IsSqlConnected);
        Assert.Equal(0, worker.LastDetectedCount);
        Assert.Null(worker.LastDetectionTime);
    }

    public void Dispose()
    {
        _stateDb.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
