using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class SandboxProcessTrustVerifierTests
{
    [Theory]
    [InlineData("notepad.exe", "NOTEPAD", true)]
    [InlineData("calc.exe", "CalculatorApp", true)]
    [InlineData("calculator", "calc.exe", true)]
    [InlineData("notepad.exe", "calc.exe", false)]
    [InlineData("notepad.exe", "PioneerPharmacy.exe", false)]
    public void TargetIdentityMatches_BindsKeyboardActionToEstablishedApp(
        string expected,
        string established,
        bool matches)
        => Assert.Equal(matches, SendInputDriver.TargetIdentityMatches(expected, established));

    [Theory]
    [InlineData("notepad.exe", "Notepad", "/windows/System32/NOTEPAD.EXE", true)]
    [InlineData("calc.exe", "CalculatorApp", "/program-files/WindowsApps/pkg/CalculatorApp.exe", true)]
    [InlineData("CALC.EXE", "calculator", "/program-files/WindowsApps/pkg/CALCULATOR.exe", true)]
    [InlineData("notepad.exe", "PioneerPharmacy", "/windows/System32/PioneerPharmacy.exe", false)]
    [InlineData("notepad.exe", "notepad", "/windows/System32/renamed.exe", false)]
    public void IdentityMatchesRequested_HandlesAliasesCaseAndRenames(
        string requested,
        string resolved,
        string path,
        bool expected)
        => Assert.Equal(expected,
            SandboxProcessTrustVerifier.IdentityMatchesRequested(requested, resolved, path));

    [Theory]
    [InlineData("notepad.exe", "notepad", "/windows/System32/notepad.exe", "NOTEPAD.EXE", "Notepad", true)]
    [InlineData("notepad.exe", "notepad", "/windows/System32/notepad.exe", "MSHTA.EXE", "MSHTA", false)]
    [InlineData("calc.exe", "CalculatorApp", "/program-files/WindowsApps/pkg/CalculatorApp.exe", "CalculatorApp.exe", "CalculatorApp", true)]
    public void IdentityMatchesRequested_PinsOriginalPeIdentityAgainstSignedBinaryRename(
        string requested,
        string resolved,
        string path,
        string originalFilename,
        string internalName,
        bool expected)
        => Assert.Equal(expected, SandboxProcessTrustVerifier.IdentityMatchesRequested(
            requested, resolved, path, originalFilename, internalName));

    [Theory]
    [InlineData("/windows/System32/notepad.exe", true)]
    [InlineData("/windows/notepad.exe", true)]
    [InlineData("/program-files/WindowsApps/pkg/Notepad.exe", true)]
    [InlineData("/windows/SysWOW64/notepad.exe", true)]
    [InlineData("/windows/Temp/notepad.exe", false)]
    [InlineData("/windows/SystemApps/notepad.exe", false)]
    [InlineData("/users/josh/notepad.exe", false)]
    [InlineData("/windows-old/notepad.exe", false)]
    [InlineData("/program-files/WindowsApps-Evil/notepad.exe", false)]
    public void IsTrustedWindowsLocation_RequiresProtectedSystemBoundary(string path, bool expected)
        => Assert.Equal(expected, SandboxProcessTrustVerifier.IsTrustedWindowsLocation(
            path, "/windows", "/program-files"));

    [Theory]
    [InlineData("CN=Microsoft Windows, O=Microsoft Corporation, C=US", true)]
    [InlineData("CN=Microsoft Corporation", false)]
    [InlineData("CN=Microsoft Tools LLC, O=Microsoft Tools LLC, C=US", false)]
    [InlineData("CN=Acme, OU=O=Microsoft Corporation, O=Acme", false)]
    [InlineData("CN=Acme Pharmacy Software, O=Acme", false)]
    [InlineData(null, false)]
    public void IsMicrosoftSignerSubject_RequiresMicrosoftPublisher(string? subject, bool expected)
        => Assert.Equal(expected, SandboxProcessTrustVerifier.IsMicrosoftSignerSubject(subject));

    [Fact]
    public void VerifyExecutablePath_RealSystemNotepad_IsMicrosoftTrusted_OnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        var verdict = SandboxProcessTrustVerifier.VerifyExecutablePath(notepad, "notepad.exe");

        Assert.True(verdict.Trusted, verdict.Code);
    }
}
