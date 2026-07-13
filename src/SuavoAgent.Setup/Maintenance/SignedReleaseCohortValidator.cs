using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record SignedReleaseCohortValidation(bool IsValid, string Code)
{
    public static SignedReleaseCohortValidation Valid() => new(true, "valid");
    public static SignedReleaseCohortValidation Reject(string code) => new(false, code);
}

/// <summary>
/// Independent pre-quiesce proof for a fresh-install/reinstall staging directory.
/// It verifies the signed release receipt itself and binds every executable in
/// the exact five-member installed cohort to that receipt. The Setup release
/// artifact is deliberately mapped to the renamed Maintenance executable.
/// </summary>
internal static partial class SignedReleaseCohortValidator
{
    private const int MaxChecksumsBytes = 1024 * 1024;
    private const int MaxSignatureBytes = 1024;
    private const int MaxFieldReceiptBytes = 64 * 1024;
    private const int MaxStagingEntries = 4_096;

    [GeneratedRegex(
        "^(?<hash>[0-9A-Fa-f]{64})[ \\t]+\\*?(?<name>[^\\r\\n]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLineRegex();

    public static SignedReleaseCohortValidation Validate(
        string stagingDirectory,
        string expectedReleaseTag) =>
        Validate(
            stagingDirectory,
            expectedReleaseTag,
            BinaryDownloader.VerifyChecksumSignature,
            AuthenticodePublisherVerifier.Verify);

    internal static SignedReleaseCohortValidation Validate(
        string stagingDirectory,
        string expectedReleaseTag,
        Func<byte[], byte[], bool> verifySignature,
        Func<string, AuthenticodePublisherTrust> verifyAuthenticode)
    {
        ArgumentNullException.ThrowIfNull(verifySignature);
        ArgumentNullException.ThrowIfNull(verifyAuthenticode);
        try
        {
            if (string.IsNullOrWhiteSpace(stagingDirectory) ||
                !Path.IsPathFullyQualified(stagingDirectory) ||
                !Directory.Exists(stagingDirectory))
                return SignedReleaseCohortValidation.Reject("staging_directory_invalid");
            if (IsReparsePoint(stagingDirectory))
                return SignedReleaseCohortValidation.Reject(
                    "staging_directory_reparse_point");
            if (ContainsReparsePoint(stagingDirectory))
                return SignedReleaseCohortValidation.Reject(
                    "cohort_contains_reparse_point");
            if (!BinaryDownloader.IsValidReleaseTag(expectedReleaseTag))
                return SignedReleaseCohortValidation.Reject("release_tag_invalid");

            var checksumsPath = Path.Combine(
                stagingDirectory,
                MaintenanceContract.ReleaseChecksumsFileName);
            var signaturePath = Path.Combine(
                stagingDirectory,
                MaintenanceContract.ReleaseChecksumsSignatureFileName);
            if (!File.Exists(checksumsPath) || !File.Exists(signaturePath))
                return SignedReleaseCohortValidation.Reject("release_receipt_missing");
            if (IsReparsePoint(checksumsPath) || IsReparsePoint(signaturePath))
                return SignedReleaseCohortValidation.Reject("release_receipt_reparse_point");

            var checksumsBytes = ReadBounded(checksumsPath, MaxChecksumsBytes);
            var signatureBytes = ReadBounded(signaturePath, MaxSignatureBytes);
            if (!verifySignature(checksumsBytes, signatureBytes))
                return SignedReleaseCohortValidation.Reject("release_signature_invalid");

            var receipt = ParseChecksums(checksumsBytes);
            if (!receipt.IsValid)
                return SignedReleaseCohortValidation.Reject(receipt.Code);

            var fieldReceiptPath = Path.Combine(
                stagingDirectory,
                MaintenanceContract.FieldReleaseReceiptFileName);
            if (!receipt.Hashes!.TryGetValue(
                    MaintenanceContract.FieldReleaseReceiptFileName,
                    out var expectedReceiptHash) ||
                !File.Exists(fieldReceiptPath))
                return SignedReleaseCohortValidation.Reject("field_release_receipt_missing");
            if (IsReparsePoint(fieldReceiptPath) ||
                !HashMatches(fieldReceiptPath, expectedReceiptHash))
                return SignedReleaseCohortValidation.Reject("field_release_receipt_hash_mismatch");
            if (!receipt.Hashes.TryGetValue(
                    MaintenanceContract.SignedSetupArtifactName,
                    out var expectedSetupHash))
                return SignedReleaseCohortValidation.Reject(
                    "release_entry_missing:" + MaintenanceContract.SignedSetupArtifactName);
            var fieldReceipt = ValidateFieldReleaseReceipt(
                ReadBounded(fieldReceiptPath, MaxFieldReceiptBytes),
                expectedReleaseTag,
                expectedSetupHash);
            if (!fieldReceipt.IsValid) return fieldReceipt;
            if (!receipt.Hashes.ContainsKey($"update-manifest-{expectedReleaseTag}.txt") ||
                !receipt.Hashes.ContainsKey($"update-manifest-{expectedReleaseTag}.sig"))
                return SignedReleaseCohortValidation.Reject("release_manifest_receipt_missing");

            foreach (var installedName in BinaryDownloader.InstalledCohort)
            {
                var signedName = string.Equals(
                    installedName,
                    MaintenanceContract.ExecutableName,
                    StringComparison.OrdinalIgnoreCase)
                    ? MaintenanceContract.SignedSetupArtifactName
                    : installedName;
                if (!receipt.Hashes!.TryGetValue(signedName, out var expectedHash))
                    return SignedReleaseCohortValidation.Reject(
                        "release_entry_missing:" + signedName);

                var installedPath = Path.Combine(stagingDirectory, installedName);
                if (!File.Exists(installedPath))
                    return SignedReleaseCohortValidation.Reject(
                        "cohort_member_missing:" + installedName);
                if (IsReparsePoint(installedPath))
                    return SignedReleaseCohortValidation.Reject(
                        "cohort_member_reparse_point:" + installedName);
                if (!HashMatches(installedPath, expectedHash))
                    return SignedReleaseCohortValidation.Reject(
                        "cohort_hash_mismatch:" + installedName);
                var publisher = verifyAuthenticode(installedPath);
                if (!publisher.IsTrusted)
                    return SignedReleaseCohortValidation.Reject(
                        "cohort_publisher_invalid:" + installedName + ":" + publisher.Code);
            }

            var executableNames = Directory.EnumerateFiles(stagingDirectory, "*.exe")
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (executableNames.Count != BinaryDownloader.InstalledCohort.Count ||
                !BinaryDownloader.InstalledCohort.All(executableNames.Contains))
            {
                return SignedReleaseCohortValidation.Reject("cohort_executable_set_not_exact");
            }

            return SignedReleaseCohortValidation.Valid();
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            DecoderFallbackException or
            JsonException or
            ArgumentException)
        {
            return SignedReleaseCohortValidation.Reject(
                "cohort_unreadable:" + ex.GetType().Name);
        }
    }

    private static SignedReleaseCohortValidation ValidateFieldReleaseReceipt(
        byte[] bytes,
        string expectedReleaseTag,
        byte[] expectedSetupHash)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        var exact = new[]
        {
            "releaseTag", "version", "sourceCommit", "artifact",
            "artifactSha256", "authenticode", "checksumSignature",
            "manifestSignature", "track2QueenValidation", "rollbackArtifact",
        };
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Count() != exact.Length ||
            exact.Any(name => !root.TryGetProperty(name, out _)))
            return SignedReleaseCohortValidation.Reject("field_release_receipt_schema_invalid");

        string? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var releaseTag = Read(root, "releaseTag");
        var version = Read(root, "version");
        var sourceCommit = Read(root, "sourceCommit");
        var artifact = Read(root, "artifact");
        var artifactSha = Read(root, "artifactSha256");
        if (!TryReleaseVersion(
                expectedReleaseTag,
                out var currentVersion,
                out var expectedNumericVersion))
            return SignedReleaseCohortValidation.Reject("field_release_receipt_binding_invalid");
        if (!string.Equals(releaseTag, expectedReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(version, expectedNumericVersion, StringComparison.Ordinal) ||
            sourceCommit is null || sourceCommit.Length != 40 || !sourceCommit.All(IsLowerHex) ||
            !string.Equals(
                artifact,
                MaintenanceContract.SignedSetupArtifactName,
                StringComparison.Ordinal) ||
            !IsSha256(artifactSha) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(artifactSha!),
                expectedSetupHash) ||
            Read(root, "authenticode") != "required-valid" ||
            Read(root, "checksumSignature") != MaintenanceContract.ReleaseChecksumsSignatureFileName ||
            Read(root, "manifestSignature") != $"update-manifest-{expectedReleaseTag}.sig" ||
            Read(root, "track2QueenValidation") != "do-not-run-against-older-tags")
            return SignedReleaseCohortValidation.Reject("field_release_receipt_binding_invalid");

        var rollback = root.GetProperty("rollbackArtifact");
        var rollbackExact = new[] { "releaseTag", "artifact", "artifactSha256", "releaseUrl" };
        if (rollback.ValueKind != JsonValueKind.Object ||
            rollback.EnumerateObject().Count() != rollbackExact.Length ||
            rollbackExact.Any(name => !rollback.TryGetProperty(name, out _)))
            return SignedReleaseCohortValidation.Reject("field_release_rollback_invalid");
        var rollbackTag = Read(rollback, "releaseTag");
        var rollbackArtifact = Read(rollback, "artifact");
        var expectedRollbackArtifact = MaintenanceContract.SignedSetupArtifactName;
        var expectedRollbackUrl =
            $"https://github.com/{BinaryDownloader.RepoOwner}/{BinaryDownloader.RepoName}/releases/download/{rollbackTag}/{expectedRollbackArtifact}";
        if (!TryStableVersion(rollbackTag, out var rollbackVersion) ||
            rollbackVersion >= currentVersion ||
            !string.Equals(rollbackArtifact, expectedRollbackArtifact, StringComparison.Ordinal) ||
            !IsSha256(Read(rollback, "artifactSha256")) ||
            !string.Equals(Read(rollback, "releaseUrl"), expectedRollbackUrl, StringComparison.Ordinal))
            return SignedReleaseCohortValidation.Reject("field_release_rollback_invalid");

        return SignedReleaseCohortValidation.Valid();
    }

