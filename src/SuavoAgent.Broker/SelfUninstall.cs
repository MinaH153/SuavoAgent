using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Broker;

internal enum SelfUninstallLaunchStatus
{
    NoRequest,
    UnsupportedHost,
    IdentityUnavailable,
    ClaimFailed,
    RequestRejected,
    MaintenanceMissing,
    MaintenanceUntrusted,
    MaintenanceStagingFailed,
    LaunchFailed,
    LaunchAccepted,
}

/// <summary>
/// Authenticated dashboard self-uninstall. Broker atomically claims the Core handoff,
/// independently verifies both cloud signatures and every local identity/digest binding,
/// then launches the trusted native maintenance host detached. Invalid request content is
/// never logged; rejected requests are consumed without privileged action.
/// </summary>
internal static class SelfUninstall
{
    internal const string SilentSwitch = "--silent";
    private const int MaxAppSettingsBytes = 1024 * 1024;

    internal static string RequestPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        SelfUninstallContract.RequestFileName);

    internal static string ClaimedRequestPath(string requestPath) => requestPath + ".claimed";

    internal static string MaintenanceExecutablePath(string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Path.IsPathFullyQualified(installDir))
            throw new ArgumentException("Install directory is required.", nameof(installDir));
        return Path.Combine(Path.GetFullPath(installDir), MaintenanceContract.ExecutableName);
    }

    internal static ProcessStartInfo BuildMaintenanceStartInfo(
        PrivilegedStagedExecutable staged,
        string? authenticatedRequestPath = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var executablePath = Path.GetFullPath(staged.ExecutablePath);
        var stagingDirectory = Path.GetFullPath(staged.DirectoryPath);
        if (!string.Equals(
                Path.GetDirectoryName(executablePath),
                stagingDirectory,
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Staged maintenance executable escaped its staging directory.",
                nameof(staged));
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = stagingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(MaintenanceContract.UninstallSwitch);
        startInfo.ArgumentList.Add(SilentSwitch);
        // Remote uninstall can never purge retained compliance evidence.
        startInfo.ArgumentList.Add(SelfUninstallContract.PreserveDataSwitch);
        startInfo.ArgumentList.Add(MaintenanceContract.ProtectedStagingSwitch);
        if (!string.IsNullOrWhiteSpace(authenticatedRequestPath))
        {
            startInfo.ArgumentList.Add(SelfUninstallContract.AuthenticatedRequestSwitch);
            startInfo.ArgumentList.Add(Path.GetFullPath(authenticatedRequestPath));
        }
        return startInfo;
    }

    internal static SelfUninstallLaunchStatus TryClaimAuthenticatedRequestAndLaunch(
        string installDir,
        ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return SelfUninstallLaunchStatus.UnsupportedHost;

        var requestPath = RequestPath();
        var claimedPath = ClaimedRequestPath(requestPath);
        if (!File.Exists(requestPath) && !File.Exists(claimedPath))
            return SelfUninstallLaunchStatus.NoRequest;

        if (!TryLoadInstalledIdentity(
                installDir,
                ReadAuthoritativeMachineFingerprint,
                out var agentId,
                out var fingerprint,
                out var maintenanceKeyId))
        {
            logger.LogError("Self-uninstall blocked: installed_identity_unavailable");
            return SelfUninstallLaunchStatus.IdentityUnavailable;
        }

        return TryClaimAuthenticatedRequestAndLaunch(
            requestPath,
            installDir,
            agentId,
            fingerprint,
            maintenanceKeyId,
            MaintenanceAttestationKeyProvider.CreateProduction(),
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            DateTimeOffset.UtcNow,
            logger,
            startInfo =>
            {
                using var process = Process.Start(startInfo);
                return process is not null;
            },
            MaintenanceHostTrustVerifier.Verify,
            (source, expectedSha256) => StageProductionMaintenance(
                source,
                expectedSha256,
                installDir,
                Path.GetDirectoryName(requestPath)!),
            PrivilegedExecutableStaging.VerifyMkmExecutable,
            PrivilegedExecutableStaging.TryCleanupDirectory);
    }

    internal static SelfUninstallLaunchStatus TryClaimAuthenticatedRequestAndLaunch(
        string requestPath,
        string installDir,
        string expectedAgentId,
        string expectedFingerprint,
        string expectedMaintenanceKeyId,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now,
        ILogger logger,
        Func<ProcessStartInfo, bool> launch,
        Func<string, MaintenanceHostTrustResult> verifyMaintenanceTrust,
        Func<string, string, PrivilegedStagedExecutable> stageMaintenance,
        Func<string, string, bool> verifyStagedMaintenance,
        Action<string?, string?> cleanupStagedMaintenance)
    {
        ArgumentNullException.ThrowIfNull(stageMaintenance);
        ArgumentNullException.ThrowIfNull(maintenanceKeys);
        ArgumentNullException.ThrowIfNull(verifyStagedMaintenance);
        ArgumentNullException.ThrowIfNull(cleanupStagedMaintenance);
        var claimedPath = ClaimedRequestPath(requestPath);
        if (!File.Exists(claimedPath))
        {
            try
            {
                File.Move(requestPath, claimedPath, overwrite: false);
            }
            catch (FileNotFoundException)
            {
                return SelfUninstallLaunchStatus.NoRequest;
            }
            catch (DirectoryNotFoundException)
            {
                return SelfUninstallLaunchStatus.NoRequest;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Self-uninstall blocked: request_claim_failed ({ErrorType})", ex.GetType().Name);
                return SelfUninstallLaunchStatus.ClaimFailed;
            }
        }

        SelfUninstallRequest? request;
        string rejectionCode;
        try
        {
            var info = new FileInfo(claimedPath);
            if (info.Length <= 0 || info.Length > SelfUninstallContract.MaxRequestBytes)
            {
                rejectionCode = "request_size_invalid";
                request = null;
            }
            else if (!SelfUninstallContract.TryDeserialize(
                         File.ReadAllText(claimedPath),
                         out request,
                         out rejectionCode))
            {
                request = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Self-uninstall blocked: request_read_failed ({ErrorType})", ex.GetType().Name);
            return SelfUninstallLaunchStatus.ClaimFailed;
        }

        if (request is null)
        {
            ConsumeRejectedClaim(claimedPath);
            logger.LogWarning("Self-uninstall request rejected: {Code}", rejectionCode);
            return SelfUninstallLaunchStatus.RequestRejected;
        }

        string exactClaim;
        try { exactClaim = File.ReadAllText(claimedPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Self-uninstall blocked: request_read_failed ({ErrorType})", ex.GetType().Name);
            return SelfUninstallLaunchStatus.ClaimFailed;
        }
        var acceptancePath = SelfUninstallAcceptanceContract.PathForClaim(claimedPath);
        var hadAcceptance = File.Exists(acceptancePath);
        var acceptance = EnsureBrokerAcceptance(
            claimedPath,
            exactClaim,
            request,
            expectedAgentId,
            expectedFingerprint,
            expectedMaintenanceKeyId,
            maintenanceKeys,
            trustedPublicKeys,
            now);
        if (!acceptance.IsValid)
        {
            if (!hadAcceptance) ConsumeRejectedClaim(claimedPath);
            logger.LogWarning("Self-uninstall request rejected: {Code}", acceptance.Code);
            return SelfUninstallLaunchStatus.RequestRejected;
        }

        string maintenancePath;
        try
        {
            maintenancePath = MaintenanceExecutablePath(installDir);
        }
        catch
        {
            logger.LogError("Self-uninstall blocked: maintenance_path_invalid");
            return SelfUninstallLaunchStatus.MaintenanceMissing;
        }

        if (!File.Exists(maintenancePath))
        {
            logger.LogError("Self-uninstall blocked: maintenance_host_missing");
            return SelfUninstallLaunchStatus.MaintenanceMissing;
        }

        // Bind staging to the exact hash authorized by the signed release/OTA
        // receipt. Keep the authenticated claim in place when trust proof is
        // unavailable so a repaired receipt/host can recover the command.
        MaintenanceHostTrustResult trust;
        try
        {
            trust = verifyMaintenanceTrust(maintenancePath);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Self-uninstall blocked: maintenance_trust_check_failed ({ErrorType})",
                ex.GetType().Name);
            return SelfUninstallLaunchStatus.MaintenanceUntrusted;
        }
        if (!trust.IsTrusted)
        {
            logger.LogError("Self-uninstall blocked: maintenance_host_untrusted ({Code})", trust.Code);
            return SelfUninstallLaunchStatus.MaintenanceUntrusted;
        }

        if (!IsSha256Hex(trust.ExecutableSha256))
        {
            logger.LogError(
                "Self-uninstall blocked: maintenance_trust_digest_missing");
            return SelfUninstallLaunchStatus.MaintenanceUntrusted;
        }

        PrivilegedStagedExecutable staged;
        try
        {
            staged = stageMaintenance(maintenancePath, trust.ExecutableSha256!);
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Self-uninstall blocked: maintenance_staging_failed ({ErrorType})",
                ex.GetType().Name);
            return SelfUninstallLaunchStatus.MaintenanceStagingFailed;
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = BuildMaintenanceStartInfo(staged, claimedPath);
        }
        catch (Exception ex)
        {
            cleanupStagedMaintenance(staged.DirectoryPath, staged.ExecutablePath);
            logger.LogError(
                "Self-uninstall blocked: staged_maintenance_path_invalid ({ErrorType})",
                ex.GetType().Name);
            return SelfUninstallLaunchStatus.MaintenanceStagingFailed;
        }

        try
        {
            // Keep this exact-byte, ACL, path, and signer proof immediately
            // adjacent to Process.Start. The live install-tree path is never
            // passed to the launch delegate.
            if (!verifyStagedMaintenance(
                    staged.ExecutablePath,
                    trust.ExecutableSha256!))
            {
                cleanupStagedMaintenance(
                    staged.DirectoryPath,
                    staged.ExecutablePath);
                logger.LogError(
                    "Self-uninstall blocked: staged_maintenance_untrusted");
                return SelfUninstallLaunchStatus.MaintenanceStagingFailed;
            }
            if (!launch(startInfo))
            {
                cleanupStagedMaintenance(
                    staged.DirectoryPath,
                    staged.ExecutablePath);
                logger.LogError("Self-uninstall blocked: maintenance_launch_failed");
                return SelfUninstallLaunchStatus.LaunchFailed;
            }
        }
        catch (Exception ex)
        {
            cleanupStagedMaintenance(
                staged.DirectoryPath,
                staged.ExecutablePath);
            logger.LogError("Self-uninstall blocked: maintenance_launch_exception ({ErrorType})", ex.GetType().Name);
            return SelfUninstallLaunchStatus.LaunchFailed;
        }

        // The claim is completion authority and audit evidence, not a launch latch.
        // Maintenance revalidates it, moves it into retained evidence, and only then
        // creates the device-signed terminal ticket. Deleting it here made truthful
        // completion impossible and lost recovery after a launch crash.
        logger.LogWarning("Authenticated self-uninstall maintenance launch accepted");
        return SelfUninstallLaunchStatus.LaunchAccepted;
    }

    private static PrivilegedStagedExecutable StageProductionMaintenance(
        string source,
        string expectedSha256,
        string installDirectory,
        string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Protected maintenance staging is Windows-only.");
        return PrivilegedExecutableStaging.StageVerifiedMkmExecutable(
            source,
            expectedSha256,
            PrivilegedExecutableStaging.UninstallFilePrefix,
            installDirectory,
            dataDirectory);
    }

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    internal static bool TryLoadInstalledIdentity(
        string installDir,
        Func<string?> machineFingerprintProvider,
        out string agentId,
        out string machineFingerprint,
        out string maintenanceKeyId)
    {
        agentId = string.Empty;
        machineFingerprint = string.Empty;
        maintenanceKeyId = string.Empty;
        try
        {
            var path = Path.Combine(Path.GetFullPath(installDir), "appsettings.json");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxAppSettingsBytes)
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Agent", out var agent) ||
                agent.ValueKind != JsonValueKind.Object ||
                !agent.TryGetProperty("AgentId", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !agent.TryGetProperty("MaintenanceAttestationKeyId", out var maintenanceElement) ||
                maintenanceElement.ValueKind != JsonValueKind.String)
                return false;

            agentId = idElement.GetString() ?? string.Empty;
            maintenanceKeyId = maintenanceElement.GetString() ?? string.Empty;
            // appsettings is writable by Core's current service identity and is not an
            // authority for machine binding. Read the actual HKLM MachineGuid instead.
            machineFingerprint = machineFingerprintProvider() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(agentId) &&
                   !string.IsNullOrWhiteSpace(machineFingerprint) &&
                   maintenanceKeyId.Length == 64;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            JsonException or
            ArgumentException)
        {
            return false;
        }
    }

    internal static string? ReadAuthoritativeMachineFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography",
                writable: false);
            return key?.GetValue("MachineGuid") is string guid &&
                   !string.IsNullOrWhiteSpace(guid)
                ? guid
                : null;
        }
        catch
        {
            // A missing/unreadable machine identity is a hard fail for remote uninstall.
            return null;
        }
    }

    private static void ConsumeRejectedClaim(string claimedPath)
    {
        try { File.Delete(claimedPath); } catch { }
    }

    private static SelfUninstallValidationResult EnsureBrokerAcceptance(
        string claimedPath,
        string exactClaim,
        SelfUninstallRequest request,
        string expectedAgentId,
        string expectedFingerprint,
        string expectedMaintenanceKeyId,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now)
    {
        var acceptancePath = SelfUninstallAcceptanceContract.PathForClaim(claimedPath);
        MaintenanceKeyRegistration registration;
        try { registration = maintenanceKeys.OpenExisting(expectedFingerprint); }
        catch { return SelfUninstallValidationResult.Reject("broker_acceptance_key_invalid"); }
        if (!string.Equals(
                registration.Enrollment.KeyId,
                expectedMaintenanceKeyId,
                StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("broker_acceptance_key_invalid");
        if (File.Exists(acceptancePath))
        {
            try
            {
                if (OperatingSystem.IsWindows() &&
                    !PioneerRxApprovalMetadataAcl.ValidateFile(
                        acceptancePath, interactiveRead: false))
                    return SelfUninstallValidationResult.Reject(
                        "broker_acceptance_acl_invalid");
                return SelfUninstallAcceptanceContract.TryDeserialize(
                           File.ReadAllText(acceptancePath), out var existing) && existing is not null
                    ? SelfUninstallAcceptanceContract.Validate(
                        existing, request, exactClaim, expectedAgentId,
                        expectedFingerprint, expectedMaintenanceKeyId, trustedPublicKeys)
                    : SelfUninstallValidationResult.Reject("broker_acceptance_invalid");
            }
            catch { return SelfUninstallValidationResult.Reject("broker_acceptance_read_failed"); }
        }

        var requestValidation = SelfUninstallContract.Validate(
            request, expectedAgentId, expectedFingerprint, trustedPublicKeys, now);
        if (!requestValidation.IsValid) return requestValidation;
        if (!SelfUninstallContract.TryReadCommandAuthorityData(
                request.DataJson, out _, out var expiresAt))
            return SelfUninstallValidationResult.Reject("broker_acceptance_time_invalid");
        try
        {
            var unsigned = new SelfUninstallBrokerAcceptance(
                SelfUninstallAcceptanceContract.SchemaVersion,
                request.CommandId,
                request.Nonce,
                request.AgentId,
                request.MachineFingerprint,
                RemoteCommandTrust.ComputeSha256Hex(exactClaim),
                SelfUninstallAcceptanceContract.FormatTimestamp(now),
                expiresAt,
                registration.Enrollment.KeyId,
                registration.Enrollment.PublicKeySpki,
                string.Empty);
            var signed = maintenanceKeys.Sign(
                expectedFingerprint,
                expectedMaintenanceKeyId,
                Encoding.UTF8.GetBytes(
                    SelfUninstallAcceptanceContract.BuildCanonical(unsigned)));
            var receipt = unsigned with
            {
                Signature = SelfUninstallAcceptanceContract.Base64UrlEncode(
                    signed.Signature.Span),
            };
            var validation = SelfUninstallAcceptanceContract.Validate(
                receipt, request, exactClaim, expectedAgentId, expectedFingerprint,
                expectedMaintenanceKeyId, trustedPublicKeys);
            if (!validation.IsValid) return validation;
            WriteAcceptanceDurably(acceptancePath, receipt);
            return validation;
        }
        catch { return SelfUninstallValidationResult.Reject("broker_acceptance_persist_failed"); }
    }

    private static void WriteAcceptanceDurably(
        string path,
        SelfUninstallBrokerAcceptance receipt)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                SelfUninstallAcceptanceContract.Serialize(receipt));
            using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows())
                PioneerRxApprovalMetadataAcl.ProtectHighWaterFile(temp);
            File.Move(temp, path, overwrite: false);
            if (OperatingSystem.IsWindows() &&
                !PioneerRxApprovalMetadataAcl.ValidateFile(
                    path, interactiveRead: false))
                throw new UnauthorizedAccessException(
                    "Broker acceptance ACL verification failed.");
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
