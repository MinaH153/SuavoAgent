using System.Text.Json;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class LegacyLifecycleMigrationTests
{
    [Fact]
    public void Exact_known_files_are_removed_and_receipt_contains_counts_only()
    {
        using var fixture = new MigrationFixture();
        fixture.Write(fixture.Data, "bootstrap.ps1");
        fixture.Write(fixture.Install, "install.ps1");
        fixture.Write(fixture.Legacy, "suavo-check.ps1");
        fixture.Write(fixture.Legacy, "upgrade.ps1");
        fixture.Write(fixture.Legacy, Path.Combine("scripts", "quick-install.ps1"));

        var result = LegacyLifecycleMigration.RunCore(
            fixture.Install,
            fixture.Data,
            fixture.Legacy,
            () => new(true, 2, false),
            () => new(true, 3, false),
            DateTimeOffset.Parse("2026-07-11T12:34:56Z"),
            () => new(true, 1, 1, 2, false));

        Assert.True(result.Succeeded);
        Assert.Equal("legacy_script_lifecycle_retired", result.Code);
        Assert.Equal(5, result.FilesRemoved);
        Assert.Equal(2, result.ScheduledTasksRemoved);
        Assert.Equal(3, result.RegistryEntriesRemoved);
        Assert.Equal(1, result.ShortcutsRemoved);
        Assert.Equal(1, result.ProcessesStopped);
        Assert.Equal(2, result.UnclassifiedShortcutsPreserved);
        Assert.NotNull(result.ReceiptPath);
        Assert.True(File.Exists(result.ReceiptPath));
        var json = File.ReadAllText(result.ReceiptPath!);
        using var receipt = JsonDocument.Parse(json);
        Assert.Equal("completed", receipt.RootElement.GetProperty("status").GetString());
        Assert.False(receipt.RootElement.GetProperty("runnableLegacyPathsRemaining").GetBoolean());
        Assert.Equal(1, receipt.RootElement.GetProperty("shortcutsRemoved").GetInt32());
        Assert.Equal(1, receipt.RootElement.GetProperty("legacyProcessesStopped").GetInt32());
        Assert.Equal(
            2,
            receipt.RootElement.GetProperty("unclassifiedShortcutsPreserved").GetInt32());
        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain("bootstrap.ps1", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patient", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_files_are_never_deleted()
    {
        using var fixture = new MigrationFixture();
        var preserved = fixture.Write(fixture.Legacy, "operator-evidence.txt");

        var result = LegacyLifecycleMigration.RunCore(
            fixture.Install,
            fixture.Data,
            fixture.Legacy,
            () => new(true, 0, false),
            () => new(true, 0, false),
            DateTimeOffset.UtcNow);

        Assert.True(result.Succeeded);
        Assert.Equal("preserve", File.ReadAllText(preserved));
    }

    [Fact]
    public void Redirected_nested_scripts_directory_is_refused_without_touching_target()
    {
        using var fixture = new MigrationFixture();
        var victim = Path.Combine(fixture.Root, "victim");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "quick-install.ps1");
        File.WriteAllText(victimFile, "preserve");
        var scripts = Path.Combine(fixture.Legacy, "scripts");
        try
        {
            Directory.CreateSymbolicLink(scripts, victim);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var tasksCalled = false;
        var result = LegacyLifecycleMigration.RunCore(
            fixture.Install,
            fixture.Data,
            fixture.Legacy,
            () => { tasksCalled = true; return new(true, 0, false); },
            () => new(true, 0, false),
            DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_file_removal_failed", result.Code);
        Assert.False(tasksCalled);
        Assert.Equal("preserve", File.ReadAllText(victimFile));
    }

    [Fact]
    public void Redirected_legacy_root_is_refused_before_any_cleanup()
    {
        using var fixture = new MigrationFixture(createLegacy: false);
        var victim = Path.Combine(fixture.Root, "victim-root");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "bootstrap.ps1");
        File.WriteAllText(victimFile, "preserve");
        try
        {
            Directory.CreateSymbolicLink(fixture.Legacy, victim);
        }
        catch (Exception exception) when (exception is
            UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = LegacyLifecycleMigration.RunCore(
            fixture.Install,
            fixture.Data,
            fixture.Legacy,
            () => throw new InvalidOperationException("must not run"),
            () => throw new InvalidOperationException("must not run"),
            DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal("path_boundary_redirected", result.Code);
        Assert.Equal("preserve", File.ReadAllText(victimFile));
    }

    [Fact]
    public void Runnable_scheduled_task_blocks_restart_and_records_failure()
    {
        using var fixture = new MigrationFixture();
        fixture.Write(fixture.Data, "bootstrap.ps1");
        var registryCalled = false;

        var result = LegacyLifecycleMigration.RunCore(
            fixture.Install,
            fixture.Data,
            fixture.Legacy,
            () => new(false, 0, true),
            () => { registryCalled = true; return new(true, 0, false); },
            DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_scheduled_task_remains", result.Code);
        Assert.False(registryCalled);
        Assert.NotNull(result.ReceiptPath);
        var receipt = File.ReadAllText(result.ReceiptPath!);
        Assert.Contains("\"runnableLegacyPathsRemaining\":true", receipt);
        Assert.DoesNotContain(fixture.Root, receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void Runnable_registry_command_blocks_restart()
    {
        using var fixture = new MigrationFixture();

        var result = LegacyLifecycleMigration.RunCore(
            fixture.Install,
            fixture.Data,
            fixture.Legacy,
            () => new(true, 1, false),
            () => new(false, 0, true),
            DateTimeOffset.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_registry_command_remains", result.Code);
        Assert.NotNull(result.ReceiptPath);
    }

    [Fact]
    public void Scheduled_task_parser_selects_only_exact_retired_owned_identity()
    {
        const string csv =
            "\"\\Native Maintenance\",\"Ready\",\"C:\\Program Files\\Suavo\\Agent\\SuavoAgent.Maintenance.exe --repair-services\"\r\n" +
            "\"\\Legacy Repair\",\"Ready\",\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe C:\\ProgramData\\SuavoAgent\\bootstrap.ps1 -Repair\"\r\n" +
            "\"\\Legacy Node\",\"Ready\",\"C:\\SuavoAgent\\node C:\\SuavoAgent\\watchdog.js\"\r\n" +
            "\"\\SuavoAgent Repair\",\"Ready\",\"cmd.exe /c legacy-repair.cmd\"\r\n" +
            "\"\\My SuavoAgent Backup\",\"Ready\",\"cmd.exe /c backup\"\r\n" +
            "\"\\SuavoSelfUninstall\",\"Ready\",\"powershell -NoProfile -ExecutionPolicy Bypass -File C:\\Windows\\Temp\\suavo_selfuninstall_0123456789abcdef0123456789abcdef.ps1\"\r\n";

        Assert.Equal(
            new[] { @"\SuavoSelfUninstall" },
            LegacyLifecycleMigration.ParseLegacyScheduledTaskNames(csv));
    }

    [Theory]
    [InlineData(@"powershell.exe -File C:\ProgramData\SuavoAgent\bootstrap.ps1", true)]
    [InlineData(@"powershell.exe -EncodedCommand AAAA", true)]
    [InlineData(@"C:\Program Files\Suavo\Agent\SuavoAgent.Maintenance.exe --repair-services", false)]
    [InlineData("", false)]
    public void Registry_command_classifier_is_exact(string command, bool expected) =>
        Assert.Equal(expected, LegacyLifecycleMigration.IsLegacyRunnableCommand(command));

    [Theory]
    [InlineData(@"C:\Users\queen\suavo-publish\Broker\SuavoAgent.Broker.exe", true)]
    [InlineData(@"C:\Users\queen\SUAVO-PUBLISH\Broker\SuavoAgent.Broker.exe", true)]
    [InlineData(@"C:\Program Files\Suavo\Agent\SuavoAgent.Broker.exe", false)]
    [InlineData(@"C:\Users\queen\suavo-publish\Core\SuavoAgent.Core.exe", false)]
    [InlineData(@"C:\Users\queen\other\Broker\SuavoAgent.Broker.exe", false)]
    [InlineData(@"C:\Temp\suavo-publish\Broker\SuavoAgent.Broker.exe", false)]
    [InlineData(@"C:\Users\queen\nested\suavo-publish\Broker\SuavoAgent.Broker.exe", false)]
    public void LegacyInteractiveBrokerClassifier_IsExact(string path, bool expected) =>
        Assert.Equal(
            expected,
            LegacyInteractiveLaunchRetirement.IsExactLegacyBrokerPath(path));

    [Theory]
    [InlineData(
        @"C:\Users\queen\suavo-publish\Broker\SuavoAgent.Broker.exe",
        "",
        true)]
    [InlineData(
        @"C:\Windows\System32\cmd.exe",
        "/k \"C:\\Users\\queen\\suavo-publish\\Broker\\SuavoAgent.Broker.exe\"",
        true)]
    [InlineData(
        @"C:\Windows\System32\cmd.exe",
        "/k \"C:\\Program Files\\Suavo\\Agent\\SuavoAgent.Broker.exe\"",
        false)]
    [InlineData(
        @"C:\Program Files\Suavo\Agent\SuavoAgent.Maintenance.exe",
        "--connect-installed",
        false)]
    public void LegacyInteractiveShortcutClassifier_PreservesNonOwnedTargets(
        string target,
        string arguments,
        bool expected)
    {
        var actual = LegacyInteractiveLaunchRetirement.IsExactLegacyBrokerPath(target)
            ? LegacyInteractiveLaunchRetirement.IsExactLegacyLaunch(target, arguments)
            : LegacyInteractiveLaunchRetirement.IsExactLegacyCommandHostLaunch(
                target,
                arguments,
                @"C:\Windows");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"C:\evil\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\..\System32\cmd.exe")]
    [InlineData(@"D:\Windows\System32\cmd.exe")]
    public void LegacyInteractiveShortcutClassifier_RejectsUntrustedCommandHosts(
        string target) =>
        Assert.False(
            LegacyInteractiveLaunchRetirement.IsExactLegacyCommandHostLaunch(
                target,
                @"/k C:\Users\queen\suavo-publish\Broker\SuavoAgent.Broker.exe",
                @"C:\Windows"));

    private sealed class MigrationFixture : IDisposable
    {
        public string Root { get; }
        public string Install { get; }
        public string Data { get; }
        public string Legacy { get; }

        public MigrationFixture(bool createLegacy = true)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "suavo-legacy-migration-" + Guid.NewGuid().ToString("N"));
            Install = Path.Combine(Root, "install");
            Data = Path.Combine(Root, "data");
            Legacy = Path.Combine(Root, "legacy");
            Directory.CreateDirectory(Install);
            Directory.CreateDirectory(Data);
            if (createLegacy) Directory.CreateDirectory(Legacy);
        }

        public string Write(string root, string relativePath)
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "preserve");
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Legacy) &&
                    File.GetAttributes(Legacy).HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(Legacy);
            }
            catch { }
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
