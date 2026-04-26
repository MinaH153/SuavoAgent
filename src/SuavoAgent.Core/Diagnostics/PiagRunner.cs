using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
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
    /// pass/degraded/fail verdict. Returns null on transport failure —
    /// caller distinguishes that from a "fail" verdict.
    /// </summary>
    public async Task<PiagResult?> RunAsync(
        string trigger,
        bool includeWatchdogKillTest,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.AgentId))
        {
            _logger.LogWarning("PIAG run skipped — AgentId not configured");
            return null;
        }
        if (string.IsNullOrEmpty(_options.MachineFingerprint))
        {
            _logger.LogWarning("PIAG run skipped — MachineFingerprint not configured");
            return null;
        }

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
        try
        {
            var ipcServer = _services.GetService(typeof(IpcPipeServer)) as IpcPipeServer;
            localChecks["ipcConnected"] = ipcServer?.IsConnected ?? false;
            localChecks["ipcRejectionCount"] = IpcRejectionStats.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PIAG: IPC state collection failed");
        }

        // ---- watchdog_sla evidence (opt-in only) ------------------
        if (includeWatchdogKillTest)
        {
            // The kill-test would shoot Core, wait for Watchdog restart,
            // measure the latency. Implementing the actual shoot here is
            // dangerous: this code IS Core. The runner can't kill itself
            // and observe its own restart. Real implementation belongs
            // in the Watchdog process (or in bootstrap.ps1 -RunPiag),
            // which can supervise the kill from outside. For now, mark
            // skipped with an explicit reason so cloud knows why.
            localChecks["watchdogRestartSec"] = null;
            _logger.LogInformation(
                "PIAG: kill-test requested but not yet wired — Core can't kill itself + observe own restart. " +
                "Real measurement lives in bootstrap.ps1 -RunPiag (PR C) or Watchdog supervisor.");
        }

        // ---- POST to cloud ----------------------------------------
        var payload = new
        {
            agentId = _options.AgentId,
            machineFingerprint = _options.MachineFingerprint,
            trigger,
            localChecks,
        };

        try
        {
            var response = await _cloudClient.PostSignedAsync("/api/agent/piag-verify", payload, ct);
            if (response is null)
            {
                _logger.LogWarning("PIAG: cloud returned null — transport failure or unsigned response");
                return null;
            }
            if (!response.Value.TryGetProperty("success", out var successProp) ||
                !successProp.GetBoolean())
            {
                var error = response.Value.TryGetProperty("error", out var errorProp)
                    ? errorProp.GetString()
                    : "unknown";
                _logger.LogWarning("PIAG: cloud rejected request — {Error}", error);
                return null;
            }

            var result = response.Value.TryGetProperty("result", out var resultProp)
                ? resultProp.GetString() ?? "unknown"
                : "unknown";
            var overallReason = response.Value.TryGetProperty("overallReason", out var orProp)
                ? orProp.GetString()
                : null;

            _logger.LogInformation(
                "PIAG-1 result: {Result} (reason: {Reason})",
                result, overallReason ?? "all checks passed");

            return new PiagResult(result, overallReason, response.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PIAG: POST to cloud failed");
            return null;
        }
    }

    /// <summary>
    /// SHA-256 of every SuavoAgent.* .exe in the install dir. Cloud
    /// compares against the release manifest to detect tamper or wrong-
    /// version installs (Trip A's PR #179 was exactly this — dashboard
    /// "Reinstall" button delivered Node v1.x agent).
    /// </summary>
    private Dictionary<string, string> HashInstalledBinaries()
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Resolve install dir from current process — same pattern as
        // IpcPipeServer's anti-spoofing check. Works across install layouts.
        var installRoot = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?? AppContext.BaseDirectory;

        if (!Directory.Exists(installRoot))
        {
            _logger.LogDebug("PIAG: install root {Path} does not exist — skipping hash", installRoot);
            return hashes;
        }

        foreach (var path in Directory.EnumerateFiles(installRoot, "SuavoAgent.*.exe", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var name = Path.GetFileName(path);
                using var fs = File.OpenRead(path);
                var hashBytes = SHA256.HashData(fs);
                hashes[name] = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PIAG: hash failed for {Path}", path);
            }
        }
        return hashes;
    }
}

/// <summary>
/// Cloud response shape. result is one of "pass" | "degraded" | "fail".
/// rawResponse is kept for ack flows that want to forward the full body.
/// </summary>
public sealed record PiagResult(string Result, string? OverallReason, JsonElement RawResponse);
