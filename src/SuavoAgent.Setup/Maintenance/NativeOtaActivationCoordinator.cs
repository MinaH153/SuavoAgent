using System.Security.Principal;
using System.Diagnostics;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Privileged OTA coordinator hosted by the signed Maintenance executable.
/// Initial mode claims LocalService staging and re-execs outside Program Files;
/// runner mode assembles and activates the full cohort transactionally.
/// </summary>
internal sealed class NativeOtaActivationCoordinator
{
    internal const int Success = 0;
    internal const int UnsupportedHost = 40;
    internal const int InvalidArguments = 41;
    internal const int UntrustedHost = 42;
    internal const int IdentityInvalid = 43;
    internal const int ClaimFailed = 44;
    internal const int RunnerLaunchFailed = 45;
    internal const int ActivationFailed = 46;

    private static readonly TimeSpan ActivationHealthTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RunnerProgressLease = TimeSpan.FromMinutes(2);

    public static int RunInitial(string[] args)
    {
        if (!TryReadSinglePathArgument(
                args,
                UpdateActivationContract.RequestPathSwitch,
                out var requestPath) ||
            !PathEquals(requestPath, UpdateActivationContract.DefaultActivationRequestPath()))
            return InvalidArguments;
        return BuildDefault().ClaimAndLaunch(requestPath!);
    }

    public static int RunResume(string[] args)
    {
        if (!TryReadSinglePathArgument(
                args,
                UpdateActivationContract.ClaimPathSwitch,
                out var claimPath) ||
            !PathEquals(claimPath, UpdateActivationContract.DefaultActiveClaimPath()))
            return InvalidArguments;
        return BuildDefault().ResumeAndLaunch(claimPath!);
    }

    public static int RunRunner(string[] args)
    {
        if (!TryReadSinglePathArgument(
                args,
                UpdateActivationContract.RequestPathSwitch,
                out var requestPath))
            return InvalidArguments;
        return BuildDefault().RunDurableClaim(requestPath!);
    }

    private readonly string _installDirectory;
    private readonly string _dataDirectory;
    private readonly string _updateRoot;
    private readonly string _maintenanceRoot;
    private readonly Func<bool> _isLocalSystem;
    private readonly Func<string, MaintenanceHostTrustResult> _verifyHostTrust;
    private readonly NativeUpdateClaimValidator _validator;
    private readonly AuthoritativeUpdateReplayLedger _ledger;
    private readonly NativeUpdateClaimStore _claimStore;
    private readonly UpdateClaimPointerStore _pointerStore;
    private readonly NativeMaintenanceRunnerStager _runnerStager;
    private readonly NativeOtaCohortAssembler _assembler;
    private readonly NativeInstallCoordinator _installCoordinator;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IDisposable> _acquireTransactionLock;
    private readonly INativeOtaActivationRuntime? _runtime;

    internal NativeOtaActivationCoordinator(
        string installDirectory,
        string dataDirectory,
        string updateRoot,
        string maintenanceRoot,
        Func<bool> isLocalSystem,
        Func<string, MaintenanceHostTrustResult> verifyHostTrust,
        NativeUpdateClaimValidator validator,
        AuthoritativeUpdateReplayLedger ledger,
        NativeUpdateClaimStore claimStore,
        UpdateClaimPointerStore pointerStore,
        NativeMaintenanceRunnerStager runnerStager,
        NativeOtaCohortAssembler assembler,
        NativeInstallCoordinator installCoordinator,
        Func<DateTimeOffset>? clock = null,
        Func<IDisposable>? acquireTransactionLock = null,
        INativeOtaActivationRuntime? runtime = null)
    {
        _installDirectory = Path.GetFullPath(installDirectory);
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _updateRoot = Path.GetFullPath(updateRoot);
        _maintenanceRoot = Path.GetFullPath(maintenanceRoot);
        _isLocalSystem = isLocalSystem;
        _verifyHostTrust = verifyHostTrust;
        _validator = validator;
        _ledger = ledger;
        _claimStore = claimStore;
        _pointerStore = pointerStore;
        _runnerStager = runnerStager;
        _assembler = assembler;
        _installCoordinator = installCoordinator;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _acquireTransactionLock = acquireTransactionLock ??
                                  (() => InstallerTransactionLock.Acquire());
        _runtime = runtime;
    }

