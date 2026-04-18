using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Extracts visible UI elements (buttons, inputs, labels) from the current
/// foreground window via UI Automation. Produces exact pixel-space bounding
/// boxes and element roles — complementary to Tesseract OCR which extracts
/// pixel-rendered text regions.
///
/// Lives in the Helper (interactive user session) because UIA queries require
/// desktop access. Returns empty list on any failure.
/// </summary>
internal interface IUiaElementExtractor
{
    /// <summary>
    /// Walks the UIA tree of the currently foreground top-level window and
    /// returns up to <paramref name="maxElements"/> visible UI elements.
    /// </summary>
    Task<IReadOnlyList<VisualElement>> ExtractAsync(int maxElements, CancellationToken ct);
}
