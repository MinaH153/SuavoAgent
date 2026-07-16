using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup.Verify;

/// <summary>
/// Strong local activation milestone used before deleting the prior cohort.
/// Running SCM entries alone are insufficient: Core must expose its nonce-bound
/// command pipe and the complete on-disk cohort must pass the maintenance hash
/// proof. Transient startup states are retried within a bounded window.
/// </summary>
internal static class NativeInstallHealthMilestone
{
    public static async Task<VerifyOutcome> WaitAsync(
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<Task<VerifyOutcome>>? probe = null)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var deadline = DateTimeOffset.UtcNow + timeout;
        VerifyOutcome? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            latest = probe is null
                ? await VerifierFactory.BuildDefault().RunAsync(cancellationToken)
                : await probe();

            var cohort = MaintenanceCohortValidator.Validate(
                installDirectory,
                Path.Combine(dataDirectory, "binaries.manifest"));
            var pipe = latest.Gates.FirstOrDefault(g => g.Name == "Pipe");
            var services = latest.Gates.FirstOrDefault(g => g.Name == "Services");
            var cloud = latest.Gates.FirstOrDefault(g => g.Name == "Cloud auth");
            var workstation = latest.Gates.FirstOrDefault(g => g.Name == "Workstation");
            var definitiveFailure = latest.Gates.Any(g =>
                g.State == GateState.Fail &&
                g.Name is not ("Pipe" or "Services"));

            if (cohort.IsValid &&
                pipe?.State == GateState.Ok &&
                services?.State is GateState.Ok or GateState.Warn &&
                cloud?.State == GateState.Ok &&
                workstation?.State == GateState.Ok &&
                !definitiveFailure)
                return latest;
            if (definitiveFailure)
                return latest;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var gates = (latest?.Gates ?? Array.Empty<GateResult>()).ToList();
        gates.Add(new GateResult(
            "Activation",
            GateState.Fail,
            "The target release did not prove its signed cohort, cloud auth, interactive Helper, IPC, and PioneerRx path before timeout."));
        return new VerifyOutcome(false, gates, "Activation health milestone timed out.");
    }
}
