using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public class VisionAssetsAclGrantTests
{
    [Fact]
    public void Grants_users_read_on_json_and_recursive_on_dir()
    {
        var args = VisionAssetsAclGrant.BuildIcaclsArgs(
            @"C:\ProgramData\SuavoAgent\vision.json", @"C:\ProgramData\SuavoAgent\vision");

        Assert.Equal(2, args.Count);
        // vision.json — single-file RX to BUILTIN\Users (SID form, locale-independent).
        Assert.Contains("vision.json", args[0]);
        Assert.Contains("*S-1-5-32-545:(RX)", args[0]);
        Assert.DoesNotContain("(OI)(CI)", args[0]); // not inheritable — it's a file
        // vision dir — recursive (OI)(CI)(RX) so DLLs + tessdata inherit read.
        Assert.Contains("(OI)(CI)(RX)", args[1]);
        Assert.Contains("/t", args[1]);
    }
}
