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
    bool IsInteractive,
    VisionRuntimeReadiness? VisionRuntime = null)
{
    /// <summary>
    /// The authoritative interactivity verdict, recomputed from the raw session ids rather
    /// than trusting the serialized <see cref="IsInteractive"/> self-report. The safety gate
    /// uses THIS so a buggy, partial, or spoofed ping (e.g. <c>IsInteractive=true</c> with no
    /// or mismatched session ids) cannot false-pass: a Helper can only drive the screen when it
    /// shares the active physical console session and that session isn't the services Session 0.
    /// </summary>
    public bool IsConsoleInteractive => HelperSessionId != 0 && HelperSessionId == ActiveConsoleSessionId;

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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in data.Value.EnumerateObject())
            {
                if (!names.Add(property.Name) || property.Name is not (
                        "Pid" or "pid" or
                        "HelperSessionId" or "helperSessionId" or
                        "ActiveConsoleSessionId" or "activeConsoleSessionId" or
                        "IsInteractive" or "isInteractive" or
                        "IsConsoleInteractive" or "isConsoleInteractive" or
                        "VisionRuntime" or "visionRuntime"))
                    return null;
            }

            var parsed = JsonSerializer.Deserialize<HelperPingInfo>(
                data.Value.GetRawText(), ParseOptions);
            if (parsed is null || parsed.Pid <= 0)
                return null;

            var runtimeProperty = data.Value.EnumerateObject()
                .FirstOrDefault(property => string.Equals(
                    property.Name,
                    "VisionRuntime",
                    StringComparison.OrdinalIgnoreCase));
            if (runtimeProperty.Name is not null &&
                runtimeProperty.Value.ValueKind != JsonValueKind.Null)
            {
                var runtime = VisionRuntimeReadiness.TryParse(runtimeProperty.Value);
                if (runtime is null)
                    return null;
                parsed = parsed with { VisionRuntime = runtime };
            }

            return parsed;
        }
        catch
        {
            return null;
        }
    }
}
