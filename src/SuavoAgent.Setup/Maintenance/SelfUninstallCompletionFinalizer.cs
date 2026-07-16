using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record SelfUninstallFinalizePostResult(
    bool IsSuccessStatusCode,
    string ResponseBody);

internal sealed record SelfUninstallFinalizationResult(
    bool IsFinalized,
    string Code,
    ServiceInstaller.UninstallResult? Cleanup = null)
{
    internal static SelfUninstallFinalizationResult Finalized(
        ServiceInstaller.UninstallResult? cleanup = null) =>
        new(true, "finalized", cleanup);

    internal static SelfUninstallFinalizationResult Pending(
        string code,
        ServiceInstaller.UninstallResult? cleanup = null) =>
        new(false, code, cleanup);
}

internal sealed record SelfUninstallInstalledIdentity(
    string AgentId,
    string PharmacyId,
    string MachineFingerprint,
    string DeviceKeyId,
    string MaintenanceKeyId,
    Uri CloudOrigin);

/// <summary>
/// Owns the only truthful terminal transition for dashboard self-uninstall:
/// independently revalidate the signed claim, prove zero runtime residue, sign
/// exact cleanup evidence with the active device key, durably retain that ticket,
/// destroy private device authority, then POST the same bytes without HMAC.
/// </summary>
internal static partial class SelfUninstallCompletionFinalizer
{
    internal const string OriginFileName = "self-uninstall-completion.origin";
    internal const string CloudReceiptFileName = "self-uninstall-completion.cloud-receipt.json";
    private const int MaxAppSettingsBytes = 1024 * 1024;
    private const int MaxOriginBytes = 512;
    private const int MaxRetentionDirectories = 128;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    internal static async Task<SelfUninstallFinalizationResult> ExecuteProductionAsync(
        string claimPath,
        string installDirectory,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var identity = ReadInstalledIdentity(
            installDirectory,
            dataDirectory,
            ReadAuthoritativeMachineFingerprint);
        if (identity is null)
            return SelfUninstallFinalizationResult.Pending("installed_identity_invalid");
        using var http = CreateHttpClient();
        return await ExecuteAsync(
            claimPath,
            installDirectory,
            dataDirectory,
            identity,
            DeviceAttestationKeyProvider.CreateProduction(),
            MaintenanceAttestationKeyProvider.CreateProduction(),
            () => ServiceInstaller.Uninstall(
                installDirectory,
                dataDirectory,
                purgeRetainedData: false),
            (origin, body, ct) => PostAsync(http, origin, body, ct),
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            () => DateTimeOffset.UtcNow,
            CurrentMaintenanceVersion(),
            cancellationToken,
            retryDelay: null).ConfigureAwait(false);
    }

    internal static async Task<SelfUninstallFinalizationResult> ExecuteAsync(
        string claimPath,
        string installDirectory,
        string dataDirectory,
        SelfUninstallInstalledIdentity identity,
        IDeviceAttestationKeyProvider deviceKeys,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        Func<ServiceInstaller.UninstallResult> uninstall,
        Func<Uri, string, CancellationToken, Task<SelfUninstallFinalizePostResult>> post,
        IReadOnlyDictionary<string, string> trustedCommandKeys,
        Func<DateTimeOffset> utcNow,
        string maintenanceVersion,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(deviceKeys);
        ArgumentNullException.ThrowIfNull(maintenanceKeys);
        ArgumentNullException.ThrowIfNull(uninstall);
        ArgumentNullException.ThrowIfNull(post);
        if (!IsExactClaimPath(claimPath, dataDirectory))
            return SelfUninstallFinalizationResult.Pending("claim_path_invalid");
        if (!TryReadClaim(claimPath, out var request, out var claimCode))
            return SelfUninstallFinalizationResult.Pending(claimCode);

        if (!TryValidateBrokerAcceptance(
                claimPath,
                request!,
                identity.AgentId,
                identity.MachineFingerprint,
                identity.MaintenanceKeyId,
                trustedCommandKeys,
                out _,
                out var acceptanceCode))
            return SelfUninstallFinalizationResult.Pending(acceptanceCode);

        if (!TryPrepareRecoveryContext(
                claimPath,
                installDirectory,
                dataDirectory,
                identity,
                request!,
                maintenanceKeys,
                trustedCommandKeys,
                utcNow(),
                maintenanceVersion,
                out var recoveryContext,
                out var recoveryCode) ||
            recoveryContext is null)
            return SelfUninstallFinalizationResult.Pending(recoveryCode);

        ServiceInstaller.UninstallResult cleanup;
        try
        {
            cleanup = uninstall();
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending("cleanup_failed");
        }

        return await CompleteTerminalCleanupAsync(
            recoveryContext,
            request!,
            cleanup,
            deviceKeys,
            maintenanceKeys,
            trustedCommandKeys,
            post,
            utcNow,
            cancellationToken,
            retryDelay).ConfigureAwait(false);
    }

