// tests/SuavoAgent.Setup.Tests/Doctor/ConfigDoctorProbeTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class ConfigDoctorProbeTests
{
    private static GateResult Run(string? json) => new ConfigDoctorProbe(() => json).Check();

    [Fact]
    public void Reports_effective_pricing_executor_nested()
    {
        var r = Run("{\"Agent\":{\"PricingExecutor\":\"SqlFirst\"}}");
        Assert.Equal(GateState.Ok, r.State);
        Assert.Contains("SqlFirst", r.Detail);
    }

    [Fact]
    public void Reports_effective_pricing_executor_flat()
    {
        var r = Run("{\"Agent.PricingExecutor\":\"SqlFirst\"}");
        Assert.Contains("SqlFirst", r.Detail);
    }

    [Fact]
    public void Missing_file_defaults_to_UiaFirst()
    {
        var r = Run(null);
        Assert.Equal(GateState.Ok, r.State);
        Assert.Contains("UiaFirst", r.Detail);
    }

    [Fact]
    public void Relax_ipc_gate_on_is_Fail()
    {
        var r = Run("{\"Agent\":{\"RelaxIpcClientPathValidation\":true}}");
        Assert.Equal(GateState.Fail, r.State);
    }
}
