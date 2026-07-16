using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Maintenance;

internal enum PioneerRxApprovalInstallPhase
{
    AuthorityInvalidated,
    HighWaterCommitted,
    CatalogInstalled,
    ReceiptInstalled,
    ProjectionInstalled,
    AuthorityInstalled,
    CompletionInstalled,
}

internal sealed record PioneerRxApprovalInstallExecutionResult(
    bool Succeeded,
    string Code,
    string? Outcome = null);

internal sealed record PioneerRxApprovalInstalledIdentity(
    string PharmacyId,
    string MachineFingerprint,
    string MaintenanceKeyId,
    string SqlServerCertificateSha256);

/// <summary>
/// LocalSystem-only commit path for live PioneerRx approval metadata. The old authority is removed
/// before the protected generation advances, and the replacement authority is published last. A
/// crash at any intermediate phase therefore leaves runtime actuation denied, never rolled back.
/// </summary>
internal static class PioneerRxApprovalInstallCoordinator
{
    private const int MaximumSettingsBytes = 1024 * 1024;

    internal static int Run(string[] args)
    {
        if (!TryReadExactRequestArgument(args, out var requestPath) ||
            !PioneerRxApprovalMaintenanceContract.IsExactRequestPath(requestPath))
            return 2;
        if (!OperatingSystem.IsWindows() || !IsLocalSystem()) return 3;

        try
        {
            using var transaction = InstallerTransactionLock.Acquire();
            var installDirectory = Path.GetDirectoryName(Environment.ProcessPath)
                                   ?? AppContext.BaseDirectory;
            if (!string.Equals(
                    Path.GetFileName(Environment.ProcessPath),
                    MaintenanceContract.ExecutableName,
                    StringComparison.OrdinalIgnoreCase))
                return 4;
            var settingsPath = Path.Combine(installDirectory, "appsettings.json");
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent");
            var certificatePath = Path.Combine(
                dataDirectory,
                PioneerRxSqlCertificatePinContract.InstalledFileName);
            var result = Install(
                requestPath,
                settingsPath,
                certificatePath,
                PioneerRxApprovalMaintenanceContract.DefaultAuthorityDirectory(),
                DateTimeOffset.UtcNow,
                RemoteCommandTrust.CreateProductionKeyRegistry(),
                MaintenanceAttestationKeyProvider.CreateProduction(),
                protectDirectory: PioneerRxApprovalMetadataAcl.ProtectDirectory,
                protectMetadata: PioneerRxApprovalMetadataAcl.ProtectMetadataFile,
                validateMetadata: path => PioneerRxApprovalMetadataAcl.ValidateFile(
                    path,
                    interactiveRead: true),
                protectHighWater: PioneerRxApprovalMetadataAcl.ProtectHighWaterFile,
                validateHighWater: path => PioneerRxApprovalMetadataAcl.ValidateFile(
                    path,
                    interactiveRead: false),
                validateAppSettings: path => ValidateProtectedInputFile(
                    path,
                    installDirectory,
                    "appsettings.json"),
                validateCertificate: path => ValidateProtectedInputFile(
                    path,
                    dataDirectory,
                    PioneerRxSqlCertificatePinContract.InstalledFileName));
            Console.WriteLine(result.Code);
            return result.Succeeded ? 0 : 4;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            CryptographicException or JsonException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine("pioneerrx_approval_system_install_failed");
            return 5;
        }
    }

    internal static PioneerRxApprovalInstallExecutionResult Install(
        string requestPath,
        string appSettingsPath,
        string certificatePath,
        string authorityDirectory,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        Action<string>? protectDirectory = null,
        Action<string>? protectMetadata = null,
        Func<string, bool>? validateMetadata = null,
        Action<string>? protectHighWater = null,
        Func<string, bool>? validateHighWater = null,
        Func<string, bool>? validateAppSettings = null,
        Func<string, bool>? validateCertificate = null,
        Action<PioneerRxApprovalInstallPhase>? afterPhase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appSettingsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorityDirectory);
        ArgumentNullException.ThrowIfNull(trustedCloudKeys);
        ArgumentNullException.ThrowIfNull(maintenanceKeys);
        protectDirectory ??= _ => { };
        protectMetadata ??= _ => { };
        validateMetadata ??= File.Exists;
        protectHighWater ??= _ => { };
        validateHighWater ??= File.Exists;
        validateAppSettings ??= File.Exists;
        validateCertificate ??= File.Exists;

