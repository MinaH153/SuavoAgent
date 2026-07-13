using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Security;

internal static class PioneerRxApprovalBootstrapRequestWriter
{
    internal static string Queue(
        string approvedBySid,
        string consentReceiptJson,
        string? requestPath = null,
        DateTimeOffset? now = null)
    {
        if (!IsSid(approvedBySid) ||
            string.IsNullOrWhiteSpace(consentReceiptJson) ||
            Encoding.UTF8.GetByteCount(consentReceiptJson) > 64 * 1024)
            throw new InvalidDataException("PioneerRx bootstrap consent evidence is invalid.");
        var consentBytes = Encoding.UTF8.GetBytes(consentReceiptJson);
        byte[] bytes;
        try
        {
            var digest = SHA256.HashData(consentBytes);
            string digestHex;
            try { digestHex = Convert.ToHexString(digest).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(digest); }
            var instant = now ?? DateTimeOffset.UtcNow;
            var request = new PioneerRxApprovalBootstrapRequest(
                PioneerRxApprovalBootstrapContract.SchemaVersion,
                approvedBySid,
                digestHex,
                instant.UtcDateTime.ToString(
                    PioneerRxProcessApprovalContract.UtcTimestampFormat,
                    System.Globalization.CultureInfo.InvariantCulture));
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                request,
                PioneerRxApprovalMaintenanceContract.JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(consentBytes);
        }

        try
        {
            var path = requestPath ?? PioneerRxApprovalBootstrapContract.DefaultRequestPath();
            if (requestPath is null && !PioneerRxApprovalBootstrapContract.IsExactRequestPath(path))
                throw new InvalidDataException("PioneerRx bootstrap request path is invalid.");
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidDataException("PioneerRx bootstrap parent is missing.");
            Directory.CreateDirectory(directory);
            if (OperatingSystem.IsWindows())
                PioneerRxApprovalMetadataAcl.ProtectDirectory(directory);
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
                if (OperatingSystem.IsWindows())
                {
                    PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(temporary);
                    if (!PioneerRxApprovalMetadataAcl.ValidateFile(
                            temporary,
                            interactiveRead: false))
                        throw new UnauthorizedAccessException(
                            "PioneerRx bootstrap request ACL is invalid.");
                }
                File.Move(temporary, path, overwrite: true);
                if (OperatingSystem.IsWindows())
                {
                    PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(path);
                    if (!PioneerRxApprovalMetadataAcl.ValidateFile(
                            path,
                            interactiveRead: false))
                        throw new UnauthorizedAccessException(
                            "PioneerRx bootstrap request ACL is invalid.");
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return path;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool IsSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("S-1-", StringComparison.Ordinal))
            return false;
        var segments = value.Split('-');
        return segments.Length >= 4 && segments[0] == "S" && segments[1] == "1" &&
               segments.Skip(2).All(segment =>
                   segment.Length > 0 && segment.All(char.IsAsciiDigit));
    }
}
