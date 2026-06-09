namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// GUI counterpart to <see cref="InstallOrchestrator"/>. Drives the symmetric
/// uninstall from the progress step: stop + remove the three Windows services
/// (watchdog-first, kills the orphan Helper that locks binaries), remove the
/// data directory, then the install directory. All the real work lives in
/// <see cref="ServiceInstaller.Uninstall(string,string)"/> — this just sequences
/// progress phases around the single blocking call and surfaces the final
/// <see cref="ServiceInstaller.UninstallResult"/> for zero-residue verification.
/// </summary>
internal sealed class UninstallOrchestrator
{
    public enum Phase { StopServices, RemoveData, RemoveInstall, Done }

    public sealed record PhaseEvent(Phase Phase, string Message);

    /// <summary>Default install/data dirs, matching ConsoleInstaller / UninstallInstaller.</summary>
    public const string DefaultInstallDir = @"C:\Program Files\Suavo\Agent";
    public const string DefaultDataDir = @"C:\ProgramData\SuavoAgent";

    private readonly string _installDir;
    private readonly string _dataDir;

    public UninstallOrchestrator(string? installDir = null, string? dataDir = null)
    {
        _installDir = string.IsNullOrWhiteSpace(installDir) ? DefaultInstallDir : installDir!;
        _dataDir = string.IsNullOrWhiteSpace(dataDir) ? DefaultDataDir : dataDir!;
    }

    /// <summary>
    /// Runs the uninstall end-to-end. The single <see cref="ServiceInstaller.Uninstall"/>
    /// call is blocking (it shells out to sc.exe and deletes directories), so the
    /// caller runs this on a background thread (Task.Run) — exactly like install —
    /// while ConsoleUI.Write* messages flow into the GUI via the installed reporter.
    /// Returns the <see cref="ServiceInstaller.UninstallResult"/> so the caller can
    /// route to a clean Success or a residue Error.
    /// </summary>
    public async Task<ServiceInstaller.UninstallResult> RunAsync(
        IProgress<PhaseEvent> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.StopServices, "Stopping SuavoAgent services"));
        ConsoleUI.WriteStep("Phase 1: Stopping and removing Windows services");

        // ServiceInstaller.Uninstall is one atomic, blocking operation: it stops the
        // services (Phase 1), removes the data dir (Phase 2), then the install dir
        // (Phase 3). It reports its own ConsoleUI.Write* lines for each, which the GUI
        // reporter renders live — so we surface the phase markers around the single call.
        var result = await Task.Run(() =>
        {
            progress.Report(new PhaseEvent(Phase.RemoveData, "Removing local data"));
            ConsoleUI.WriteStep("Phase 2: Removing local data directory");

            var r = ServiceInstaller.Uninstall(_installDir, _dataDir);

            progress.Report(new PhaseEvent(Phase.RemoveInstall, "Removing program files"));
            ConsoleUI.WriteStep("Phase 3: Removing install directory");
            return r;
        }, ct);

        if (result.FullyClean)
            ConsoleUI.WriteOk("SuavoAgent fully removed — this computer is clean.");
        else
            ConsoleUI.WriteWarn(
                $"Uninstall finished with residue: servicesRemaining={result.ServicesRemaining}, "
                + $"dataDirRemoved={result.DataDirRemoved}, installDirRemoved={result.InstallDirRemoved}.");

        progress.Report(new PhaseEvent(Phase.Done, "Uninstall complete"));
        return result;
    }
}
