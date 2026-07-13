using System.Text.RegularExpressions;
using Xunit;

namespace SuavoAgent.Core.Tests.Diagnostics;

public sealed class ServiceDiagnosticSourceGuardTests
{
    private static readonly Regex RawMicrosoftExceptionLog = new(
        @"\.(?:LogTrace|LogDebug|LogInformation|LogWarning|LogError|LogCritical)\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex RawSerilogExceptionLog = new(
        @"(?:Serilog\.)?Log\.(?:Verbose|Debug|Information|Warning|Error|Fatal)\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex RawMemberExceptionLog = new(
        @"(?:LogTrace|LogDebug|LogInformation|LogWarning|LogError|LogCritical|Log\.(?:Verbose|Debug|Information|Warning|Error|Fatal))\s*\(\s*[A-Za-z_][A-Za-z0-9_.?]*\.(?:Exception|InnerException|ExceptionObject)\s*,",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex CaughtExceptionMessage = new(
        @"\b(?:exception|[A-Za-z_][A-Za-z0-9_]*(?:ex|Ex))\.Message\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex InterpolatedDiagnosticMessage = new(
        @"(?:LogTrace|LogDebug|LogInformation|LogWarning|LogError|LogCritical|Log\.(?:Verbose|Debug|Information|Warning|Error|Fatal))\s*\(\s*\$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [Fact]
    public void ServiceSources_NeverSendRawExceptionsToDiagnosticsSinks()
    {
        foreach (var file in ServiceSourceFiles())
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotMatch(RawMicrosoftExceptionLog, source);
            Assert.DoesNotMatch(RawSerilogExceptionLog, source);
            Assert.DoesNotMatch(RawMemberExceptionLog, source);
            Assert.DoesNotContain("ExceptionObject?.ToString()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ex.ToString()", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ServiceSources_DoNotForwardCaughtExceptionMessages()
    {
        foreach (var file in ServiceSourceFiles())
        {
            // This coordinator inspects the already locally-sanitized cloud reason to
            // distinguish the retired-agent 401. It does not log, ACK, or persist the message.
            if (file.EndsWith(
                    Path.Combine("Cloud", "AgentCredentialRecoveryClient.cs"),
                    StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotMatch(CaughtExceptionMessage, File.ReadAllText(file));
        }
    }

    [Fact]
    public void CoreSources_NeverBuildDiagnosticMessagesWithStringInterpolation()
    {
        foreach (var file in CoreSourceFiles())
            Assert.DoesNotMatch(InterpolatedDiagnosticMessage, File.ReadAllText(file));
    }

    [Fact]
    public void CoreConsoleAndFileSinks_InstallTheMandatoryPrivacyBoundary()
    {
        var program = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SuavoAgent.Core",
            "Program.cs"));

        Assert.Equal(2, Regex.Matches(program, @"\.SanitizeCoreDiagnostics\(\)").Count);
        Assert.True(
            program.LastIndexOf(".Enrich.FromLogContext()", StringComparison.Ordinal)
            < program.LastIndexOf(".SanitizeCoreDiagnostics()", StringComparison.Ordinal),
            "The redaction enricher must be the last enricher before the main sinks are built.");
        Assert.Contains("CoreDiagnosticRedactionEnricher", File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SuavoAgent.Core",
            "Diagnostics",
            "CoreDiagnosticRedactionEnricher.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void WritebackDiagnostics_NeverUsePublicFallbackOrIdentifierHashing()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SuavoAgent.Core",
            "Workers",
            "WritebackProcessor.cs"));

        Assert.DoesNotContain("[no-hmac-salt]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PhiScrubber.HmacHash", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{RxHash}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{TaskId}", source, StringComparison.Ordinal);
        Assert.DoesNotContain("{SessionId}", source, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ServiceSourceFiles()
    {
        var root = FindRepositoryRoot();
        foreach (var component in new[]
                 {
                     "SuavoAgent.Core",
                     "SuavoAgent.Broker",
                     "SuavoAgent.Watchdog",
                 })
        {
            var sourceRoot = Path.Combine(root, "src", component);
            foreach (var file in Directory.EnumerateFiles(
                         sourceRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> CoreSourceFiles()
    {
        var sourceRoot = Path.Combine(FindRepositoryRoot(), "src", "SuavoAgent.Core");
        return Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SuavoAgent.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate SuavoAgent.sln.");
    }
}
