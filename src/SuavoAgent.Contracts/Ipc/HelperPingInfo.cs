using System.Text.Json;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// Session diagnostics returned in the <see cref="IpcCommands.Ping"/> response so Core can
/// verify — at probe time — that the Helper answering the command pipe is actually running
/// in the interactive console session and can therefore see and drive the screen.
///
/// A Helper stranded in Session 0 (the non-interactive services session) is the silent
/// "commands sent but never actuate" failure: the pipe connects, commands ACK as sent, yet
/// nothing happens on the physical screen. Such a Helper reports
/// <see cref="IsInteractive"/> = false; the pricing pre-flight gate refuses to drive the
/// live PMS screen in that state.
/// </summary>
public sealed record HelperPingInfo(
    int Pid,
    uint HelperSessionId,
    uint ActiveConsoleSessionId,
    bool IsInteractive)
{
    private static readonly JsonSerializerOptions ParseOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Parses the ping payload. Returns null when the data is absent or malformed —
    /// e.g. an older Helper that answers ping with no body. Callers treat null as
    /// "cannot prove interactivity" and fail closed.
    /// </summary>
    public static HelperPingInfo? TryParse(JsonElement? data)
    {
        if (data is null || data.Value.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            return JsonSerializer.Deserialize<HelperPingInfo>(data.Value.GetRawText(), ParseOptions);
        }
        catch
        {
            return null;
        }
    }
}
