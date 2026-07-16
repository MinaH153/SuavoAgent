namespace SuavoAgent.Setup.Maintenance;

using System.Text.Json;

internal sealed record NativeInstallPreparation(
    string TransactionId,
    string LiveDirectory,
    string StagingDirectory,
    string DataDirectory,
    string MaintenanceRoot,
    string DataManifestPath,
    string PreparedManifestPath);

/// <summary>
/// Native fresh-install/reinstall coordinator. Downloads and configuration are
/// prepared while the currently installed cohort remains online. Only a complete
/// signed five-member stage is allowed to enter the durable directory-swap
/// transaction. All service operations use the Windows Service Control utility
/// directly, never a command shell or script host.
/// </summary>
internal sealed class NativeInstallCoordinator
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private readonly IWindowsServiceControl _services;
    private readonly Func<string, string, bool> _reassertAcls;
    private readonly Func<bool> _killCohortProcesses;
    private readonly Func<string, DateTimeOffset, bool> _hasFreshActiveHeartbeat;
    private readonly Func<string, DateTimeOffset, bool> _hasFreshActiveReadiness;
    private readonly Func<string, string, bool> _retireLegacyLifecycle;
    private readonly Action<TimeSpan> _delay;

    public NativeInstallCoordinator()
        : this(
            new ScWindowsServiceControl(),
            ServiceInstaller.ReassertMaintenanceAcls,
            ServiceInstaller.KillCohortProcessesExceptCurrent,
            retireLegacyLifecycle: (installDirectory, dataDirectory) =>
                LegacyLifecycleMigration.Execute(
                    installDirectory,
                    dataDirectory).Succeeded)
    {
    }

    internal NativeInstallCoordinator(
        IWindowsServiceControl services,
        Func<string, string, bool> reassertAcls,
        Func<bool> killCohortProcesses,
        Func<string, DateTimeOffset, bool>? hasFreshActiveHeartbeat = null,
        Func<string, DateTimeOffset, bool>? hasFreshActiveReadiness = null,
        Func<string, string, bool>? retireLegacyLifecycle = null,
        Action<TimeSpan>? delay = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _reassertAcls = reassertAcls ?? throw new ArgumentNullException(nameof(reassertAcls));
        _killCohortProcesses = killCohortProcesses ?? throw new ArgumentNullException(nameof(killCohortProcesses));
        _hasFreshActiveHeartbeat = hasFreshActiveHeartbeat ?? HasFreshActiveHeartbeat;
        _hasFreshActiveReadiness = hasFreshActiveReadiness ?? HasFreshActiveReadiness;
        // Internal test coordinators are side-effect free unless a migration
        // double is explicitly supplied. The public Windows coordinator above
        // always installs the real fail-closed migration boundary.
        _retireLegacyLifecycle = retireLegacyLifecycle ?? ((_, _) => true);
        _delay = delay ?? Thread.Sleep;
    }

    public static NativeInstallPreparation CreatePreparation(
        string liveDirectory,
        string dataDirectory,
        string? maintenanceRootOverride = null,
        string? transactionIdOverride = null)
    {
        var live = CanonicalDirectory(liveDirectory);
        var data = CanonicalDirectory(dataDirectory);
        var transactionId = transactionIdOverride ?? Guid.NewGuid().ToString("N");
        if (transactionId.Length != 32 || !transactionId.All(Uri.IsHexDigit))
            throw new ArgumentException("Transaction id must be 32 hexadecimal characters.");
        transactionId = transactionId.ToLowerInvariant();
        var maintenanceRoot = CanonicalDirectory(
            maintenanceRootOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent-Maintenance"));
        return new NativeInstallPreparation(
            transactionId,
            live,
            live + ".staging-" + transactionId,
            data,
            maintenanceRoot,
            Path.Combine(data, "binaries.manifest"),
            Path.Combine(maintenanceRoot, "binaries.manifest.new-" + transactionId));
    }

    /// <summary>
    /// Creates and protects the SYSTEM-owned maintenance root and same-volume
    /// install stage. Call before any download or credential write.
    /// </summary>
    public static void SecurePreparationDirectories(NativeInstallPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        Directory.CreateDirectory(preparation.MaintenanceRoot);
        ServiceInstaller.LockdownMaintenanceDirectoryAcl(preparation.MaintenanceRoot);
        if (Directory.Exists(preparation.StagingDirectory))
            throw new IOException("A staging directory already exists for this transaction.");
        Directory.CreateDirectory(preparation.StagingDirectory);
        ServiceInstaller.LockdownInstallDirectoryAcl(preparation.StagingDirectory);
        Directory.CreateDirectory(preparation.DataDirectory);
        ServiceInstaller.LockdownDataDirectoryAcl(preparation.DataDirectory);
    }

    /// <summary>
    /// Performs the final pre-quiesce proof and writes the prospective immutable
    /// manifest/install marker. No live service or live binary is touched here.
    /// </summary>
    public static SignedReleaseCohortValidation SealPreparedCohort(
        NativeInstallPreparation preparation,
        string version)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        var validation = SignedReleaseCohortValidator.Validate(
            preparation.StagingDirectory,
            version);
        if (!validation.IsValid) return validation;
        if (!BinaryDownloader.WriteBinariesManifest(
                preparation.StagingDirectory,
                preparation.PreparedManifestPath))
            return SignedReleaseCohortValidation.Reject("prepared_manifest_incomplete");
        MaintenanceHostInstaller.WriteInstallState(
            preparation.StagingDirectory,
            preparation.PreparedManifestPath,
            version);
        return SignedReleaseCohortValidator.Validate(
            preparation.StagingDirectory,
            version);
    }

    public InstallTransactionResult Execute(
        NativeInstallPreparation preparation,
        Func<bool> verifyHealthMilestone,
        Func<bool>? beforeActivate = null,
        Action? transactionProgress = null,
        Func<AuthorityPromotionOutcome>? promoteAuthority = null,
        Func<bool>? finalizeAuthority = null,
        bool requiresAuthorityPromotion = false,
        Action? afterJournalPrepared = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(verifyHealthMilestone);
        var authorityCallbackCount = new Delegate?[]
            { promoteAuthority, finalizeAuthority }.Count(callback => callback is not null);
        if (requiresAuthorityPromotion != (authorityCallbackCount == 2))
        {
            throw new ArgumentException(
                "Device-replacement installs require both authority callbacks and the explicit authority mode; same-credential OTA requires neither.");
        }
        var callbacks = Callbacks(
            preparation,
            verifyHealthMilestone,
            beforeActivate,
            transactionProgress,
            promoteAuthority,
            finalizeAuthority,
            afterJournalPrepared);
        var transaction = new InstallCohortTransaction(
            preparation.LiveDirectory,
            preparation.StagingDirectory,
            preparation.MaintenanceRoot,
            preparation.DataManifestPath,
            preparation.PreparedManifestPath,
            preparation.TransactionId,
            callbacks,
            requiresAuthorityPromotion);
        return transaction.Execute();
    }

    public InstallTransactionResult RecoverIncomplete(
        string liveDirectory,
        string dataDirectory,
        string? maintenanceRootOverride = null,
        Action? transactionProgress = null,
        Func<AuthorityPromotionOutcome>? promoteAuthority = null,
        Func<bool>? finalizeAuthority = null,
        bool forwardAuthorityOnly = false,
        SetupConfig? replacementConfig = null)
    {
        var preparation = CreatePreparation(
            liveDirectory,
            dataDirectory,
            maintenanceRootOverride,
            Guid.NewGuid().ToString("N"));
        var suppliedAuthorityCallbacks = new Delegate?[]
            { promoteAuthority, finalizeAuthority }.Count(callback => callback is not null);
        if (suppliedAuthorityCallbacks == 1)
            return InstallTransactionResult.Failed(
                "authority_callbacks_invalid",
                rolledBack: false);
        var requiresAuthority = InstallCohortTransaction.TryReadRecoveryAuthorityMode(
            preparation.LiveDirectory,
            preparation.MaintenanceRoot,
            preparation.DataManifestPath);
        var journalWasPresent = File.Exists(Path.Combine(
            preparation.MaintenanceRoot,
            "install-transaction.json"));
        if (requiresAuthority == true && suppliedAuthorityCallbacks == 0)
        {
            promoteAuthority = () =>
                InitialCredentialPersister.ReplayPendingAuthorityPromotion(dataDirectory);
            finalizeAuthority = () =>
                InitialCredentialPersister.FinalizePendingAuthority(dataDirectory) &&
                RestartPromotedCohort(
                    preparation.LiveDirectory,
                    preparation.DataDirectory,
                    TimeSpan.FromSeconds(90));
        }
        var recovery = InstallCohortTransaction.Recover(
            preparation.LiveDirectory,
            preparation.MaintenanceRoot,
            preparation.DataManifestPath,
            Callbacks(
                preparation,
                () => false,
                transactionProgress: transactionProgress,
                promoteAuthority: promoteAuthority,
                finalizeAuthority: finalizeAuthority),
            forwardAuthorityOnly);
        if (recovery.Succeeded || recovery.RolledBack)
        {
            var authorityReconciled = journalWasPresent && recovery.Succeeded
                ? InitialCredentialPersister.CompleteRecoveredPendingAuthority(dataDirectory)
                : InitialCredentialPersister.ReconcilePendingAuthorityWithoutTransaction(
                    dataDirectory,
                    replacementConfig);
            if (!authorityReconciled)
                return InstallTransactionResult.Failed(
                    "authority_pending_cleanup_mismatch",
                    rolledBack: false);
            if (!CleanupAbandonedPreparationArtifacts(
                    preparation.LiveDirectory,
                    preparation.MaintenanceRoot))
                return InstallTransactionResult.Failed(
                    "abandoned_preparation_cleanup_failed",
                    rolledBack: false);
        }
        return recovery;
    }

    internal bool Quiesce(Action? progress = null)
    {
        // Watchdog first so it cannot restart the cohort during the swap.
        foreach (var spec in new[]
                 {
                     NativeServiceSpecs.Watchdog,
                     NativeServiceSpecs.Broker,
                     NativeServiceSpecs.Core,
                 })
        {
            progress?.Invoke();
            if (!_services.StopAndWait(spec.Name, StopTimeout))
                return false;
        }
        progress?.Invoke();
        var killed = _killCohortProcesses();
        progress?.Invoke();
        return killed;
    }

    internal bool QuiesceAndRetireLegacyLifecycle(
        string installDirectory,
        string dataDirectory,
        Action? progress = null) =>
        Quiesce(progress) &&
        _retireLegacyLifecycle(installDirectory, dataDirectory);

    internal bool Activate(
        string installDirectory,
        string dataDirectory,
        Action? progress = null)
    {
        foreach (var spec in NativeServiceSpecs.All)
        {
            progress?.Invoke();
            if (!_services.EnsureConfigured(spec, installDirectory))
                return false;
        }
        // Enable/reassert Core's unique service SID before granting that SID
        // access to the live install and ProgramData trees. This also upgrades
        // older LocalService-wide installs during a native reinstall.
        progress?.Invoke();
        if (!_reassertAcls(installDirectory, dataDirectory))
            return false;
        foreach (var spec in NativeServiceSpecs.All)
        {
            progress?.Invoke();
            if (!_services.StartAndWait(spec.Name, StartTimeout))
                return false;
        }
        progress?.Invoke();
        return NativeServiceSpecs.All.All(
            spec => _services.Query(spec.Name) == NativeServiceState.Running);
    }

    /// <summary>
    /// Starts a cohort whose service registrations are owned by Windows
    /// Installer. This path deliberately never calls EnsureConfigured: device
    /// pairing may update protected configuration and authority, but it cannot
    /// recreate or rewrite MSI-owned service definitions.
    /// </summary>
    internal bool StartInstalledCohort(
        string installDirectory,
        string dataDirectory,
        Action? progress = null)
    {
        foreach (var spec in NativeServiceSpecs.All)
        {
            progress?.Invoke();
            if (_services.Query(spec.Name) is
                NativeServiceState.NotInstalled or NativeServiceState.Unknown)
                return false;
        }
        progress?.Invoke();
        if (!_reassertAcls(installDirectory, dataDirectory))
            return false;
        foreach (var spec in NativeServiceSpecs.All)
        {
            progress?.Invoke();
            if (!_services.StartAndWait(spec.Name, StartTimeout))
                return false;
        }
        return NativeServiceSpecs.All.All(
            spec => _services.Query(spec.Name) == NativeServiceState.Running);
    }

    /// <summary>
    /// After the pending credential and TPM key become the local active binding,
    /// restart the cohort so Core cannot continue running with its probation DI
    /// graph. Success requires a new active cloud heartbeat written after the
    /// restart; a stale probation health file can never satisfy this gate.
    /// </summary>
    internal bool RestartPromotedCohort(
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        Action? progress = null)
    {
        if (timeout <= TimeSpan.Zero) return false;
        if (!Quiesce(progress)) return false;
        var healthPath = Path.Combine(dataDirectory, "cloud-auth-health.json");
        var readinessPath = Path.Combine(dataDirectory, "activation-readiness.json");
        try
        {
            if (File.Exists(healthPath)) File.Delete(healthPath);
            if (File.Exists(readinessPath)) File.Delete(readinessPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var activatedAt = DateTimeOffset.UtcNow;
        if (!Activate(installDirectory, dataDirectory, progress)) return false;
        var deadline = activatedAt + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            progress?.Invoke();
            if (_hasFreshActiveHeartbeat(healthPath, activatedAt) &&
                _hasFreshActiveReadiness(readinessPath, activatedAt))
                return true;
            _delay(TimeSpan.FromMilliseconds(250));
        }
        return false;
    }

    /// <summary>
    /// Restarts the already-installed MSI cohort after authority promotion and
    /// requires new active health evidence. Service registration remains
    /// completely outside this operation.
    /// </summary>
    internal bool RestartPromotedInstalledCohort(
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        Action? progress = null)
    {
        if (timeout <= TimeSpan.Zero) return false;
        if (!Quiesce(progress)) return false;
        var healthPath = Path.Combine(dataDirectory, "cloud-auth-health.json");
        var readinessPath = Path.Combine(dataDirectory, "activation-readiness.json");
        try
        {
            if (File.Exists(healthPath)) File.Delete(healthPath);
            if (File.Exists(readinessPath)) File.Delete(readinessPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var activatedAt = DateTimeOffset.UtcNow;
        if (!StartInstalledCohort(installDirectory, dataDirectory, progress))
            return false;
        var deadline = activatedAt + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            progress?.Invoke();
            if (_hasFreshActiveHeartbeat(healthPath, activatedAt) &&
                _hasFreshActiveReadiness(readinessPath, activatedAt))
                return true;
            _delay(TimeSpan.FromMilliseconds(250));
        }
        return false;
    }

    private static bool HasFreshActiveHeartbeat(string path, DateTimeOffset notBefore)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 64 * 1024)
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) &&
                   status.GetString() == "ok" &&
                   root.TryGetProperty("lastSuccessAt", out var successAt) &&
                   successAt.ValueKind == JsonValueKind.String &&
                   DateTimeOffset.TryParse(successAt.GetString(), out var parsed) &&
                   parsed >= notBefore &&
                   root.TryGetProperty("consecutiveFailures", out var failures) &&
                   failures.TryGetInt32(out var failureCount) &&
                   failureCount == 0 &&
                   root.TryGetProperty("lastErrorKind", out var lastError) &&
                   lastError.ValueKind == JsonValueKind.Null &&
                   root.TryGetProperty("restartRequested", out var restart) &&
                   restart.ValueKind == JsonValueKind.False;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool HasFreshActiveReadiness(string path, DateTimeOffset notBefore)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 64 * 1024)
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) &&
                   status.GetString() == "ok" &&
                   root.TryGetProperty("provisioningId", out var provisioningId) &&
                   provisioningId.ValueKind == JsonValueKind.Null &&
                   root.TryGetProperty("checkedAt", out var checkedAt) &&
                   checkedAt.ValueKind == JsonValueKind.String &&
                   DateTimeOffset.TryParse(checkedAt.GetString(), out var parsed) &&
                   parsed >= notBefore &&
                   ReadTrue(root, "helperAttached") &&
                   ReadTrue(root, "ipcConnected") &&
                   ReadTrue(root, "actuationReady") &&
                   ReadTrue(root, "sqlConnected") &&
                   ReadTrue(root, "schemaCanaryGreen") &&
                   root.TryGetProperty("pmsCode", out var pms) &&
                   pms.GetString() == "pms_operational" &&
                   root.TryGetProperty("deviceProof", out var deviceProof) &&
                   deviceProof.ValueKind == JsonValueKind.Null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static bool ReadTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private InstallTransactionCallbacks Callbacks(
        NativeInstallPreparation preparation,
        Func<bool> verifyHealthMilestone,
        Func<bool>? beforeActivate = null,
        Action? transactionProgress = null,
        Func<AuthorityPromotionOutcome>? promoteAuthority = null,
        Func<bool>? finalizeAuthority = null,
        Action? afterJournalPrepared = null) =>
        new(
            () => QuiesceAndRetireLegacyLifecycle(
                preparation.LiveDirectory,
                preparation.DataDirectory,
                transactionProgress),
            () => (beforeActivate?.Invoke() ?? true) &&
                  Activate(
                      preparation.LiveDirectory,
                      preparation.DataDirectory,
                      transactionProgress),
            verifyHealthMilestone,
            () => Activate(
                preparation.LiveDirectory,
                preparation.DataDirectory,
                transactionProgress),
            _ => transactionProgress?.Invoke(),
            promoteAuthority,
            finalizeAuthority,
            AfterJournalPrepared: afterJournalPrepared);

    /// <summary>
    /// Reclaims only transaction-shaped stages and temporary journals after the
    /// authoritative journal has been recovered (or proven absent). Rollback
    /// directories are intentionally excluded because they may be the only copy
    /// of the prior cohort after journal corruption.
    /// </summary>
    internal static bool CleanupAbandonedPreparationArtifacts(
        string liveDirectory,
        string maintenanceRoot)
    {
        try
        {
            var live = CanonicalDirectory(liveDirectory);
            var maintenance = CanonicalDirectory(maintenanceRoot);
            var parent = Path.GetDirectoryName(live)
                         ?? throw new InvalidDataException("Install parent is unavailable.");
            if (Directory.Exists(parent))
            {
                var prefix = Path.GetFileName(live) + ".staging-";
                foreach (var path in Directory.EnumerateFileSystemEntries(
                             parent,
                             prefix + "*",
                             SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    if (!name.StartsWith(prefix, PathComparison) ||
                        name.Length != prefix.Length + 32 ||
                        !IsHexId(name.AsSpan(prefix.Length)))
                        continue;
                    if (!DeleteOwnedStage(path)) return false;
                }
            }

            if (!Directory.Exists(maintenance)) return true;
            foreach (var path in Directory.EnumerateFiles(
                         maintenance,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (IsBoundedTempName(name, "binaries.manifest.new-") ||
                    IsBoundedTempName(name, "install-transaction.json.tmp-"))
                    File.Delete(path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   InvalidDataException or
                                   ArgumentException)
        {
            return false;
        }
    }

    private static bool IsBoundedTempName(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) &&
        name.Length == prefix.Length + 32 &&
        IsHexId(name.AsSpan(prefix.Length));

    private static bool IsHexId(ReadOnlySpan<char> value)
    {
        if (value.Length != 32) return false;
        foreach (var character in value)
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f') and
                not (>= 'A' and <= 'F'))
                return false;
        return true;
    }

    private static bool DeleteOwnedStage(string path)
    {
        const int maxEntries = 8_192;
        try
        {
            FileAttributes rootAttributes;
            try { rootAttributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            var rootIsDirectory = (rootAttributes & FileAttributes.Directory) != 0;
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                if (rootIsDirectory) Directory.Delete(path, recursive: false);
                else File.Delete(path);
                return !File.Exists(path) && !Directory.Exists(path);
            }
            if (!rootIsDirectory) return false;
            var count = 0;
            var pending = new Stack<string>();
            var directories = new List<string> { path };
            var leaves = new List<(string Path, bool IsDirectory)>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    if (++count > maxEntries) return false;
                    var attributes = File.GetAttributes(entry);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || !isDirectory)
                    {
                        leaves.Add((entry, isDirectory));
                        continue;
                    }
                    directories.Add(entry);
                    pending.Push(entry);
                }
            }
            foreach (var leaf in leaves)
            {
                if (leaf.IsDirectory) Directory.Delete(leaf.Path, recursive: false);
                else File.Delete(leaf.Path);
            }
            foreach (var directory in directories.OrderByDescending(value => value.Length))
                Directory.Delete(directory, recursive: false);
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string CanonicalDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
