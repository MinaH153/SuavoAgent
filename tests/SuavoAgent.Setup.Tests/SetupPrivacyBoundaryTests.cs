using SuavoAgent.Setup.Gui.ViewModels;
using System.Text.RegularExpressions;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class SetupPrivacyBoundaryTests
{
    [Theory]
    [InlineData("Patient: Jane Doe | DOB: 01/02/1980")]
    [InlineData("Rx # 123456 for John Smith")]
    [InlineData("MRN: ABC123")]
    public void SetupLog_ScrubsPhiBeforePersistence(string sensitive)
    {
        var sanitized = SetupLog.SanitizeForLog(sensitive);

        Assert.DoesNotContain("Jane Doe", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("John Smith", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ABC123", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("01/02/1980", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("123456", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuiFailureDetail_NeverReflectsExceptionMessage()
    {
        const string sensitive = @"Jane Doe C:\Patients\rx-1234.txt password=secret";

        var detail = MainWindowViewModel.BuildSafeFailureDetail(
            "install",
            new InvalidOperationException(sensitive));

        Assert.DoesNotContain(sensitive, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("password=secret", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", detail, StringComparison.Ordinal);
        Assert.Contains("Support code: SETUP-INSTALL-SAFE-FAIL", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressDetails_ScrubPhiEvenForDirectAppend()
    {
        var viewModel = new ProgressViewModel(() => { });

        viewModel.AppendLog("Patient: Jane Doe | DOB: 01/02/1980", LogLineKind.Fail);

        var text = Assert.Single(viewModel.LogLines).Text;
        Assert.DoesNotContain("Jane Doe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("01/02/1980", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupSource_DoesNotReflectRawExceptionMessagesOrGuiLogTail()
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Setup"));
        if (!Directory.Exists(sourceRoot)) return;

        var source = string.Join('\n', Directory.EnumerateFiles(
            sourceRoot,
            "*.cs",
            SearchOption.AllDirectories).Select(File.ReadAllText));

        Assert.DoesNotMatch(
            new Regex(@"\b(?:ex|exception|error|failure|lastEx|rollbackEx)\??\.Message\b"),
            source);
        Assert.DoesNotContain("BuildLogTail", source, StringComparison.Ordinal);
    }
}
