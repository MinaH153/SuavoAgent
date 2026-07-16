using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.InstallerSupport;

/// <summary>
/// The exact, hidden Windows Installer session identity supplied to every
/// deferred, rollback, and commit action in one MSI invocation. The Restart
/// Manager session key is captured before InstallValidate clears it.
/// </summary>
internal sealed record MsiInstallerInvocation(
    string InvocationId,
    string ProductCode,
    string OriginalDatabase,
    string InstallDirectory)
{
    internal const string SchemaTag = "v1";
    internal const int MaximumCustomActionDataCharacters = 32 * 1024;

    internal static bool TryParse(string? value, out MsiInstallerInvocation invocation)
    {
        invocation = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumCustomActionDataCharacters)
            return false;

        var fields = value.Split('|');
        if (fields.Length != 5 ||
            !string.Equals(fields[0], SchemaTag, StringComparison.Ordinal) ||
            !Guid.TryParseExact(fields[1], "B", out var productCode) ||
            !ValidBoundedText(fields[2], 128) ||
            !ValidBoundedText(fields[3], 16 * 1024) ||
            !ValidBoundedText(fields[4], 16 * 1024))
            return false;

        invocation = new(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant(),
            productCode.ToString("B").ToUpperInvariant(),
            fields[3],
            fields[4]);
        return true;
    }

    internal static string BuildForTests(
        string productCode,
        string restartManagerSessionKey,
        string originalDatabase,
        string installDirectory = @"C:\Program Files\Suavo\Agent\") =>
        string.Join(
            '|',
            SchemaTag,
            productCode,
            restartManagerSessionKey,
            originalDatabase,
            installDirectory);

    internal static bool IsValidInvocationId(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string RequireFixedInstallDirectory(string candidate)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The fixed MSI install directory is a Windows boundary.");
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Path.IsPathFullyQualified(candidate))
            throw new InvalidDataException("The MSI install directory is invalid.");

        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles) ||
            !Path.IsPathFullyQualified(programFiles))
            throw new InvalidDataException("The fixed Program Files root is unavailable.");
        var expected = Path.GetFullPath(Path.Combine(programFiles, "Suavo", "Agent"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actual = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The MSI install directory is not the fixed product path.");

        var root = Path.GetPathRoot(expected);
        if (string.IsNullOrWhiteSpace(root) ||
            new DriveInfo(root).DriveType == DriveType.Network)
            throw new InvalidDataException("The fixed install path must be local.");
        var current = root;
        foreach (var segment in expected[root.Length..].Split(
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar,
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "The fixed MSI install path is unavailable or redirected.");
        }
        return expected;
    }

    private static bool ValidBoundedText(string value, int maximumCharacters) =>
        value.Length is > 0 &&
        value.Length <= maximumCharacters &&
        value.All(character => character is >= ' ' and not '|' and not '\u007f');
}

internal interface IMsiInstallerTransactionActivation
{
    void RequireAbsent();
    void Arm(string invocationId);
    void RequireCurrent(string invocationId);
    void Disarm(string invocationId);
}

/// <summary>
/// Protected active-invocation token. Rollback requires both this token and its
/// journal to match the current hidden CustomActionData identity, preventing a
/// queued rollback action from replaying a prior MSI invocation's journal.
/// </summary>
internal sealed class FileMsiInstallerTransactionActivation
    : IMsiInstallerTransactionActivation
{
    internal const string FileName = ".msi-installer-transaction.active.json";
    internal const int SchemaVersion = 1;
    internal const int MaximumBytes = 1024;
    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly Action<string> _secureActiveFile;

    internal FileMsiInstallerTransactionActivation(string path)
        : this(path, SecureActiveFile)
    {
    }

    internal FileMsiInstallerTransactionActivation(
        string path,
        Action<string> secureActiveFile)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException(
                "A fully qualified MSI activation path is required.",
                nameof(path));
        _path = path;
        _temporaryPath = path + ".tmp";
        _secureActiveFile = secureActiveFile ??
            throw new ArgumentNullException(nameof(secureActiveFile));
    }

    internal static FileMsiInstallerTransactionActivation CreateForInstallDirectory(
        string installDirectory) =>
        new(Path.Combine(
            MsiInstallerInvocation.RequireFixedInstallDirectory(installDirectory),
            FileName));

    public void Arm(string invocationId)
    {
        ValidateInvocationId(invocationId);
        RequireAbsent();

        var bytes = CanonicalBytes(new ActivationDocument(
            SchemaVersion,
            invocationId));
        var installed = false;
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

            MoveIntoPlace(_temporaryPath, _path, replaceExisting: false);
            installed = true;
            _secureActiveFile(_path);
            var persisted = Read();
            if (!string.Equals(
                    persisted.InvocationId,
                    invocationId,
                    StringComparison.Ordinal))
                throw new IOException("The MSI activation token is not durable.");
        }
        catch
        {
            TryDelete(_temporaryPath);
            if (installed)
                TryDelete(_path);
            throw;
        }
    }

    public void RequireAbsent()
    {
        if (File.Exists(_path))
            throw new IOException(
                "An MSI installer transaction is already active.");
    }

    public void RequireCurrent(string invocationId)
    {
        ValidateInvocationId(invocationId);
        var current = Read();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(current.InvocationId),
                Encoding.ASCII.GetBytes(invocationId)))
            throw new InvalidDataException(
                "The active MSI transaction does not match this invocation.");
    }

    public void Disarm(string invocationId)
    {
        RequireCurrent(invocationId);
        File.Delete(_path);
        if (File.Exists(_path))
            throw new IOException("The active MSI transaction remains present.");
    }

    private ActivationDocument Read()
    {
        EnsureRegularBoundedFile(_path);
        _secureActiveFile(_path);
        var bytes = File.ReadAllBytes(_path);
        var document = JsonSerializer.Deserialize<ActivationDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("The MSI activation token is empty.");
        if (document.SchemaVersion != SchemaVersion ||
            !MsiInstallerInvocation.IsValidInvocationId(document.InvocationId) ||
            !CryptographicOperations.FixedTimeEquals(bytes, CanonicalBytes(document)))
            throw new InvalidDataException("The MSI activation token is invalid.");
        return document;
    }

    private static byte[] CanonicalBytes(ActivationDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("The MSI activation token is invalid.");
        return bytes;
    }

    private static void EnsureRegularBoundedFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumBytes ||
            file.Attributes.HasFlag(FileAttributes.Directory) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The MSI activation token is untrusted.");
    }

    private static void ValidateInvocationId(string invocationId)
    {
        if (!MsiInstallerInvocation.IsValidInvocationId(invocationId))
            throw new InvalidDataException("The MSI invocation identity is invalid.");
    }

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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    internal static HandleBoundAclPolicy BuildActiveFileAclPolicy() => new(
        HandleBoundAcl.SystemSid,
        [
            new HandleBoundAclAce(
                HandleBoundAcl.SystemSid,
                FileSystemRights.FullControl),
            new HandleBoundAclAce(
                HandleBoundAcl.AdministratorsSid,
                FileSystemRights.FullControl),
        ]);

    private static void SecureActiveFile(string path) =>
        new HandleBoundAcl().ApplyBatch(
        [
            new HandleBoundAclMutation(
                path,
                IsDirectory: false,
                BuildActiveFileAclPolicy()),
        ]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

    private sealed record ActivationDocument(
        int SchemaVersion,
        string InvocationId);

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
