namespace SuavoAgent.Contracts.Vision;

// ---------------------------------------------------------------------------
// Vision pipeline data contracts.
//
// Three-stage pipeline from pharmacy screen → brain-consumable structure:
//   1. IScreenCapture        → raw pixels (ScreenBytes)
//   2. IScreenStore          → DPAPI-encrypted storage + TTL
//   3. IScreenExtractor      → structured ScreenFrame (PHI-scrubbed)
//
// Only ScreenFrame is allowed to cross the prompt boundary into Tier-2.
// Raw pixels and unscrubbed extractions stay on the machine, behind ACLs.
// ---------------------------------------------------------------------------

/// <summary>
/// Structured, PHI-scrubbed view of a single screen frame. This is the output
/// that can flow to Tier-2 reasoning and (if the operator enables it) to the
/// cloud reasoning tier. Raw bytes never reach this type.
/// </summary>
public sealed record ScreenFrame
{
    public required string Id { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>OCR / VLM text regions. All text is PHI-scrubbed.</summary>
    public IReadOnlyList<TextRegion> TextRegions { get; init; } = Array.Empty<TextRegion>();

    /// <summary>Detected UI elements (buttons, inputs). Labels are PHI-scrubbed.</summary>
    public IReadOnlyList<VisualElement> Elements { get; init; } = Array.Empty<VisualElement>();

    /// <summary>Extractor id (e.g. "null", "tesseract-5", "phi-3.5-vision"). Audit field.</summary>
    public required string ExtractorId { get; init; }

    /// <summary>How long the extractor took, for pattern miner + perf tracking.</summary>
    public long ExtractionLatencyMs { get; init; }
}

/// <summary>A single text region — line, paragraph, or field label.</summary>
public sealed record TextRegion
{
    public required string Text { get; init; }
    public required Rect Bounds { get; init; }

    /// <summary>OCR / VLM self-reported confidence, 0.0–1.0.</summary>
    public double Confidence { get; init; }
}

/// <summary>A detected visual UI element (button, input, icon).</summary>
public sealed record VisualElement
{
    /// <summary>Element type label — "button", "input", "checkbox", etc.</summary>
    public required string Role { get; init; }

    /// <summary>Visible text inside the element, PHI-scrubbed. Null if none.</summary>
    public string? Name { get; init; }

    public required Rect Bounds { get; init; }
    public double Confidence { get; init; }
}

/// <summary>Pixel-space bounding rectangle.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int Area => Width * Height;
}

/// <summary>
/// Raw screen bytes — lives only inside the Helper process, never serialized
/// to anything that crosses the HIPAA boundary. Callers should hand this
/// directly to IScreenStore and then drop the reference.
///
/// <para>
/// <paramref name="Hwnd"/> is the Win32 HWND of the foreground window AT
/// THE MOMENT of capture. Downstream UIA extraction must bind to THIS hwnd
/// rather than re-querying <c>GetForegroundWindow()</c>, otherwise a
/// fast alt-tab between capture and UIA walk can target the wrong process
/// (Codex C-2). Default 0 = not captured (legacy tests).
/// </para>
/// </summary>
public readonly record struct ScreenBytes(
    byte[] Png, int Width, int Height, DateTimeOffset CapturedAt, long Hwnd = 0);
