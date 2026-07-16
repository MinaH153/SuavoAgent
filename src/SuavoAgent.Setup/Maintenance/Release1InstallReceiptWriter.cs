using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.InstallerSupport;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record Release1InstallReceiptWriteResult(
    bool Succeeded,
    bool Required,
    string Code,
    SignedRelease1InstallReceipt? Envelope = null);

internal sealed record Release1InstalledReceiptIdentity(
    string ReleaseTag,
    string MachineFingerprint,
    string MaintenanceKeyId);

internal static class Release1InstalledReceiptIdentityReader
{
    private const int MaxSettingsBytes = 1024 * 1024;
    private const int MaxInstallStateBytes = 64 * 1024;

    internal static Release1InstalledReceiptIdentity? TryRead(
        string installDirectory)
    {
        try
        {
            using var settings = JsonDocument.Parse(BoundedFile.ReadBytes(
                Path.Combine(installDirectory, "appsettings.json"),
                MaxSettingsBytes));
            using var state = JsonDocument.Parse(BoundedFile.ReadBytes(
                Path.Combine(
                    installDirectory,
                    MaintenanceContract.InstallStateFileName),
                MaxInstallStateBytes));
            if (!settings.RootElement.TryGetProperty("Agent", out var agent) ||
                agent.ValueKind != JsonValueKind.Object)
                return null;
            var fingerprint = ReadString(agent, "MachineFingerprint");
            var maintenanceKeyId = ReadString(
                agent,
                "MaintenanceAttestationKeyId");
            var version = ReadString(state.RootElement, "version");
            if (string.IsNullOrWhiteSpace(fingerprint) ||
                fingerprint.Length > 256 ||
                !LowerHex64(maintenanceKeyId) ||
                !BinaryDownloader.IsValidReleaseTag(version))
                return null;
            return new(
                version!.StartsWith('v') || version.StartsWith('V')
                    ? "v" + version[1..]
                    : "v" + version,
                fingerprint,
                maintenanceKeyId!);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Consumes the commit-only MSI marker and produces a maintenance-TPM-signed,
/// PHI-negative proof of the exact Release 1 cohort. A shortcut launch without
/// a fresh successful MSI transaction cannot mint this receipt.
/// </summary>
internal sealed class Release1InstallReceiptWriter
{
    private const int MaxReceiptBytes = 256 * 1024;
    private static readonly TimeSpan MaximumMarkerAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    private readonly IMaintenanceAttestationKeyProvider _maintenanceKeys;
    private readonly Func<string, string, SignedReleaseCohortValidation> _validateCohort;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _currentBootToken;
    private readonly Func<string> _proofDirectory;
    private readonly Action _afterTransactionCheck;

    internal Release1InstallReceiptWriter(
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        Func<string, string, SignedReleaseCohortValidation>? validateCohort = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? currentBootToken = null,
        Func<string>? proofDirectory = null,
        Action? afterTransactionCheck = null)
    {
        _maintenanceKeys = maintenanceKeys ??
            throw new ArgumentNullException(nameof(maintenanceKeys));
        _validateCohort = validateCohort ?? SignedReleaseCohortValidator.Validate;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _currentBootToken = currentBootToken ??
            Release1ConvergenceContract.CurrentBootToken;
        _proofDirectory = proofDirectory ??
            Release1MsiInstallMarkerStore.DefaultProofDirectory;
        _afterTransactionCheck = afterTransactionCheck ?? (static () => { });
    }

    internal static Release1InstallReceiptWriter CreateProduction() => new(
        MaintenanceAttestationKeyProvider.CreateProduction());

    internal Release1InstallReceiptWriteResult Write(
        string installDirectory,
        string dataDirectory,
        string releaseTag,
        string machineFingerprint,
        string maintenanceKeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineFingerprint);
        if (!LowerHex64(maintenanceKeyId))
            throw new InvalidDataException("Maintenance key identity is invalid.");

        var proofDirectory = _proofDirectory();
        // This gate is shared with the MSI Arm action. Hold it across cohort
        // validation, journal checks, signing, durable receipt write, and marker
        // consumption so repair/upgrade cannot begin between check and consume.
        using var proofLock = Release1MsiInstallMarkerTransaction.AcquireProofLock(
            proofDirectory);
        var validation = _validateCohort(installDirectory, releaseTag);
        if (!validation.IsValid || validation.Evidence is null)
            throw new InvalidDataException(
                "The installed signed release cohort cannot be proven: " +
                validation.Code);
        var evidence = validation.Evidence;
        if (evidence.OtaSigningKeyId != OtaUpdateTrust.LegacyV1KeyId)
            return new(true, false, "not_historic_v1_release");

        var registration = _maintenanceKeys.OpenExisting(machineFingerprint);
        if (!string.Equals(
                registration.Enrollment.KeyId,
                maintenanceKeyId,
                StringComparison.Ordinal) ||
            !MaintenanceAttestationKeyProvider.VerifyPossessionProof(
                registration,
                machineFingerprint))
            throw new InvalidDataException(
                "The enrolled maintenance TPM key does not match this workstation.");
        var receiptPath = Path.Combine(
            SafeDataDirectory(dataDirectory),
            Release1ConvergenceContract.InstallReceiptFileName);
        if (Release1MsiInstallMarkerTransaction.HasPendingJournal(proofDirectory) ||
            File.Exists(Path.Combine(
                installDirectory,
                FileMsiInstallerTransactionActivation.FileName)) ||
            File.Exists(Path.Combine(
                installDirectory,
                FileInstallerServiceHardeningJournal.FileName)))
            throw new InvalidDataException(
                "The MSI rollback transaction has not reached durable cleanup.");
        _afterTransactionCheck();
        if (!Release1MsiInstallMarkerStore.Exists(proofDirectory))
        {
            if (TryReadExisting(
                    receiptPath,
                    evidence,
                    machineFingerprint,
                    maintenanceKeyId,
                    registration.Enrollment.PublicKeySpki,
                    out var existing))
                return new(true, true, "already_written", existing);
            throw new InvalidDataException(
                "A fresh successful MSI install marker is required before Release 1 can be attested.");
        }

        var marker = Release1MsiInstallMarkerStore.Read(proofDirectory);
        var markerTime = ParseExactUtc(marker.InstallCompletedAtUtc);
        var now = _utcNow().ToUniversalTime();
        if (markerTime > now + MaximumFutureSkew ||
            markerTime < now - MaximumMarkerAge)
            throw new InvalidDataException(
                "The successful MSI install marker is outside the Release 1 campaign window.");
        if (!ReleaseTagsEquivalent(marker.InstalledReleaseTag, evidence.ReleaseTag) ||
            !FixedHexEquals(
                marker.MaintenanceHostSha256,
                evidence.MaintenanceHostSha256) ||
            !FixedHexEquals(
                marker.InstallerArtifactSha256,
                evidence.MsiArtifactSha256))
            throw new InvalidDataException(
                "The successful MSI install marker does not bind this signed release.");

        var currentBootToken = _currentBootToken();
        if (!FixedTextEquals(marker.BootTokenAtInstall, currentBootToken))
            throw new InvalidDataException(
                "The workstation rebooted before its Release 1 install receipt was sealed; reinstall Release 1 again.");

        var receipt = new Release1InstallReceipt(
            SchemaVersion: 1,
            Purpose: Release1ConvergenceContract.InstallReceiptPurpose,
            HostDigest: Release1ConvergenceContract.HostDigest(machineFingerprint),
            MaintenanceKeyId: maintenanceKeyId,
            InstalledReleaseTag: evidence.ReleaseTag,
            InstalledSourceSha: evidence.SourceCommit,
            InstallerType: Release1ConvergenceContract.MsiInstallerType,
            InstallerArtifactSha256: marker.InstallerArtifactSha256,
            ReleaseReceiptSha256: evidence.ReleaseReceiptSha256,
            ChecksumsSha256: evidence.ChecksumsSha256,
            ChecksumsSignatureSha256: evidence.ChecksumsSignatureSha256,
            InstalledCohort: new Dictionary<string, string>(
                evidence.InstalledCohort,
                StringComparer.Ordinal),
            InstallTransactionId: marker.InstallTransactionId,
            InstallCompletedAtUtc: marker.InstallCompletedAtUtc,
            BootIdAtInstall: Release1ConvergenceContract.BootIdFromToken(
                machineFingerprint,
                marker.BootTokenAtInstall),
            InstallMode: Release1ConvergenceContract.FullReinstallMode);
        var canonical = Release1ConvergenceContract.CanonicalBytes(receipt);
        var signed = _maintenanceKeys.Sign(
            machineFingerprint,
            maintenanceKeyId,
            canonical);
        if (!string.Equals(
                signed.Enrollment.KeyId,
                maintenanceKeyId,
                StringComparison.Ordinal) ||
            signed.Signature.Length != 64)
            throw new CryptographicException(
                "The maintenance TPM returned an invalid Release 1 signature.");

        var envelope = new SignedRelease1InstallReceipt(
            receipt,
            Release1ConvergenceContract.Base64Url(signed.Signature.Span),
            registration.Enrollment.PublicKeySpki);
        if (!VerifyEnvelope(envelope, registration.Enrollment.PublicKeySpki))
            throw new CryptographicException(
                "The Release 1 install receipt failed immediate signature verification.");

        var bytes = Release1ConvergenceContract.CanonicalBytes(envelope);
        if (bytes.Length is <= 0 or > MaxReceiptBytes)
            throw new InvalidDataException("Release 1 install receipt exceeds its bound.");
        WriteAtomic(receiptPath, bytes);
        EnsureRegularBoundedFile(receiptPath, MaxReceiptBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                bytes,
                File.ReadAllBytes(receiptPath)))
            throw new IOException("Release 1 install receipt durability check failed.");

        Release1MsiInstallMarkerStore.Consume(
            proofDirectory,
            marker.InstallTransactionId);
        return new(true, true, "written", envelope);
    }

    private static bool TryReadExisting(
        string path,
        SignedReleaseCohortEvidence evidence,
        string machineFingerprint,
        string maintenanceKeyId,
        string publicKeySpki,
        out SignedRelease1InstallReceipt? envelope)
    {
        envelope = null;
        try
        {
            EnsureRegularBoundedFile(path, MaxReceiptBytes);
            var raw = File.ReadAllBytes(path);
            var parsed = JsonSerializer.Deserialize<SignedRelease1InstallReceipt>(
                raw,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (parsed is null ||
                !CryptographicOperations.FixedTimeEquals(
                    raw,
                    Release1ConvergenceContract.CanonicalBytes(parsed)) ||
                !VerifyEnvelope(parsed, publicKeySpki))
                return false;
            var receipt = parsed.InstallReceipt;
            if (receipt.SchemaVersion != 1 ||
                receipt.Purpose != Release1ConvergenceContract.InstallReceiptPurpose ||
                receipt.HostDigest != Release1ConvergenceContract.HostDigest(
                    machineFingerprint) ||
                receipt.MaintenanceKeyId != maintenanceKeyId ||
                receipt.InstalledReleaseTag != evidence.ReleaseTag ||
                receipt.InstalledSourceSha != evidence.SourceCommit ||
                receipt.InstallerType != Release1ConvergenceContract.MsiInstallerType ||
                !FixedHexEquals(
                    receipt.InstallerArtifactSha256,
                    evidence.MsiArtifactSha256) ||
                !FixedHexEquals(
                    receipt.ReleaseReceiptSha256,
                    evidence.ReleaseReceiptSha256) ||
                !FixedHexEquals(
                    receipt.ChecksumsSha256,
                    evidence.ChecksumsSha256) ||
                !FixedHexEquals(
                    receipt.ChecksumsSignatureSha256,
                    evidence.ChecksumsSignatureSha256) ||
                receipt.InstallMode != Release1ConvergenceContract.FullReinstallMode ||
                !LowerHex64(receipt.InstallTransactionId) ||
                !LowerHex64(receipt.BootIdAtInstall) ||
                !InstalledCohortEquals(
                    receipt.InstalledCohort,
                    evidence.InstalledCohort))
                return false;
            _ = ParseExactUtc(receipt.InstallCompletedAtUtc);
            envelope = parsed;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or CryptographicException or FormatException or
            ArgumentException)
        {
            return false;
        }
    }

    private static bool InstalledCohortEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var expected) &&
            FixedHexEquals(pair.Value, expected));

