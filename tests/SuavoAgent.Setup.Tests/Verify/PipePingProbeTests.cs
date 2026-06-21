// tests/SuavoAgent.Setup.Tests/Verify/PipePingProbeTests.cs
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class PipePingProbeTests
{
    [Fact]
    public async Task Missing_nonce_is_Warn()
    {
        var probe = new PipePingProbe(() => null, (_, _) => Task.FromResult(true));
        var r = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(GateState.Warn, r.State);
    }

    [Fact]
    public async Task Connect_success_is_Ok_and_uses_cmd_pipe_name()
    {
        string? attempted = null;
        var probe = new PipePingProbe(() => "abc123", (name, _) => { attempted = name; return Task.FromResult(true); });
        var r = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(GateState.Ok, r.State);
        Assert.Equal("SuavoAgent-cmd-abc123", attempted);
    }

    [Fact]
    public async Task Connect_failure_is_Fail()
    {
        var probe = new PipePingProbe(() => "abc123", (_, _) => Task.FromResult(false));
        var r = await probe.CheckAsync(CancellationToken.None);
        Assert.Equal(GateState.Fail, r.State);
    }
}
