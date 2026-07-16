using System.Diagnostics;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class InstallerTransactionLockTests
{
    [Fact]
    public async Task CompetingInstallerCannotEnterUntilOwnerReleasesFileLock()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "suavo-installer-lock-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "install.lock");
        try
        {
            using (InstallerTransactionLock.Acquire(path, TimeSpan.FromSeconds(1)))
            {
                var elapsed = Stopwatch.StartNew();
                await Assert.ThrowsAsync<TimeoutException>(() => Task.Run(() =>
                {
                    using var _ = InstallerTransactionLock.Acquire(
                        path,
                        TimeSpan.FromMilliseconds(150));
                }));
                Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(100));
            }

            using var afterRelease = InstallerTransactionLock.Acquire(
                path,
                TimeSpan.FromSeconds(1));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReparsePointDirectoryIsRejectedBeforeElevatedLockOpen()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "suavo-installer-lock-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "redirect");
        Directory.CreateDirectory(target);

        try
        {
            if (!TryCreateDirectoryLink(link, target))
                return;

            var exception = Assert.Throws<InvalidDataException>(() =>
                InstallerTransactionLock.Acquire(
                    Path.Combine(link, "install.lock"),
                    TimeSpan.Zero));
            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(target, "install.lock")));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ReparsePointLockFileIsRejectedBeforeElevatedLockOpen()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "suavo-installer-lock-file-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target.lock");
        var link = Path.Combine(root, "install.lock");
        Directory.CreateDirectory(root);
        File.WriteAllText(target, "do-not-open");

        try
        {
            if (!TryCreateFileLink(link, target))
                return;

            var exception = Assert.Throws<InvalidDataException>(() =>
                InstallerTransactionLock.Acquire(link, TimeSpan.Zero));
            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("do-not-open", File.ReadAllText(target));
        }
        finally
        {
            try { File.Delete(link); } catch { }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
