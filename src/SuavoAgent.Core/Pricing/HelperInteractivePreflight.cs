using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Stable machine-readable failure codes for <see cref="HelperPreflightResult.Code"/> — the
/// actuation-readiness probe ships these to the cloud, so they must stay static (no dynamic
/// content, no PHI) and additions must be append-only.
/// </summary>
public static class HelperPreflightCodes
{
    public const string IpcNotConfigured = "ipc_not_configured";
    /// <summary>Could not even connect — Helper down, or the single pipe instance is held.</summary>
    public const string PipeUnreachable = "pipe_unreachable";
    /// <summary>The strand: pipe connected (or stale-connected) but ping got no answer.</summary>
    public const string PingUnanswered = "ping_unanswered";
    public const string PingBadStatus = "ping_bad_status";
    public const string PingNoDiagnostics = "ping_no_diagnostics";
    /// <summary>Ping answered, but the Helper is not in the active interactive console session.</summary>
    public const string NotInteractive = "not_interactive";
}

/// <summary>
/// Outcome of <see cref="HelperInteractivePreflight"/>. <see cref="Ok"/> false means the
/// caller must NOT drive the live screen; <see cref="Error"/> is an operator-facing reason,
/// <see cref="Code"/> the stable machine code (see <see cref="HelperPreflightCodes"/>), and
/// <see cref="Info"/> the raw session diagnostics when the ping round-trip succeeded.
/// </summary>
public sealed record HelperPreflightResult(
    bool Ok, string? Error, bool IsInteractive, uint HelperSessionId,
    string? Code = null, HelperPingInfo? Info = null)
{
    public static HelperPreflightResult Pass(uint session, HelperPingInfo? info = null) =>
        new(true, null, true, session, null, info);
    public static HelperPreflightResult Fail(string error, uint session = 0, string? code = null, HelperPingInfo? info = null) =>
        new(false, error, false, session, code, info);
}

/// <summary>
/// Pre-flight gate run at the start of a pricing job before any UIA actuation touches the
/// live PMS screen. It confirms three things, failing closed on any of them:
/// <list type="number">
///   <item>the Helper command pipe is reachable (Helper process is up);</item>
///   <item>the Helper answers a round-trip ping within the timeout — ruling out a stranded
///     Core→Broker pipe where commands ACK as "sent" but never reach the Helper;</item>
///   <item>the Helper is in the interactive console session (not Session 0) — ruling out the
///     blind-actuation failure where the Helper can't see or drive the screen.</item>
/// </list>
/// Fail-closed is deliberate: if we cannot prove the Helper can see the screen, we do not let
/// the job type NDCs into the live PMS. See feedback-helper-must-run-in-interactive-session.
/// </summary>
public static class HelperInteractivePreflight
{
    public static async Task<HelperPreflightResult> CheckAsync(
        IIpcCommandClient? ipc,
        TimeSpan connectTimeout,
        TimeSpan pingTimeout,
        CancellationToken ct)
    {
        if (ipc is null)
            return HelperPreflightResult.Fail(
                "Helper IPC client not configured on this agent",
                code: HelperPreflightCodes.IpcNotConfigured);

        if (!ipc.IsConnected)
        {
            var connected = await ipc.ConnectAsync(connectTimeout, ct).ConfigureAwait(false);
            if (!connected)
                return HelperPreflightResult.Fail(
                    "Helper command pipe unreachable — the Helper isn't running in the interactive session",
                    code: HelperPreflightCodes.PipeUnreachable);
        }

        var ping = new IpcRequest(Guid.NewGuid().ToString("N"), IpcCommands.Ping, 1, null);
        var resp = await ipc.SendAsync(ping, pingTimeout, ct).ConfigureAwait(false);
        if (resp is null)
            return HelperPreflightResult.Fail(
                "Helper did not answer ping — command pipe is stranded; use restart_helper to relaunch it",
                code: HelperPreflightCodes.PingUnanswered);
        if (resp.Status != IpcStatus.Ok)
            return HelperPreflightResult.Fail(
                $"Helper ping failed with status {resp.Status}",
                code: HelperPreflightCodes.PingBadStatus);

        var info = HelperPingInfo.TryParse(resp.Data);
        if (info is null)
            return HelperPreflightResult.Fail(
                "Helper ping is missing session diagnostics — the agent is out of date; update it before running",
                code: HelperPreflightCodes.PingNoDiagnostics);

        // Re-derive the verdict from the raw session ids — do not trust the Helper's
        // self-reported IsInteractive bool. The safety decision lives in Core, not in the
        // process being checked, so a buggy/partial/spoofed self-report can't false-pass.
        if (!info.IsConsoleInteractive)
            return HelperPreflightResult.Fail(
                $"Helper is not in the active interactive console session (helper session {info.HelperSessionId}, " +
                $"active console {info.ActiveConsoleSessionId}) and is blind to the screen. " +
                "Restart the Broker so it relaunches the Helper into the interactive console session.",
                info.HelperSessionId,
                code: HelperPreflightCodes.NotInteractive,
                info: info);

        return HelperPreflightResult.Pass(info.HelperSessionId, info);
    }
}
