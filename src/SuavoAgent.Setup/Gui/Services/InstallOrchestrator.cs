using System.Text.Json;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Drives phases 3–5 of the install flow from the GUI progress step:
/// download binaries, write ACL-locked appsettings + consent receipt, then
/// register and start the Windows services. Phases 1 (PioneerRx discovery)
/// and 2 (SQL credential discovery) are handled eagerly during System Check;
/// this orchestrator assumes both are already populated on the context.
/// </summary>
internal sealed class InstallOrchestrator
{
    public enum Phase { Download, WriteConfig, InstallServices, Done }

    public sealed record PhaseEvent(Phase Phase, string Message);

    private readonly InstallContext _ctx;

    public InstallOrchestrator(InstallContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Runs the install end-to-end. Exceptions bubble — the caller (the
    /// progress view-model) surfaces them in the GUI log + swaps to an error
    /// state. Cancellation aborts between phases but in-flight HTTP downloads
    /// may complete the current chunk before observing the token.
    /// </summary>
    public async Task RunAsync(IProgress<PhaseEvent> progress, CancellationToken ct)
    {
        if (_ctx.Pioneer == null)
            throw new InvalidOperationException("Pioneer discovery must complete before install.");
        if (_ctx.SqlCredentials == null)
            throw new InvalidOperationException("SQL credentials must be set before install.");
        if (_ctx.Consent == null)
            throw new InvalidOperationException("Consent must be captured before install.");

        progress.Report(new PhaseEvent(Phase.Download, "Downloading SuavoAgent binaries"));
        ConsoleUI.WriteStep("Phase 3: Downloading SuavoAgent binaries");
        var downloaded = await BinaryDownloader.DownloadAndVerifyAsync(
            _ctx.Config.ReleaseTag, _ctx.InstallDir);
        if (!downloaded)
            throw new InstallException("Binary download or verification failed.");

        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.WriteConfig, "Writing configuration"));
        ConsoleUI.WriteStep("Phase 4: Writing configuration");
        WriteConfigFiles();

        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.InstallServices, "Installing Windows services"));
        ConsoleUI.WriteStep("Phase 5: Installing Windows services");
        var started = ServiceInstaller.InstallAndStart(_ctx.InstallDir, _ctx.DataDir);
        if (!started)
            ConsoleUI.WriteWarn("Core service did not report running. Check post-install logs.");

        progress.Report(new PhaseEvent(Phase.Done, "Installation complete"));
    }

    private void WriteConfigFiles()
    {
        _ctx.AgentId ??= "agent-" + Guid.NewGuid().ToString("N")[..12];
        _ctx.MachineFingerprint ??= GetMachineFingerprint();

        Directory.CreateDirectory(_ctx.InstallDir);
        ServiceInstaller.LockdownDirectoryAcl(_ctx.InstallDir);
        Directory.CreateDirectory(_ctx.DataDir);

        File.WriteAllText(
            Path.Combine(_ctx.InstallDir, "appsettings.json"),
            BuildAppSettings());

        File.WriteAllText(
            Path.Combine(_ctx.DataDir, "consent-receipt.json"),
            _ctx.Consent!.ToJson(
                pharmacyId: _ctx.Config.PharmacyId,
                agentId: _ctx.AgentId!,
                installerVersion: _ctx.InstallerVersion,
                machineFingerprint: _ctx.MachineFingerprint!));

        ConsoleUI.WriteOk("appsettings.json + consent-receipt.json written (ACL applied first)");

        // L-1 parity with ConsoleInstaller — drop setup.json after successful config write.
        var setupJson = Path.Combine(AppContext.BaseDirectory, "setup.json");
        if (File.Exists(setupJson))
        {
            try
            {
                File.Delete(setupJson);
                ConsoleUI.WriteOk("setup.json deleted (credentials no longer on disk)");
            }
            catch (Exception ex)
            {
                ConsoleUI.WriteWarn($"Could not delete setup.json: {ex.Message}");
            }
        }
    }

    private string BuildAppSettings()
    {
        var sql = _ctx.SqlCredentials!;
        var settings = new Dictionary<string, object>
        {
            ["Agent"] = new Dictionary<string, object?>
            {
                ["CloudUrl"] = _ctx.Config.CloudUrl,
                ["ApiKey"] = _ctx.Config.ApiKey,
                ["AgentId"] = _ctx.AgentId,
                ["PharmacyId"] = _ctx.Config.PharmacyId,
                ["MachineFingerprint"] = _ctx.MachineFingerprint,
                ["Version"] = _ctx.Config.ReleaseTag.TrimStart('v'),
                ["SqlServer"] = sql.Server,
                ["SqlDatabase"] = sql.Database,
                ["SqlUser"] = sql.User,
                ["SqlPassword"] = sql.Password,
                ["LearningMode"] = _ctx.Config.LearningMode,
            },
        };

        if (sql.IsWindowsAuth)
        {
            var agentSection = (Dictionary<string, object?>)settings["Agent"];
            agentSection.Remove("SqlUser");
            agentSection.Remove("SqlPassword");
        }

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string GetMachineFingerprint()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", writable: false);
            var guid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrEmpty(guid))
                return guid;
        }
        catch
        {
            // Registry unavailable — fall back to machine name.
        }
        return Environment.MachineName;
    }
}

internal sealed class InstallException : Exception
{
    public InstallException(string message) : base(message) { }
}
