using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public class RxDetectionWorkerTests
{
    [Fact]
    public void InitialState_NotConnected()
    {
        var options = Options.Create(new AgentOptions { ApiKey = "test-key" });
        var client = new SuavoCloudClient(options.Value);
        var worker = new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance, options, client);

        Assert.False(worker.IsSqlConnected);
        Assert.Equal(0, worker.LastDetectedCount);
        Assert.Null(worker.LastDetectionTime);

        client.Dispose();
    }
}
