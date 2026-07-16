using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Watchdog;

internal sealed record RemoteRepairGateResult(
    bool IsValid,
    string Code,
    RemoteRepairRequest? Request = null,
    string? ReplayId = null,
    string? RequestDigest = null)
{
    public static RemoteRepairGateResult Valid(
        RemoteRepairRequest request,
        string replayId,
        string requestDigest) => new(true, "valid", request, replayId, requestDigest);

    public static RemoteRepairGateResult Reject(
        string code,
        string? requestDigest = null) => new(false, code, RequestDigest: requestDigest);
}

/// <summary>
/// LocalSystem's independent trust boundary for a LocalService-written repair request.
/// The request is read once through a bounded regular-file handle, then the original
/// cloud signature and exact raw data binding are verified again.
/// </summary>
internal sealed class RemoteRepairGate
{
    private readonly IReadOnlyDictionary<string, string> _trustedPublicKeys;
    private readonly ILogger _logger;

    public RemoteRepairGate(
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        ILogger logger)
    {
        _trustedPublicKeys = new Dictionary<string, string>(
            trustedPublicKeys,
            StringComparer.Ordinal);
        _logger = logger;
    }

    public RemoteRepairGateResult Validate(
        string requestPath,
        string? expectedAgentId,
        string? expectedMachineFingerprint,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(expectedAgentId) ||
            string.IsNullOrWhiteSpace(expectedMachineFingerprint))
            return RemoteRepairGateResult.Reject("watchdog_identity_unavailable");

        try
        {
            var bytes = BoundedRegularFile.Read(
                requestPath,
                RemoteRepairContract.MaxRequestBytes);
            var requestDigest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var json = new UTF8Encoding(false, true).GetString(bytes);
            if (!RemoteRepairContract.TryDeserialize(
                    json,
                    out var request,
                    out var deserializeCode))
                return RemoteRepairGateResult.Reject(deserializeCode, requestDigest);

            var validation = RemoteRepairContract.Validate(
                request!,
                expectedAgentId,
                expectedMachineFingerprint,
                _trustedPublicKeys,
                now);
            return validation.IsValid
                ? RemoteRepairGateResult.Valid(request!, validation.ReplayId!, requestDigest)
                : RemoteRepairGateResult.Reject(validation.Code, requestDigest);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            DecoderFallbackException or
            JsonException or
            Win32Exception or
            ArgumentException or
            NotSupportedException)
        {
            _logger.LogSafeWarning(ex);
            return RemoteRepairGateResult.Reject("repair_request_unreadable");
        }
    }
}

internal sealed record RemoteRepairReplayEntry(
    string ReplayId,
    string RecordedAtUtc);

internal sealed record RemoteRepairReplayDocument(
    int SchemaVersion,
    IReadOnlyList<RemoteRepairReplayEntry> Entries);

internal sealed record RemoteRepairReplayResult(bool Recorded, string Code)
{
    public static RemoteRepairReplayResult Success() => new(true, "recorded");
    public static RemoteRepairReplayResult Reject(string code) => new(false, code);
}

/// <summary>
/// Non-expiring SYSTEM/Admin-only replay journal. A replay ID is committed and
/// flushed before maintenance is invoked, so a process crash cannot execute the
/// same signed repair twice after restart. Corruption always fails closed.
/// </summary>
internal sealed class RemoteRepairReplayLedger
{
    private const int SchemaVersion = 1;
    private const int MaximumEntries = 4096;
    private const int MaximumLedgerBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public RemoteRepairReplayLedger(string path) => _path = Path.GetFullPath(path);

    public RemoteRepairReplayResult TryRecord(string replayId, DateTimeOffset now)
    {
        lock (_gate)
        {
            try
            {
                if (!IsSha256(replayId))
                    return RemoteRepairReplayResult.Reject("repair_replay_id_invalid");

                var entries = Read();
                if (entries.Any(entry =>
                        string.Equals(entry.ReplayId, replayId, StringComparison.OrdinalIgnoreCase)))
                    return RemoteRepairReplayResult.Reject("repair_request_replay");
                if (entries.Count >= MaximumEntries)
                    return RemoteRepairReplayResult.Reject("repair_replay_ledger_full");

                entries.Add(new RemoteRepairReplayEntry(replayId, now.ToString("O")));
                Write(entries);
                return RemoteRepairReplayResult.Success();
            }
            catch (Exception ex) when (ex is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                DecoderFallbackException or
                JsonException or
                ArgumentException or
                NotSupportedException)
            {
                return RemoteRepairReplayResult.Reject("repair_replay_ledger_corrupt");
            }
        }
    }

    private List<RemoteRepairReplayEntry> Read()
    {
        if (!File.Exists(_path))
            return [];

        var bytes = BoundedRegularFile.Read(_path, MaximumLedgerBytes);
        var json = new UTF8Encoding(false, true).GetString(bytes);
        var document = JsonSerializer.Deserialize<RemoteRepairReplayDocument>(json, JsonOptions)
            ?? throw new InvalidDataException("Repair replay ledger is null");
        if (document.SchemaVersion != SchemaVersion ||
            document.Entries is null ||
            document.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Repair replay ledger shape is invalid");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Entries)
        {
            if (entry is null ||
                !IsSha256(entry.ReplayId) ||
                !seen.Add(entry.ReplayId) ||
                !DateTimeOffset.TryParseExact(
                    entry.RecordedAtUtc,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out _))
                throw new InvalidDataException("Repair replay ledger entry is invalid");
        }
        return document.Entries.ToList();
    }

    private void Write(IReadOnlyList<RemoteRepairReplayEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("Repair replay ledger has no parent directory");
        Directory.CreateDirectory(directory);
        BoundedRegularFile.RejectReparsePoint(directory);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteRepairReplayDocument(SchemaVersion, entries),
            JsonOptions);
        if (bytes.Length <= 0 || bytes.Length > MaximumLedgerBytes)
            throw new InvalidDataException("Repair replay ledger exceeds its bound");

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                BoundedRegularFile.RejectReparsePoint(_path);
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>Handle-based bounded regular-file reader used at privileged file boundaries.</summary>
internal static class BoundedRegularFile
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    public static byte[] Read(string path, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || maximumBytes <= 0)
            throw new ArgumentException("A bounded regular-file path is required", nameof(path));

        return OperatingSystem.IsWindows()
            ? ReadWindows(Path.GetFullPath(path), maximumBytes)
            : ReadPortable(Path.GetFullPath(path), maximumBytes);
    }

    public static void RejectReparsePoint(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Reparse points are forbidden at a privileged file boundary");
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (info.LinkTarget is not null)
                throw new InvalidDataException("Symbolic links are forbidden at a privileged file boundary");
        }
    }

    private static byte[] ReadPortable(string path, int maximumBytes)
    {
        RejectReparsePoint(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return ReadExactBounded(stream, maximumBytes);
    }

    private static byte[] ReadWindows(string path, int maximumBytes)
    {
        var handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((information.FileAttributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                throw new InvalidDataException("Repair request is not a regular non-reparse file");

            var length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            if (length <= 0 || length > maximumBytes)
                throw new InvalidDataException("Privileged input file size is invalid");

            using var stream = new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false);
            handle = null!; // FileStream now owns the handle.
            return ReadExactBounded(stream, maximumBytes);
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static byte[] ReadExactBounded(Stream stream, int maximumBytes)
    {
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Privileged input file size is invalid");

        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total <= 0 || total > maximumBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("Privileged input file exceeded its bound while reading");
        return bytes.AsSpan(0, total).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);
}
