using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record OtaCohortAssemblyResult(
    bool Succeeded,
    string Code,
    NativeInstallPreparation? Preparation = null)
{
    public static OtaCohortAssemblyResult Success(NativeInstallPreparation preparation) =>
        new(true, "assembled", preparation);
    public static OtaCohortAssemblyResult Fail(string code) => new(false, code);
}

/// <summary>
/// Builds a same-volume immutable install stage from the SYSTEM-only durable
/// claim. Transition manifests update the four runtime executables while
/// retaining the already-trusted Maintenance host; full manifests replace all
/// five. App configuration is copied and stamped with the target version.
/// </summary>
internal sealed class NativeOtaCohortAssembler
{
    private static readonly string[] TrustReceiptNames =
    [
        MaintenanceContract.ReleaseChecksumsFileName,
        MaintenanceContract.ReleaseChecksumsSignatureFileName,
        MaintenanceContract.FieldReleaseReceiptFileName,
        MaintenanceContract.CurrentOtaManifestFileName,
        MaintenanceContract.CurrentOtaManifestSignatureFileName,
    ];

    private readonly Action<string> _lockInstallDirectory;
    private readonly Func<string, MaintenanceHostTrustResult> _verifyMaintenanceTrust;
    private readonly Func<string, AuthenticodePublisherTrust> _verifyAuthenticode;
    private readonly string? _updatePublicKeyOverride;

    public NativeOtaCohortAssembler(
        Action<string>? lockInstallDirectory = null,
        Func<string, MaintenanceHostTrustResult>? verifyMaintenanceTrust = null,
        Func<string, AuthenticodePublisherTrust>? verifyAuthenticode = null,
        string? updatePublicKeyOverride = null)
    {
        _lockInstallDirectory = lockInstallDirectory ?? ServiceInstaller.LockdownInstallDirectoryAcl;
        _verifyMaintenanceTrust = verifyMaintenanceTrust ?? MaintenanceHostTrustVerifier.Verify;
        _verifyAuthenticode = verifyAuthenticode ?? AuthenticodePublisherVerifier.Verify;
        _updatePublicKeyOverride = updatePublicKeyOverride;
    }

