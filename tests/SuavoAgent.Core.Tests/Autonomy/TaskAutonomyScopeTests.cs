using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Autonomy;

public sealed class TaskAutonomyScopeTests
{
    [Fact]
    public void Build_BindsTaskAppActionAndExecutor()
    {
        var baseline = TaskAutonomyScope.Build(
            "pricing", "PioneerPharmacy.exe", "pricing_sql_read", PricingExecutorMode.SqlFirst);

        Assert.NotEqual(baseline, TaskAutonomyScope.Build(
            "pricing", "PioneerPharmacy.exe", "click_by_signature", PricingExecutorMode.SqlFirst));
        Assert.NotEqual(baseline, TaskAutonomyScope.Build(
            "pricing", "notepad.exe", "pricing_sql_read", PricingExecutorMode.SqlFirst));
        Assert.NotEqual(baseline, TaskAutonomyScope.Build(
            "pricing", "PioneerPharmacy.exe", "pricing_sql_read", PricingExecutorMode.UiaFirst));
        Assert.NotEqual(baseline, TaskAutonomyScope.Build(
            "another-task", "PioneerPharmacy.exe", "pricing_sql_read", PricingExecutorMode.SqlFirst));
    }

    [Fact]
    public void Build_NormalizesCaseAndPathWithoutWeakeningScope()
    {
        var left = TaskAutonomyScope.Build(
            "Pricing", @"C:\Apps\PioneerPharmacy.EXE", "CLICK_BY_SIGNATURE", PricingExecutorMode.UiaFirst);
        var right = TaskAutonomyScope.Build(
            "pricing", "pioneerpharmacy", "click_by_signature", PricingExecutorMode.UiaFirst);

        Assert.Equal(left, right);
    }
}
