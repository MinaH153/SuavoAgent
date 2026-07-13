using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Captures one policy-approved visual surface into PNG-encoded bytes. Live PMS
/// implementations are required to be exact-window scoped; Core (SYSTEM) cannot
/// capture the user's interactive desktop directly.
///
/// Returns null on any failure (capture disabled, non-Windows, GDI error,
/// rate-limited). Never throws for capture failures — always fail-closed so
/// callers can cleanly skip vision when unavailable.
/// </summary>
public interface IScreenCapture
{
    /// <summary>
    /// True if capture is configured and the platform supports it. Checked
    /// before the interactive-session user goes to the trouble of preparing
    /// a capture context.
    /// </summary>
    bool IsAvailable { get; }

    // Historical method name retained for compatibility. Production live-PMS
    // implementations must never interpret "Primary" as the primary monitor.
    Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct);
}