    public OtaCohortAssemblyResult Assemble(
        DurableUpdateClaim claim,
        string liveDirectory,
        string dataDirectory,
        string maintenanceRoot,
        Action? progress = null)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var transactionId = claim.Validated.Request.StagingId[..32];
        var preparation = NativeInstallCoordinator.CreatePreparation(
            liveDirectory,
            dataDirectory,
            maintenanceRoot,
            transactionId);
        try
        {
            // A runner may have died after assembly but before the transaction
            // journal was created. The exact claim-derived path is safe to
            // discard and deterministically rebuild after recovery proved no
            // active journal owns it.
            if (Directory.Exists(preparation.StagingDirectory))
                Directory.Delete(preparation.StagingDirectory, recursive: true);
            if (File.Exists(preparation.PreparedManifestPath))
                File.Delete(preparation.PreparedManifestPath);
            Directory.CreateDirectory(preparation.StagingDirectory);
            _lockInstallDirectory(preparation.StagingDirectory);

            foreach (var file in claim.Validated.Manifest.Files)
            {
                progress?.Invoke();
                CopyAndVerify(
                    Path.Combine(claim.PayloadDirectory, file.FileName),
                    Path.Combine(preparation.StagingDirectory, file.FileName),
                    file.Sha256);
            }

            var liveMaintenance = Path.Combine(liveDirectory, MaintenanceContract.ExecutableName);
            if (!claim.Validated.Manifest.IncludesMaintenance)
            {
                var currentTrust = _verifyMaintenanceTrust(liveMaintenance);
                if (!currentTrust.IsTrusted)
                    return OtaCohortAssemblyResult.Fail(
                        "current_maintenance_untrusted:" + currentTrust.Code);
                CopyRegularFile(
                    liveMaintenance,
                    Path.Combine(preparation.StagingDirectory, MaintenanceContract.ExecutableName));
            }

            CopyConfiguration(liveDirectory, preparation.StagingDirectory, claim.Validated.Manifest.Version);
            progress?.Invoke();
            CopyTrustReceipts(liveDirectory, preparation.StagingDirectory);
            if (claim.Validated.Manifest.IncludesMaintenance)
            {
                WriteAtomic(
                    Path.Combine(
                        preparation.StagingDirectory,
                        MaintenanceContract.CurrentOtaManifestFileName),
                    claim.Validated.Manifest.Canonical);
                WriteAtomic(
                    Path.Combine(
                        preparation.StagingDirectory,
                        MaintenanceContract.CurrentOtaManifestSignatureFileName),
                    claim.Validated.Request.ManifestSignature);
            }

            var stagedMaintenance = Path.Combine(
                preparation.StagingDirectory,
                MaintenanceContract.ExecutableName);
            var stagedTrust = _verifyMaintenanceTrust(stagedMaintenance);
            if (!stagedTrust.IsTrusted)
                return OtaCohortAssemblyResult.Fail(
                    "staged_maintenance_untrusted:" + stagedTrust.Code);

            if (!BinaryDownloader.WriteBinariesManifest(
                    preparation.StagingDirectory,
                    preparation.PreparedManifestPath))
                return OtaCohortAssemblyResult.Fail("prepared_manifest_incomplete");
            MaintenanceHostInstaller.WriteInstallState(
                preparation.StagingDirectory,
                preparation.PreparedManifestPath,
                claim.Validated.Manifest.Version);
            var cohort = MaintenanceCohortValidator.Validate(
                preparation.StagingDirectory,
                preparation.PreparedManifestPath,
                _updatePublicKeyOverride,
                _verifyAuthenticode,
                _verifyMaintenanceTrust);
            if (!cohort.IsValid)
                return OtaCohortAssemblyResult.Fail("assembled_cohort_invalid:" + cohort.Code);
            progress?.Invoke();
            return OtaCohortAssemblyResult.Success(preparation);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            JsonException or
            ArgumentException or
            InvalidDataException)
        {
            return OtaCohortAssemblyResult.Fail("assembly_failed:" + ex.GetType().Name);
        }
    }

    private static void CopyConfiguration(
        string liveDirectory,
        string stageDirectory,
        string targetVersion)
    {
        var source = Path.Combine(liveDirectory, "appsettings.json");
        if (!File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Installed appsettings is missing or invalid.");
        var root = JsonNode.Parse(
                       BoundedFile.ReadUtf8(source, 1024 * 1024),
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = false,
                           CommentHandling = JsonCommentHandling.Disallow,
                           MaxDepth = 32,
                       }) as JsonObject
                   ?? throw new InvalidDataException("Installed appsettings root is invalid.");
        var agent = root["Agent"] as JsonObject
                    ?? throw new InvalidDataException("Installed Agent settings are missing.");
        foreach (var secretName in agent
                     .Select(property => property.Key)
                     .Where(name => string.Equals(name, "ApiKey", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            agent.Remove(secretName);
        agent["Version"] = targetVersion.TrimStart('v');
        WriteAtomic(
            Path.Combine(stageDirectory, "appsettings.json"),
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CopyTrustReceipts(string liveDirectory, string stageDirectory)
    {
        foreach (var receiptName in TrustReceiptNames)
        {
            var source = Path.Combine(liveDirectory, receiptName);
            if (File.Exists(source))
                CopyRegularFile(source, Path.Combine(stageDirectory, receiptName));
        }
    }

    private static void CopyAndVerify(string source, string destination, string expectedHash)
    {
        CopyRegularFile(source, destination);
        using var stream = File.OpenRead(destination);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Copied OTA artifact hash mismatch.");
    }

    private static void CopyRegularFile(string source, string destination)
    {
        if (!File.Exists(source) ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("OTA source is not a regular file.");
        File.Copy(source, destination, overwrite: false);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