    internal static SelfUninstallCleanupEvidence CreateEvidence(
        ServiceInstaller.UninstallResult cleanup,
        string maintenanceVersion) =>
        SelfUninstallCompletionContract.CreateCleanupEvidence(
            NormalizeMaintenanceVersion(maintenanceVersion),
            servicesAbsent: cleanup.ServicesRemaining == 0,
            scheduledUninstallTaskAbsent: cleanup.ScheduledUninstallTaskAbsent,
            protocolRegistrationAbsent: cleanup.ProtocolRegistrationAbsent,
            arpRegistrationAbsent: cleanup.ArpRegistrationAbsent,
            installDirectoryAbsent: cleanup.InstallDirRemoved,
            runtimeDirectoryAbsent: cleanup.DataDirRemoved,
            retainedEvidencePresent: cleanup.DataPreserved && cleanup.RetainedEvidencePresent,
            operationalCredentialsAbsent: cleanup.OperationalCredentialsAbsent);

    internal static async Task<SelfUninstallFinalizationResult> ReplayPendingBeforePairingAsync(
        string retentionRoot,
        IDeviceAttestationKeyProvider deviceKeys,
        IMaintenanceAttestationKeyProvider maintenanceKeys,
        Func<Uri, string, CancellationToken, Task<SelfUninstallFinalizePostResult>> post,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        Func<SelfUninstallRecoveryContext, string, ServiceInstaller.UninstallResult>?
            recoveredCleanup = null,
        IReadOnlyDictionary<string, string>? trustedCommandKeys = null)
    {
        trustedCommandKeys ??= RemoteCommandTrust.CreateProductionKeyRegistry();
        if (!Directory.Exists(retentionRoot))
            return SelfUninstallFinalizationResult.Finalized();
        string[] directories;
        try
        {
            var root = new DirectoryInfo(Path.GetFullPath(retentionRoot));
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return SelfUninstallFinalizationResult.Pending("retention_root_untrusted");
            var candidates = root.EnumerateDirectories(
                    "retained-*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(directory => directory.Name, StringComparer.Ordinal)
                .Take(MaxRetentionDirectories + 1)
                .ToArray();
            if (candidates.Any(directory =>
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                return SelfUninstallFinalizationResult.Pending(
                    "retention_directory_untrusted");
            directories = candidates.Select(directory => directory.FullName).ToArray();
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending("retention_scan_failed");
        }
        if (directories.Length > MaxRetentionDirectories)
            return SelfUninstallFinalizationResult.Pending("retention_scan_limit_exceeded");

        foreach (var directory in directories)
        {
            var pendingPath = Path.Combine(
                directory,
                SelfUninstallCompletionContract.PendingFileName);
            if (!File.Exists(pendingPath))
            {
                var incompleteClaim = Path.Combine(
                    directory,
                    SelfUninstallContract.RequestFileName + ".claimed");
                var recoveryContext = Path.Combine(
                    directory,
                    RecoveryContextFileName);
                var finalizedTicket = Path.Combine(
                    directory,
                    SelfUninstallCompletionContract.FinalizedFileName);
                if (File.Exists(incompleteClaim) && !File.Exists(finalizedTicket))
                {
                    if (!File.Exists(recoveryContext))
                        return SelfUninstallFinalizationResult.Pending(
                            "incomplete_uninstall_requires_recovery");
                    if (!TryLoadAndValidateRecoveryContext(
                            directory,
                            trustedCommandKeys,
                            out var context,
                            out var request,
                            out var incompleteRecoveryCode) ||
                        context is null || request is null ||
                        !IsExpectedRetentionDirectory(retentionRoot, directory, context))
                        return SelfUninstallFinalizationResult.Pending(
                            incompleteRecoveryCode == "valid"
                                ? "recovery_retention_binding_invalid"
                                : incompleteRecoveryCode);

                    ServiceInstaller.UninstallResult cleanup;
                    try
                    {
                        cleanup = (recoveredCleanup ?? ProbeRecoveredTerminalCleanup)(
                            context,
                            directory);
                    }
                    catch
                    {
                        return SelfUninstallFinalizationResult.Pending(
                            "recovery_cleanup_probe_failed");
                    }
                    var recovered = await CompleteTerminalCleanupAsync(
                        context,
                        request,
                        cleanup,
                        deviceKeys,
                        maintenanceKeys,
                        trustedCommandKeys,
                        post,
                        () => DateTimeOffset.UtcNow,
                        cancellationToken,
                        retryDelay).ConfigureAwait(false);
                    if (!recovered.IsFinalized) return recovered;
                    continue;
                }
                continue;
            }
            var load = TryLoadPending(directory, out var envelope, out _, out var code);
            if (!load)
                return SelfUninstallFinalizationResult.Pending(code);

            if (!TryLoadAndValidateRecoveryContext(
                    directory,
                    trustedCommandKeys,
                    out var retainedContext,
                    out _,
                    out var recoveryCode) ||
                retainedContext is null ||
                !string.Equals(
                    retainedContext.MaintenanceKeyId,
                    envelope!.Ticket.DeviceKeyId,
                    StringComparison.Ordinal))
                return SelfUninstallFinalizationResult.Pending(
                    recoveryCode == "valid"
                        ? "replay_recovery_context_mismatch"
                        : recoveryCode);
            var replaySignature = SelfUninstallCompletionContract.ValidateReplaySignature(
                envelope,
                retainedContext.MaintenancePublicKeySpki);
            if (!replaySignature.IsValid)
                return SelfUninstallFinalizationResult.Pending(replaySignature.Code);

            try
            {
                deviceKeys.DestroyForUninstall(
                    envelope.Ticket.MachineFingerprint,
                    retainedContext.OrdinaryDeviceKeyId);
                maintenanceKeys.DestroyForUninstall(
                    envelope!.Ticket.MachineFingerprint,
                    envelope.Ticket.DeviceKeyId);
            }
            catch
            {
                return SelfUninstallFinalizationResult.Pending("replay_device_key_destroy_failed");
            }

            var finalized = await FinalizeRetainedAsync(
                directory,
                post,
                cancellationToken,
                retryDelay).ConfigureAwait(false);
            if (!finalized.IsFinalized) return finalized;
        }
        return SelfUninstallFinalizationResult.Finalized();
    }

    internal static async Task<SelfUninstallFinalizationResult> ReplayProductionBeforePairingAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return SelfUninstallFinalizationResult.Finalized();
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        var retentionRoot = ServiceInstaller.DefaultRetentionRoot(dataDirectory);
        using var http = CreateHttpClient();
        var activeRecovery = await ResumeActiveRecoveryBeforePairingAsync(
            dataDirectory,
            DeviceAttestationKeyProvider.CreateProduction(),
            MaintenanceAttestationKeyProvider.CreateProduction(),
            (origin, body, ct) => PostAsync(http, origin, body, ct),
            cancellationToken).ConfigureAwait(false);
        if (!activeRecovery.IsFinalized)
            return activeRecovery;
        return await ReplayPendingBeforePairingAsync(
            retentionRoot,
            DeviceAttestationKeyProvider.CreateProduction(),
            MaintenanceAttestationKeyProvider.CreateProduction(),
            (origin, body, ct) => PostAsync(http, origin, body, ct),
            cancellationToken,
            retryDelay: null).ConfigureAwait(false);
    }

    private static async Task<SelfUninstallFinalizationResult>
        ResumeActiveRecoveryBeforePairingAsync(
            string dataDirectory,
            IDeviceAttestationKeyProvider deviceKeys,
            IMaintenanceAttestationKeyProvider maintenanceKeys,
            Func<Uri, string, CancellationToken, Task<SelfUninstallFinalizePostResult>> post,
            CancellationToken cancellationToken)
    {
        var contextPath = Path.Combine(dataDirectory, RecoveryContextFileName);
        var claimPath = Path.Combine(
            dataDirectory,
            SelfUninstallContract.RequestFileName + ".claimed");
        if (!File.Exists(contextPath) && !File.Exists(claimPath))
            return SelfUninstallFinalizationResult.Finalized();
        if (!File.Exists(contextPath) || !File.Exists(claimPath))
            return SelfUninstallFinalizationResult.Pending(
                "incomplete_uninstall_requires_recovery");
        var keys = RemoteCommandTrust.CreateProductionKeyRegistry();
        if (!TryLoadAndValidateRecoveryContext(
                dataDirectory,
                keys,
                out var context,
                out var request,
                out var code) ||
            context is null || request is null ||
            !string.Equals(
                NormalizeRecoveryPath(dataDirectory),
                context.DataDirectory,
                StringComparison.OrdinalIgnoreCase))
            return SelfUninstallFinalizationResult.Pending(
                code == "valid" ? "recovery_data_binding_invalid" : code);

        ServiceInstaller.UninstallResult cleanup;
        try
        {
            cleanup = ServiceInstaller.Uninstall(
                context.InstallDirectory,
                context.DataDirectory,
                purgeRetainedData: false);
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending("recovery_cleanup_failed");
        }
        return await CompleteTerminalCleanupAsync(
            context,
            request,
            cleanup,
            deviceKeys,
            maintenanceKeys,
            keys,
            post,
            () => DateTimeOffset.UtcNow,
            cancellationToken,
            retryDelay: null).ConfigureAwait(false);
    }

    private static ServiceInstaller.UninstallResult ProbeRecoveredTerminalCleanup(
        SelfUninstallRecoveryContext context,
        string retainedDirectory)
    {
        var terminal = UninstallTerminalCleanup.ExecuteAndProbe(retainedDirectory);
        return new ServiceInstaller.UninstallResult
        {
            ServicesRemoved = terminal.ServicesRemaining == 0,
            ServicesRemaining = terminal.ServicesRemaining,
            DataDirRemoved = !Directory.Exists(context.DataDirectory),
            DataPreserved = true,
            RetainedDataPath = retainedDirectory,
            InstallDirRemoved = !Directory.Exists(context.InstallDirectory),
            ScheduledUninstallTaskAbsent = terminal.ScheduledUninstallTaskAbsent,
            ProtocolRegistrationAbsent = terminal.ProtocolRegistrationAbsent,
            ArpRegistrationAbsent = terminal.ArpRegistrationAbsent,
            RetainedEvidencePresent = terminal.RetainedEvidencePresent,
            OperationalCredentialsAbsent = terminal.OperationalCredentialsAbsent,
        };
    }

    private static bool IsExpectedRetentionDirectory(
        string retentionRoot,
        string retainedDirectory,
        SelfUninstallRecoveryContext context)
    {
        try
        {
            var expectedRoot = NormalizeRecoveryPath(
                ServiceInstaller.DefaultRetentionRoot(context.DataDirectory));
            var suppliedRoot = NormalizeRecoveryPath(retentionRoot);
            var retainedParent = NormalizeRecoveryPath(
                Path.GetDirectoryName(Path.GetFullPath(retainedDirectory))!);
            return string.Equals(expectedRoot, suppliedRoot, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(suppliedRoot, retainedParent, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<SelfUninstallFinalizationResult> FinalizeRetainedAsync(
        string retainedDirectory,
        Func<Uri, string, CancellationToken, Task<SelfUninstallFinalizePostResult>> post,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? retryDelay)
    {
        if (!TryLoadPending(
                retainedDirectory,
                out var envelope,
                out var origin,
                out var code))
            return SelfUninstallFinalizationResult.Pending(code);
        var pendingPath = Path.Combine(
            retainedDirectory,
            SelfUninstallCompletionContract.PendingFileName);
        string exactBody;
        try
        {
            exactBody = ReadBoundedFile(
                pendingPath,
                SelfUninstallCompletionContract.MaxEnvelopeBytes);
        }
        catch { return SelfUninstallFinalizationResult.Pending("completion_read_failed"); }

        retryDelay ??= static (delay, ct) => Task.Delay(delay, ct);
        SelfUninstallFinalizePostResult? response = null;
        var responseValid = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await retryDelay(
                    attempt == 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5),
                    cancellationToken).ConfigureAwait(false);
            try
            {
                response = await post(origin!, exactBody, cancellationToken).ConfigureAwait(false);
                responseValid = response.IsSuccessStatusCode &&
                                SelfUninstallCompletionContract.IsExactFinalizedResponse(
                                    response.ResponseBody,
                                    envelope!,
                                    out _);
                if (responseValid) break;
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                response = null;
            }
        }
        if (!responseValid || response is null)
            return SelfUninstallFinalizationResult.Pending(
                response is null ? "completion_post_failed" : "completion_response_invalid");

        try
        {
            WriteAtomic(
                Path.Combine(retainedDirectory, CloudReceiptFileName),
                response.ResponseBody,
                SelfUninstallCompletionContract.MaxResponseBytes);
            MarkFinalized(retainedDirectory, exactBody);
            return SelfUninstallFinalizationResult.Finalized();
        }
        catch
        {
            return SelfUninstallFinalizationResult.Pending("completion_finalize_persist_failed");
        }
    }

    private static bool TryLoadPending(
        string retainedDirectory,
        out SelfUninstallCompletionEnvelope? envelope,
        out Uri? origin,
        out string code)
    {
        envelope = null;
        origin = null;
        code = "completion_missing";
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(retainedDirectory));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                code = "completion_directory_untrusted";
                return false;
            }
            var path = Path.Combine(
                directory.FullName,
                SelfUninstallCompletionContract.PendingFileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > SelfUninstallCompletionContract.MaxEnvelopeBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            if (!SelfUninstallCompletionContract.TryDeserialize(
                    ReadBoundedFile(
                        path,
                        SelfUninstallCompletionContract.MaxEnvelopeBytes),
                    out envelope,
                    out code))
                return false;
            var structural = SelfUninstallCompletionContract.ValidateForReplay(envelope!);
            if (!structural.IsValid)
            {
                code = structural.Code;
                return false;
            }
            var originPath = Path.Combine(directory.FullName, OriginFileName);
            var originInfo = new FileInfo(originPath);
            if (!originInfo.Exists || originInfo.Length is <= 0 or > MaxOriginBytes ||
                originInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !TryValidateCloudOrigin(
                    ReadBoundedFile(originPath, MaxOriginBytes),
                    out origin))
            {
                code = "completion_origin_invalid";
                return false;
            }
            code = "valid";
            return true;
        }
        catch
        {
            code = "completion_read_failed";
            return false;
        }
    }

    private static void PersistPending(
        string retainedDirectory,
        Uri cloudOrigin,
        SelfUninstallCompletionEnvelope envelope)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(retainedDirectory));
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Retained evidence directory is unavailable.");
        var origin = cloudOrigin.GetLeftPart(UriPartial.Authority);
        WriteAtomic(Path.Combine(directory.FullName, OriginFileName), origin, MaxOriginBytes);
        var json = SelfUninstallCompletionContract.Serialize(envelope);
        WriteAtomic(
            Path.Combine(
                directory.FullName,
                SelfUninstallCompletionContract.PendingFileName),
            json,
            SelfUninstallCompletionContract.MaxEnvelopeBytes);
        var persisted = ReadBoundedFile(
            Path.Combine(
                directory.FullName,
                SelfUninstallCompletionContract.PendingFileName),
            SelfUninstallCompletionContract.MaxEnvelopeBytes);
        if (!string.Equals(json, persisted, StringComparison.Ordinal))
            throw new IOException("Retained completion ticket read-back mismatch.");
    }

