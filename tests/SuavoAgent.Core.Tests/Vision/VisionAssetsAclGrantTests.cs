using System.IO;
using System.Linq;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public class VisionAssetsAclGrantTests
{
    [Fact]
    public void Json_grant_is_single_file_rx_to_builtin_users()
    {
        var args = VisionAssetsAclGrant.JsonGrantArgs(@"C:\ProgramData\SuavoAgent\vision.json");
        // ArgumentList form — path, /grant, sid:(RX) as separate elements (no interpolation).
        Assert.Equal(3, args.Count);
        Assert.EndsWith("vision.json", args[0]);
        Assert.Equal("/grant", args[1]);
        Assert.Equal("*S-1-5-32-545:(RX)", args[2]);
        Assert.DoesNotContain("(OI)(CI)", args[2]); // a file, not inheritable
    }

    [Fact]
    public void Dir_grant_is_recursive_inheritable_rx()
    {
        var args = VisionAssetsAclGrant.DirGrantArgs(@"C:\ProgramData\SuavoAgent\vision");
        Assert.Equal(4, args.Count);
        Assert.Equal("*S-1-5-32-545:(OI)(CI)(RX)", args[2]);
        Assert.Equal("/t", args[3]);
    }

    [Theory]
    [InlineData("vision", true)]
    [InlineData("vision.json", true)]
    [InlineData("vision/tessdata", true)]
    [InlineData("../../Windows", false)]          // escape attempt (resolved by GetFullPath)
    [InlineData("../OtherApp", false)]
    public void Only_paths_under_the_suavo_root_are_allowed(string rel, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "suavo-acl-root");
        // OS-agnostic: split on '/' and Path.Combine so the platform separator + ".." resolution apply.
        var candidate = Path.Combine(new[] { root }.Concat(rel.Split('/')).ToArray());
        Assert.Equal(expected, VisionAssetsAclGrant.IsUnderSuavoRoot(candidate, root));
    }

    [Fact]
    public void An_absolute_path_outside_the_root_is_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "suavo-acl-root");
        Assert.False(VisionAssetsAclGrant.IsUnderSuavoRoot(@"C:\Windows\System32", root));
    }
}