    internal int ClaimAndLaunch(string sourceRequestPath)
    {
        if (!TryAcquireTransactionLock(out var transactionLock)) return ClaimFailed;
        using (transactionLock)
        {
            var host = _runtime?.ValidateInstalledHost() ?? ValidateInstalledHost();
            if (host.ExitCode != Success) return host.ExitCode;
            var identity = InstalledUpdateIdentityReader.TryRead(_installDirectory);
            if (identity is null) return IdentityInvalid;
            if (!TryReadRequest(sourceRequestPath, out var request)) return ClaimFailed;
            var payload = UpdateActivationContract.GetIncomingStagingDirectory(
                _updateRoot,
                request!.StagingId);
            var claimed = _claimStore.Claim(
                sourceRequestPath,
                payload,
                identity,
                _clock());
            if (!claimed.Succeeded) return ClaimFailed;

            UpdateActivationClaimPointer pointer;
            try { pointer = _pointerStore.Begin(claimed.Claim!, _clock()); }
            catch { return ClaimFailed; }

            var runner = _runnerStager.Stage(
                host.ProcessPath!,
                _maintenanceRoot,
                pointer.StagingId);
            if (!runner.Succeeded ||
                !_runnerStager.LaunchRunner(runner.RunnerPath!, claimed.Claim!.RequestPath))
                return RunnerLaunchFailed;

            // The immutable SYSTEM claim, pointer, authoritative replay reservation,
            // and runner launch all exist before the LocalService source is removed.
            TryDelete(sourceRequestPath);
            return Success;
        }
    }

    internal int ResumeAndLaunch(string claimPointerPath)
    {
        if (!TryAcquireTransactionLock(out var transactionLock)) return ClaimFailed;
        using (transactionLock)
        {
            var host = _runtime?.ValidateInstalledHost() ?? ValidateInstalledHost();
            if (host.ExitCode != Success) return host.ExitCode;
            if (!PathEquals(claimPointerPath, _pointerStore.PointerPath))
                return InvalidArguments;
            var pointer = TryReadPointer();
            if (pointer is null) return ClaimFailed;
            var identity = InstalledUpdateIdentityReader.TryRead(_installDirectory);
            if (identity is null) return IdentityInvalid;
            var validation = _validator.Validate(
                pointer.RequestPath,
                pointer.PayloadDirectory,
                identity,
                _clock(),
                requireStrictUpgrade: false,
                allowExpiredDurableClaim: true);
            if (!validation.IsValid ||
                !string.Equals(validation.Claim!.ReplayId, pointer.ReplayId, StringComparison.Ordinal))
                return ClaimFailed;
            var runner = _runnerStager.Stage(
                host.ProcessPath!,
                _maintenanceRoot,
                pointer.StagingId);
            if (!runner.Succeeded) return RunnerLaunchFailed;
            if (!DateTimeOffset.TryParse(pointer.LastHeartbeatAtUtc, out var heartbeatAt))
                return ClaimFailed;
            if (_clock() - heartbeatAt <= RunnerProgressLease)
                return Success;
            var terminated = _runtime is null
                ? TerminateExactStaleRunner(runner.RunnerPath!)
                : _runtime.TerminateExactStaleRunner(runner.RunnerPath!);
            if (!terminated)
                return RunnerLaunchFailed;
            return _runnerStager.LaunchRunner(runner.RunnerPath!, pointer.RequestPath)
                ? Success
                : RunnerLaunchFailed;
        }
    }

