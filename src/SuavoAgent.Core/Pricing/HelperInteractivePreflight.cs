using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Outcome of <see cref="HelperInteractivePreflight"/>. <see cref="Ok"/> false means the
/// caller must NOT drive the live screen; <see cref="Error"/> is an operator-facing reason.
/// </summary>
public sealed record HelperPreflightResult(bool Ok, string? Error, bool IsInteractive, uint HelperSessionId)
{
    public static HelperPreflightResult Pass(uint session) => new(true, null, true, session);
    public static HelperPreflightResult Fail(string error, uint session = 0) => new(false, error, false, session);
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
            return HelperPreflightResult.Fail("Helper IPC client not configured on this agent");

        if (!ipc.IsConnected)
        {
            var connected = await ipc.ConnectAsync(connectTimeout, ct).ConfigureAwait(false);
            if (!connected)
                return HelperPreflightResult.Fail(
                    "Helper command pipe unreachable — the Helper isn't running in the interactive session");
        }

        var ping = new IpcRequest(Guid.NewGuid().ToString("N"), IpcCommands.Ping, 1, null);
        var resp = await ipc.SendAsync(ping, pingTimeout, ct).ConfigureAwait(false);
        if (resp is null)
            return HelperPreflightResult.Fail(
                "Helper did not answer ping — command pipe is stranded; restart the Broker and Core");
        if (resp.Status != IpcStatus.Ok)
            return HelperPreflightResult.Fail($"Helper ping failed with status {resp.Status}");

        var info = HelperPingInfo.TryParse(resp.Data);
        if (info is null)
            return HelperPreflightResult.Fail(
                "Helper ping is missing session diagnostics — the agent is out of date; update it before running");

        if (!info.IsInteractive)
            return HelperPreflightResult.Fail(
                $"Helper is in non-interactive Session 0 (session {info.HelperSessionId}) and is blind to the screen. " +
                "Restart the Broker so it relaunches the Helper into the interactive console session.",
                info.HelperSessionId);

        return HelperPreflightResult.Pass(info.HelperSessionId);
    }
}
