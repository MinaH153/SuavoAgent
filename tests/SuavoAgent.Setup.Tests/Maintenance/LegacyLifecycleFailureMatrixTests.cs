using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class LegacyLifecycleFailureMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-legacy-failure-matrix-" + Guid.NewGuid().ToString("N"));

    public LegacyLifecycleFailureMatrixTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Execute_IsExplicitlyUnsupportedOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        var result = LegacyLifecycleMigration.Execute(
            Path.Combine(_root, "install"),
            Path.Combine(_root, "data"));

        Assert.False(result.Succeeded);
        Assert.Equal("unsupported_host", result.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("   ")]
    public void RunCore_RejectsNonAbsoluteRootsBeforeCallbacks(string invalidRoot)
    {
        var called = false;

        var result = LegacyLifecycleMigration.RunCore(
            invalidRoot,
            Path.Combine(_root, "data"),
            Path.Combine(_root, "legacy"),
            () => { called = true; return new(true, 0, false); },
            () => { called = true; return new(true, 0, false); },
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal("path_boundary_invalid", result.Code);
        Assert.False(called);
    }

    [Fact]
    public void RunCore_RejectsMissingCleanupDelegates()
    {
        Assert.Throws<ArgumentNullException>(() => LegacyLifecycleMigration.RunCore(
            Path.Combine(_root, "install"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "legacy"),
            null!,
            () => new(true, 0, false),
            DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentNullException>(() => LegacyLifecycleMigration.RunCore(
            Path.Combine(_root, "install"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "legacy"),
            () => new(true, 0, false),
            null!,
            DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData(false, false, "legacy_scheduled_task_cleanup_failed")]
    [InlineData(false, true, "legacy_scheduled_task_remains")]
    public void ScheduledTaskFailure_StopsBeforeRegistryCleanup(
        bool callbackSucceeded,
        bool runnableRemains,
        string code)
    {
        var paths = Paths();
        var registryCalled = false;

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => new(callbackSucceeded, 3, runnableRemains),
            () => { registryCalled = true; return new(true, 0, false); },
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Code);
        Assert.Equal(3, result.ScheduledTasksRemoved);
        Assert.False(registryCalled);
        Assert.NotNull(result.ReceiptPath);
    }

    [Fact]
    public void ThrowingScheduledTaskCleanup_IsContainedAndRecorded()
    {
        var paths = Paths();

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => throw new IOException("injected task query failure"),
            () => throw new InvalidOperationException("must not run"),
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_scheduled_task_remains", result.Code);
        Assert.NotNull(result.ReceiptPath);
    }

    [Theory]
    [InlineData(false, false, "legacy_registry_cleanup_failed")]
    [InlineData(false, true, "legacy_registry_command_remains")]
    public void RegistryFailure_IsDurablyRecorded(
        bool callbackSucceeded,
        bool runnableRemains,
        string code)
    {
        var paths = Paths();

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => new(true, 2, false),
            () => new(callbackSucceeded, 4, runnableRemains),
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.Code);
        Assert.Equal(2, result.ScheduledTasksRemoved);
        Assert.Equal(4, result.RegistryEntriesRemoved);
        Assert.NotNull(result.ReceiptPath);
    }

    [Fact]
    public void ThrowingRegistryCleanup_IsContainedAndBlocksRestart()
    {
        var paths = Paths();

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => new(true, 0, false),
            () => throw new UnauthorizedAccessException("injected registry denial"),
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_registry_command_remains", result.Code);
    }

    [Fact]
    public void ArtifactReintroducedDuringCleanup_FailsFinalAbsenceProof()
    {
        var paths = Paths();

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () =>
            {
                File.WriteAllText(Path.Combine(paths.Install, "bootstrap.ps1"), "race");
                return new(true, 0, false);
            },
            () => new(true, 0, false),
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_file_proof_failed", result.Code);
        Assert.True(File.Exists(Path.Combine(paths.Install, "bootstrap.ps1")));
    }

    [Fact]
    public void ProvenRunnableInteractiveLegacyLaunchBlocksServiceRestart()
    {
        var paths = Paths();

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => new(true, 0, false),
            () => new(true, 0, false),
            DateTimeOffset.UnixEpoch,
            () => new(false, 1, 0, 0, true));

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_interactive_launch_remains", result.Code);
        Assert.Equal(1, result.ShortcutsRemoved);
        Assert.True(File.Exists(result.ReceiptPath));
    }

    [Fact]
    public void ExactArtifactThatIsDirectory_IsNeverRecursivelyDeleted()
    {
        var paths = Paths();
        var directory = Path.Combine(paths.Data, "bootstrap.ps1");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "preserve");

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => throw new InvalidOperationException("must not run"),
            () => throw new InvalidOperationException("must not run"),
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal("legacy_file_removal_failed", result.Code);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(directory, "keep.txt")));
    }

    [Fact]
    public void ReceiptDestinationThatIsDirectory_FailsWithoutDeletingIt()
    {
        var paths = Paths();
        var receipt = Path.Combine(paths.Data, LegacyLifecycleMigration.ReceiptFileName);
        Directory.CreateDirectory(receipt);
        File.WriteAllText(Path.Combine(receipt, "keep.txt"), "preserve");

        var result = LegacyLifecycleMigration.RunCore(
            paths.Install,
            paths.Data,
            paths.Legacy,
            () => new(true, 0, false),
            () => new(true, 0, false),
            DateTimeOffset.UnixEpoch);

        Assert.False(result.Succeeded);
        Assert.Equal("receipt_path_invalid", result.Code);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(receipt, "keep.txt")));
    }

    [Theory]
    [InlineData("pwsh -File upgrade.ps1", true)]
    [InlineData(@"C:\SuavoAgent\scripts\quick-install.ps1", true)]
    [InlineData("native-maintenance --repair-services", false)]
    public void LegacyCommandClassifier_CoversRetiredAndNativeShapes(
        string command,
        bool expected) =>
        Assert.Equal(expected, LegacyLifecycleMigration.IsLegacyRunnableCommand(command));

    [Fact]
    public void OversizedLegacyCommand_RemainsBlockingWhenItCannotBeClassified()
    {
        Assert.True(LegacyLifecycleMigration.IsLegacyRunnableCommand(
            new string('x', 32 * 1024 + 1)));
    }

    [Fact]
    public void ScheduledTaskParser_DeduplicatesCaseInsensitivelyAndHandlesEmptyOutput()
    {
        Assert.Empty(LegacyLifecycleMigration.ParseLegacyScheduledTaskNames(string.Empty));
        const string output =
            "\"\\SuavoSelfUninstall\",\"Ready\"\r\n" +
            "\"\\SUAVOSELFUNINSTALL\",\"Running\"\r\n";
        Assert.Single(LegacyLifecycleMigration.ParseLegacyScheduledTaskNames(output));
    }

    private (string Install, string Data, string Legacy) Paths()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var install = Path.Combine(_root, suffix, "install");
        var data = Path.Combine(_root, suffix, "data");
        var legacy = Path.Combine(_root, suffix, "legacy");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(legacy);
        return (install, data, legacy);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
