using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Captures the primary monitor into PNG-encoded bytes. Lives in the Helper's
/// interactive session — Core (SYSTEM) cannot capture the user's desktop.
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

    Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct);
}
