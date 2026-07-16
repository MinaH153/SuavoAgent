using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

public sealed class ProtectedDesktopProcessClassifierTests
{
    [Theory]
    [InlineData("PioneerPharmacy.exe")]
    [InlineData("pIoNeErPhArMaCy")]
    [InlineData(@"C:\Program Files\PioneerRx\renamed.exe")]
    [InlineData("Computer-Rx")]
    [InlineData("Rx30.exe")]
    [InlineData("BestRx.exe")]
    [InlineData("QS1-NexGen.exe")]
    [InlineData("LibertySoftware.exe")]
    [InlineData("FrameworkLTC.exe")]
    [InlineData("McKessonPharmaserv.exe")]
    [InlineData("PioneerPharmacyHost*")]
    [InlineData("chrome.exe")]
    [InlineData("EXCEL.EXE")]
    [InlineData("pwsh")]
    [InlineData("notepad.exe")]
    [InlineData("msedgewebview2.exe")]
    [InlineData("WINWORD.EXE")]
    public void IsProtectedIdentity_HandlesAliasesCasePathsAndGlobs(string identity)
        => Assert.True(ProtectedDesktopProcessClassifier.IsProtectedIdentity(identity));

    [Fact]
    public void IsProtectedIdentity_RenamedBinaryStillDetectedFromProductOrPathMetadata()
        => Assert.True(ProtectedDesktopProcessClassifier.IsProtectedIdentity(
            "notepad.exe",
            @"C:\Vendor\PioneerRx\notepad.exe",
            "Pioneer Pharmacy Management System"));

    [Theory]
    [InlineData("calc.exe")]
    [InlineData("mspaint.exe")]
    public void IsProtectedIdentity_KnownSystemSandboxAppsRemainEligible(string identity)
        => Assert.False(ProtectedDesktopProcessClassifier.IsProtectedIdentity(identity));

    [Theory]
    [InlineData(@"C:\Program Files\PioneerRx\PioneerPharmacy.EXE", "pioneerpharmacy")]
    [InlineData("PioneerPharmacyHost*", "pioneerpharmacyhost")]
    [InlineData("calc.exe", "calc")]
    public void CanonicalProcessStem_NormalizesPathExtensionCaseAndGlob(string raw, string expected)
        => Assert.Equal(expected, ProtectedDesktopProcessClassifier.CanonicalProcessStem(raw));
}
