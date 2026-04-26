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
    /// How long to retain encrypted screens before TTL-based auto-purge. Default 24 hours.
    /// <para>
    /// <b>0 = disable TTL-based purge</b>. Files still purge once MaxStoredScreens
    /// is exceeded (oldest-first), but there is no time-based cutoff. The old
    /// doc-comment claimed "0 = purge after every read" — that was never what
    /// the code did (Codex M-3). If you need purge-after-read behavior, take
    /// it up per-skill at the caller.
    /// </para>
    /// </summary>
    public int RetentionHours { get; set; } = 24;

    /// <summary>
    /// Max screens retained before oldest-first purge kicks in, regardless of
    /// retention window. Prevents disk runaway if capture cadence spikes.
    /// Must be &gt;= 1 — the store validates at construction (Codex M-5).
    /// </summary>
    public int MaxStoredScreens { get; set; } = 500;

    /// <summary>
    /// Minimum milliseconds between captures — a simple rate limiter so a
    /// buggy caller can't overwhelm disk or extractor.
    /// </summary>
    public int MinIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Optional Tesseract OCR configuration. When Enabled=false OR required
    /// binaries/traineddata aren't present, the pipeline falls back to
    /// NullScreenExtractor (empty frames).
    /// </summary>
    public TesseractOptions Tesseract { get; set; } = new();
}

/// <summary>
/// Tesseract OCR opt-in. Mirrors the Tier-2 native-libs pattern — we do NOT
/// ship Tesseract binaries by default. Operators who want OCR drop the
/// native libs and traineddata files at the configured paths.
/// </summary>
public sealed class TesseractOptions
{
    /// <summary>Default false. Enable requires operator to drop binaries.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Directory containing tesseract.dll / leptonica-1.83.1.dll / etc. on
    /// Windows. Required when Enabled=true. Extractor fails closed and
    /// returns null on load if this isn't readable.
    /// </summary>
    public string? NativeLibraryPath { get; set; }

    /// <summary>
    /// Directory containing language traineddata files (eng.traineddata
    /// and friends). Required when Enabled=true.
    /// </summary>
    public string? TessdataPath { get; set; }

    /// <summary>Tesseract language id. Default "eng".</summary>
    public string Language { get; set; } = "eng";

    /// <summary>
    /// Confidence floor (0–100 per Tesseract convention). Regions below this
    /// confidence are dropped from the output. Default 50 — Tesseract treats
    /// anything &lt; 60 as unreliable, so we default slightly more permissive
    /// but still filter out garbage.
    /// </summary>
    public int MinConfidence { get; set; } = 50;

    /// <summary>
    /// Unload the engine after this many idle seconds to free RAM (~50-100 MB).
    /// Default 45 (Codex post-Trip-A recommendation for Vision-On safety —
    /// the prior 120s let the engine sit resident long enough that the
    /// resource pressure that caused Nadim's hard reset could rebuild
    /// before the next idle-unload fired). Set 0 to keep loaded forever.
    /// </summary>
    public int IdleUnloadSeconds { get; set; } = 45;

    /// <summary>
    /// Refuse to load the Tesseract engine if the Helper process working set
    /// is already at or above this byte threshold. Default 350 MB — pairs
    /// with ResourceBudgetGuard's 500 MB soft warn / 800 MB hard kill so
    /// OCR can't push Helper into the danger zone. Set 0 to disable the
    /// headroom check.
    /// </summary>
    public long MemoryHeadroomBytes { get; set; } = 350L * 1024 * 1024;
}
