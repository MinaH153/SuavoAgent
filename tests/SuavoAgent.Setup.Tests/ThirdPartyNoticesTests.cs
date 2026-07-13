using SuavoAgent.Setup.Gui;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class ThirdPartyNoticesTests
{
    [Fact]
    public void Embedded_notice_covers_required_runtime_and_external_asset_families()
    {
        var notice = ThirdPartyNotices.Read();

        Assert.Contains("MICROSOFT .NET RUNTIME 8.0.28", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Avalonia.Fonts.Inter", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("SIL OPEN FONT LICENSE", notice, StringComparison.Ordinal);
        Assert.Contains("qwen3-1.7b-q4-k-m", notice, StringComparison.Ordinal);
        Assert.Contains("llamasharp-backend-cpu-0.24.0", notice, StringComparison.Ordinal);
        Assert.Contains("tesseract-native-5.2.0-eng", notice, StringComparison.Ordinal);
        Assert.Contains("pharmacist-panda", notice, StringComparison.Ordinal);
    }
}