    private static bool TryStableVersion(string? tag, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(tag) || tag.Contains('-', StringComparison.Ordinal))
            return false;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    private static bool TryReleaseVersion(
        string? tag,
        out Version version,
        out string numericVersion)
    {
        version = new Version();
        numericVersion = string.Empty;
        if (!BinaryDownloader.IsValidReleaseTag(tag))
            return false;
        var normalized = tag!.TrimStart('v', 'V');
        var suffix = normalized.IndexOf('-');
        numericVersion = suffix >= 0 ? normalized[..suffix] : normalized;
        if (!Version.TryParse(numericVersion, out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(IsLowerHex);

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static ParsedChecksums ParseChecksums(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var match = ChecksumLineRegex().Match(line);
            if (!match.Success)
                return ParsedChecksums.Reject("release_checksums_malformed");
            var name = match.Groups["name"].Value.Trim();
            if (!IsSafeArtifactName(name) || hashes.ContainsKey(name))
                return ParsedChecksums.Reject("release_checksums_unsafe_or_duplicate_name");
            hashes[name] = Convert.FromHexString(match.Groups["hash"].Value);
        }
        return hashes.Count == 0
            ? ParsedChecksums.Reject("release_checksums_empty")
            : ParsedChecksums.Valid(hashes);
    }

    private static bool IsSafeArtifactName(string name) =>
        name.Length is > 0 and <= 160 &&
        string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) &&
        name.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_');

    private static bool HashMatches(string path, byte[] expectedHash)
    {
        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool ContainsReparsePoint(string root)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > MaxStagingEntries)
                    throw new InvalidDataException(
                        "Signed release stage exceeds its bounded entry count.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        return false;
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException("Signed release receipt has an invalid length.");
        return File.ReadAllBytes(path);
    }

    private sealed record ParsedChecksums(
        bool IsValid,
        string Code,
        IReadOnlyDictionary<string, byte[]>? Hashes)
    {
        public static ParsedChecksums Valid(IReadOnlyDictionary<string, byte[]> hashes) =>
            new(true, "valid", hashes);
        public static ParsedChecksums Reject(string code) => new(false, code, null);
    }
}
