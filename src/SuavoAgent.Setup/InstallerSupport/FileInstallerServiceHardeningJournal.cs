using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.InstallerSupport;

/// <summary>
/// Durable, non-PHI rollback state for the MSI service-hardening action. The
/// installed host stores it beside itself under the protected Program Files
/// directory and immediately applies a SYSTEM/Administrators-only exact DACL.
/// </summary>
internal sealed class FileInstallerServiceHardeningJournal
    : IInstallerServiceHardeningJournal
{
    internal const string FileName = ".msi-service-hardening.rollback.json";
    internal const int SchemaVersion = 1;
    internal const int MaximumBytes = 4 * 1024;
    private const uint MoveFileWriteThrough = 0x00000008;

    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly Action<string> _secureJournal;

    internal FileInstallerServiceHardeningJournal(string path)
        : this(path, SecureJournal)
    {
    }

    internal FileInstallerServiceHardeningJournal(
        string path,
        Action<string> secureJournal)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A fully qualified journal path is required.", nameof(path));

        _path = path;
        _temporaryPath = path + ".tmp";
        _secureJournal = secureJournal
            ?? throw new ArgumentNullException(nameof(secureJournal));
    }

    internal static FileInstallerServiceHardeningJournal CreateForInstalledHost() =>
        new(Path.Combine(AppContext.BaseDirectory, FileName));

    public void Save(
        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots)
    {
        ValidateSnapshots(snapshots);
        if (File.Exists(_path))
            throw new IOException("A service-hardening rollback journal already exists.");

        // A stale temporary file can only precede the first SCM mutation because
        // the final rename completes before Execute starts writing services.
        File.Delete(_temporaryPath);
        var document = new JournalDocument(
            SchemaVersion,
            MsiServiceHardeningTransaction.ServiceNames
                .Select(name => new JournalEntry(
                    name,
                    snapshots[name].DelayedAutoStart,
                    snapshots[name].ServiceSidType))
                .ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("The rollback journal exceeds its fixed bound.");

        try
        {
            using (var stream = new FileStream(
                       _temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            MoveIntoPlace(_temporaryPath, _path);
            _secureJournal(_path);
        }
        catch
        {
            TryDeleteTemporaryFile();
            TryDeleteJournalFile();
            throw;
        }
    }

    public IReadOnlyDictionary<string, InstallerServiceConfiguration>? Load()
    {
        if (!File.Exists(_path))
            return null;

        byte[] bytes;
        using (var stream = new FileStream(
                   _path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaximumBytes)
                throw new InvalidDataException("The rollback journal has an invalid size.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }

        var document = JsonSerializer.Deserialize<JournalDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The rollback journal is empty.");
        if (document.SchemaVersion != SchemaVersion ||
            document.Services.Count != MsiServiceHardeningTransaction.ServiceNames.Count)
        {
            throw new InvalidDataException("The rollback journal schema is invalid.");
        }

        var snapshots = new Dictionary<string, InstallerServiceConfiguration>(
            StringComparer.Ordinal);
        for (var index = 0; index < document.Services.Count; index++)
        {
            var expectedName = MsiServiceHardeningTransaction.ServiceNames[index];
            var entry = document.Services[index];
            if (!string.Equals(entry.Name, expectedName, StringComparison.Ordinal) ||
                !IsSupportedSidType(entry.ServiceSidType) ||
                !snapshots.TryAdd(
                    entry.Name,
                    new InstallerServiceConfiguration(
                        entry.DelayedAutoStart,
                        entry.ServiceSidType)))
            {
                throw new InvalidDataException("The rollback journal service cohort is invalid.");
            }
        }

        ValidateSnapshots(snapshots);
        return snapshots;
    }

    public void Delete()
    {
        // Delete the committed journal last. If temporary cleanup fails, the
        // rollback action must still have the durable snapshot available.
        File.Delete(_temporaryPath);
        File.Delete(_path);
    }

    private static void ValidateSnapshots(
        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count != MsiServiceHardeningTransaction.ServiceNames.Count ||
            MsiServiceHardeningTransaction.ServiceNames.Any(name =>
                !snapshots.TryGetValue(name, out var value) ||
                !IsSupportedSidType(value.ServiceSidType)) ||
            snapshots.Keys.Any(name =>
                !MsiServiceHardeningTransaction.ServiceNames.Contains(
                    name,
                    StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The exact service-hardening snapshot is required.");
        }
    }

    private static bool IsSupportedSidType(uint value) => value is 0 or 1 or 3;

    private static void MoveIntoPlace(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: false);
            return;
        }

        // MOVEFILE_WRITE_THROUGH does not return until the durable rename has
        // completed. Omitting REPLACE_EXISTING keeps stale journals fail-closed.
        if (!MoveFileEx(source, destination, MoveFileWriteThrough))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private void TryDeleteTemporaryFile()
    {
        try { File.Delete(_temporaryPath); }
        catch { }
    }

    private void TryDeleteJournalFile()
    {
        try { File.Delete(_path); }
        catch { }
    }

    internal static HandleBoundAclPolicy BuildJournalAclPolicy() =>
        new(
            HandleBoundAcl.SystemSid,
            [
                new HandleBoundAclAce(
                    HandleBoundAcl.SystemSid,
                    FileSystemRights.FullControl),
                new HandleBoundAclAce(
                    HandleBoundAcl.AdministratorsSid,
                    FileSystemRights.FullControl),
            ]);

    private static void SecureJournal(string path)
    {
        // Apply the exact protected DACL through the existing no-follow,
        // file-ID-pinned boundary. It rejects reparse points and hard links.
        new HandleBoundAcl().ApplyBatch(
        [
            new HandleBoundAclMutation(
                path,
                IsDirectory: false,
                BuildJournalAclPolicy()),
        ]);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private sealed record JournalDocument(
        int SchemaVersion,
        IReadOnlyList<JournalEntry> Services);

    private sealed record JournalEntry(
        string Name,
        bool DelayedAutoStart,
        uint ServiceSidType);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}