        if (!validateAppSettings(appSettingsPath))
            return new(false, "pioneerrx_appsettings_acl_or_path_invalid");

        var requestBytes = BoundedFile.ReadBytes(
            requestPath,
            PioneerRxApprovalMaintenanceContract.MaximumJsonBytes);
        string requestFileDigest;
        PioneerRxApprovalInstallRequest request;
        try
        {
            requestFileDigest = LowerSha256(requestBytes);
            if (!PioneerRxApprovalMaintenanceContract.TryDeserializeRequest(
                    requestBytes,
                    out var parsed,
                    out var requestCode) ||
                parsed is null)
                return new(false, StableCode(requestCode, "pioneerrx_approval_request_invalid"));
            request = parsed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
        }

        var identity = TryReadInstalledIdentity(appSettingsPath);
        if (identity is null)
            return new(false, "pioneerrx_approval_identity_unavailable");
        var revoked = PioneerRxProcessApprovalContract.IsReceiptRevoked(
            request.Authority,
            request.Receipt.ReceiptId);
        var receiptAuthentic = revoked
            ? PioneerRxProcessApprovalContract.TryValidateHistoricalForRevocation(
                request.Receipt,
                request.VendorCatalog,
                identity.PharmacyId,
                identity.MachineFingerprint,
                identity.MaintenanceKeyId,
                now,
                trustedCloudKeys,
                out var code)
            : PioneerRxProcessApprovalContract.TryValidate(
                request.Receipt,
                request.VendorCatalog,
                identity.PharmacyId,
                identity.MachineFingerprint,
                identity.MaintenanceKeyId,
                now,
                trustedCloudKeys,
                out code);
        if (!receiptAuthentic)
            return new(false, StableCode(code, "pioneerrx_approval_invalid"));
        if (!PioneerRxProcessApprovalContract.TryValidateAuthorityDocument(
                request.Authority,
                identity.PharmacyId,
                identity.MachineFingerprint,
                now,
                trustedCloudKeys,
                out code))
            return new(false, StableCode(code, "pioneerrx_approval_authority_invalid"));
        if (!string.Equals(
                request.Authority.ActiveReceiptId,
                request.Receipt.ReceiptId,
                StringComparison.Ordinal) ||
            request.Authority.CurrentApprovalCounter != request.Receipt.ApprovalCounter)
            return new(false, "pioneerrx_approval_authority_generation_mismatch");

        if (!revoked)
        {
            if (!validateCertificate(certificatePath))
                return new(false, "sql_certificate_acl_or_path_invalid");
            if (!FixedHexEquals(
                    identity.SqlServerCertificateSha256,
                    request.Receipt.SqlServerCertificateSha256))
                return new(false, "sql_certificate_approval_digest_mismatch");
            if (!PioneerRxSqlCertificatePinContract.TryVerifyFile(
                    certificatePath,
                    request.Receipt.SqlServerCertificateSha256,
                    now,
                    out code))
                return new(false, StableCode(code, "sql_certificate_pin_invalid"));
        }

        var directory = new DirectoryInfo(Path.GetFullPath(authorityDirectory));
        if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return new(false, "pioneerrx_authority_directory_untrusted");
        Directory.CreateDirectory(directory.FullName);
        protectDirectory(directory.FullName);

        var receiptPath = Path.Combine(
            directory.FullName,
            PioneerRxApprovalMaintenanceContract.ReceiptFileName);
        var authorityPath = Path.Combine(
            directory.FullName,
            PioneerRxApprovalMaintenanceContract.AuthorityFileName);
        var catalogPath = Path.Combine(
            directory.FullName,
            PioneerRxVendorIdentityCatalogContract.InstalledFileName);
        var highWaterPath = Path.Combine(
            directory.FullName,
            PioneerRxApprovalMaintenanceContract.HighWaterFileName);
        var projectionPath = Path.Combine(
            directory.FullName,
            PioneerRxApprovalMaintenanceContract.HighWaterProjectionFileName);
        var completionPath = Path.Combine(
            directory.FullName,
            PioneerRxApprovalMaintenanceContract.CompletionFileName);

