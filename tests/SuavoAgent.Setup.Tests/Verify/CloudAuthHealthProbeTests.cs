// tests/SuavoAgent.Setup.Tests/Verify/CloudAuthHealthProbeTests.cs
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class CloudAuthHealthProbeTests
{
    private static GateResult Run(string? json) =>
        new CloudAuthHealthProbe(() => json).Check();

    [Fact]
    public void Status_ok_is_Ok()
    {
        var r = Run("{\"status\":\"ok\",\"lastSuccessAt\":\"2026-06-20T10:00:00Z\",\"lastErrorKind\":null}");
        Assert.Equal(GateState.Ok, r.State);
    }

    [Fact]
    public void Auth_error_kind_is_Fail()
    {
        var r = Run("{\"status\":\"failed\",\"lastErrorKind\":\"401_unauthorized\"}");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("401", r.Detail);
    }

    [Fact]
    public void Missing_file_is_Warn_not_Fail()
    {
        var r = Run(null);
        Assert.Equal(GateState.Warn, r.State);
    }
}
