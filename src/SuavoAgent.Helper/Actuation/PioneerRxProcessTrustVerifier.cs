using System.Diagnostics;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Helper.Actuation;

public sealed record PioneerRxApprovalLoadResult(
    bool Approved,
    string Code,
    PioneerRxProcessApprovalReceipt? Receipt)
{
    public static PioneerRxApprovalLoadResult Denied(string code) => new(false, code, null);
}

/// <summary>Loads and verifies the device-signed local PMS process approval.</summary>
public static class PioneerRxProcessApprovalLoader
{
    public const string ReceiptFileName = "pioneerrx-process-approval.json";
    public const string AuthorityStateFileName = "pioneerrx-approval-authority.json";
    public const string VendorCatalogFileName = "pioneerrx-vendor-catalog.json";
    public const string HighWaterProjectionFileName = "pioneerrx-approval-high-water.projection.json";
    private const int MaximumReceiptBytes = 64 * 1024;
    private const int MaximumSettingsBytes = 512 * 1024;

    public static PioneerRxApprovalLoadResult Load(
        string? receiptPath = null,
        string? appSettingsPath = null,
        DateTimeOffset? now = null,
        Func<string?>? authoritativeFingerprint = null,
        string? authorityStatePath = null,
        string? vendorCatalogPath = null,
        string? highWaterProjectionPath = null,
        bool verifyExecutable = true,
        IReadOnlyDictionary<string, string>? trustedCloudKeys = null)
    {
        receiptPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            PioneerRxApprovalMetadataAcl.AuthorityDirectoryName,
            ReceiptFileName);
        // Kept as a compatibility/test seam only. Production Helper deliberately cannot read
        // install-dir appsettings; its public trust anchor is the SYSTEM-published,
        // maintenance-signed high-water projection below.
        _ = appSettingsPath;
        authorityStatePath ??= Path.Combine(
            Path.GetDirectoryName(receiptPath) ?? string.Empty,
            AuthorityStateFileName);
        vendorCatalogPath ??= Path.Combine(
            Path.GetDirectoryName(receiptPath) ?? string.Empty,
            VendorCatalogFileName);
        highWaterProjectionPath ??= Path.Combine(
            Path.GetDirectoryName(receiptPath) ?? string.Empty,
            HighWaterProjectionFileName);

        var authorityDirectory = Path.GetDirectoryName(receiptPath) ?? string.Empty;
        if (OperatingSystem.IsWindows() &&
            (!PioneerRxApprovalMetadataAcl.ValidateDirectory(authorityDirectory) ||
             !PioneerRxApprovalMetadataAcl.ValidateFile(receiptPath, interactiveRead: true) ||
             !PioneerRxApprovalMetadataAcl.ValidateFile(authorityStatePath, interactiveRead: true) ||
             !PioneerRxApprovalMetadataAcl.ValidateFile(vendorCatalogPath, interactiveRead: true) ||
             !PioneerRxApprovalMetadataAcl.ValidateFile(
                 highWaterProjectionPath,
                 interactiveRead: true)))
            return PioneerRxApprovalLoadResult.Denied("approval_metadata_acl_invalid");

        if (!File.Exists(receiptPath))
            return PioneerRxApprovalLoadResult.Denied("pioneerrx_not_approved");
        var machineFingerprint = (authoritativeFingerprint ?? ReadAuthoritativeMachineFingerprint)();
        if (string.IsNullOrWhiteSpace(machineFingerprint))
            return PioneerRxApprovalLoadResult.Denied("authoritative_machine_identity_unavailable");

