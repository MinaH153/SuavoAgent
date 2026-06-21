// tests/SuavoAgent.Setup.Tests/Verify/ServicesRunningGateTests.cs
using SuavoAgent.Setup;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class ServicesRunningGateTests
{
    [Fact]
    public void All_running_is_Ok()
        => Assert.Equal(GateState.Ok, ServiceInstaller.ClassifyServices(true, true, true, true).State);

    [Fact]
    public void Core_down_is_Fail()
    {
        var r = ServiceInstaller.ClassifyServices(false, true, true, true);
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("Core", r.Detail);
    }

    [Fact]
    public void Watchdog_down_is_Warn_not_Fail()
        => Assert.Equal(GateState.Warn, ServiceInstaller.ClassifyServices(true, true, false, true).State);
}
