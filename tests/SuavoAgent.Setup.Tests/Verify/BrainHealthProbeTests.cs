// tests/SuavoAgent.Setup.Tests/Verify/BrainHealthProbeTests.cs
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class BrainHealthProbeTests
{
    private static GateResult Run(string? log) => new BrainHealthProbe(() => log).Check();

    [Fact]
    public void Native_load_failure_is_Fail_with_remediation()
    {
        var r = Run("INF Tier-2 LocalInference ENABLED\nERR LLamaLocalInference: model load failed\nNativeApi threw");
        Assert.Equal(GateState.Fail, r.State);
        Assert.Contains("VC++", r.Detail);
    }

    [Fact]
    public void Model_loaded_is_Ok()
    {
        var r = Run("INF LLamaLocalInference: model loaded in 716ms (qwen3-1.7b)");
        Assert.Equal(GateState.Ok, r.State);
    }

    [Fact]
    public void Reasoning_disabled_is_Skip()
    {
        var r = Run("INF Tier-2 LocalInference disabled (Reasoning.Enabled=false) — running rules-only");
        Assert.Equal(GateState.Skip, r.State);
    }

    [Fact]
    public void Enabled_but_not_yet_loaded_is_Ok_provisioned()
    {
        var r = Run("INF Tier-2 LocalInference ENABLED — model 'qwen3-1.7b' (deferred: provisioning if absent)");
        Assert.Equal(GateState.Ok, r.State);
    }

    [Fact]
    public void No_markers_is_Warn()
    {
        var r = Run("INF some unrelated startup line");
        Assert.Equal(GateState.Warn, r.State);
    }
}