        if (!TryReadStrictJson(receiptPath, MaximumReceiptBytes, out PioneerRxProcessApprovalReceipt? receipt))
            return PioneerRxApprovalLoadResult.Denied("approval_receipt_unreadable");
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.PharmacyId) ||
            string.IsNullOrWhiteSpace(receipt.MaintenanceKeyId) ||
            !string.Equals(
                machineFingerprint,
                receipt.MachineFingerprint,
                StringComparison.Ordinal))
            return PioneerRxApprovalLoadResult.Denied("authoritative_machine_identity_mismatch");

        if (!TryReadStrictJson(
                vendorCatalogPath,
                MaximumReceiptBytes,
                out PioneerRxVendorIdentityCatalog? vendorCatalog))
            return PioneerRxApprovalLoadResult.Denied("approval_vendor_catalog_unreadable");
        var instant = now ?? DateTimeOffset.UtcNow;
        var approvalValid = trustedCloudKeys is null
            ? PioneerRxProcessApprovalContract.TryValidate(
                receipt,
                vendorCatalog,
                receipt.PharmacyId,
                machineFingerprint,
                receipt.MaintenanceKeyId,
                instant,
                out var code)
            : PioneerRxProcessApprovalContract.TryValidate(
                receipt,
                vendorCatalog,
                receipt.PharmacyId,
                machineFingerprint,
                receipt.MaintenanceKeyId,
                instant,
                trustedCloudKeys,
                out code);
        if (!approvalValid)
            return PioneerRxApprovalLoadResult.Denied(code);

        if (!TryReadStrictJson(
                authorityStatePath,
                MaximumReceiptBytes,
                out PioneerRxApprovalAuthorityState? authorityState))
            return PioneerRxApprovalLoadResult.Denied("approval_authority_unreadable");
        if (!TryReadStrictJson(
                highWaterProjectionPath,
                MaximumReceiptBytes,
                out PioneerRxApprovalHighWaterProjection? highWaterProjection))
            return PioneerRxApprovalLoadResult.Denied("approval_high_water_projection_unreadable");
        if (!PioneerRxApprovalMaintenanceContract.TryValidateProjection(
                highWaterProjection,
                receipt!,
                authorityState!,
                vendorCatalog!,
                instant,
                out code))
            return PioneerRxApprovalLoadResult.Denied(code);
        var authorityValid = trustedCloudKeys is null
            ? PioneerRxProcessApprovalContract.TryValidateAuthorityState(
                authorityState,
                receipt!,
                receipt.PharmacyId,
                machineFingerprint,
                instant,
                out code)
            : PioneerRxProcessApprovalContract.TryValidateAuthorityState(
                authorityState,
                receipt!,
                receipt.PharmacyId,
                machineFingerprint,
                instant,
                trustedCloudKeys,
                out code);
        if (!authorityValid)
            return PioneerRxApprovalLoadResult.Denied(code);

        var result = new PioneerRxApprovalLoadResult(true, "approved", receipt);
        if (!verifyExecutable) return result;
        var verifier = new PioneerRxProcessTrustVerifier(result);
        var executable = verifier.VerifyApprovedExecutable();
        return executable.Trusted
            ? result
            : PioneerRxApprovalLoadResult.Denied(executable.Code);
    }

    internal static bool TryReadInstalledIdentity(
        string path,
        out string pharmacyId,
        out string machineFingerprint,
        out string maintenanceKeyId)
    {
        pharmacyId = string.Empty;
        machineFingerprint = string.Empty;
        maintenanceKeyId = string.Empty;
        try
        {
            var bytes = ReadBoundedRegularFile(path, MaximumSettingsBytes);
            if (bytes is null) return false;
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
                AllowTrailingCommas = false,
            });
            if (!HasUniqueProperties(doc.RootElement) ||
                !doc.RootElement.TryGetProperty("Agent", out var agent) ||
                !HasUniqueProperties(agent))
                return false;
            if (
                agent.ValueKind != JsonValueKind.Object ||
                !agent.TryGetProperty("PharmacyId", out var pharmacy) ||
                pharmacy.ValueKind != JsonValueKind.String ||
                !agent.TryGetProperty("MachineFingerprint", out var fingerprint) ||
                fingerprint.ValueKind != JsonValueKind.String ||
                !agent.TryGetProperty("MaintenanceAttestationKeyId", out var keyId) ||
                keyId.ValueKind != JsonValueKind.String)
                return false;
            pharmacyId = pharmacy.GetString() ?? string.Empty;
            machineFingerprint = fingerprint.GetString() ?? string.Empty;
            maintenanceKeyId = keyId.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(pharmacyId) &&
                   !string.IsNullOrWhiteSpace(machineFingerprint) &&
                   maintenanceKeyId.Length == 64 &&
                   maintenanceKeyId.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadStrictJson<T>(string path, int maxBytes, out T? value)
    {
        value = default;
        try
        {
            var bytes = ReadBoundedRegularFile(path, maxBytes);
            if (bytes is null || !HasUniquePropertyNamesRecursive(bytes)) return false;
            value = JsonSerializer.Deserialize<T>(bytes, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 8,
                AllowTrailingCommas = false,
                ReadCommentHandling = JsonCommentHandling.Disallow,
            });
            return value is not null;
        }
        catch
        {
            value = default;
            return false;
        }
    }

    private static byte[]? ReadBoundedRegularFile(string path, int maxBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0) return null;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return null;
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maxBytes) return null;
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return stream.Position == stream.Length ? bytes : null;
    }

    private static bool HasUniquePropertyNamesRecursive(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
                AllowTrailingCommas = false,
            });
            return HasUniquePropertiesRecursive(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasUniquePropertiesRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) ||
                    !HasUniquePropertiesRecursive(property.Value))
                    return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                if (!HasUniquePropertiesRecursive(child)) return false;
        }
        return true;
    }

    private static bool HasUniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name)) return false;
        }
        return true;
    }

    private static string? ReadAuthoritativeMachineFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography",
                writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Revalidates the real PID against every immutable receipt field immediately
