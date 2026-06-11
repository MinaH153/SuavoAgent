using System.IO;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// Guards the exact icacls grant the de-privileged Helper needs to self-extract its single-file
/// apphost. The grant runs on Windows only (icacls), so these assert the ARGUMENT SHAPE — the same
/// strategy as <c>ServiceInstallerTests</c> — so nobody silently widens it (e.g. adds (OI)(CI),
/// which would leak appsettings.json reads) or changes the principal off BUILTIN\Users.
/// </summary>
public class HelperExeAclGrantTests
{
    private const string Users = "*S-1-5-32-545"; // BUILTIN\Users

    [Fact]
    public void Principal_is_builtin_users_sid()
    {
        Assert.Equal(Users, HelperExeAclGrant.HelperPrincipal);
    }

    [Fact]
    public void BuildIcaclsArgs_grants_dir_traverse_then_helper_exe_rx()
    {
        const string installDir = @"C:\Program Files\Suavo\Agent";
        // Expected helper path uses Path.Combine so the separator matches the host running the test
        // (the production target is Windows = backslash; this keeps the assertion green on the CI gate).
        var helperExe = Path.Combine(installDir, HelperExeAclGrant.HelperExeName);
        var args = HelperExeAclGrant.BuildIcaclsArgs(installDir);

        Assert.Equal(2, args.Count);

        // 1) Dir grant — traverse/list THIS DIR ONLY. RX, and crucially NO (OI)(CI): inherited file
        //    reads would expose appsettings.json (ApiKey + SQL creds) + the other service binaries.
        Assert.Equal($"\"{installDir}\" /grant \"{Users}:(RX)\"", args[0]);
        Assert.DoesNotContain("(OI)", args[0]);
        Assert.DoesNotContain("(CI)", args[0]);

        // 2) Per-file RX on the single-file apphost itself — the only install-dir file the Helper reads.
        Assert.Equal($"\"{helperExe}\" /grant \"{Users}:(RX)\"", args[1]);
        Assert.DoesNotContain("(OI)", args[1]);
        Assert.DoesNotContain("(CI)", args[1]);
    }

    [Fact]
    public void BuildIcaclsArgs_targets_the_helper_apphost_by_name()
    {
        var args = HelperExeAclGrant.BuildIcaclsArgs(@"D:\install");
        Assert.Contains(HelperExeAclGrant.HelperExeName, args[1]);
        Assert.EndsWith(Path.Combine("install", HelperExeAclGrant.HelperExeName) + "\" /grant \"" + Users + ":(RX)\"", args[1]);
    }

    [Fact]
    public void Apply_returns_false_when_install_dir_missing()
    {
        // Best-effort + never throws; a non-existent dir is a clean false (nothing to grant).
        var missing = Path.Combine(Path.GetTempPath(), "no-such-suavo-" + System.Guid.NewGuid().ToString("N"));
        Assert.False(HelperExeAclGrant.Apply(missing));
    }
}
