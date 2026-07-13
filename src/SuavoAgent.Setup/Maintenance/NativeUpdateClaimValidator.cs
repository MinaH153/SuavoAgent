using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record ValidatedUpdateClaim(
    UpdateActivationRequest Request,
    UpdatePackageManifest Manifest,
    byte[] RequestBytes,
    string ReplayId,
    string PayloadDirectory);

internal sealed record UpdateClaimValidationResult(
    bool IsValid,
    string Code,
    ValidatedUpdateClaim? Claim = null)
{
    public static UpdateClaimValidationResult Valid(ValidatedUpdateClaim claim) =>
        new(true, "valid", claim);
    public static UpdateClaimValidationResult Reject(string code) => new(false, code);
}

/// <summary>
/// Maintenance-side independent verification of the signed activation envelope
/// and every staged byte. This intentionally does not trust Watchdog's prior
/// decision or its LocalService-writable launch-dedupe ledger.
/// </summary>
internal sealed class NativeUpdateClaimValidator
{
    private readonly IReadOnlyDictionary<string, string> _commandKeys;
    private readonly string _updatePublicKey;

    public NativeUpdateClaimValidator()
        : this(
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            UpdateActivationContract.ProductionUpdatePublicKeyDer)
    {
    }

    internal NativeUpdateClaimValidator(
        IReadOnlyDictionary<string, string> commandKeys,
        string updatePublicKey)
    {
        _commandKeys = commandKeys;
        _updatePublicKey = updatePublicKey;
    }

    public UpdateClaimValidationResult Validate(
        string requestPath,
        string payloadDirectory,
        InstalledUpdateIdentity identity,
        DateTimeOffset now,
        Action? betweenVerificationPasses = null,
        bool requireStrictUpgrade = true,
        bool allowExpiredDurableClaim = false,
        Action? progress = null)
    {
        try
        {
            if (!Directory.Exists(payloadDirectory) ||
                HasReparsePoint(payloadDirectory))
                return UpdateClaimValidationResult.Reject("claim_path_invalid");

            var requestBytes = BoundedFile.ReadBytes(
                requestPath,
                UpdateActivationContract.MaxRequestBytes);
            var json = new UTF8Encoding(false, true).GetString(requestBytes);
            if (!UpdateActivationContract.TryDeserialize(
                    json,
                    out var request,
                    out var deserializeCode))
                return UpdateClaimValidationResult.Reject(deserializeCode);
            var validation = UpdateActivationContract.Validate(
                request!,
                _commandKeys,
                _updatePublicKey,
                now,
                identity.AgentId,
                identity.MachineFingerprint,
                maximumAge: allowExpiredDurableClaim
                    ? TimeSpan.MaxValue
                    : null);
            if (!validation.IsValid)
                return UpdateClaimValidationResult.Reject(validation.Code);
            var manifest = validation.Manifest
                           ?? throw new InvalidDataException("Valid update manifest is missing.");
            if (requireStrictUpgrade &&
                !IsStrictUpgrade(manifest.Version, identity.Version))
                return UpdateClaimValidationResult.Reject("version_not_strictly_newer");
            if (!requireStrictUpgrade &&
                IsOlder(manifest.Version, identity.Version))
                return UpdateClaimValidationResult.Reject("version_downgrade_rejected");

            var expected = manifest.Files.ToDictionary(
                file => file.FileName,
                file => file.Sha256,
                StringComparer.OrdinalIgnoreCase);
            var actualNames = Directory.EnumerateFiles(
                    payloadDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray();
            if (actualNames.Length != expected.Count ||
                actualNames.Any(name => !expected.ContainsKey(name)))
                return UpdateClaimValidationResult.Reject("payload_file_set_mismatch");

            var first = Capture(payloadDirectory, expected, progress);
            if (first is null)
                return UpdateClaimValidationResult.Reject("payload_hash_invalid");
            betweenVerificationPasses?.Invoke();
            var second = Capture(payloadDirectory, expected, progress);
            if (second is null || !SnapshotsEqual(first, second))
                return UpdateClaimValidationResult.Reject("payload_toctou_detected");

            var secondRequest = BoundedFile.ReadBytes(
                requestPath,
                UpdateActivationContract.MaxRequestBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(requestBytes),
                    SHA256.HashData(secondRequest)))
                return UpdateClaimValidationResult.Reject("request_toctou_detected");

            return UpdateClaimValidationResult.Valid(new ValidatedUpdateClaim(
                request!,
                manifest,
                requestBytes,
                UpdateActivationContract.ComputeReplayId(request!),
                Path.GetFullPath(payloadDirectory)));
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            FormatException or
            ArgumentException or
            DecoderFallbackException)
        {
            return UpdateClaimValidationResult.Reject(
                "claim_unreadable:" + ex.GetType().Name);
        }
    }

    internal static bool IsStrictUpgrade(string targetVersion, string currentVersion)
    {
        var targetText = (targetVersion ?? string.Empty).TrimStart('v').Split('-', 2)[0];
        var currentText = (currentVersion ?? string.Empty).TrimStart('v').Split('-', 2)[0];
        return Version.TryParse(targetText, out var target) &&
               Version.TryParse(currentText, out var current) &&
               target > current;
    }

    private static bool IsOlder(string targetVersion, string currentVersion)
    {
        var targetText = (targetVersion ?? string.Empty).TrimStart('v').Split('-', 2)[0];
        var currentText = (currentVersion ?? string.Empty).TrimStart('v').Split('-', 2)[0];
        return !Version.TryParse(targetText, out var target) ||
               !Version.TryParse(currentText, out var current) ||
               target < current;
    }

    private static Dictionary<string, FileSnapshot>? Capture(
        string payloadDirectory,
        IReadOnlyDictionary<string, string> expected,
        Action? progress)
    {
        var snapshots = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, expectedHash) in expected)
        {
            progress?.Invoke();
            var path = Path.Combine(payloadDirectory, fileName);
            if (!File.Exists(path) || HasReparsePoint(path)) return null;
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            var length = stream.Length;
            if (length <= 0 || length > 200L * 1024 * 1024) return null;
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
                stream.Position != stream.Length)
                return null;
            snapshots[fileName] = new FileSnapshot(
                hash,
                length);
        }
        return snapshots;
    }

    private static bool SnapshotsEqual(
        IReadOnlyDictionary<string, FileSnapshot> left,
        IReadOnlyDictionary<string, FileSnapshot> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var other) && pair.Value == other);

    private static bool HasReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private sealed record FileSnapshot(string Hash, long Length);
}