/// before attach and every live click/type. Process name is only a discovery
/// hint; exact canonical path, file bytes, Authenticode signer certificate and
/// version are the authority.
/// </summary>
public sealed class PioneerRxProcessTrustVerifier
{
    public readonly record struct Verdict(bool Trusted, string Code, string? CanonicalPath = null)
    {
        public static Verdict Allow(string path) => new(true, "trusted", path);
        public static Verdict Deny(string code, string? path = null) => new(false, code, path);
    }

    private readonly PioneerRxApprovalLoadResult _initialApproval;
    private readonly Func<PioneerRxApprovalLoadResult>? _refreshApproval;

    public PioneerRxProcessTrustVerifier(
        PioneerRxApprovalLoadResult approval,
        Func<PioneerRxApprovalLoadResult>? refreshApproval = null)
    {
        ArgumentNullException.ThrowIfNull(approval);
        _initialApproval = approval;
        _refreshApproval = refreshApproval;
    }

    public bool IsApproved
    {
        get
        {
            var approval = CurrentApproval();
            return approval.Approved && approval.Receipt is not null;
        }
    }

    public string ApprovalCode
    {
        get
        {
            var approval = CurrentApproval();
            return approval.Approved ? "approved" : approval.Code;
        }
    }

    public string ApprovedProcessName =>
        CurrentApproval().Receipt?.ProcessName ?? string.Empty;

    public IReadOnlySet<string> ApprovedBaaScopeTags =>
        (CurrentApproval().Receipt?.ApprovedBaaScopeTags ?? Array.Empty<string>())
        .ToFrozenSet(StringComparer.Ordinal);

    public Verdict VerifyApprovedExecutable()
    {
        var approval = CurrentApproval();
        if (!approval.Approved || approval.Receipt is null) return Verdict.Deny(approval.Code);
        return VerifyImageIdentity(
            approval.Receipt,
            approval.Receipt.ProcessName,
            approval.Receipt.CanonicalExecutablePath);
    }

