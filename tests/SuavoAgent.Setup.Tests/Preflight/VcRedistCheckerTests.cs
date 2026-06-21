// tests/SuavoAgent.Setup.Tests/Preflight/VcRedistCheckerTests.cs
using System.Collections.Generic;
using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests.Preflight;

public class VcRedistCheckerTests
{
    private static VcRedistChecker WithDlls(params string[] present)
    {
        var set = new HashSet<string>(present, System.StringComparer.OrdinalIgnoreCase);
        return new VcRedistChecker(
            fileExists: path => set.Contains(System.IO.Path.GetFileName(path)),
            readRegistryVersion: () => "v14.40.33810");
    }

    [Fact]
    public void Installed_only_when_all_three_dlls_present()
    {
        var status = WithDlls("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll").Check();
        Assert.True(status.Installed);
        Assert.Empty(status.MissingDlls);
    }

    [Fact]
    public void Missing_vcruntime140_1_is_detected_even_when_base_runtime_present()
    {
        // The exact Nadim case: .NET ran (vcruntime140.dll present) but the brain bricked.
        var status = WithDlls("vcruntime140.dll", "msvcp140.dll").Check();
        Assert.False(status.Installed);
        Assert.Contains("vcruntime140_1.dll", status.MissingDlls);
    }
}
