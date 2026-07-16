using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record SelfUninstallRecoveryContext(
    int SchemaVersion,
    string AgentId,
    string PharmacyId,
    string MachineFingerprint,
    string OrdinaryDeviceKeyId,
    string MaintenanceKeyId,
    string MaintenancePublicKeySpki,
    string CloudOrigin,
    string InstallDirectory,
    string DataDirectory,
    string MaintenanceVersion,
    string ValidatedAtUtc,
    string ClaimDigest,
    string Signature);

internal static partial class SelfUninstallCompletionFinalizer
{
    internal const string RecoveryContextFileName =
        "self-uninstall-recovery.context.json";
    private const int MaxRecoveryContextBytes = 16 * 1024;
    private const string RecoveryContextDomain = "suavo.self-uninstall-recovery.v1";
    private const string RecoveryTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly JsonSerializerOptions RecoveryJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    private static bool TryPrepareRecoveryContext(
        string claimPath,
        string installDirectory,
        string dataDirectory,
        SelfUninstallInstalledIdentity identity,
        SelfUninstallRequest request,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        DateTimeOffset now,
        string maintenanceVersion,
        out SelfUninstallRecoveryContext? context,
        out string code)
    {
        context = null;
        code = "recovery_context_invalid";
        var contextPath = Path.Combine(dataDirectory, RecoveryContextFileName);
        if (File.Exists(contextPath))
            return TryLoadAndValidateRecoveryContext(
                dataDirectory,
                trustedCommandKeys,
                out context,
                out _,
                out code);

        try
        {
            var exactClaim = ReadBoundedFile(
                claimPath,
                SelfUninstallContract.MaxRequestBytes);
            if (!TryValidateBrokerAcceptance(
                    claimPath,
                    request,
                    identity.AgentId,
                    identity.MachineFingerprint,
                    identity.MaintenanceKeyId,
                    trustedCommandKeys,
                    out var acceptance,
                    out code) || acceptance is null)
                return false;
            var normalizedInstall = NormalizeRecoveryPath(installDirectory);
            var normalizedData = NormalizeRecoveryPath(dataDirectory);
            var origin = identity.CloudOrigin.GetLeftPart(UriPartial.Authority);
            var maintenance = maintenanceKeys.OpenExisting(
                identity.MachineFingerprint);
            if (!string.Equals(
                    maintenance.Enrollment.KeyId,
                    identity.MaintenanceKeyId,
                    StringComparison.Ordinal))
            {
                code = "broker_acceptance_key_invalid";
                return false;
            }
            var unsigned = new SelfUninstallRecoveryContext(
                1,
                identity.AgentId,
                identity.PharmacyId,
                identity.MachineFingerprint,
                identity.DeviceKeyId,
                maintenance.Enrollment.KeyId,
                maintenance.Enrollment.PublicKeySpki,
                origin,
                normalizedInstall,
                normalizedData,
                NormalizeMaintenanceVersion(maintenanceVersion),
                DateTimeOffset.Parse(acceptance.AcceptedAtUtc).UtcDateTime.ToString(
                    RecoveryTimestampFormat, CultureInfo.InvariantCulture),
                LowerSha256(exactClaim),
                string.Empty);
            var signed = maintenanceKeys.Sign(
                identity.MachineFingerprint,
                maintenance.Enrollment.KeyId,
                Encoding.UTF8.GetBytes(BuildRecoveryContextCanonical(unsigned)));
            if (!string.Equals(
                    signed.Enrollment.KeyId,
                    maintenance.Enrollment.KeyId,
                    StringComparison.Ordinal) ||
                signed.Signature.Length != 64)
            {
                code = "device_key_binding_mismatch";
                return false;
            }
            context = unsigned with
            {
                Signature = Base64UrlEncode(signed.Signature.Span),
            };
            var validation = ValidateRecoveryContext(
                context,
                request,
                exactClaim,
                signed.Enrollment.PublicKeySpki,
                trustedCommandKeys);
            if (!validation.IsValid)
            {
                code = validation.Code;
                context = null;
                return false;
            }

            var json = JsonSerializer.Serialize(context, RecoveryJson);
            WriteAtomic(contextPath, json, MaxRecoveryContextBytes);
            if (!string.Equals(
                    json,
                    ReadBoundedFile(contextPath, MaxRecoveryContextBytes),
                    StringComparison.Ordinal))
                throw new IOException("Recovery context read-back mismatch.");
            code = "valid";
            return true;
        }
        catch
        {
            context = null;
            code = "recovery_context_persist_failed";
            return false;
        }
    }

