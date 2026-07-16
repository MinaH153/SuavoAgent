using System.Reflection;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class PioneerRxDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-pioneer-discovery-" + Guid.NewGuid().ToString("N"));

    public PioneerRxDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ValidatePath_RequiresBothExecutableAndConfiguration()
    {
        Assert.Null(Validate(Path.Combine(_root, "missing")));
        Assert.Null(Validate(_root));

        var exe = Path.Combine(_root, "PioneerPharmacy.exe");
        File.WriteAllText(exe, "binary-placeholder");
        Assert.Null(Validate(_root));

        var config = exe + ".config";
        File.WriteAllText(config, "<configuration />");
        var result = Validate(_root);

        Assert.NotNull(result);
        Assert.Equal(_root, result.PioneerDir);
        Assert.Equal(exe, result.PioneerExe);
        Assert.Equal(config, result.PioneerConfig);
    }

    [Fact]
    public void Discover_ExhaustsStrategiesAndReturnsNullWhenPioneerIsAbsent()
    {
        if (OperatingSystem.IsWindows()) return;

        Assert.Null(PioneerRxDiscovery.Discover());
    }

    private static PioneerRxDiscovery.DiscoveryResult? Validate(string path)
    {
        var method = typeof(PioneerRxDiscovery).GetMethod(
            "ValidatePath",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (PioneerRxDiscovery.DiscoveryResult?)method.Invoke(null, [path]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
