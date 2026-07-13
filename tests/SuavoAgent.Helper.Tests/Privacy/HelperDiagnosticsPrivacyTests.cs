using System.Text.RegularExpressions;
using Xunit;

namespace SuavoAgent.Helper.Tests.Privacy;

public sealed class HelperDiagnosticsPrivacyTests
{
    [Fact]
    public void ProductionHelperNeverLogsOrReturnsRawExceptionContent()
    {
        var helperRoot = FindRepoDirectory("src/SuavoAgent.Helper");
        var sources = Directory.EnumerateFiles(helperRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .ToArray();
        var exceptionLogger = new Regex(
            @"(?:_logger|Log(?:\.Logger)?)\.(?:Error|Warning|Information|Debug|Fatal)\s*\(\s*ex\s*,",
            RegexOptions.CultureInvariant);

        Assert.DoesNotContain(sources, source => exceptionLogger.IsMatch(source.Text));
        Assert.DoesNotContain(sources, source => source.Text.Contains("ex.Message", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Text.Contains("exception.Message", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.Text.Contains("ex.ToString()", StringComparison.Ordinal));
    }

    private static string FindRepoDirectory(string relativePath)
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = Path.Combine(cursor.FullName, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            cursor = cursor.Parent;
        }

        throw new DirectoryNotFoundException(relativePath);
    }
}
