using SuavoAgent.Setup.Preflight;
using Xunit;

namespace SuavoAgent.Setup.Tests.Preflight;

public sealed class WindowsVolumeEncryptionProbeTests
{
    [Fact]
    public void Non_system_phi_volume_is_the_volume_that_must_be_protected()
    {
        var data = OperatingSystem.IsWindows()
            ? @"D:\SuavoAgent\state.db"
            : Path.Combine(Path.GetTempPath(), "suavo", "state.db");
        string? probed = null;

        var result = WindowsVolumeEncryptionProbe.Evaluate(
            [data],
            root =>
            {
                probed = root;
                return new(root, ReturnCode: 0, ProtectionStatus: 1);
            });

        Assert.True(result.IsProtected, result.Detail);
        Assert.Equal(Path.GetPathRoot(Path.GetFullPath(data)), probed);
        if (OperatingSystem.IsWindows()) Assert.Equal(@"D:\", probed);
    }

    [Fact]
    public void Numeric_wmi_status_is_authority_and_localized_diagnostic_is_never_parsed()
    {
        var data = Path.Combine(Path.GetTempPath(), "suavo-localized", "state.db");

        var enabled = WindowsVolumeEncryptionProbe.Evaluate(
            [data],
            root => new(
                root,
                ReturnCode: 0,
                ProtectionStatus: 1,
                ProviderDiagnostic: "Protección activada"));
        var disabled = WindowsVolumeEncryptionProbe.Evaluate(
            [data],
            root => new(
                root,
                ReturnCode: 0,
                ProtectionStatus: 0,
                ProviderDiagnostic: "Protection On"));
        var apiFailure = WindowsVolumeEncryptionProbe.Evaluate(
            [data],
            root => new(
                root,
                ReturnCode: 5,
                ProtectionStatus: 1,
                ProviderDiagnostic: "Protection On"));

        Assert.True(enabled.IsProtected, enabled.Detail);
        Assert.False(disabled.IsProtected);
        Assert.False(apiFailure.IsProtected);
    }

    [Fact]
    public void Every_distinct_phi_volume_must_return_success_and_protection_on()
    {
        var first = OperatingSystem.IsWindows()
            ? @"C:\ProgramData\SuavoAgent\state.db"
            : Path.Combine(Path.GetTempPath(), "volume-a", "state.db");
        var second = OperatingSystem.IsWindows()
            ? @"D:\SuavoAgent-Retained\evidence.json"
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "var", "tmp", "volume-b", "evidence.json");

        var result = WindowsVolumeEncryptionProbe.Evaluate(
            [first, second],
            root => new(
                root,
                ReturnCode: 0,
                ProtectionStatus: root == Path.GetPathRoot(Path.GetFullPath(first)) ? 1U : 0U));

        if (OperatingSystem.IsWindows())
        {
            Assert.False(result.IsProtected);
            Assert.Equal(2, result.Volumes.Count);
        }
        else
        {
            // Unix has one filesystem root; production invocation is Windows-only.
            Assert.True(result.IsProtected);
            Assert.Single(result.Volumes);
        }
    }
}
