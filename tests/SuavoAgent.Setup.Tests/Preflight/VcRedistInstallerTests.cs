// tests/SuavoAgent.Setup.Tests/Preflight/VcRedistInstallerTests.cs
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests.Preflight;

public class VcRedistInstallerTests
{
    private static VcRedistInstaller Make(int exitCode, bool installedAfter)
    {
        var checker = new VcRedistChecker(
            fileExists: _ => installedAfter, readRegistryVersion: () => null);
        return new VcRedistInstaller(
            runProcess: (_, _, _) => Task.FromResult(exitCode), checker: checker);
    }

    [Fact]
    public async Task ExitCode0_with_dlls_present_is_success()
    {
        var r = await Make(0, installedAfter: true).InstallAsync("vc_redist.x64.exe", CancellationToken.None);
        Assert.True(r.Success);
        Assert.True(r.VerifiedAfter);
        Assert.False(r.RebootPending);
    }

    [Fact]
    public async Task ExitCode3010_sets_reboot_pending_but_succeeds()
    {
        var r = await Make(3010, installedAfter: true).InstallAsync("vc_redist.x64.exe", CancellationToken.None);
        Assert.True(r.Success);
        Assert.True(r.RebootPending);
    }

    [Fact]
    public async Task NonZero_unknown_exit_is_failure()
    {
        var r = await Make(1603, installedAfter: false).InstallAsync("vc_redist.x64.exe", CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(1603, r.ExitCode);
    }
}
