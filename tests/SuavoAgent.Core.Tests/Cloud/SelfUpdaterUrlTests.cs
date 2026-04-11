using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class SelfUpdaterUrlTests
{
    [Theory]
    [InlineData("https://github.com/MinaH153/SuavoAgent/releases/download/v2.1.0/SuavoAgent.Core.exe", true)]
    [InlineData("https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/file", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("https://suavollc.com/updates/v2.1.0.exe", true)]
    [InlineData("https://github.com.attacker.com/evil.exe", false)]
    [InlineData("https://evil-github.com/evil.exe", false)]
    [InlineData("https://notssuavollc.com/evil.exe", false)]
    [InlineData("http://github.com/insecure.exe", false)]
    [InlineData("ftp://github.com/ftp.exe", false)]
    [InlineData("https://attacker.com/github.com/evil.exe", false)]
    [InlineData("", false)]
    public void IsAllowedUrl_ValidatesCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, SelfUpdater.IsAllowedUrl(url));
    }
}
