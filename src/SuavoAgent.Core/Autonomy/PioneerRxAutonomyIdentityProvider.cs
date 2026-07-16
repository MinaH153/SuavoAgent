using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Autonomy;

internal sealed record PioneerRxAutonomyIdentity(
    string FileVersion,
    string ExecutableSha256,
    string SignerCertificateSha256,
    string ApprovalReceiptDigest,
    string AuthorityDigest,
    long ApprovalCounter);

internal interface IPioneerRxAutonomyIdentityProvider
{
    PioneerRxAutonomyIdentity? Current(DateTimeOffset now);
}

/// <summary>
/// Reads the SYSTEM-installed approval set, verifies both device/cloud
/// signatures and counter high-water, then proves the executable currently on
/// disk still has the approved hash, file version, and Authenticode certificate.
/// </summary>
internal sealed class PioneerRxAutonomyIdentityProvider : IPioneerRxAutonomyIdentityProvider
{
    private const int MaximumJsonBytes = 256 * 1024;
    private readonly AgentOptions _options;
    private readonly string _root;
    private readonly IReadOnlyDictionary<string, string> _trustedCloudKeys;
    private readonly Func<string, string, string, string, bool> _verifyLiveExecutable;
    private readonly Func<string, bool> _validateDirectory;
    private readonly Func<string, bool> _validateFile;

    internal PioneerRxAutonomyIdentityProvider(AgentOptions options)
        : this(
            options,
            PioneerRxApprovalMaintenanceContract.DefaultAuthorityDirectory(),
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            VerifyLiveExecutable,
            ValidateProductionDirectory,
            ValidateProductionFile)
    {
    }

    private static bool ValidateProductionDirectory(string path) =>
        OperatingSystem.IsWindows() && PioneerRxApprovalMetadataAcl.ValidateDirectory(path);

    private static bool ValidateProductionFile(string path) =>
        OperatingSystem.IsWindows() &&
        PioneerRxApprovalMetadataAcl.ValidateFile(path, interactiveRead: true);

    internal PioneerRxAutonomyIdentityProvider(
        AgentOptions options,
        string root,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        Func<string, string, string, string, bool> verifyLiveExecutable,
        Func<string, bool> validateDirectory,
        Func<string, bool> validateFile)
    {
        _options = options;
        _root = root;
        _trustedCloudKeys = trustedCloudKeys;
        _verifyLiveExecutable = verifyLiveExecutable;
        _validateDirectory = validateDirectory;
        _validateFile = validateFile;
    }

    public PioneerRxAutonomyIdentity? Current(DateTimeOffset now)
    {
        try
        {
            var receiptPath = Path.Combine(
                _root, PioneerRxApprovalMaintenanceContract.ReceiptFileName);
            var authorityPath = Path.Combine(
                _root, PioneerRxApprovalMaintenanceContract.AuthorityFileName);
            var catalogPath = Path.Combine(
                _root, PioneerRxVendorIdentityCatalogContract.InstalledFileName);
            var highWaterPath = Path.Combine(
                _root, PioneerRxApprovalMaintenanceContract.HighWaterFileName);
            if (!_validateDirectory(_root) ||
                !_validateFile(receiptPath) ||
                !_validateFile(authorityPath) ||
                !_validateFile(catalogPath) ||
                !_validateFile(highWaterPath))
                return null;

            var receipt = ReadStrict<PioneerRxProcessApprovalReceipt>(receiptPath);
            var authority = ReadStrict<PioneerRxApprovalAuthorityState>(authorityPath);
            var catalog = ReadStrict<PioneerRxVendorIdentityCatalog>(catalogPath);
            var highWater = ReadStrict<PioneerRxApprovalHighWaterState>(highWaterPath);
            if (receipt is null || authority is null || catalog is null || highWater is null ||
                string.IsNullOrWhiteSpace(_options.PharmacyId) ||
                string.IsNullOrWhiteSpace(_options.MachineFingerprint) ||
                string.IsNullOrWhiteSpace(_options.MaintenanceAttestationKeyId) ||
                !PioneerRxProcessApprovalContract.TryValidate(
                    receipt,
                    catalog,
                    _options.PharmacyId,
                    _options.MachineFingerprint,
                    _options.MaintenanceAttestationKeyId,
                    now,
                    _trustedCloudKeys,
                    out _) ||
                !PioneerRxProcessApprovalContract.TryValidateAuthorityState(
                    authority,
                    receipt,
                    _options.PharmacyId,
                    _options.MachineFingerprint,
                    now,
                    _trustedCloudKeys,
                    out _) ||
                !PioneerRxApprovalMaintenanceContract.HighWaterMatches(
                    highWater, receipt, authority, catalog) ||
                !_verifyLiveExecutable(
                    receipt.CanonicalExecutablePath,
                    receipt.ExecutableSha256,
                    receipt.FileVersion,
                    receipt.SignerCertificateSha256))
                return null;

            return new(
                receipt.FileVersion,
                receipt.ExecutableSha256,
                receipt.SignerCertificateSha256,
                Sha256Hex(PioneerRxProcessApprovalContract.Canonical(receipt)),
                PioneerRxApprovalMaintenanceContract.ComputeAuthorityDigest(authority),
                receipt.ApprovalCounter);
        }
        catch
        {
            return null;
        }
    }

    private static bool VerifyLiveExecutable(
        string path,
        string expectedSha256,
        string expectedFileVersion,
        string expectedSignerCertificateSha256)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            if (!string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
                return false;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            var executableDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            using var signer = X509Certificate.CreateFromSignedFile(path);
            var signerDigest = Convert.ToHexString(
                SHA256.HashData(signer.GetRawCertData())).ToLowerInvariant();
            return FixedHexEquals(executableDigest, expectedSha256) &&
                string.Equals(version, expectedFileVersion, StringComparison.Ordinal) &&
                FixedHexEquals(signerDigest, expectedSignerCertificateSha256);
        }
        catch
        {
            return false;
        }
    }

    private static T? ReadStrict<T>(string path)
    {
        byte[]? bytes = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return default;
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumJsonBytes) return default;
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (!HasUniqueRootProperties(bytes)) return default;
            return JsonSerializer.Deserialize<T>(
                bytes, PioneerRxApprovalMaintenanceContract.JsonOptions);
        }
        catch
        {
            return default;
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool HasUniqueRootProperties(ReadOnlySpan<byte> json)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 12,
            AllowTrailingCommas = false,
        });
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 &&
                !names.Add(reader.GetString() ?? string.Empty))
                return false;
        }
        return true;
    }

    private static string Sha256Hex(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedHexEquals(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch
        {
            return false;
        }
    }
}
