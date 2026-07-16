using System.Diagnostics;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Cross-process gate for install, rollback, and authority journal mutation.
/// A file handle is used instead of a mutex because an async installer may
/// resume on another thread; file locks are process-scoped and crash-released.
/// </summary>
internal sealed class InstallerTransactionLock : IDisposable
{
    private readonly FileStream _handle;

    private InstallerTransactionLock(FileStream handle) => _handle = handle;

    internal static InstallerTransactionLock Acquire(
        string? pathOverride = null,
        TimeSpan? timeout = null)
    {
        var path = Path.GetFullPath(pathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent-Maintenance",
            "install-transaction.lock"));
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Installer lock directory is unavailable.");

        // The maintenance process runs elevated. Never let an unprivileged junction or
        // symbolic link redirect its lock creation/open into an attacker-controlled target.
        ValidateNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        ValidateNoReparsePoints(directory);
        if (pathOverride is null && OperatingSystem.IsWindows())
            ServiceInstaller.LockdownMaintenanceDirectoryAcl(directory);
        ValidateNoReparsePoints(directory);
        ValidateNoReparsePoints(path, allowMissingLeaf: true);

        var budget = timeout ?? TimeSpan.FromSeconds(30);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var handle = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                try
                {
                    // Re-check every component after opening. This catches a leaf or
                    // directory swapped to a reparse point between validation and open.
                    ValidateNoReparsePoints(path);
                    return new InstallerTransactionLock(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            catch (IOException ex)
            {
                if (elapsed.Elapsed >= budget)
                    throw new TimeoutException(
                        "Another Suavo maintenance transaction is already running.",
                        ex);
                Thread.Sleep(50);
            }
        }
    }

    private static void ValidateNoReparsePoints(
        string path,
        bool allowMissingLeaf = false)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException("Installer lock path has no filesystem root.");

        var current = root;
        var segments = fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var isLeaf = index == segments.Length - 1;
            var exists = File.Exists(current) || Directory.Exists(current);
            if (!exists)
            {
                if (isLeaf && allowMissingLeaf)
                    return;

                // A missing ancestor means all descendants are missing too. Creation is
                // followed by a second validation, so stop without probing fake children.
                return;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "Installer transaction lock path must not contain reparse points.");
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}
