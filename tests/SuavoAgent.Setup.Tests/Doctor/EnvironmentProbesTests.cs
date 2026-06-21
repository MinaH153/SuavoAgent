// tests/SuavoAgent.Setup.Tests/Doctor/EnvironmentProbesTests.cs
using SuavoAgent.Setup.Doctor;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Doctor;

public class EnvironmentProbesTests
{
    [Fact]
    public void Version_known_is_Ok_with_version_in_detail()
    {
        var r = new VersionProbe(() => "3.71.0").Check();
        Assert.Equal(GateState.Ok, r.State);
        Assert.Contains("3.71.0", r.Detail);
    }

    [Fact]
    public void Version_unknown_is_Warn()
        => Assert.Equal(GateState.Warn, new VersionProbe(() => null).Check().State);

    [Fact]
    public void Avx2_cpu_with_noavx_build_is_Warn()
        => Assert.Equal(GateState.Warn, new CpuVariantProbe(() => true, () => "noavx").Check().State);

    [Fact]
    public void Matched_variant_is_Ok()
        => Assert.Equal(GateState.Ok, new CpuVariantProbe(() => true, () => "avx2").Check().State);

    [Fact]
    public void Non_avx2_cpu_on_noavx_is_Ok()
        => Assert.Equal(GateState.Ok, new CpuVariantProbe(() => false, () => "noavx").Check().State);
}
