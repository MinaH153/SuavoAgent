using System.IO;
using System.Security.AccessControl;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// Guards the exact handle-bound policy the de-privileged Helper needs to
/// self-extract its single-file apphost.
/// </summary>
public class HelperExeAclGrantTests
{
    private const string Users = "S-1-5-32-545"; // BUILTIN\Users

    [Fact]
    public void Principal_is_builtin_users_sid()
    {
        Assert.Equal(Users, HelperExeAclGrant.HelperSid);
    }

    [Fact]
    public void BuildMutations_grants_dir_traverse_then_helper_exe_rx()
    {
        const string installDir = @"C:\Program Files\Suavo\Agent";
        // Expected helper path uses Path.Combine so the separator matches the host running the test
        // (the production target is Windows = backslash; this keeps the assertion green on the CI gate).
        var helperExe = Path.Combine(installDir, HelperExeAclGrant.HelperExeName);
        var mutations = HelperExeAclGrant.BuildMutations(installDir);

        Assert.Equal(2, mutations.Count);

        // 1) Dir grant — traverse/list THIS DIR ONLY. RX, and crucially NO (OI)(CI): inherited file
        //    reads would expose appsettings.json (configuration + DPAPI-sealed SQL creds)
        //    + the other service binaries.
        Assert.Equal(installDir, mutations[0].Path);
        var rootUsers = Assert.Single(mutations[0].Policy.Aces, ace =>
            ace.Sid == HelperExeAclGrant.HelperSid);
        Assert.Equal(FileSystemRights.ReadAndExecute, rootUsers.Rights);
        Assert.Equal(InheritanceFlags.None, rootUsers.InheritanceFlags);

        // 2) Per-file RX on the single-file apphost itself — the only install-dir file the Helper reads.
        Assert.Equal(helperExe, mutations[1].Path);
        var fileUsers = Assert.Single(mutations[1].Policy.Aces, ace =>
            ace.Sid == HelperExeAclGrant.HelperSid);
        Assert.Equal(FileSystemRights.ReadAndExecute, fileUsers.Rights);
        Assert.Equal(InheritanceFlags.None, fileUsers.InheritanceFlags);
        Assert.All(mutations, mutation => Assert.Equal(
            "S-1-5-18",
            mutation.Policy.OwnerSid));
    }

    [Fact]
    public void BuildMutations_targets_the_helper_apphost_by_name()
    {
        var mutations = HelperExeAclGrant.BuildMutations(@"D:\install");
        Assert.Contains(HelperExeAclGrant.HelperExeName, mutations[1].Path);
        Assert.EndsWith(
            Path.Combine("install", HelperExeAclGrant.HelperExeName),
            mutations[1].Path);
    }

    [Fact]
    public void Apply_returns_false_when_install_dir_missing()
    {
        // Best-effort + never throws; a non-existent dir is a clean false (nothing to grant).
        var missing = Path.Combine(Path.GetTempPath(), "no-such-suavo-" + System.Guid.NewGuid().ToString("N"));
        Assert.False(HelperExeAclGrant.Apply(missing));
    }
}
