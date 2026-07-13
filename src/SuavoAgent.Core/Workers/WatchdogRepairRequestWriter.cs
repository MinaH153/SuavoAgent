using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Health;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Persists the exact, already-verified cloud repair command for an independent
/// LocalSystem verification pass. This handoff never invents or normalizes a
/// payload: the original raw JSON is bound to the original command signature.
/// </summary>
internal static class WatchdogRepairRequestWriter
{
    internal static readonly string[] AllowedReasons =
        RemoteRepairContract.AllowedReasons.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    public enum ReasonValidation
    {
        Defaulted,
        Accepted,
        Rejected,
    }

    public static string Queue(
        string? configuredPath,
        SignedCommand command,
        string rawDataJson,
        DateTimeOffset? requestedAtUtc = null)
    {
        if (command.Command is not ("repair" or "repair_agent"))
            throw new InvalidDataException("Only a signed repair command may cross the watchdog boundary");
        if (!RemoteRepairContract.TryReadMinimumNecessaryData(
                rawDataJson,
                out var commandId,
                out var reason,
                out var expiresAt))
            throw new InvalidDataException("Repair command data is not minimum necessary");
        if (!string.Equals(command.ExpiresAt, expiresAt, StringComparison.Ordinal))
            throw new InvalidDataException("Repair authority does not match the signed payload");

        var expectedHash = RemoteCommandTrust.ComputeSha256Hex(rawDataJson);
        if (!FixedTimeHexEquals(expectedHash, command.DataHash))
            throw new InvalidDataException("Repair command data no longer matches the signed envelope");

        var request = new RemoteRepairRequest(
            RemoteRepairContract.SchemaVersion,
            command.Command,
            command.AgentId,
            command.MachineFingerprint,
            command.Timestamp,
            command.Nonce,
            command.KeyId,
            command.Signature,
            rawDataJson,
            command.DataHash,
            commandId,
            reason,
            (requestedAtUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        var json = RemoteRepairContract.Serialize(request);
        var bytes = new UTF8Encoding(false, true).GetBytes(json);
        if (bytes.Length <= 0 || bytes.Length > RemoteRepairContract.MaxRequestBytes)
            throw new InvalidDataException("Repair handoff exceeds its bounded contract");

        var requestPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(RuntimeHealthEvidence.ProgramDataRoot, RemoteRepairContract.RequestFileName)
            : Path.GetFullPath(configuredPath);
        WriteAtomic(requestPath, bytes);
        return requestPath;
    }

    public static string ReadReason(JsonElement dataEl)
    {
        if (!dataEl.TryGetProperty("reason", out var reasonEl) ||
            reasonEl.ValueKind != JsonValueKind.String)
            return "remote_command";

        var value = reasonEl.GetString();
        return string.IsNullOrWhiteSpace(value) ? "remote_command" : value;
    }

    public static (string? raw, ReasonValidation result) InspectReason(JsonElement dataEl)
    {
        if (!dataEl.TryGetProperty("reason", out var reasonEl))
            return (null, ReasonValidation.Defaulted);
        if (reasonEl.ValueKind != JsonValueKind.String)
            return (null, ReasonValidation.Rejected);

        var raw = reasonEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return (null, ReasonValidation.Rejected);

        return RemoteRepairContract.AllowedReasons.Contains(raw)
            ? (raw, ReasonValidation.Accepted)
            : (raw, ReasonValidation.Rejected);
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("Repair request path has no parent directory");
        if (FileSystemEntryExists(directory))
            RejectReparsePoint(directory, requireDirectory: true);
        else
            Directory.CreateDirectory(directory);
        RejectReparsePoint(directory, requireDirectory: true);

        if (FileSystemEntryExists(path))
            RejectReparsePoint(path, requireDirectory: false);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
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

            // MoveFileEx/rename replaces the destination directory entry itself; it never opens
            // or follows an existing link target. The explicit checks above still reject links
            // so an attempted boundary redirection is visible instead of silently repaired.
            File.Move(temporaryPath, path, overwrite: true);
            RejectReparsePoint(path, requireDirectory: false);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool FileSystemEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectReparsePoint(string path, bool requireDirectory)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || isDirectory != requireDirectory)
            throw new InvalidDataException("Repair handoff paths must be regular non-reparse entries");

        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo info = requireDirectory
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (info.LinkTarget is not null)
                throw new InvalidDataException("Repair handoff paths must not be symbolic links");
        }
    }

    private static bool FixedTimeHexEquals(string? left, string? right)
    {
        if (left is null || right is null ||
            left.Length != 64 || right.Length != 64 ||
            !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit))
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}