    private static bool TryLoadAndValidateRecoveryContext(
        string directory,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        out SelfUninstallRecoveryContext? context,
        out SelfUninstallRequest? request,
        out string code)
    {
        context = null;
        request = null;
        code = "recovery_context_missing";
        try
        {
            var root = new DirectoryInfo(Path.GetFullPath(directory));
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                code = "recovery_directory_untrusted";
                return false;
            }
            var contextPath = Path.Combine(root.FullName, RecoveryContextFileName);
            var claimPath = Path.Combine(
                root.FullName,
                SelfUninstallContract.RequestFileName + ".claimed");
            var contextInfo = new FileInfo(contextPath);
            var claimInfo = new FileInfo(claimPath);
            if (!contextInfo.Exists ||
                contextInfo.Length is <= 0 or > MaxRecoveryContextBytes ||
                contextInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !claimInfo.Exists ||
                claimInfo.Length is <= 0 or > SelfUninstallContract.MaxRequestBytes ||
                claimInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;

            context = JsonSerializer.Deserialize<SelfUninstallRecoveryContext>(
                ReadBoundedFile(contextPath, MaxRecoveryContextBytes),
                RecoveryJson);
            var exactClaim = ReadBoundedFile(
                claimPath,
                SelfUninstallContract.MaxRequestBytes);
            if (context is null ||
                !SelfUninstallContract.TryDeserialize(
                    exactClaim,
                    out request,
                    out code) ||
                request is null)
                return false;

            if (!TryValidateBrokerAcceptance(
                    claimPath,
                    request,
                    context.AgentId,
                    context.MachineFingerprint,
                    context.MaintenanceKeyId,
                    trustedCommandKeys,
                    out _,
                    out code))
                return false;

            var validation = ValidateRecoveryContext(
                context,
                request,
                exactClaim,
                context.MaintenancePublicKeySpki,
                trustedCommandKeys);
            code = validation.Code;
            return validation.IsValid;
        }
        catch (JsonException)
        {
            code = "recovery_context_invalid_json";
            return false;
        }
        catch
        {
            code = "recovery_context_read_failed";
            return false;
        }
    }

