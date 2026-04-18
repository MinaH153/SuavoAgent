namespace SuavoAgent.Core.Config;

/// <summary>
/// Vision pipeline configuration. Like ReasoningOptions, Tier-2 vision is
/// OFF by default — enabling it is a conscious operator choice because it
/// adds a new HIPAA surface (screenshots on disk, even if encrypted).
/// </summary>
public sealed class VisionOptions
{
    /// <summary>
    /// When false, screen capture is disabled at the service level. Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Directory for DPAPI-encrypted screen frames. Defaults to
    /// %ProgramData%\SuavoAgent\screens\ (resolved at runtime if null).
    /// </summary>
    public string? StorageDirectory { get; set; }

    /// <summary>
    /// How long to retain encrypted screens before auto-purge. Default 24 hours.
    /// Set to 0 to disable retention (rare — mostly for compliance-heavy
    /// tenants that purge after every read).
    /// </summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>
    /// Max screens retained before oldest-first purge kicks in, regardless of
    /// retention window. Prevents disk runaway if capture cadence spikes.
    /// </summary>
    public int MaxStoredScreens { get; set; } = 500;

    /// <summary>
    /// Minimum milliseconds between captures — a simple rate limiter so a
    /// buggy caller can't overwhelm disk or extractor.
    /// </summary>
    public int MinIntervalMs { get; set; } = 1000;
}