    private static void WriteAtomic(string path, string content, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(content) is <= 0 ||
            Encoding.UTF8.GetByteCount(content) > maximumBytes)
            throw new InvalidDataException("Retained completion content has an invalid size.");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(
                       stream,
                       new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                       4096,
                       leaveOpen: true))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void MarkFinalized(string directory, string exactBody)
    {
        var pending = Path.Combine(
            directory,
            SelfUninstallCompletionContract.PendingFileName);
        var finalized = Path.Combine(
            directory,
            SelfUninstallCompletionContract.FinalizedFileName);
        if (File.Exists(finalized))
        {
            if (!string.Equals(
                    ReadBoundedFile(
                        finalized,
                        SelfUninstallCompletionContract.MaxEnvelopeBytes),
                    exactBody,
                    StringComparison.Ordinal))
                throw new InvalidDataException("Finalized completion ticket conflicts with pending ticket.");
            File.Delete(pending);
        }
        else
        {
            File.Move(pending, finalized, overwrite: false);
        }
        try { File.Delete(Path.Combine(directory, OriginFileName)); } catch { }
    }

    private static bool TryReadClaim(
        string path,
        out SelfUninstallRequest? request,
        out string code)
    {
        request = null;
        code = "claim_invalid";
        try
        {
            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists || info.Length is <= 0 or > SelfUninstallContract.MaxRequestBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            return SelfUninstallContract.TryDeserialize(
                ReadBoundedFile(info.FullName, SelfUninstallContract.MaxRequestBytes),
                out request,
                out code);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExactClaimPath(string claimPath, string dataDirectory)
    {
        try
        {
            var data = new DirectoryInfo(Path.GetFullPath(dataDirectory));
            if (!data.Exists || data.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            var expected = Path.Combine(
                data.FullName,
                SelfUninstallContract.RequestFileName + ".claimed");
            return string.Equals(
                Path.GetFullPath(claimPath),
                expected,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string CurrentMaintenanceVersion()
    {
        var version = typeof(SelfUninstallCompletionFinalizer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
            typeof(SelfUninstallCompletionFinalizer).Assembly.GetName().Version?.ToString() ??
            "0.0.0";
        return NormalizeMaintenanceVersion(version);
    }

    private static string NormalizeMaintenanceVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v').Split('+', 2)[0];
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 80 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_' and not ':'))
            throw new InvalidDataException("Maintenance version is invalid.");
        return normalized;
    }
}
