using System.Text.Json;
using System.Security.Principal;
using SuavoAgent.Core.Compliance;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Security;
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
    private string? _enrolledSqlServerCertificateDigest;

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
        using var installerTransactionLock = InstallerTransactionLock.Acquire();
        // Repeat every UI preflight invariant here so a future alternate caller
        // cannot bypass pharmacy activation requirements and stage a cohort that
        // can never earn probation health.
        if (_ctx.Consent == null)
            throw new InvalidOperationException("Consent must be captured before install.");
        if (_ctx.Pioneer == null)
            throw new InstallException(
                "PioneerRx must be installed and detected before SuavoAgent can be installed.");
        if (_ctx.SqlCredentials == null)
            throw new InstallException(
                "PioneerRx SQL access must be verified before SuavoAgent can be installed.");

        var nativeCoordinator = new NativeInstallCoordinator();
        var recovery = nativeCoordinator.RecoverIncomplete(
            _ctx.InstallDir,
            _ctx.DataDir,
            replacementConfig: _ctx.Config);
        if (!recovery.Succeeded && !recovery.RolledBack)
            throw new InstallException(
                $"A prior native install transaction could not be recovered ({recovery.Code}). " +
                "The existing agent was left untouched; contact Suavo support.");
        var preparation = NativeInstallCoordinator.CreatePreparation(_ctx.InstallDir, _ctx.DataDir);
        NativeInstallCoordinator.SecurePreparationDirectories(preparation);

        progress.Report(new PhaseEvent(Phase.Download, "Downloading SuavoAgent binaries"));
        ConsoleUI.WriteStep("Phase 3: Downloading SuavoAgent binaries");
        ConsoleUI.WriteInfo("Staging the signed release while the current agent remains online...");
        var downloaded = await BinaryDownloader.DownloadAndVerifyAsync(
            _ctx.Config.ReleaseTag, preparation.StagingDirectory);
        if (!downloaded)
            throw new InstallException("Binary download or verification failed.");

        // Mandatory native maintenance bridge: stage the exact running signed Setup PE
        // inside the already-locked install directory. Watchdog/Broker repair must never
        // depend on a mutable script or the user's Downloads copy.
        var maintenance = MaintenanceHostInstaller.StageCurrentProcess(preparation.StagingDirectory);
        ConsoleUI.WriteOk(
            $"Native maintenance host staged ({maintenance.Sha256[..12]}...)");

        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.WriteConfig, "Writing configuration"));
        ConsoleUI.WriteStep("Phase 4: Writing configuration");
        WriteConfigFiles(preparation.StagingDirectory);

        // Regenerate the Broker's integrity manifest from the just-placed binaries
        // BEFORE the services start. Without this, an install over an existing agent
        // leaves a stale/absent binaries.manifest -> the Broker rejects the (new)
        // Helper as tampered and exits -> the agent comes up "online" but BLIND.
        // Seal the native cohort before any service mutation.
        var sealedCohort = NativeInstallCoordinator.SealPreparedCohort(
            preparation,
            _ctx.Config.ReleaseTag);
        if (!sealedCohort.IsValid)
            throw new InstallException(
                $"The staged signed release failed its complete-cohort proof ({sealedCohort.Code}); " +
                "the running agent was not stopped.");
        ConsoleUI.WriteOk("Complete signed five-binary cohort sealed for native activation");

        ct.ThrowIfCancellationRequested();

        // Land the complete model/native pair in a content-addressed cohort
        // while the prior Core remains online. A configured brain is a required
        // activation prerequisite: continuing after a partial/failed package
        // would let the new Core mutate its live native directory on first boot.
        progress.Report(new PhaseEvent(Phase.InstallBrain, "Installing the SuavoAgent brain", 0));
        ConsoleUI.WriteStep("Phase 5: Installing the SuavoAgent brain");
        if (_ctx.Config.Reasoning is { Enabled: true } reasoning)
        {
            var brainProgress = new Progress<int>(p =>
                progress.Report(new PhaseEvent(Phase.InstallBrain, "Installing the SuavoAgent brain", p)));
            _ctx.BrainInstalled = await BrainInstaller.InstallAsync(
                reasoning, _ctx.DataDir, brainProgress, ct);
            if (_ctx.BrainInstalled)
                ConsoleUI.WriteOk($"Brain installed — {reasoning.ModelId} verified on disk.");
            else
                throw new InstallException(
                    "The signed on-device brain package could not be fully verified. " +
                    "No reasoning cohort was activated; check the network and retry Setup.");
        }
        else
        {
            ConsoleUI.WriteInfo("No brain config from the cloud — the agent provisions it later.");
        }

        ct.ThrowIfCancellationRequested();

        progress.Report(new PhaseEvent(Phase.InstallServices, "Installing Windows services"));
        ConsoleUI.WriteStep("Phase 6: Installing Windows services");
        VerifyOutcome? verifyOutcome = null;
        var provisioningId = InitialCredentialPersister.Stage(_ctx.DataDir, _ctx.Config);
        ConsoleUI.WriteOk("Target-bound cloud credential staged for health probation");
        InstallTransactionResult transaction;
        try
        {
            transaction = nativeCoordinator.Execute(
                preparation,
                () =>
                {
                    verifyOutcome = NativeInstallHealthMilestone.WaitAsync(
                            _ctx.InstallDir,
                            _ctx.DataDir,
                            TimeSpan.FromSeconds(90),
                            ct)
                        .GetAwaiter()
                        .GetResult();
                    return verifyOutcome.Passed;
                },
                promoteAuthority: () =>
                    DeviceTokenConfirmation.ConfirmAsync(
                            _ctx.Config,
                            provisioningId,
                            ct,
                            sqlServerCertificateSha256: _enrolledSqlServerCertificateDigest)
                        .GetAwaiter()
                        .GetResult(),
                finalizeAuthority: () =>
                {
                    InitialCredentialPersister.Commit(_ctx.DataDir, _ctx.Config);
                    DeviceKeyCutover.Commit(
                        _ctx.Config,
                        _ctx.MachineFingerprint ?? throw new InstallException(
                            "Machine identity is missing during TPM key cutover."));
                    return nativeCoordinator.RestartPromotedCohort(
                        preparation.LiveDirectory,
                        preparation.DataDirectory,
                        TimeSpan.FromSeconds(90));
                },
                requiresAuthorityPromotion: true,
                afterJournalPrepared: () =>
                    DeviceKeyCutover.PreserveForRecovery(_ctx.Config));
        }
        catch
        {
            DeviceKeyCutover.Abort(
                _ctx.Config,
                _ctx.MachineFingerprint ?? throw new InstallException(
                    "Machine identity is missing while aborting TPM key probation."));
            InitialCredentialPersister.Abort(_ctx.DataDir, _ctx.Config);
            throw;
        }
        if (!transaction.Succeeded)
        {
            var forwardOnly = transaction.Code.StartsWith(
                "authority_finalization_failed",
                StringComparison.Ordinal) || transaction.Code.StartsWith(
                "forward_recovery_required:",
                StringComparison.Ordinal) || transaction.Code.StartsWith(
                "authority_promotion_unknown",
                StringComparison.Ordinal);
            if (!forwardOnly)
            {
                DeviceKeyCutover.Abort(
                    _ctx.Config,
                    _ctx.MachineFingerprint ?? throw new InstallException(
                        "Machine identity is missing while aborting TPM key probation."));
                InitialCredentialPersister.Abort(_ctx.DataDir, _ctx.Config);
            }
            // HARD FAIL — "Installation complete" with zero running services is a lie
            // that bricks the install invisibly (2026-06-10: missing Watchdog.exe made
            // InstallAndStart bail before registering anything; the GUI still showed
            // success and the agent never heartbeated). The ViewModel routes this to
            // the Error view with a retry path.
            throw new InstallException(
                $"Windows activation failed ({transaction.Code}). " +
                (transaction.RolledBack
                    ? "The prior working agent was restored. "
                    : "Automatic rollback could not be proven; contact Suavo support. ") +
                $"Details: {SetupLog.LogPath}");
        }
        InitialCredentialPersister.Complete(_ctx.DataDir, _ctx.Config);
        ConsoleUI.WriteOk("Healthy credential and versioned device authority key promoted locally");

        // Never reuse the pre-promotion probation verdict. The restarted Core
        // must independently re-prove active cloud auth plus the complete
        // Helper/IPC/PioneerRx workstation path before Setup can show success.
        verifyOutcome = null;

        // Phase B self-verify: prove the agent actually works before reporting "complete".
        // Same philosophy as the services hard-fail above — a green checkmark over a broken
        // install (e.g. the brain that couldn't load its native lib) is a lie. A Fail gate
        // throws here so GoToSuccess() is never reached; Warn/Skip do not block.
        progress.Report(new PhaseEvent(Phase.Verify, "Verifying installation"));
        ConsoleUI.WriteStep("Phase 7: Verifying installation");
        verifyOutcome ??= await NativeInstallHealthMilestone.WaitAsync(
            _ctx.InstallDir,
            _ctx.DataDir,
            TimeSpan.FromSeconds(30),
            ct);
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
        // This is a mandatory lifecycle control: failure bubbles to the GUI and
        // installation is never reported complete without Repair/Uninstall.
        ServiceInstaller.RegisterUninstallEntry(_ctx.InstallDir, _ctx.Config.ReleaseTag.TrimStart('v'));

        QueuePioneerRxHumanApproval();

        progress.Report(new PhaseEvent(Phase.Done, "Installation complete"));
    }

    internal void WriteConfigFiles(string installDirectory)
    {
        _ctx.AgentId ??= _ctx.Config.AgentId;
        if (string.IsNullOrWhiteSpace(_ctx.AgentId))
            throw new InstallException("Cloud agent identity is missing. Download a connected installer from the dashboard.");
        _ctx.MachineFingerprint ??= GetMachineFingerprint();

        // QA W2-C2: install dir was created + ACL-locked before the download (above), so the binaries
        // and the immutable config written below lands in an already-protected dir.
        // The de-privileged Helper must read its single-file self-extracting apphost; without this
        // carve-out the install-dir lockdown makes it die pre-log and the Broker churns it (2026-06-10).
        ServiceInstaller.GrantInteractiveHelperExeAccess(installDirectory);
        Directory.CreateDirectory(_ctx.DataDir);
        // Certificate enrollment performs an elevated file create/move. Pin the
        // complete ProgramData tree first so a preplanted junction cannot turn
        // that write or its ACL assignment into a target-side mutation.
        ServiceInstaller.LockdownDataDirectoryAcl(_ctx.DataDir);

        var enrollment = SqlServerCertificateEnrollment.EnrollSelectedOrExisting(
            _ctx.SqlServerCertificateSourcePath,
            _ctx.DataDir);
        _enrolledSqlServerCertificateDigest = enrollment?.Digest;
        if (enrollment is not null)
        {
            ConsoleUI.WriteOk("Exact PioneerRx SQL server certificate enrolled");
        }

        File.WriteAllText(
            Path.Combine(installDirectory, "appsettings.json"),
            BuildAppSettings());
        // Persist the last-known-good compliance posture so a later install/OTA can't
        // silently relax it (anti-downgrade floor). Same posture computed in BuildAppSettings.
        var resolvedMode = ResolveInstallPosture(
            _ctx.Config,
            lastKnownGood: LastKnownGoodStore.TryRead(_ctx.DataDir) ?? ComplianceMode.None).ComplianceMode;
        LastKnownGoodStore.Write(_ctx.DataDir, CompliancePosture.Resolve(resolvedMode));

        File.WriteAllText(
            Path.Combine(_ctx.DataDir, "consent-receipt.json"),
            _ctx.Consent!.ToJson(
                pharmacyId: _ctx.Config.PharmacyId,
                agentId: _ctx.AgentId!,
                installerVersion: _ctx.InstallerVersion,
                machineFingerprint: _ctx.MachineFingerprint!));

        ConsoleUI.WriteOk("Secret-free appsettings + consent receipt written");

    }

    internal string? EnrolledSqlServerCertificateDigest =>
        _enrolledSqlServerCertificateDigest;

    internal void QueuePioneerRxHumanApproval()
    {
        if (string.IsNullOrWhiteSpace(_enrolledSqlServerCertificateDigest))
        {
            ConsoleUI.WriteWarn(
                "PioneerRx live control remains observe-only until an exact SQL server certificate is enrolled.");
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value;
        if (string.IsNullOrWhiteSpace(sid))
            throw new InstallException(
                "The approving Windows administrator identity could not be verified.");
        var consentJson = _ctx.Consent!.ToJson(
            pharmacyId: _ctx.Config.PharmacyId,
            agentId: _ctx.AgentId!,
            installerVersion: _ctx.InstallerVersion,
            machineFingerprint: _ctx.MachineFingerprint!);
        PioneerRxApprovalBootstrapRequestWriter.Queue(sid, consentJson);
        ConsoleUI.WriteOk(
            "PioneerRx identity review queued — observation stays active while privileged approval is pending.");
    }

    internal string BuildAppSettings(
        Func<string, string>? protectSqlPassword = null,
        VerticalConfigVerifier? verticalConfigVerifier = null,
        string? sqlServerCertificateDigest = null)
    {
        var agent = new Dictionary<string, object?>
        {
            ["CloudUrl"] = _ctx.Config.CloudUrl,
            ["AgentId"] = _ctx.AgentId,
            ["PharmacyId"] = _ctx.Config.PharmacyId,
            ["MachineFingerprint"] = _ctx.MachineFingerprint,
            // Public key identifier only (never private material). Helper binds
            // device-signed local PioneerRx approvals to the exact enrolled TPM
            // key instead of trusting a self-supplied key inside the receipt.
            ["DeviceAttestationKeyId"] = _ctx.Config.DeviceKeyId,
            ["MaintenanceAttestationKeyId"] = _ctx.Config.MaintenanceKeyId,
            ["Version"] = _ctx.Config.ReleaseTag.TrimStart('v'),
            ["LearningMode"] = _ctx.Config.LearningMode,
        };
        if (_ctx.Config.LearningMode)
        {
            agent["TemplateLearning"] = new Dictionary<string, object?>
            {
                ["Enabled"] = true,
                ["Mode"] = "capture",
                ["RuleGeneration"] = false,
                ["AutoApproveOnFingerprintMatch"] = false,
            };
        }

        // Install preflight requires a verified PioneerRx SQL connection. Windows
        // authentication intentionally omits user/password; SQL authentication
        // includes only the credentials already validated during System Check.
        var sql = _ctx.SqlCredentials;
        if (sql != null)
        {
            agent["SqlServer"] = sql.Server;
            agent["SqlDatabase"] = sql.Database;
            var enrolledDigest = sqlServerCertificateDigest ?? _enrolledSqlServerCertificateDigest;
            if (enrolledDigest is not null)
                agent["SqlServerCertificateSha256"] = enrolledDigest;
            if (!sql.IsWindowsAuth)
            {
                agent["SqlUser"] = sql.User;
                var password = sql.Password
                               ?? throw new InvalidOperationException("SQL authentication password is missing.");
                agent["SqlPassword"] = (protectSqlPassword ?? InitialCredentialPersister.ProtectSqlPassword)(password);
            }
        }

        // On-device brain: bake the cloud-supplied reasoning config so the agent
        // boots reasoning-enabled and self-provisions the model + native libs on
        // first run (no restart, no cloud command). The cloud owns the URLs/SHAs;
        // we compute the on-box paths from the data dir (Codex-corrected: the
        // provisioners hard-fail without ModelPath/NativeLibraryPath).
        BakeReasoning(agent, _ctx.Config.Reasoning, _ctx.DataDir);

        // Vertical-config posture reads the box's last-known-good so a verified downgrade is
        // refused (anti-downgrade); absent/unsigned/invalid config → HIPAA+PioneerRx default.
        var lkg = LastKnownGoodStore.TryRead(_ctx.DataDir) ?? ComplianceMode.None;
        BakeVerticalConfig(agent, ResolveInstallPosture(
            _ctx.Config,
            verticalConfigVerifier,
            lastKnownGood: lkg));

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
            ["NativeLibsSizeBytes"] = reasoning.NativeLibsSizeBytes,
            ["NativePackageKind"] = reasoning.NativePackageKind,
            ["NativeLibraryPath"] = reasoning.GetNativeLibsDir(dataDir),
            ["ContextSize"] = reasoning.ContextSize,
            ["MaxOutputTokens"] = reasoning.MaxOutputTokens,
            // The Core re-verifies the independent offline publisher
            // authorization before Tier-2 can load or provision anything.
            ["SchemaVersion"] = reasoning.SchemaVersion,
            ["CohortId"] = reasoning.CohortId,
            ["IssuedAtUtc"] = reasoning.IssuedAtUtc,
            ["ExpiresAtUtc"] = reasoning.ExpiresAtUtc,
            ["KeyId"] = reasoning.KeyId,
            ["Signature"] = reasoning.Signature,
            ["ModelKeyId"] = reasoning.ModelKeyId,
            ["ModelSignature"] = reasoning.ModelSignature,
            ["NativeKeyId"] = reasoning.NativeKeyId,
            ["NativeSignature"] = reasoning.NativeSignature,
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
        SetupConfig config, VerticalConfigVerifier? verifier = null,
        ComplianceMode lastKnownGood = ComplianceMode.None)
    {
        var vc = new ParsedVerticalConfig(
            config.VerticalConfigRaw,
            config.VerticalConfig,
            config.VerticalConfigSignature,
            config.VerticalConfigKeyId);

        verifier ??= VerticalConfigVerifier.LoadEmbeddedTrustStore();
        var result = verifier.Verify(vc);

        if (!result.IsVerified || result.Config is null)
            throw new InstallException(
                "The cloud did not provide a valid signed workstation profile " +
                $"({result.FailureReason ?? "vertical_config_invalid"}). Retry after Suavo restores signing.");

        // Anti-downgrade (spec rule #2, TLS-style): a verified config may HOLD or RAISE
        // strictness vs the last-known-good, never relax below it. A verified *downgrade*
        // (e.g. a once-HIPAA box receiving a signed 'none') is refused → strict HIPAA+PioneerRx.
        var incoming = CompliancePosture.Resolve(result.Config.ComplianceMode);
        if (CompliancePosture.Enforce(incoming, lastKnownGood) != incoming)
            return InstallPosture.HipaaDefault;
        // ponytail: pci doesn't ship; lkg ∈ {none,hipaa} so HipaaDefault is the right refusal
        // target. Add a PciDefault here only when a pci vertical ships.

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