    private static async Task<SelfUninstallFinalizationResult> CompleteTerminalCleanupAsync(
        SelfUninstallRecoveryContext expectedContext,
        SelfUninstallRequest request,
        ServiceInstaller.UninstallResult cleanup,
        IDeviceAttestationKeyProvider deviceKeys,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        Func<Uri, string, CancellationToken, Task<SelfUninstallFinalizePostResult>> post,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? retryDelay)
    {
        var evidence = CreateEvidence(cleanup, expectedContext.MaintenanceVersion);
        var terminal = SelfUninstallCompletionContract.ValidateCleanupEvidence(evidence);
        if (!terminal.IsValid || string.IsNullOrWhiteSpace(cleanup.RetainedDataPath))
            return SelfUninstallFinalizationResult.Pending("cleanup_not_terminal", cleanup);

        if (!TryLoadAndValidateRecoveryContext(
                cleanup.RetainedDataPath,
                trustedCommandKeys,
                out var retainedContext,
                out var retainedRequest,
                out var recoveryCode) ||
            !Equals(retainedContext, expectedContext) ||
            !Equals(retainedRequest, request))
            return SelfUninstallFinalizationResult.Pending(
                recoveryCode == "valid"
                    ? "retained_recovery_context_mismatch"
                    : recoveryCode,
                cleanup);

        SelfUninstallCompletionEnvelope envelope;
        DeviceMaintenanceSignature? completionSignature = null;
        try
        {
            envelope = SelfUninstallCompletionContract.CreateSignedEnvelope(
                request,
                expectedContext.PharmacyId,
                evidence,
                expectedContext.MaintenanceKeyId,
                bytes =>
                {
                    completionSignature = maintenanceKeys.Sign(
                        expectedContext.MachineFingerprint,
                        expectedContext.MaintenanceKeyId,
                        bytes);
                    return completionSignature.Signature.ToArray();
                },
                utcNow());
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending(
                "completion_signing_failed",
                cleanup);
        }
        if (completionSignature is null)
            return SelfUninstallFinalizationResult.Pending(
                "completion_signing_failed",
                cleanup);
        var validation = SelfUninstallCompletionContract.Validate(
            envelope,
            request,
            expectedContext.PharmacyId,
            completionSignature.Enrollment.KeyId,
            completionSignature.Enrollment.PublicKeySpki);
        if (!validation.IsValid)
            return SelfUninstallFinalizationResult.Pending(validation.Code, cleanup);

        try
        {
            PersistPending(
                cleanup.RetainedDataPath,
                new Uri(expectedContext.CloudOrigin, UriKind.Absolute),
                envelope);
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending(
                "completion_persist_failed",
                cleanup);
        }

        // Exact ticket bytes are flushed and read back before private device
        // authority is removed. A crash after this point replays the retained
        // ticket against the cloud enrollment without needing the private key.
        try
        {
            deviceKeys.DestroyForUninstall(
                expectedContext.MachineFingerprint,
                expectedContext.OrdinaryDeviceKeyId);
            maintenanceKeys.DestroyForUninstall(
                expectedContext.MachineFingerprint,
                expectedContext.MaintenanceKeyId);
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending(
                "device_key_destroy_failed",
                cleanup);
        }

        var finalization = await FinalizeRetainedAsync(
            cleanup.RetainedDataPath,
            post,
            cancellationToken,
            retryDelay).ConfigureAwait(false);
        return finalization with { Cleanup = cleanup };
    }

    private static SelfUninstallCompletionValidation ValidateRecoveryContext(
        SelfUninstallRecoveryContext context,
        SelfUninstallRequest request,
        string exactClaim,
        string devicePublicKeySpki,
        IReadOnlyDictionary<string, string> trustedCommandKeys)
    {
        if (context.SchemaVersion != 1 ||
            !SelfUninstallContract.IsCanonicalUuid(context.AgentId) ||
            !SelfUninstallContract.IsCanonicalUuid(context.PharmacyId) ||
            !IsSafeRecoveryToken(context.MachineFingerprint, 160) ||
            !IsLowerHex64(context.OrdinaryDeviceKeyId) ||
            !IsLowerHex64(context.MaintenanceKeyId) ||
            !IsCanonicalP256Spki(
                context.MaintenancePublicKeySpki,
                context.MaintenanceKeyId) ||
            string.Equals(
                context.OrdinaryDeviceKeyId,
                context.MaintenanceKeyId,
                StringComparison.Ordinal) ||
            !IsLowerHex64(context.ClaimDigest) ||
            !string.Equals(context.AgentId, request.AgentId, StringComparison.Ordinal) ||
            !string.Equals(
                context.MachineFingerprint,
                request.MachineFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                context.ClaimDigest,
                LowerSha256(exactClaim),
                StringComparison.Ordinal))
            return SelfUninstallCompletionValidation.Reject("recovery_context_binding_invalid");

        if (!TryValidateCloudOrigin(context.CloudOrigin, out var origin) ||
            !string.Equals(
                context.CloudOrigin,
                origin!.GetLeftPart(UriPartial.Authority),
                StringComparison.Ordinal) ||
            !TryNormalizeExactRecoveryPath(context.InstallDirectory) ||
            !TryNormalizeExactRecoveryPath(context.DataDirectory))
            return SelfUninstallCompletionValidation.Reject("recovery_context_path_invalid");
        try
        {
            if (!string.Equals(
                    NormalizeMaintenanceVersion(context.MaintenanceVersion),
                    context.MaintenanceVersion,
                    StringComparison.Ordinal))
                return SelfUninstallCompletionValidation.Reject(
                    "recovery_maintenance_version_invalid");
        }
        catch
        {
            return SelfUninstallCompletionValidation.Reject(
                "recovery_maintenance_version_invalid");
        }

        if (!DateTimeOffset.TryParseExact(
                context.ValidatedAtUtc,
                RecoveryTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var validatedAt))
            return SelfUninstallCompletionValidation.Reject(
                "recovery_validation_timestamp_invalid");
        var requestValidation = SelfUninstallContract.Validate(
            request,
            context.AgentId,
            context.MachineFingerprint,
            trustedCommandKeys,
            validatedAt);
        if (!requestValidation.IsValid)
            return SelfUninstallCompletionValidation.Reject(requestValidation.Code);
        if (!SelfUninstallCompletionContract.VerifyDeviceSignature(
                devicePublicKeySpki,
                BuildRecoveryContextCanonical(context),
                context.Signature))
            return SelfUninstallCompletionValidation.Reject(
                "recovery_context_signature_invalid");
        return SelfUninstallCompletionValidation.Valid();
    }