        var ledger = new PioneerRxApprovalHighWaterLedger(
            highWaterPath,
            protectHighWater,
            validateHighWater);
        var decision = ledger.Evaluate(request, now);
        if (decision.Kind == PioneerRxHighWaterDecisionKind.Rollback)
            return new(false, decision.Code);
        if (decision.Kind == PioneerRxHighWaterDecisionKind.Conflict)
            return new(false, decision.Code);

        // This is the visible fail-closed point. No replacement metadata becomes authoritative
        // until the old signed authority is gone. Completion is invalidated at the same time so
        // Core cannot acknowledge an interrupted generation swap.
        DeleteRegularFile(authorityPath);
        DeleteRegularFile(completionPath);
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.AuthorityInvalidated);

        if (decision.Kind is PioneerRxHighWaterDecisionKind.Advance or
            PioneerRxHighWaterDecisionKind.ExactReplay)
            ledger.Commit(decision.Proposed);
        var committed = ledger.Read()
                        ?? throw new InvalidDataException("PioneerRx high-water commit is missing.");
        if (!PioneerRxApprovalMaintenanceContract.HighWaterMatches(
                committed,
                request.Receipt,
                request.Authority,
                request.VendorCatalog))
            throw new InvalidDataException("PioneerRx high-water does not bind the request.");
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.HighWaterCommitted);

        WriteMetadataAtomic(
            catalogPath,
            request.VendorCatalog,
            protectMetadata,
            validateMetadata);
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.CatalogInstalled);

        WriteMetadataAtomic(
            receiptPath,
            request.Receipt,
            protectMetadata,
            validateMetadata);
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.ReceiptInstalled);

        var projection = SignProjection(
            committed,
            identity.MachineFingerprint,
            identity.MaintenanceKeyId,
            maintenanceKeys);
        WriteMetadataAtomic(
            projectionPath,
            projection,
            protectMetadata,
            validateMetadata);
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.ProjectionInstalled);

        // Authority is last: Helper/Core require every sibling and will deny while this file is absent.
        WriteMetadataAtomic(
            authorityPath,
            request.Authority,
            protectMetadata,
            validateMetadata);
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.AuthorityInstalled);

        ValidateInstalledSet(
            request,
            committed,
            projection,
            receiptPath,
            authorityPath,
            catalogPath,
            projectionPath,
            identity,
            now,
            trustedCloudKeys,
            validateMetadata);

        var outcome = revoked
            ? PioneerRxApprovalMaintenanceContract.RevokedOutcome
            : PioneerRxApprovalMaintenanceContract.InstalledOutcome;
        var completion = new PioneerRxApprovalInstallCompletion(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            request.ProtocolEpoch,
            request.CommandId,
            request.PayloadDigest,
            request.Receipt.ApprovalCounter,
            request.Receipt.ReceiptId,
            outcome,
            now.UtcDateTime.ToString(
                PioneerRxProcessApprovalContract.UtcTimestampFormat,
                System.Globalization.CultureInfo.InvariantCulture));
        WriteMetadataAtomic(
            completionPath,
            completion,
            protectMetadata,
            validateMetadata);
        afterPhase?.Invoke(PioneerRxApprovalInstallPhase.CompletionInstalled);

        ConsumeRequestIfUnchanged(requestPath, requestFileDigest);
        return new(true, outcome, outcome);
    }

    private static PioneerRxApprovalHighWaterProjection SignProjection(
        PioneerRxApprovalHighWaterState state,
        string machineFingerprint,
        string expectedMaintenanceKeyId,
        IMaintenanceAttestationKeyProvider maintenanceKeys)
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(
            PioneerRxApprovalMaintenanceContract.Canonical(state));
        try
        {
            var signed = maintenanceKeys.Sign(
                machineFingerprint,
                expectedMaintenanceKeyId,
                canonicalBytes);
            if (!string.Equals(
                    signed.Enrollment.KeyId,
                    expectedMaintenanceKeyId,
                    StringComparison.Ordinal) ||
                signed.Signature.Length != 64)
                throw new CryptographicException("Maintenance high-water signature is invalid.");
            return new PioneerRxApprovalHighWaterProjection(
                PioneerRxApprovalMaintenanceContract.SchemaVersion,
                state,
                signed.Enrollment.KeyId,
                PioneerRxProcessApprovalContract.Base64UrlEncode(signed.Signature.Span));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
        }
    }

    private static void ValidateInstalledSet(
        PioneerRxApprovalInstallRequest request,
        PioneerRxApprovalHighWaterState highWater,
        PioneerRxApprovalHighWaterProjection projection,
        string receiptPath,
        string authorityPath,
        string catalogPath,
        string projectionPath,
        PioneerRxApprovalInstalledIdentity identity,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedCloudKeys,
        Func<string, bool> validateMetadata)
    {
        foreach (var path in new[] { receiptPath, authorityPath, catalogPath, projectionPath })
            if (!validateMetadata(path))
                throw new UnauthorizedAccessException("PioneerRx installed metadata ACL is invalid.");

        var receipt = ReadStrict<PioneerRxProcessApprovalReceipt>(receiptPath);
        var authority = ReadStrict<PioneerRxApprovalAuthorityState>(authorityPath);
        var catalog = ReadStrict<PioneerRxVendorIdentityCatalog>(catalogPath);
        var installedProjection = ReadStrict<PioneerRxApprovalHighWaterProjection>(projectionPath);
        var receiptAuthentic = highWater.Revoked
            ? PioneerRxProcessApprovalContract.TryValidateHistoricalForRevocation(
                receipt,
                catalog,
                identity.PharmacyId,
                identity.MachineFingerprint,
                identity.MaintenanceKeyId,
                now,
                trustedCloudKeys,
                out _)
            : PioneerRxProcessApprovalContract.TryValidate(
                receipt,
                catalog,
                identity.PharmacyId,
                identity.MachineFingerprint,
                identity.MaintenanceKeyId,
                now,
                trustedCloudKeys,
                out _);
        if (!receiptAuthentic ||
            !PioneerRxProcessApprovalContract.TryValidateAuthorityDocument(
                authority,
                identity.PharmacyId,
                identity.MachineFingerprint,
                now,
                trustedCloudKeys,
                out _) ||
            !PioneerRxApprovalMaintenanceContract.HighWaterMatches(
                highWater,
                receipt,
                authority,
                catalog) ||
            !PioneerRxApprovalMaintenanceContract.TryValidateProjection(
                installedProjection,
                receipt,
                authority,
                catalog,
                now,
                out _) ||
            !Equals(projection, installedProjection) ||
            !string.Equals(request.CommandId, highWater.CommandId, StringComparison.Ordinal))
            throw new InvalidDataException("PioneerRx installed approval read-back failed.");
    }

    private static PioneerRxApprovalInstalledIdentity? TryReadInstalledIdentity(string path)
    {
        try
        {
            var bytes = BoundedFile.ReadBytes(path, MaximumSettingsBytes);
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16,
                });
                if (!Unique(document.RootElement) ||
                    !document.RootElement.TryGetProperty("Agent", out var agent) ||
                    agent.ValueKind != JsonValueKind.Object || !Unique(agent))
                    return null;
                var pharmacy = ReadString(agent, "PharmacyId");
                var fingerprint = ReadString(agent, "MachineFingerprint");
                var maintenanceKey = ReadString(agent, "MaintenanceAttestationKeyId");
                var sqlDigest = ReadString(agent, "SqlServerCertificateSha256");
                if (string.IsNullOrWhiteSpace(pharmacy) || string.IsNullOrWhiteSpace(fingerprint) ||
                    !LowerHex64(maintenanceKey) || !LowerHex64(sqlDigest))
                    return null;
                return new(pharmacy!, fingerprint!, maintenanceKey!, sqlDigest!);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static void WriteMetadataAtomic<T>(
        string path,
        T value,
        Action<string> protect,
        Func<string, bool> validate)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            PioneerRxApprovalMaintenanceContract.JsonOptions);
        try
        {
            if (bytes.Length is <= 0 or > PioneerRxApprovalMaintenanceContract.MaximumJsonBytes)
                throw new InvalidDataException("PioneerRx metadata exceeds its bound.");
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidDataException("PioneerRx metadata parent is missing.");
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
                protect(temporary);
                if (!validate(temporary))
                    throw new UnauthorizedAccessException("PioneerRx metadata temporary ACL is invalid.");
                File.Move(temporary, path, overwrite: true);
                protect(path);
                if (!validate(path))
                    throw new UnauthorizedAccessException("PioneerRx metadata installed ACL is invalid.");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static T ReadStrict<T>(string path)
    {
        var bytes = BoundedFile.ReadBytes(
            path,
            PioneerRxApprovalMaintenanceContract.MaximumJsonBytes);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            });
            if (!Unique(document.RootElement))
                throw new InvalidDataException("PioneerRx metadata has duplicate properties.");
            return JsonSerializer.Deserialize<T>(
                       bytes,
                       PioneerRxApprovalMaintenanceContract.JsonOptions)
                   ?? throw new InvalidDataException("PioneerRx metadata is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void DeleteRegularFile(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("PioneerRx active metadata entry is untrusted.");
        File.Delete(path);
    }

    private static void ConsumeRequestIfUnchanged(string path, string expectedFileDigest)
    {
        try
        {
            var bytes = BoundedFile.ReadBytes(
                path,
                PioneerRxApprovalMaintenanceContract.MaximumJsonBytes);
            try
            {
                if (FixedHexEquals(expectedFileDigest, LowerSha256(bytes))) File.Delete(path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static bool TryReadExactRequestArgument(string[] args, out string path)
    {
        path = string.Empty;
        return args.Length == 3 &&
               string.Equals(
                   args[0],
                   PioneerRxApprovalMaintenanceContract.InstallSwitch,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   args[1],
                   PioneerRxApprovalMaintenanceContract.RequestPathSwitch,
                   StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(path = args[2]);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (!Unique(child)) return false;
        }
        return true;
    }

    private static string LowerSha256(ReadOnlySpan<byte> bytes)
    {
        var digest = SHA256.HashData(bytes);
        try { return Convert.ToHexString(digest).ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(digest); }
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

    private static string StableCode(string? value, string fallback) =>
        value is { Length: > 0 and <= 80 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? value
            : fallback;

    [SupportedOSPlatform("windows")]
    private static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return string.Equals(
            identity.User?.Value,
            "S-1-5-18",
            StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    internal static bool ValidateProtectedInputFile(
        string path,
        string expectedDirectory,
        string expectedFileName)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.Ordinal) ||
                !string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(Path.Combine(expectedDirectory, expectedFileName)),
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path) ||
                (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !Directory.Exists(expectedDirectory) ||
                (File.GetAttributes(expectedDirectory) & FileAttributes.ReparsePoint) != 0)
                return false;
            var rules = new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            if (rules.Length != 3 || rules.Any(rule =>
                    rule.AccessControlType != AccessControlType.Allow ||
                    rule.PropagationFlags != PropagationFlags.None ||
                    rule.InheritanceFlags != InheritanceFlags.None))
                return false;
            return HasExactRule(rules, "S-1-5-18", FileSystemRights.FullControl) &&
                   HasExactRule(rules, "S-1-5-32-544", FileSystemRights.FullControl) &&
                   HasExactRule(
                       rules,
                       CoreServiceIdentity.ServiceSid,
                       FileSystemRights.ReadAndExecute);
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasExactRule(
        IEnumerable<FileSystemAccessRule> rules,
        string sid,
        FileSystemRights rights) => rules.Count(rule =>
        string.Equals(rule.IdentityReference.Value, sid, StringComparison.Ordinal) &&
        rule.FileSystemRights == rights) == 1;
}