    public Verdict VerifyResolvedProcess(int pid)
    {
        // Reload the SYSTEM-published authority generation immediately before every live mutation.
        // This makes a same-session revocation effective without trusting a Helper restart race.
        var approval = CurrentApproval();
        if (!approval.Approved || approval.Receipt is null) return Verdict.Deny(approval.Code);
        if (!OperatingSystem.IsWindows() || pid <= 0)
            return Verdict.Deny("pioneerrx_process_identity_unavailable");

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return Verdict.Deny("pioneerrx_process_gone");
            var imagePath = SuavoAgent.Helper.ProcessImageInterop.Get((uint)pid, out _);
            if (string.IsNullOrWhiteSpace(imagePath))
                return Verdict.Deny("pioneerrx_image_path_unavailable");
            var processName = process.ProcessName;
            return VerifyImageIdentity(
                approval.Receipt,
                processName,
                imagePath,
                () => !process.HasExited);
        }
        catch
        {
            return Verdict.Deny("pioneerrx_process_gone");
        }
    }

    private PioneerRxApprovalLoadResult CurrentApproval()
    {
        if (_refreshApproval is null) return _initialApproval;
        try { return _refreshApproval(); }
        catch { return PioneerRxApprovalLoadResult.Denied("approval_refresh_failed"); }
    }

    private static Verdict VerifyImageIdentity(
        PioneerRxProcessApprovalReceipt receipt,
        string processName,
        string imagePath,
        Func<bool>? processStillLive = null)
    {
        if (!string.Equals(
                ProtectedDesktopProcessClassifier.CanonicalProcessStem(processName),
                ProtectedDesktopProcessClassifier.CanonicalProcessStem(receipt.ProcessName),
                StringComparison.OrdinalIgnoreCase))
            return Verdict.Deny("pioneerrx_process_name_mismatch", imagePath);

        try
        {
            // Deny write/delete sharing for the whole identity read. Canonical path,
            // Authenticode, signer, version, and digest therefore describe one file
            // generation instead of a path that can be swapped between checks.
            using var stream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var canonical = SandboxProcessTrustVerifier.CanonicalizeExistingFile(
                stream.SafeFileHandle);
            if (canonical is null)
                return Verdict.Deny("pioneerrx_canonical_path_unavailable", imagePath);
            if (!string.Equals(
                    canonical,
                    receipt.CanonicalExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
                return Verdict.Deny("pioneerrx_executable_path_mismatch", canonical);
            if (!SandboxProcessTrustVerifier.VerifyAuthenticode(canonical) ||
                !SandboxProcessTrustVerifier.TryReadSignerEvidence(
                    canonical,
                    out var signerSubject,
                    out var signerCertificateSha256))
                return Verdict.Deny("pioneerrx_authenticode_invalid", canonical);

            var version = FileVersionInfo.GetVersionInfo(canonical);
            stream.Position = 0;
            var executableSha256 = Convert.ToHexString(
                SHA256.HashData(stream)).ToLowerInvariant();
            if (processStillLive is not null && !processStillLive())
                return Verdict.Deny("pioneerrx_process_gone", canonical);
            return VerifyEvidence(
                receipt,
                processName,
                canonical,
                executableSha256,
                signerSubject,
                signerCertificateSha256,
                version.ProductName ?? string.Empty,
                version.FileVersion ?? string.Empty)
                ? Verdict.Allow(canonical)
                : Verdict.Deny("pioneerrx_executable_evidence_mismatch", canonical);
        }
        catch
        {
            return Verdict.Deny("pioneerrx_executable_evidence_unavailable", imagePath);
        }
    }

    internal static bool VerifyEvidence(
        PioneerRxProcessApprovalReceipt receipt,
        string processName,
        string canonicalPath,
        string executableSha256,
        string signerSubject,
        string signerCertificateSha256,
        string productName,
        string fileVersion)
    {
        if (!string.Equals(
                ProtectedDesktopProcessClassifier.CanonicalProcessStem(processName),
                ProtectedDesktopProcessClassifier.CanonicalProcessStem(receipt.ProcessName),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(canonicalPath, receipt.CanonicalExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(signerSubject, receipt.AuthenticodeSignerSubject, StringComparison.Ordinal) ||
            !string.Equals(productName, receipt.ProductName, StringComparison.Ordinal) ||
            !string.Equals(fileVersion, receipt.FileVersion, StringComparison.Ordinal))
            return false;

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                       Convert.FromHexString(executableSha256),
                       Convert.FromHexString(receipt.ExecutableSha256)) &&
                   CryptographicOperations.FixedTimeEquals(
                       Convert.FromHexString(signerCertificateSha256),
                       Convert.FromHexString(receipt.SignerCertificateSha256));
        }
        catch
        {
            return false;
        }
    }
}