    internal int RunDurableClaim(string trustedRequestPath)
    {
        if (!TryAcquireTransactionLock(out var transactionLock)) return ActivationFailed;
        using (transactionLock)
        {
            var host = _runtime?.ValidateRunnerHost() ?? ValidateRunnerHost();
            if (host.ExitCode != Success) return host.ExitCode;
            var pointer = TryReadPointer();
            if (pointer is null || !PathEquals(trustedRequestPath, pointer.RequestPath))
                return ClaimFailed;
            using var runnerLease = _runtime is null
                ? TryAcquireRunnerLease(pointer)
                : _runtime.TryAcquireRunnerLease(pointer);
            if (runnerLease is null)
                return Success; // another live runner owns the exact claim
            var startedAt = _clock();

            void Touch() => pointer = _pointerStore.Heartbeat(pointer, _clock());

            try
            {
                Touch();
                var identity = InstalledUpdateIdentityReader.TryRead(_installDirectory);
                if (identity is null)
                    return Finish(pointer, AuthoritativeReplayState.Failed, "failed", startedAt, IdentityInvalid);

                var validation = _validator.Validate(
                    trustedRequestPath,
                    pointer.PayloadDirectory,
                    identity,
                    _clock(),
                    requireStrictUpgrade: false,
                    allowExpiredDurableClaim: true,
                    progress: Touch);
                if (!validation.IsValid ||
                    !string.Equals(validation.Claim!.ReplayId, pointer.ReplayId, StringComparison.Ordinal))
                    return Finish(pointer, AuthoritativeReplayState.Failed, "rejected", startedAt, ClaimFailed);
                var durableClaim = new DurableUpdateClaim(
                    validation.Claim,
                    Path.GetDirectoryName(pointer.RequestPath)!,
                    pointer.RequestPath,
                    pointer.PayloadDirectory,
                    WasAlreadyClaimed: true);
                var health = _runtime?.CreateHealth(
                                 _updateRoot,
                                 durableClaim.ClaimDirectory)
                             ?? new NativeOtaActivationHealthAdapter(
                                 _updateRoot,
                                 durableClaim.ClaimDirectory);

                var replay = _ledger.Find(pointer.ReplayId);
                if (replay is null)
                    return ClaimFailed;
                Touch();
                if (replay.State is AuthoritativeReplayState.Completed or
                    AuthoritativeReplayState.RolledBack or
                    AuthoritativeReplayState.Failed)
                {
                    var terminalOutcome = replay.State switch
                    {
                        AuthoritativeReplayState.Completed => "committed",
                        AuthoritativeReplayState.RolledBack => "rolled_back",
                        _ => "failed",
                    };
                    return Finish(
                        pointer,
                        replay.State,
                        terminalOutcome,
                        replay.ClaimedAtUtc,
                        replay.State == AuthoritativeReplayState.Completed ? Success : ActivationFailed);
                }
                if (replay.State == AuthoritativeReplayState.Claimed &&
                    !_ledger.TryTransition(
                        pointer.ReplayId,
                        AuthoritativeReplayState.Claimed,
                        AuthoritativeReplayState.Activating,
                        _clock()))
                    return ClaimFailed;

                // If a prior runner reached commit and died before writing completion,
                // the target version plus complete cohort and pipe proof closes that
                // narrow window without applying the same update twice.
                if (UpdateActivationContract.VersionsEquivalent(
                        identity.Version,
                        pointer.TargetVersion) &&
                    health.HasDurableMilestone(pointer, identity, _clock()) &&
                    (_runtime?.IsCurrentCohortHealthy() ?? IsCurrentCohortHealthy()))
                    return Finish(pointer, AuthoritativeReplayState.Completed, "committed", startedAt, Success);

                var recovery = _runtime is null
                    ? _installCoordinator.RecoverIncomplete(
                        _installDirectory,
                        _dataDirectory,
                        _maintenanceRoot,
                        Touch)
                    : _runtime.RecoverIncomplete(
                        _installDirectory,
                        _dataDirectory,
                        _maintenanceRoot,
                        Touch);
                if (!recovery.Succeeded)
                {
                    if (recovery.RolledBack)
                        return Finish(pointer, AuthoritativeReplayState.RolledBack, "rolled_back", startedAt, ActivationFailed);
                    return Finish(pointer, AuthoritativeReplayState.Failed, "failed", startedAt, ActivationFailed);
                }

                var assembly = _runtime is null
                    ? _assembler.Assemble(
                        durableClaim,
                        _installDirectory,
                        _dataDirectory,
                        _maintenanceRoot,
                        Touch)
                    : _runtime.Assemble(
                        durableClaim,
                        _installDirectory,
                        _dataDirectory,
                        _maintenanceRoot,
                        Touch);
                if (!assembly.Succeeded)
                    return Finish(pointer, AuthoritativeReplayState.Failed, "failed", startedAt, ActivationFailed);

                UpdateActivationHealthChallenge? challenge = null;

                InstallTransactionResult transaction;
                try
                {
                    bool VerifyHealth()
                    {
                        if (challenge is null)
                            return false;
                        var outcome = health.WaitAsync(
                                challenge,
                                _installDirectory,
                                _dataDirectory,
                                ActivationHealthTimeout,
                                CancellationToken.None,
                                Touch)
                            .GetAwaiter()
                            .GetResult();
                        return outcome.Passed;
                    }

                    bool BeforeActivate()
                    {
                        Touch();
                        challenge = health.Issue(pointer, identity, _clock());
                        return true;
                    }

                    transaction = _runtime is null
                        ? _installCoordinator.Execute(
                            assembly.Preparation!,
                            VerifyHealth,
                            beforeActivate: BeforeActivate,
                            transactionProgress: Touch)
                        : _runtime.Execute(
                            assembly.Preparation!,
                            VerifyHealth,
                            BeforeActivate,
                            Touch);
                }
                finally
                {
                    // A thrown service/filesystem callback must not leave a stale
                    // target challenge or milestone visible to a later activation.
                    health.CleanupRuntimeProofs();
                }

                if (transaction.Succeeded)
                    return Finish(pointer, AuthoritativeReplayState.Completed, "committed", startedAt, Success);
                if (transaction.RolledBack)
                    return Finish(
                        pointer,
                        AuthoritativeReplayState.RolledBack,
                        "rolled_back",
                        startedAt,
                        ActivationFailed);
                // Journal/rollback artifacts remain authoritative. Keep the active
                // pointer and Activating ledger so Watchdog can resume recovery;
                // a terminal Failed receipt here would strand a recoverable swap.
                return ActivationFailed;
            }
            catch
            {
                return Finish(pointer, AuthoritativeReplayState.Failed, "failed", startedAt, ActivationFailed);
            }
        }
    }

