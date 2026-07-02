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

    [Fact]
    public void Recovered_status_is_Ok_not_a_permanent_Fail()
    {
        // A successful credential recovery writes status:"recovered" with lastErrorKind null (the
        // recovery client no longer leaves the triggering 401 on a success). The probe must read this
        // as healthy — the prior code tested errKind BEFORE status and pinned the gate to Fail forever.
        var r = Run("{\"status\":\"recovered\",\"lastSuccessAt\":\"2026-06-20T10:00:00Z\",\"lastErrorKind\":null}");
        Assert.Equal(GateState.Ok, r.State);
    }
}
