using System.Text.Json;
using SuavoAgent.Setup.Verify;

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
    public enum Phase { Download, WriteConfig, InstallBrain, InstallServices, Verify, Done }

    /// <summary>Percent is per-phase (0-100); null = indeterminate/no progress info.</summary>
    public sealed record PhaseEvent(Phase Phase, string Message, int? Percent = null);

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
        // Consent is the one true pre-install gate (HIPAA — no install without
        // a captured authorization). PioneerRx + SQL are deferred by design:
        // when they're absent the agent installs in "no-PMS mode", comes online,
        // heartbeats, and self-heals the SQL connection once PioneerRx appears
        // (RxDetectionWorker retries every cycle). So their absence is logged,
        // not fatal — that's the minimum-viable-control path.
        if (_ctx.Consent == null)
            throw new InvalidOperationException("Consent must be captured before install.");

        if (_ctx.Pioneer == null)
            ConsoleUI.WriteWarn("PioneerRx not detected — installing in no-PMS mode (agent self-heals when it appears).");
        if (_ctx.SqlCredentials == null)
            ConsoleUI.WriteWarn("No SQL credentials — deferred. Pricing/detection stays idle until SQL self-configures.");

        progress.Report(new PhaseEvent(Phase.Download, "Downloading SuavoAgent binaries"));
        ConsoleUI.WriteStep("Phase 3: Downloading SuavoAgent binaries");
        ConsoleUI.WriteInfo("Stopping any running SuavoAgent services before download...");
        ServiceInstaller.StopServices();
        // QA W2-C2: create + lock the install dir BEFORE downloading so the signed binaries + checksums
        // don't sit world-readable (inherited Program-Files ACL) during the download. The elevated
        // installer is an Administrator → keeps FullControl through the lockdown, so the download writes.
        Directory.CreateDirectory(_ctx.InstallDir);
        ServiceInstaller.LockdownDirectoryAcl(_ctx.InstallDir);
        var downloaded = await BinaryDownloader.DownloadAndVerifyAsync(
            _ctx.Config.ReleaseTag, _ctx.InstallDir);
        if (!downloaded)
            throw new InstallException("Binary download or verification failed.");

        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.WriteConfig, "Writing configuration"));
        ConsoleUI.WriteStep("Phase 4: Writing configuration");
        WriteConfigFiles();

        // Regenerate the Broker's integrity manifest from the just-placed binaries
        // BEFORE the services start. Without this, an install over an existing agent
        // leaves a stale/absent binaries.manifest -> the Broker rejects the (new)
        // Helper as tampered and exits -> the agent comes up "online" but BLIND.
        // The console installer (ConsoleInstaller) already does this; the GUI path
        // did not. (Parity fix — WS2 of the brain/install plan.)
        BinaryDownloader.WriteBinariesManifest(_ctx.InstallDir);

        ct.ThrowIfCancellationRequested();

        // ── The brain phase: land the model + native libs DURING install so the
        // agent boots brainReady on its first heartbeat (instead of a silent
        // multi-minute background download after). FAIL-SOFT by contract — any
        // failure logs + continues; the agent's own provisioners self-heal.
        progress.Report(new PhaseEvent(Phase.InstallBrain, "Installing the SuavoAgent brain", 0));
        ConsoleUI.WriteStep("Phase 5: Installing the SuavoAgent brain");
        if (_ctx.Config.Reasoning is { IsProvisionable: true } reasoning)
        {
            var brainProgress = new Progress<int>(p =>
                progress.Report(new PhaseEvent(Phase.InstallBrain, "Installing the SuavoAgent brain", p)));
            _ctx.BrainInstalled = await BrainInstaller.InstallAsync(
                reasoning, _ctx.DataDir, brainProgress, ct);
            if (_ctx.BrainInstalled)
                ConsoleUI.WriteOk($"Brain installed — {reasoning.ModelId} verified on disk.");
            else
                ConsoleUI.WriteWarn("Brain download deferred — the agent finishes it in the background.");
        }
        else
        {
            ConsoleUI.WriteInfo("No brain config from the cloud — the agent provisions it later.");
        }

        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.InstallServices, "Installing Windows services"));
        ConsoleUI.WriteStep("Phase 6: Installing Windows services");
        var started = ServiceInstaller.InstallAndStart(_ctx.InstallDir, _ctx.DataDir);
        if (!started)
        {
            // HARD FAIL — "Installation complete" with zero running services is a lie
            // that bricks the install invisibly (2026-06-10: missing Watchdog.exe made
            // InstallAndStart bail before registering anything; the GUI still showed
            // success and the agent never heartbeated). The ViewModel routes this to
            // the Error view with a retry path.
            throw new InstallException(
                "Windows services failed to install or start — the agent is NOT running. " +
                $"Details: {SetupLog.LogPath}");
        }

        // Phase B self-verify: prove the agent actually works before reporting "complete".
        // Same philosophy as the services hard-fail above — a green checkmark over a broken
        // install (e.g. the brain that couldn't load its native lib) is a lie. A Fail gate
        // throws here so GoToSuccess() is never reached; Warn/Skip do not block.
        progress.Report(new PhaseEvent(Phase.Verify, "Verifying installation"));
        ConsoleUI.WriteStep("Phase 7: Verifying installation");
        var verifyOutcome = await VerifierFactory.BuildDefault().RunAsync(ct);
        try
        {
            File.WriteAllText(
                Path.Combine(_ctx.DataDir, "install-verify.json"),
                PostInstallVerifier.ToJson(verifyOutcome));
        }
        catch { /* best-effort forensic artifact — never break the install on a write failure */ }
        if (!verifyOutcome.Passed)
        {
            throw new InstallException(
                $"Post-install verification failed — {verifyOutcome.Summary} Details: {SetupLog.LogPath}");
        }

        // Register in Add/Remove Programs so the pharmacy can uninstall from Settings → Apps.
        ServiceInstaller.RegisterUninstallEntry(_ctx.InstallDir, _ctx.Config.ReleaseTag.TrimStart('v'));

        progress.Report(new PhaseEvent(Phase.Done, "Installation complete"));
    }

    private void WriteConfigFiles()
    {
        _ctx.AgentId ??= _ctx.Config.AgentId;
        if (string.IsNullOrWhiteSpace(_ctx.AgentId))
            throw new InstallException("Cloud agent identity is missing. Use the dashboard bootstrap installer.");
        _ctx.MachineFingerprint ??= GetMachineFingerprint();

        // QA W2-C2: install dir was created + ACL-locked before the download (above), so the binaries
        // AND the credentials written below land in an already-protected dir.
        // The de-privileged Helper must read its single-file self-extracting apphost; without this
        // carve-out the install-dir lockdown makes it die pre-log and the Broker churns it (2026-06-10).
        ServiceInstaller.GrantInteractiveHelperExeAccess(_ctx.InstallDir);
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

    internal string BuildAppSettings()
    {
        var agent = new Dictionary<string, object?>
        {
            ["CloudUrl"] = _ctx.Config.CloudUrl,
            ["ApiKey"] = _ctx.Config.ApiKey,
            ["AgentId"] = _ctx.AgentId,
            ["PharmacyId"] = _ctx.Config.PharmacyId,
            ["MachineFingerprint"] = _ctx.MachineFingerprint,
            ["Version"] = _ctx.Config.ReleaseTag.TrimStart('v'),
            ["LearningMode"] = _ctx.Config.LearningMode,
        };

        // SQL is optional. When deferred (no PioneerRx yet) the keys are omitted
        // entirely — the agent runs in no-PMS mode and self-heals the SQL
        // connection once PioneerRx is detected. Windows auth omits user/password.
        var sql = _ctx.SqlCredentials;
        if (sql != null)
        {
            agent["SqlServer"] = sql.Server;
            agent["SqlDatabase"] = sql.Database;
            if (!sql.IsWindowsAuth)
            {
                agent["SqlUser"] = sql.User;
                agent["SqlPassword"] = sql.Password;
            }
        }

        // On-device brain: bake the cloud-supplied reasoning config so the agent
        // boots reasoning-enabled and self-provisions the model + native libs on
        // first run (no restart, no cloud command). The cloud owns the URLs/SHAs;
        // we compute the on-box paths from the data dir (Codex-corrected: the
        // provisioners hard-fail without ModelPath/NativeLibraryPath).
        BakeReasoning(agent, _ctx.Config.Reasoning, _ctx.DataDir);

        var settings = new Dictionary<string, object> { ["Agent"] = agent };
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Adds the Agent:Reasoning section when a provisionable brain config is
    /// present. Pure + static so it's unit-testable. The model lands at
    /// {dataDir}\models\{file}, the native libs extract to {dataDir}\native.
    /// </summary>
    internal static void BakeReasoning(
        Dictionary<string, object?> agent, AgentReasoningConfig? reasoning, string dataDir)
    {
        if (reasoning is not { IsProvisionable: true }) return;

        agent["Reasoning"] = new Dictionary<string, object?>
        {
            ["Enabled"] = true,
            ["ModelId"] = reasoning.ModelId,
            ["ModelUrl"] = reasoning.ModelUrl,
            ["ModelSha256"] = reasoning.ModelSha256,
            // ModelSizeBytes powers the agent's provisioning-percent telemetry
            // (download progress on the dashboard's Brain card).
            ["ModelSizeBytes"] = reasoning.ModelSizeBytes,
            // Paths come from the SHARED helpers on AgentReasoningConfig so the
            // bake and the installer's brain phase can never disagree.
            ["ModelPath"] = reasoning.GetModelPath(dataDir),
            ["NativeLibsUrl"] = reasoning.NativeLibsUrl,
            ["NativeLibsSha256"] = reasoning.NativeLibsSha256,
            ["NativeLibraryPath"] = reasoning.GetNativeLibsDir(dataDir),
            ["ContextSize"] = reasoning.ContextSize,
            ["MaxOutputTokens"] = reasoning.MaxOutputTokens,
        };
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

    /// <summary>
    /// Resolves the install-time compliance posture from a signed verticalConfig.
    /// Fail-closed: absent / unsigned / blocked / unknown all → HIPAA defaults.
    /// Only a fully-verified config may relax the posture below HIPAA.
    /// </summary>
    internal static InstallPosture ResolveInstallPosture(
        SetupConfig config, VerticalConfigVerifier? verifier = null)
    {
        var vc = new ParsedVerticalConfig(
            config.VerticalConfigRaw,
            config.VerticalConfig,
            config.VerticalConfigSignature,
            config.VerticalConfigKeyId);

        verifier ??= VerticalConfigVerifier.LoadEmbeddedTrustStore();
        var result = verifier.Verify(vc);

        if (!result.IsVerified || result.Config is null)
            return InstallPosture.HipaaDefault;  // fail-closed

        return new InstallPosture(
            ComplianceMode: result.Config.ComplianceMode,
            SystemConnector: result.Config.SystemConnector,
            ConnectorLabel: result.Config.ConnectorLabel,
            RedactionProfileId: result.Config.RedactionProfileId);
    }

    /// <summary>
    /// Bakes the resolved vertical-config posture into the Agent appsettings section.
    /// Pure + static so it is unit-testable.
    /// </summary>
    internal static void BakeVerticalConfig(
        Dictionary<string, object?> agent, InstallPosture posture)
    {
        agent["ComplianceMode"]    = posture.ComplianceMode;
        agent["SystemConnector"]   = posture.SystemConnector;
        agent["ConnectorLabel"]    = posture.ConnectorLabel;
        agent["RedactionProfileId"] = posture.RedactionProfileId;
    }
}

internal sealed class InstallException : Exception
{
    public InstallException(string message) : base(message) { }
}

/// <summary>
/// Resolved install-time posture from a verified verticalConfig.
/// Fail-closed defaults target HIPAA + PioneerRx (back-compat with existing pharmacies).
/// </summary>
internal sealed record InstallPosture(
    string ComplianceMode,
    string SystemConnector,
    string ConnectorLabel,
    string RedactionProfileId)
{
    /// <summary>Back-compat default: HIPAA + PioneerRx connector (existing pharmacy installs).</summary>
    internal static readonly InstallPosture HipaaDefault = new(
        ComplianceMode: "hipaa",
        SystemConnector: "pioneerrx",
        ConnectorLabel: "PioneerRx",
        RedactionProfileId: "phi-v1");
}