    private static bool VerifyEnvelope(
        SignedRelease1InstallReceipt envelope,
        string publicKeySpkiBase64)
    {
        try
        {
            if (!string.Equals(
                    envelope.MaintenancePublicKeySpkiDerBase64,
                    publicKeySpkiBase64,
                    StringComparison.Ordinal))
                return false;
            var spki = Convert.FromBase64String(publicKeySpkiBase64);
            if (!string.Equals(
                    Convert.ToBase64String(spki),
                    publicKeySpkiBase64,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                    envelope.InstallReceipt.MaintenanceKeyId,
                    StringComparison.Ordinal))
                return false;
            var signature = Base64UrlDecode(
                envelope.InstallReceiptSignatureBase64Url);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out var consumed);
            return consumed == spki.Length &&
                   verifier.KeySize == 256 &&
                   signature.Length == 64 &&
                   verifier.VerifyData(
                       Release1ConvergenceContract.CanonicalBytes(
                           envelope.InstallReceipt),
                       signature,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        if (value.Length != 86 || value.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and not '-' and not '_'))
            throw new FormatException("Receipt signature is not canonical Base64Url.");
        return Convert.FromBase64String(
            value.Replace('-', '+').Replace('_', '/') + "==");
    }

    private static DateTimeOffset ParseExactUtc(string value) =>
        DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : throw new InvalidDataException(
                "MSI install completion time is not canonical UTC.");

    private static bool ReleaseTagsEquivalent(string left, string right) =>
        string.Equals(
            left.TrimStart('v', 'V'),
            right.TrimStart('v', 'V'),
            StringComparison.OrdinalIgnoreCase);

    private static bool FixedHexEquals(string left, string right) =>
        LowerHex64(left) && LowerHex64(right) &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));

    private static bool FixedTextEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string SafeDataDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("Release 1 data directory is invalid.");
        var directory = new DirectoryInfo(Path.GetFullPath(value));
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Release 1 data directory is unavailable.");
        return directory.FullName.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void EnsureRegularBoundedFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            file.Attributes.HasFlag(FileAttributes.Directory) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Release 1 receipt is not a regular bounded file.");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
