using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Narrow orchestration port for platform-bound OTA operations. Production
/// uses the coordinator's concrete implementations; tests use this port to
/// prove durable state transitions under process, service, and health faults.
/// </summary>
internal interface INativeOtaActivationRuntime
{
    NativeOtaHostValidation ValidateInstalledHost();

    NativeOtaHostValidation ValidateRunnerHost();

    IDisposable? TryAcquireRunnerLease(UpdateActivationClaimPointer pointer);

    bool TerminateExactStaleRunner(string expectedRunnerPath);

    OtaCohortAssemblyResult Assemble(
        DurableUpdateClaim claim,
        string installDirectory,
        string dataDirectory,
        string maintenanceRoot,
        Action progress);

    InstallTransactionResult RecoverIncomplete(
        string installDirectory,
        string dataDirectory,
        string maintenanceRoot,
        Action progress);

    InstallTransactionResult Execute(
        NativeInstallPreparation preparation,
        Func<bool> verifyHealthMilestone,
        Func<bool> beforeActivate,
        Action transactionProgress);

    INativeOtaActivationHealth CreateHealth(
        string updateRoot,
        string systemClaimDirectory);

    bool IsCurrentCohortHealthy();
}

internal interface INativeOtaActivationHealth
{
    UpdateActivationHealthChallenge Issue(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now);

    Task<VerifyOutcome> WaitAsync(
        UpdateActivationHealthChallenge challenge,
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action progress);

    void CleanupRuntimeProofs();

    bool HasDurableMilestone(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now);
}

internal sealed record NativeOtaHostValidation(int ExitCode, string? ProcessPath);

internal sealed class NativeOtaActivationHealthAdapter : INativeOtaActivationHealth
{
    private readonly OtaActivationHealthCoordinator _inner;

    public NativeOtaActivationHealthAdapter(
        string updateRoot,
        string systemClaimDirectory) =>
        _inner = new OtaActivationHealthCoordinator(updateRoot, systemClaimDirectory);

    public UpdateActivationHealthChallenge Issue(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now) => _inner.Issue(pointer, identity, now);

    public Task<VerifyOutcome> WaitAsync(
        UpdateActivationHealthChallenge challenge,
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action progress) => _inner.WaitAsync(
        challenge,
        installDirectory,
        dataDirectory,
        timeout,
        cancellationToken,
        progress);

    public void CleanupRuntimeProofs() => _inner.CleanupRuntimeProofs();

    public bool HasDurableMilestone(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now) => _inner.HasDurableMilestone(pointer, identity, now);
}