    private int Finish(
        UpdateActivationClaimPointer pointer,
        AuthoritativeReplayState ledgerState,
        string outcome,
        DateTimeOffset startedAt,
        int exitCode)
    {
        try
        {
            var current = _ledger.Find(pointer.ReplayId);
            if (current is null) return ActivationFailed;
            if (current.State != ledgerState)
            {
                var transitioned = current.State switch
                {
                    AuthoritativeReplayState.Claimed => _ledger.TryTransition(
                        pointer.ReplayId,
                        AuthoritativeReplayState.Claimed,
                        ledgerState,
                        _clock()),
                    AuthoritativeReplayState.Activating => _ledger.TryTransition(
                        pointer.ReplayId,
                        AuthoritativeReplayState.Activating,
                        ledgerState,
                        _clock()),
                    _ => false,
                };
                if (!transitioned) return ActivationFailed;
            }
            _pointerStore.Complete(pointer, outcome, startedAt, _clock());
            CleanupPayload(pointer);
        }
        catch
        {
            // Keep active claim + ledger for Watchdog resume if the terminal
            // receipt could not be made durable.
            return ActivationFailed;
        }
        return exitCode;
    }

    internal bool IsCurrentCohortHealthy()
    {
        var cohort = MaintenanceCohortValidator.Validate(
            _installDirectory,
            Path.Combine(_dataDirectory, "binaries.manifest"));
        if (!cohort.IsValid) return false;
        var outcome = NativeInstallHealthMilestone.WaitAsync(
                _installDirectory,
                _dataDirectory,
                TimeSpan.FromSeconds(30),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return outcome.Passed;
    }

    private NativeOtaHostValidation ValidateInstalledHost() =>
        ValidateInstalledHostForEnvironment(
            OperatingSystem.IsWindows(),
            _isLocalSystem(),
            Environment.ProcessPath,
            _installDirectory,
            _verifyHostTrust);

    internal static NativeOtaHostValidation ValidateInstalledHostForEnvironment(
        bool isWindows,
        bool isLocalSystem,
        string? processPath,
        string installDirectory,
        Func<string, MaintenanceHostTrustResult> verifyHostTrust)
    {
        if (!isWindows || !isLocalSystem)
            return new(UnsupportedHost, null);
        var expected = Path.Combine(installDirectory, MaintenanceContract.ExecutableName);
        if (string.IsNullOrWhiteSpace(processPath) || !PathEquals(processPath, expected))
            return new(UntrustedHost, null);
        var trust = verifyHostTrust(processPath);
        return trust.IsTrusted
            ? new(Success, processPath)
            : new(UntrustedHost, null);
    }

    private NativeOtaHostValidation ValidateRunnerHost() =>
        ValidateRunnerHostForEnvironment(
            OperatingSystem.IsWindows(),
            _isLocalSystem(),
            Environment.ProcessPath,
            _maintenanceRoot,
            _verifyHostTrust);

    internal static NativeOtaHostValidation ValidateRunnerHostForEnvironment(
        bool isWindows,
        bool isLocalSystem,
        string? processPath,
        string maintenanceRoot,
        Func<string, MaintenanceHostTrustResult> verifyHostTrust)
    {
        if (!isWindows || !isLocalSystem)
            return new(UnsupportedHost, null);
        if (string.IsNullOrWhiteSpace(processPath) ||
            !string.Equals(
                Path.GetFileName(processPath),
                MaintenanceContract.ExecutableName,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(processPath).StartsWith(
                Path.Combine(maintenanceRoot, UpdateActivationContract.RunnerDirectoryName) +
                Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return new(UntrustedHost, null);
        var trust = verifyHostTrust(processPath);
        return trust.IsTrusted
            ? new(Success, processPath)
            : new(UntrustedHost, null);
    }

    private static NativeOtaActivationCoordinator BuildDefault()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var install = Path.Combine(programFiles, "Suavo", "Agent");
        var data = Path.Combine(commonData, "SuavoAgent");
        var update = UpdateActivationContract.DefaultUpdateRoot();
        var maintenance = UpdateActivationContract.DefaultMaintenanceRoot();
        var validator = new NativeUpdateClaimValidator();
        var ledger = new AuthoritativeUpdateReplayLedger(
            Path.Combine(maintenance, UpdateActivationContract.ReplayLedgerFileName));
        var claimStore = new NativeUpdateClaimStore(
            maintenance,
            validator,
            ledger,
            sourceUpdateRoot: update);
        return new NativeOtaActivationCoordinator(
            install,
            data,
            update,
            maintenance,
            IsCurrentProcessLocalSystem,
            MaintenanceHostTrustVerifier.Verify,
            validator,
            ledger,
            claimStore,
            new UpdateClaimPointerStore(maintenance),
            new NativeMaintenanceRunnerStager(),
            new NativeOtaCohortAssembler(),
            new NativeInstallCoordinator());
    }

    internal static bool TryReadSinglePathArgument(
        string[] args,
        string switchName,
        out string? value)
    {
        value = null;
        var matches = 0;
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], switchName, StringComparison.OrdinalIgnoreCase))
                continue;
            matches++;
            value = args[index + 1];
        }
        return matches == 1 &&
               !string.IsNullOrWhiteSpace(value) &&
               Path.IsPathFullyQualified(value);
    }

    private static bool TryReadRequest(
        string path,
        out UpdateActivationRequest? request)
    {
        request = null;
        try
        {
            var json = BoundedFile.ReadUtf8(
                path,
                UpdateActivationContract.MaxRequestBytes);
            return UpdateActivationContract.TryDeserialize(json, out request, out _);
        }
        catch { return false; }
    }

    private static bool IsCurrentProcessLocalSystem()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
        catch { return false; }
    }

    private void CleanupPayload(UpdateActivationClaimPointer pointer)
    {
        try
        {
            var incoming = UpdateActivationContract.GetIncomingStagingDirectory(
                _updateRoot,
                pointer.StagingId);
            if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        }
        catch { }
        try
        {
            if (Directory.Exists(pointer.PayloadDirectory))
                Directory.Delete(pointer.PayloadDirectory, true);
        }
        catch { }
    }

    internal FileStream? TryAcquireRunnerLease(UpdateActivationClaimPointer pointer)
    {
        try
        {
            var claimDirectory = Path.GetDirectoryName(pointer.RequestPath);
            if (string.IsNullOrWhiteSpace(claimDirectory)) return null;
            var path = Path.Combine(claimDirectory, "runner.lease");
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool TerminateExactStaleRunner(string expectedRunnerPath)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch { continue; }
                    if (!PathEquals(path, expectedRunnerPath)) continue;
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(30_000)) return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(
                       Path.GetFullPath(left),
                       Path.GetFullPath(right),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private bool TryAcquireTransactionLock(out IDisposable? transactionLock)
    {
        transactionLock = null;
        try
        {
            transactionLock = _acquireTransactionLock();
            return transactionLock is not null;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            TimeoutException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }

    private UpdateActivationClaimPointer? TryReadPointer()
    {
        try { return _pointerStore.TryReadPointer(_clock()); }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException)
        {
            return null;
        }
    }
}
