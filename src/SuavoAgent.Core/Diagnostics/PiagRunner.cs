using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.Diagnostics;

/// <summary>
/// PIAG-1 (Pilot Install Acceptance Gate) local runner. Gathers
/// agent-side evidence (binary hashes, IPC state, optional Watchdog
/// kill-test) and POSTs it to the cloud /api/agent/piag-verify endpoint.
///
/// Spec: memory/piag-1-pilot-install-acceptance-gate-spec.md.
///
/// Trip A 2026-04-25 surfaced the systemic failure mode: pharmacy
/// installs claim "online + healthy" via heartbeat while the actual
/// capture path is dead. This runner produces the verifiable evidence
/// the cloud uses to decide pass / degraded / fail.
///
/// PRODUCTION USAGE
///   - Triggered by the run_piag signed command (cloud → agent)
///   - Also exposed via bootstrap.ps1 -RunPiag for install-time verify
///
/// SAFETY POSTURE
///   - Watchdog kill-test (which actually kills Core) is gated behind
///     an explicit `includeWatchdogKillTest` flag — NEVER auto-runs on
///     daily cron. Codex SRE recommendation: killing Core daily on a
///     pharmacy with PHI in flight is too aggressive for ambient
///     verification. Install-time + signed remote_troubleshoot only.
/// </summary>
public sealed class PiagRunner
{
    private readonly AgentOptions _options;
    private readonly SuavoCloudClient _cloudClient;
    private readonly ILogger<PiagRunner> _logger;
    private readonly IServiceProvider _services;
    // Singleton DI lifetime + multiple trigger paths (signed run_piag command,
    // bootstrap.ps1 -RunPiag, future heartbeat-anomaly trigger) means
    // concurrent RunAsync calls are possible. Serialize them so two PIAG
    // posts don't race on shared counters or POST the same evidence twice.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public PiagRunner(
        IOptions<AgentOptions> options,
        SuavoCloudClient cloudClient,
        ILogger<PiagRunner> logger,
        IServiceProvider services)
    {
        _options = options.Value;
        _cloudClient = cloudClient;
        _logger = logger;
        _services = services;
    }

    /// <summary>
    /// Runs the local-side checks, POSTs to cloud, returns the cloud's
    /// pass/degraded/fail verdict. The Outcome property of the result
    /// distinguishes transport-fail / cloud-rejected / verdict — callers
    /// can react accordingly (bootstrap exits non-zero on cloud-rejected,
    /// retries on transport-fail).
    /// </summary>
    public async Task<PiagResult> RunAsync(
        string trigger,
        bool includeWatchdogKillTest,
        CancellationToken ct)
    {
        // Snapshot config at entry — singleton lifetime + ConfigSyncWorker
        // can mutate AgentOptions mid-run, so we'd otherwise gate on one
        // value at the top and ship a different one in the payload (TOCTOU).
        var agentId = _options.AgentId;
        var machineFingerprint = _options.MachineFingerprint;
        if (string.IsNullOrEmpty(agentId))
        {
            _logger.LogWarning("PIAG run skipped — AgentId not configured");
            return PiagResult.Skipped("agent_id_unconfigured");
        }
        if (string.IsNullOrEmpty(machineFingerprint))
        {
            _logger.LogWarning("PIAG run skipped — MachineFingerprint not configured");
            return PiagResult.Skipped("machine_fingerprint_unconfigured");
        }

        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation(
                "PIAG-1 run starting (trigger={Trigger}, killTest={KillTest})",
                trigger, includeWatchdogKillTest);

            var localChecks = new Dictionary<string, object?>();

            // ---- installer_lineage evidence ---------------------------
            try
            {
                var hashes = HashInstalledBinaries();
                if (hashes.Count > 0)
                {
                    localChecks["installedBinariesSha256"] = hashes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PIAG: installer hash collection failed — installer_lineage will skip");
            }

            // ---- ipc_peer_validation evidence -------------------------
            // When IpcPipeServer is registered (in-service Core) we collect
            // live state. When it isn't (bootstrap.ps1 -RunPiag launches a
            // one-shot Core.exe that doesn't host the pipe), we OMIT these
            // fields so the cloud check uses heartbeat-side counters as the
            // source of truth instead of misinterpreting "not collected" as
            // "connection broken."
            try
            {
                var ipcServer = _services.GetService<IpcPipeServer>();
                if (ipcServer is null)
                {
                    _logger.LogInformation(
                        "PIAG: IpcPipeServer not registered (run-piag mode) — IPC evidence omitted; " +
                        "cloud will rely on heartbeat counters from the running service.");
                }
                else
                {
                    localChecks["ipcConnected"] = ipcServer.IsConnected;
                    // Atomic snapshot — count + reason + timestamp ship together.
                    var ipcStats = IpcRejectionStats.Snapshot();
                    localChecks["ipcRejectionCount"] = ipcStats.Count;
                    if (ipcStats.LastReason is not null)
                    {
                        localChecks["ipcLastRejectionReason"] = ipcStats.LastReason;
                    }
                    if (ipcStats.LastAt is { } at)
                    {
                        localChecks["ipcLastRejectionAt"] = at.ToString("o");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PIAG: IPC state collection failed");
            }

            // ---- watchdog_sla evidence (opt-in only) ------------------
            // When kill-test is requested but Core can't supervise its own
            // restart, OMIT the field entirely. Sending null risks the
            // cloud check function misinterpreting it as zero-instant
            // restart = pass. The cloud's watchdog_sla check correctly
            // returns 'skipped' when the field is missing. Real kill-test
            // measurement lives in bootstrap.ps1 -RunPiag (PR C) or in a
            // Watchdog-supervised path (PR follow-up).
            if (includeWatchdogKillTest)
            {
                _logger.LogInformation(
                    "PIAG: kill-test requested but Core can't kill itself + observe own restart. " +
                    "Real measurement requires bootstrap.ps1 -RunPiag (PR C) or Watchdog supervisor. " +
                    "Field will be omitted from payload so cloud check skips correctly.");
            }

            // ---- POST to cloud ----------------------------------------
            var payload = new
            {
                agentId,
                machineFingerprint,
                trigger,
                localChecks,
            };

            JsonElement? response;
            try
            {
                response = await _cloudClient
                    .PostSignedAsync("/api/agent/piag-verify", payload, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled — propagate, don't disguise as transport
                // failure (signed-command handler relies on cancellation
                // to honor stop signals).
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PIAG: POST to cloud threw");
                return PiagResult.TransportError(ex.GetType().Name);
            }

            if (response is null)
            {
                _logger.LogWarning("PIAG: cloud returned null — transport failure or unsigned response");
                return PiagResult.TransportError("null_response");
            }
            if (!response.Value.TryGetProperty("success", out var successProp) ||
                !successProp.GetBoolean())
            {
                var error = response.Value.TryGetProperty("error", out var errorProp)
                    ? errorProp.GetString() ?? "unknown"
                    : "unknown";
                _logger.LogWarning("PIAG: cloud rejected request — {Error}", error);
                return PiagResult.CloudRejected(error);
            }

            var verdict = response.Value.TryGetProperty("result", out var resultProp)
                ? resultProp.GetString() ?? "unknown"
                : "unknown";
            var overallReason = response.Value.TryGetProperty("overallReason", out var orProp)
                ? orProp.GetString()
                : null;

            _logger.LogInformation(
                "PIAG-1 result: {Verdict} (reason: {Reason})",
                verdict, overallReason ?? "all checks passed");

            return PiagResult.Verdict(verdict, overallReason);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// SHA-256 of every SuavoAgent.* .exe in the install dir. Cloud
    /// compares against the release manifest to detect tamper or wrong-
    /// version installs (Trip A's PR #179 was exactly this — dashboard
    /// "Reinstall" button delivered Node v1.x agent).
    ///
    /// CAVEAT — agent self-reports its own hashes. A tampered binary can
    /// lie about hashes. The cloud's installer_lineage check still depends
    /// on a release-pipeline-signed manifest as the trust root. Until that
    /// manifest exists, this evidence is honest-when-honest only.
    /// Documented in piag-1 spec.
    /// </summary>
    private Dictionary<string, string> HashInstalledBinaries()
    {
        // Resolve install dir from current process — same pattern as
        // IpcPipeServer's anti-spoofing check. Works across install layouts.
        var installRoot = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?? AppContext.BaseDirectory;
        return HashAgentBinariesIn(installRoot, _logger);
    }

    /// <summary>
    /// Internal-visible helper for unit tests — hash every SuavoAgent.*.exe
    /// in the provided dir, with the same reparse-point + filehandle-share
    /// hardening the production path uses.
    /// </summary>
    internal static Dictionary<string, string> HashAgentBinariesIn(
        string installRoot,
        ILogger logger)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(installRoot))
        {
            logger.LogDebug("PIAG: install root not found — skipping hash");
            return hashes;
        }

        // Enumerate with reparse-point skip. Without this, an attacker who
        // plants a junction in the install dir could redirect SHA-256 reads
        // to legit signed binaries elsewhere, defeating tamper detection.
        var enumOpts = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };

        foreach (var path in Directory.EnumerateFiles(installRoot, "SuavoAgent.*.exe", enumOpts))
        {
            try
            {
                // Defense in depth — even with EnumerationOptions filter,
                // re-check the resolved file's attributes before opening.
                var attrs = File.GetAttributes(path);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    logger.LogWarning(
                        "PIAG: skipping reparse-point in install dir — possible tamper attempt");
                    continue;
                }
                var name = Path.GetFileName(path);
                using var fs = File.Open(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    Options = FileOptions.SequentialScan,
                });
                var hashBytes = SHA256.HashData(fs);
                hashes[name] = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                // No path in info-level logs — install layout is operational
                // fingerprint. Filename only.
                logger.LogDebug(ex, "PIAG: hash failed for {File}", Path.GetFileName(path));
            }
        }
        return hashes;
    }
}

/// <summary>
/// Cloud response wrapper. Outcome distinguishes the 4 paths so callers
/// (bootstrap, signed-command handler) can react correctly:
///   - Verdict: cloud actually ran the checks; Result holds pass/degraded/fail
///   - CloudRejected: cloud said success=false (HMAC/zod/auth)
///   - TransportError: network / timeout / null response
///   - Skipped: pre-flight failed locally (config not ready)
/// </summary>
public sealed record PiagResult(
    PiagOutcome Outcome,
    string? Result,
    string? OverallReason,
    string? ErrorCode)
{
    public static PiagResult Verdict(string result, string? overallReason) =>
        new(PiagOutcome.Verdict, result, overallReason, null);
    public static PiagResult CloudRejected(string error) =>
        new(PiagOutcome.CloudRejected, null, null, error);
    public static PiagResult TransportError(string error) =>
        new(PiagOutcome.TransportError, null, null, error);
    public static PiagResult Skipped(string reason) =>
        new(PiagOutcome.Skipped, null, null, reason);
}

public enum PiagOutcome
{
    Verdict,
    CloudRejected,
    TransportError,
    Skipped,
}
