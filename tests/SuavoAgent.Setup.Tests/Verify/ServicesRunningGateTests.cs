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

    [Fact]
    public void Helper_down_is_Warn_not_Fail()
    {
        // A headless / locked / RDP-disconnected session (the console fleet-deploy path) legitimately
        // has no Helper — Core+Broker up is a healthy install and must NOT be blocked.
        var r = ServiceInstaller.ClassifyServices(core: true, broker: true, watchdog: true, helper: false);
        Assert.Equal(GateState.Warn, r.State);
    }

    [Fact]
    public void Broker_down_is_Fail()
    {
        var r = ServiceInstaller.ClassifyServices(core: true, broker: false, watchdog: true, helper: true);
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("Broker", r.Detail);
    }
}
