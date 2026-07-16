using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class UninstallTerminalCleanupEvidenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-terminal-evidence-" + Guid.NewGuid().ToString("N"));

    public UninstallTerminalCleanupEvidenceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void OffWindowsProbe_ReturnsExplicitUnprovenTerminalState()
    {
        if (OperatingSystem.IsWindows()) return;

        var state = UninstallTerminalCleanup.ExecuteAndProbe(_root);

        Assert.Equal(3, state.ServicesRemaining);
        Assert.False(state.ScheduledUninstallTaskAbsent);
        Assert.False(state.ProtocolRegistrationAbsent);
        Assert.False(state.ArpRegistrationAbsent);
        Assert.False(state.RetainedEvidencePresent);
        Assert.False(state.OperationalCredentialsAbsent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    public void RetainedEvidence_RejectsMissingOrNonAbsolutePath(string? path)
    {
        Assert.False(UninstallTerminalCleanup.IsRetainedEvidencePresent(path));
    }

    [Fact]
    public void RetainedEvidence_RequiresBoundedNonEmptyRegularMarker()
    {
        var marker = Path.Combine(_root, "retention.json");
        Assert.False(UninstallTerminalCleanup.IsRetainedEvidencePresent(_root));

        File.WriteAllBytes(marker, []);
        Assert.False(UninstallTerminalCleanup.IsRetainedEvidencePresent(_root));

        File.WriteAllBytes(marker, [1]);
        Assert.True(UninstallTerminalCleanup.IsRetainedEvidencePresent(_root));

        File.WriteAllBytes(marker, new byte[16 * 1024]);
        Assert.True(UninstallTerminalCleanup.IsRetainedEvidencePresent(_root));

        File.WriteAllBytes(marker, new byte[16 * 1024 + 1]);
        Assert.False(UninstallTerminalCleanup.IsRetainedEvidencePresent(_root));
    }

    [Fact]
    public void RetainedEvidence_RejectsDirectoryMasqueradingAsMarker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "retention.json"));

        Assert.False(UninstallTerminalCleanup.IsRetainedEvidencePresent(_root));
    }

    [Fact]
    public void RetainedEvidence_RejectsRedirectedDirectoryWithoutFollowingTarget()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "retention.json"), "valid");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(UninstallTerminalCleanup.IsRetainedEvidencePresent(link));
        Assert.Equal("valid", File.ReadAllText(Path.Combine(target, "retention.json")));
    }

    [Fact]
    public void OperationalCredentialProof_ScansOnlyExactTopLevelResidueNames()
    {
        Assert.True(UninstallTerminalCleanup.AreOperationalCredentialsAbsent(_root));
        var nested = Path.Combine(_root, "archive");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "credentials.dat"), "historical");
        File.WriteAllText(Path.Combine(_root, "backup_credentials.dat"), "historical");
        Assert.True(UninstallTerminalCleanup.AreOperationalCredentialsAbsent(_root));

        File.WriteAllText(Path.Combine(_root, "PIPE.NONCE.TMP-recovery"), "secret");
        Assert.False(UninstallTerminalCleanup.AreOperationalCredentialsAbsent(_root));
    }

    [Fact]
    public void OperationalCredentialProof_RejectsMissingAndRedirectedDirectories()
    {
        Assert.False(UninstallTerminalCleanup.AreOperationalCredentialsAbsent(
            Path.Combine(_root, "missing")));

        var target = Path.Combine(_root, "credential-target");
        var link = Path.Combine(_root, "credential-link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }
        Assert.False(UninstallTerminalCleanup.AreOperationalCredentialsAbsent(link));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<Task>")]
    [InlineData("<!DOCTYPE Task [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Task>&x;</Task>")]
    public void RetiredTaskXml_RejectsBlankMalformedAndDtdInput(string xml)
    {
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            xml,
            @"C:\Windows"));
    }

    [Fact]
    public void RetiredTaskXml_RejectsOversizeAndMissingWindowsDirectory()
    {
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            new string('x', 64 * 1024 + 1),
            @"C:\Windows"));
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            OwnedTask(),
            string.Empty));
    }

    [Theory]
    [InlineData("-NoProfile -ExecutionPolicy Bypass -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1", true)]
    [InlineData("-noprofile -executionpolicy bypass -file C:/Windows/Temp/suavo_selfuninstall_ABCDEF0123456789abcdef0123456789.ps1", true)]
    [InlineData("-NoProfile -ExecutionPolicy RemoteSigned -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1", false)]
    [InlineData("-NoProfile -ExecutionPolicy Bypass C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1", false)]
    [InlineData("-NoProfile -ExecutionPolicy Bypass -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdeg.ps1", false)]
    [InlineData("-NoProfile -ExecutionPolicy Bypass -File C:\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1", false)]
    public void RetiredTaskXml_AcceptsOnlyExactOwnedCleanerArguments(
        string arguments,
        bool expected)
    {
        Assert.Equal(expected, UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            OwnedTask(arguments),
            @"C:\Windows\"));
    }

    [Theory]
    [InlineData("<Actions />")]
    [InlineData("<Actions><Exec /><Exec /></Actions>")]
    [InlineData("<Actions><ComHandler /></Actions>")]
    [InlineData("<Actions><Exec><Command>powershell</Command></Exec></Actions>")]
    [InlineData("<Actions><Exec><Command>powershell</Command><Command>powershell</Command><Arguments>x</Arguments></Exec></Actions>")]
    [InlineData("<Actions><Exec><Command>PowerShell</Command><Arguments>-NoProfile -ExecutionPolicy Bypass -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1</Arguments></Exec></Actions>")]
    public void RetiredTaskXml_RejectsUnexpectedActionShape(string actions)
    {
        var xml = $"<Task>{actions}</Task>";
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            xml,
            @"C:\Windows"));
    }

    [Fact]
    public void RetiredTaskXml_RejectsMultipleActionsContainers()
    {
        var actions = OwnedTask().Replace("<Task>", string.Empty, StringComparison.Ordinal)
            .Replace("</Task>", string.Empty, StringComparison.Ordinal);
        Assert.False(UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
            $"<Task>{actions}{actions}</Task>",
            @"C:\Windows"));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("plain", "plain")]
    [InlineData("plain,Ready", "plain")]
    [InlineData("\"quoted\",Ready", "quoted")]
    [InlineData("\"quoted\"\"name\",Ready", "quoted\"name")]
    [InlineData("\"unterminated", null)]
    [InlineData("\uFEFFplain,Ready", "plain")]
    public void CsvFieldReader_IsBoundedToFirstField(string line, string? expected)
    {
        Assert.Equal(expected, UninstallTerminalCleanup.ReadFirstCsvField(line));
    }

    [Theory]
    [InlineData(@"\SuavoSelfUninstall", true)]
    [InlineData("SuavoSelfUninstall", true)]
    [InlineData(@"\SUAVOSELFUNINSTALL", true)]
    [InlineData(@"\SuavoSelfUninstallBackup", false)]
    [InlineData(null, false)]
    public void TaskNameOwnership_IsExact(string? value, bool expected)
    {
        Assert.Equal(expected, UninstallTerminalCleanup.IsExactOwnedScheduledTaskName(value));
    }

    private static string OwnedTask(string? arguments = null) => $$"""
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author">
            <Exec>
              <Command>powershell</Command>
              <Arguments>{{arguments ?? "-NoProfile -ExecutionPolicy Bypass -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1"}}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
