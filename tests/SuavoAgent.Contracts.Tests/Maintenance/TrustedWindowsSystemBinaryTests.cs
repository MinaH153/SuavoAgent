using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

[CollectionDefinition("trusted-windows-system-binary-global-environment", DisableParallelization = true)]
public sealed class TrustedWindowsSystemBinaryCollection
{
    public const string CollectionName = "trusted-windows-system-binary-global-environment";
}

[Collection(TrustedWindowsSystemBinaryCollection.CollectionName)]
public sealed class TrustedWindowsSystemBinaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-system-binary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Current_directory_and_path_spoof_do_not_affect_resolved_binary()
    {
        var windows = Path.Combine(_root, "Windows");
        var system = Path.Combine(windows, "System32");
        var attacker = Path.Combine(_root, "attacker");
        Directory.CreateDirectory(system);
        Directory.CreateDirectory(attacker);
        var trusted = Path.Combine(system, "sc.exe");
        File.WriteAllText(trusted, "trusted");
        File.WriteAllText(Path.Combine(attacker, "sc.exe"), "attacker");
        var previousDirectory = Environment.CurrentDirectory;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = attacker;
            Environment.SetEnvironmentVariable("PATH", attacker);

            var resolved = TrustedWindowsSystemBinary.ResolveFromTrustedDirectories(
                "sc.exe",
                system,
                windows,
                File.Exists,
                File.GetAttributes);

            Assert.Equal(Path.GetFullPath(trusted), resolved);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Fact]
    public void System_directory_outside_kernel_windows_root_is_rejected()
    {
        var windows = Path.Combine(_root, "Windows");
        var attacker = Path.Combine(_root, "attacker", "System32");
        Directory.CreateDirectory(Path.Combine(windows, "System32"));
        Directory.CreateDirectory(attacker);
        File.WriteAllText(Path.Combine(attacker, "sc.exe"), "attacker");

        Assert.Throws<InvalidDataException>(() =>
            TrustedWindowsSystemBinary.ResolveFromTrustedDirectories(
                "sc.exe",
                attacker,
                windows,
                File.Exists,
                File.GetAttributes));
    }

    [Fact]
    public void Reparse_or_unapproved_candidate_is_rejected()
    {
        var windows = Path.Combine(_root, "Windows");
        var system = Path.Combine(windows, "System32");
        Directory.CreateDirectory(system);
        var candidate = Path.Combine(system, "sc.exe");
        File.WriteAllText(candidate, "redirected");

        Assert.Throws<InvalidDataException>(() =>
            TrustedWindowsSystemBinary.ResolveFromTrustedDirectories(
                "sc.exe",
                system,
                windows,
                File.Exists,
                path => string.Equals(path, candidate, StringComparison.Ordinal)
                    ? FileAttributes.ReparsePoint
                    : FileAttributes.Directory));
        Assert.Throws<InvalidDataException>(() =>
            TrustedWindowsSystemBinary.ResolveFromTrustedDirectories(
                "cmd.exe",
                system,
                windows,
                File.Exists,
                File.GetAttributes));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
