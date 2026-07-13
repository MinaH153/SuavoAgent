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
    public void Watchdog_down_is_Fail()
    {
        var r = ServiceInstaller.ClassifyServices(true, true, false, true);
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("Watchdog", r.Detail);
    }

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

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void Required_service_cohort_controls_install_success(
        bool core, bool broker, bool watchdog, bool expected)
        => Assert.Equal(expected, ServiceInstaller.RequiredServicesRunning(core, broker, watchdog));
}
