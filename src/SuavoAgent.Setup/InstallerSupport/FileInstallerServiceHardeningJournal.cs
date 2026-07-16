using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.InstallerSupport;

/// <summary>
/// Durable, non-PHI rollback state for the MSI service-hardening action. Each
/// journal is bound to one MSI invocation and has an explicit pending/committed
/// phase so a later repair can never replay an older snapshot.
/// </summary>
internal sealed class FileInstallerServiceHardeningJournal
    : IInstallerServiceHardeningJournal
{
    internal const string FileName = ".msi-service-hardening.rollback.json";
    internal const int SchemaVersion = 2;
    internal const int MaximumBytes = 8 * 1024;
    private const uint MoveFileReplaceExisting = 0x00000001;
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

    internal static FileInstallerServiceHardeningJournal CreateForInstallDirectory(
        string installDirectory) =>
        new(Path.Combine(
            MsiInstallerInvocation.RequireFixedInstallDirectory(installDirectory),
            FileName));

    public void SavePending(
        string invocationId,
        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots)
    {
        ValidateInvocationId(invocationId);
        ValidateSnapshots(snapshots);
        if (File.Exists(_path))
            throw new IOException("A service-hardening rollback journal already exists.");

        var document = Document(
            invocationId,
            InstallerTransactionJournalPhase.Pending,
            snapshots);
        WriteDocument(document, replaceExisting: false);
    }

    public InstallerServiceHardeningJournalState? Load()
    {
        if (!File.Exists(_path))
            return null;

        var document = ReadDocument();
        var snapshots = document.Services.ToDictionary(
            static entry => entry.Name,
            static entry => new InstallerServiceConfiguration(
                entry.DelayedAutoStart,
                entry.ServiceSidType),
            StringComparer.Ordinal);
        ValidateSnapshots(snapshots);
        return new(document.InvocationId, ParsePhase(document.Phase), snapshots);
    }

    public void MarkCommitted(string invocationId)
    {
        ValidateInvocationId(invocationId);
        var state = Load() ?? throw new IOException(
            "The service-hardening rollback journal is unavailable.");
        if (!string.Equals(state.InvocationId, invocationId, StringComparison.Ordinal) ||
            state.Phase != InstallerTransactionJournalPhase.Pending)
            throw new InvalidDataException(
                "The service-hardening rollback journal cannot be committed.");

        WriteDocument(
            Document(invocationId, InstallerTransactionJournalPhase.Committed, state.Snapshots),
            replaceExisting: true);
    }

    public void Delete(
        string invocationId,
        InstallerTransactionJournalPhase phase)
    {
        ValidateInvocationId(invocationId);
        var state = Load() ?? throw new IOException(
            "The service-hardening rollback journal is unavailable.");
        if (!string.Equals(state.InvocationId, invocationId, StringComparison.Ordinal) ||
            state.Phase != phase)
            throw new InvalidDataException(
                "The service-hardening rollback journal identity is invalid.");

        // Delete the journal last. A temporary-file cleanup failure must never
        // discard the durable rollback/commit phase first.
        File.Delete(_temporaryPath);
        File.Delete(_path);
        if (File.Exists(_path))
            throw new IOException("The service-hardening journal remains present.");
    }

    private void WriteDocument(JournalDocument document, bool replaceExisting)
    {
        var bytes = CanonicalBytes(document);
        try
        {
            File.Delete(_temporaryPath);
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

            MoveIntoPlace(_temporaryPath, _path, replaceExisting);
            _secureJournal(_path);
            if (!CryptographicOperations.FixedTimeEquals(
                    bytes,
                    File.ReadAllBytes(_path)))
                throw new IOException(
                    "The service-hardening rollback journal is not durable.");
        }
        catch
        {
            TryDeleteTemporaryFile();
            if (!replaceExisting)
                TryDeleteJournalFile();
            throw;
        }
    }

    private JournalDocument ReadDocument()
    {
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
            !MsiInstallerInvocation.IsValidInvocationId(document.InvocationId) ||
            document.Services.Count != MsiServiceHardeningTransaction.ServiceNames.Count)
            throw new InvalidDataException("The rollback journal schema is invalid.");
        _ = ParsePhase(document.Phase);

        var observedNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.Services.Count; index++)
        {
            var expectedName = MsiServiceHardeningTransaction.ServiceNames[index];
            var entry = document.Services[index];
            if (!string.Equals(entry.Name, expectedName, StringComparison.Ordinal) ||
                !IsSupportedSidType(entry.ServiceSidType) ||
                !observedNames.Add(entry.Name))
                throw new InvalidDataException(
                    "The rollback journal service cohort is invalid.");
        }
        if (!CryptographicOperations.FixedTimeEquals(bytes, CanonicalBytes(document)))
            throw new InvalidDataException("The rollback journal is not canonical.");
        return document;
    }

    private static JournalDocument Document(
        string invocationId,
        InstallerTransactionJournalPhase phase,
        IReadOnlyDictionary<string, InstallerServiceConfiguration> snapshots) =>
        new(
            SchemaVersion,
            invocationId,
            PhaseText(phase),
            MsiServiceHardeningTransaction.ServiceNames
                .Select(name => new JournalEntry(
                    name,
                    snapshots[name].DelayedAutoStart,
                    snapshots[name].ServiceSidType))
                .ToArray());

    private static byte[] CanonicalBytes(JournalDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The rollback journal exceeds its fixed bound.");
        return bytes;
    }

    private static string PhaseText(InstallerTransactionJournalPhase phase) =>
        phase switch
        {
            InstallerTransactionJournalPhase.Pending => "pending",
            InstallerTransactionJournalPhase.Committed => "committed",
            _ => throw new InvalidDataException("The rollback journal phase is invalid."),
        };

    private static InstallerTransactionJournalPhase ParsePhase(string? phase) =>
        phase switch
        {
            "pending" => InstallerTransactionJournalPhase.Pending,
            "committed" => InstallerTransactionJournalPhase.Committed,
            _ => throw new InvalidDataException("The rollback journal phase is invalid."),
        };

    private static void ValidateInvocationId(string invocationId)
    {
        if (!MsiInstallerInvocation.IsValidInvocationId(invocationId))
            throw new InvalidDataException("The MSI invocation identity is invalid.");
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
            throw new InvalidDataException(
                "The exact service-hardening snapshot is required.");
    }

    private static bool IsSupportedSidType(uint value) => value is 0 or 1 or 3;

    private static void MoveIntoPlace(
        string source,
        string destination,
        bool replaceExisting)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(source, destination, overwrite: replaceExisting);
            return;
        }

        var flags = MoveFileWriteThrough |
            (replaceExisting ? MoveFileReplaceExisting : 0);
        if (!MoveFileEx(source, destination, flags))
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

    internal static HandleBoundAclPolicy BuildJournalAclPolicy() => new(
        HandleBoundAcl.SystemSid,
        [
            new HandleBoundAclAce(
                HandleBoundAcl.SystemSid,
                FileSystemRights.FullControl),
            new HandleBoundAclAce(
                HandleBoundAcl.AdministratorsSid,
                FileSystemRights.FullControl),
        ]);

    private static void SecureJournal(string path) =>
        new HandleBoundAcl().ApplyBatch(
        [
            new HandleBoundAclMutation(
                path,
                IsDirectory: false,
                BuildJournalAclPolicy()),
        ]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private sealed record JournalDocument(
        int SchemaVersion,
        string InvocationId,
        string Phase,
        IReadOnlyList<JournalEntry> Services);

    private sealed record JournalEntry(
        string Name,
        bool DelayedAutoStart,
        uint ServiceSidType);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}