    private static bool TryValidateBrokerAcceptance(
        string claimPath,
        SelfUninstallRequest request,
        string agentId,
        string fingerprint,
        string maintenanceKeyId,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        out SelfUninstallBrokerAcceptance? acceptance,
        out string code)
    {
        acceptance = null;
        code = "broker_acceptance_missing";
        try
        {
            var exactClaim = ReadBoundedFile(
                claimPath, SelfUninstallContract.MaxRequestBytes);
            var acceptancePath = SelfUninstallAcceptanceContract.PathForClaim(claimPath);
            var json = ReadBoundedFile(
                acceptancePath, SelfUninstallAcceptanceContract.MaxReceiptBytes);
            if (!SelfUninstallAcceptanceContract.TryDeserialize(json, out acceptance) ||
                acceptance is null)
            {
                code = "broker_acceptance_invalid";
                return false;
            }
            var validation = SelfUninstallAcceptanceContract.Validate(
                acceptance, request, exactClaim, agentId, fingerprint,
                maintenanceKeyId, trustedCommandKeys);
            code = validation.Code;
            return validation.IsValid;
        }
        catch
        {
            code = "broker_acceptance_missing";
            return false;
        }
    }

    private static string BuildRecoveryContextCanonical(
        SelfUninstallRecoveryContext context) =>
        string.Join('|',
            RecoveryContextDomain,
            context.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            context.AgentId,
            context.PharmacyId,
            context.MachineFingerprint,
            context.OrdinaryDeviceKeyId,
            context.MaintenanceKeyId,
            Base64UrlEncode(Encoding.UTF8.GetBytes(context.MaintenancePublicKeySpki)),
            Base64UrlEncode(Encoding.UTF8.GetBytes(context.CloudOrigin)),
            Base64UrlEncode(Encoding.UTF8.GetBytes(context.InstallDirectory)),
            Base64UrlEncode(Encoding.UTF8.GetBytes(context.DataDirectory)),
            context.MaintenanceVersion,
            context.ValidatedAtUtc,
            context.ClaimDigest);

    private static string NormalizeRecoveryPath(string value) =>
        Path.GetFullPath(value).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

    private static bool TryNormalizeExactRecoveryPath(string value)
    {
        try
        {
            return value.Length is > 0 and <= 1_024 &&
                   !value.Any(character => character == '|' || char.IsControl(character)) &&
                   Path.IsPathFullyQualified(value) &&
                   string.Equals(
                       value,
                       NormalizeRecoveryPath(value),
                       OperatingSystem.IsWindows()
                           ? StringComparison.OrdinalIgnoreCase
                           : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static bool IsSafeRecoveryToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalP256Spki(string value, string expectedKeyId)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
                return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            return consumed == bytes.Length && key.KeySize == 256 &&
                   string.Equals(
                       Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                       expectedKeyId,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string LowerSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
