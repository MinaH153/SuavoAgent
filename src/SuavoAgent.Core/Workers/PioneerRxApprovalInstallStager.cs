using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Workers;

internal sealed record PioneerRxApprovalStageResult(bool Succeeded, string Code);

/// <summary>
/// Core may only stage untrusted signed metadata for the SYSTEM maintenance host. It has no
/// path, API, or ACL authority capable of replacing the live approval receipt or authority.
/// </summary>
internal static class PioneerRxApprovalInstallStager
{
    internal static PioneerRxApprovalStageResult Stage(
        PioneerRxApprovalInstallCommand command,
        string? requestPath = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var expectedDigest = PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
            command.CommandId,
            command.Receipt,
            command.Authority,
            command.VendorCatalog,
            command.ProtocolEpoch);
        if (!FixedHexEquals(command.PayloadDigest, expectedDigest))
            return new(false, "pioneerrx_approval_payload_digest_mismatch");

        var path = requestPath ?? PioneerRxApprovalMaintenanceContract.DefaultRequestPath();
        if (requestPath is null && !PioneerRxApprovalMaintenanceContract.IsExactRequestPath(path))
            return new(false, "pioneerrx_approval_request_path_invalid");

        var requestedAt = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);
        var request = new PioneerRxApprovalInstallRequest(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            command.ProtocolEpoch,
            command.CommandId,
            command.PayloadDigest,
            command.Receipt,
            command.Authority,
            command.VendorCatalog,
            requestedAt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            PioneerRxApprovalMaintenanceContract.JsonOptions);
        try
        {
            if (bytes.Length is <= 0 or > PioneerRxApprovalMaintenanceContract.MaximumJsonBytes)
                return new(false, "pioneerrx_approval_request_size_invalid");
            WriteAtomicUntrustedHandoff(path, bytes);
            return new(true, "staged");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new(false, "pioneerrx_approval_stage_failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static bool HasExactCompletion(
        PioneerRxApprovalInstallCommand command,
        out string code,
        string? completionPath = null,
        bool requireProductionAcl = true)
    {
        ArgumentNullException.ThrowIfNull(command);
        code = "pending_system_install";
        var path = completionPath ?? PioneerRxApprovalMaintenanceContract.DefaultCompletionPath();
        try
        {
            if (!File.Exists(path)) return false;
            if (OperatingSystem.IsWindows() && requireProductionAcl &&
                !PioneerRxApprovalMetadataAcl.ValidateFile(path, interactiveRead: true))
            {
                code = "pioneerrx_approval_completion_acl_invalid";
                return false;
            }

            var bytes = ReadBoundedRegularFile(
                path,
                PioneerRxApprovalMaintenanceContract.MaximumJsonBytes);
            try
            {
                if (!PioneerRxApprovalMaintenanceContract.TryDeserializeCompletion(
                        bytes,
                        out var completion) ||
                    !PioneerRxApprovalMaintenanceContract.CompletionMatches(
                        completion,
                        command.CommandId,
                        command.PayloadDigest,
                        command.Receipt,
                        command.Authority))
                    return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }

            code = PioneerRxProcessApprovalContract.IsReceiptRevoked(
                command.Authority,
                command.Receipt.ReceiptId)
                ? PioneerRxApprovalMaintenanceContract.RevokedOutcome
                : PioneerRxApprovalMaintenanceContract.InstalledOutcome;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            code = "pioneerrx_approval_completion_unreadable";
            return false;
        }
    }

    private static void WriteAtomicUntrustedHandoff(string path, ReadOnlySpan<byte> bytes)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Approval request path must be absolute.");
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidDataException("Approval request parent is missing.");
        if (EntryExists(directory)) RejectReparse(directory, requireDirectory: true);
        else Directory.CreateDirectory(directory);
        RejectReparse(directory, requireDirectory: true);
        if (EntryExists(path)) RejectReparse(path, requireDirectory: false);

        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            RejectReparse(path, requireDirectory: false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static byte[] ReadBoundedRegularFile(string path, int maximumBytes)
    {
        RejectReparse(path, requireDirectory: false);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Approval completion length is invalid.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Approval completion changed during read.");
        return bytes;
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectReparse(string path, bool requireDirectory)
    {
        var attributes = File.GetAttributes(path);
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || isDirectory != requireDirectory)
            throw new InvalidDataException("Approval handoff entries must be regular non-reparse paths.");
        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo info = requireDirectory
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            if (info.LinkTarget is not null)
                throw new InvalidDataException("Approval handoff entries cannot be symbolic links.");
        }
    }

    private static bool FixedHexEquals(string? left, string? right)
    {
        if (!LowerHex64(left) || !LowerHex64(right)) return false;
        var leftBytes = Convert.FromHexString(left!);
        var rightBytes = Convert.FromHexString(right!);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
