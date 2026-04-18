using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Extracts visible UI elements (buttons, inputs, labels) from a captured
/// window via UI Automation. Binds to the EXACT hwnd that was foreground
/// at capture time (via <see cref="ScreenBytes.Hwnd"/>), not the current
/// foreground — this prevents alt-tab races (Codex C-2).
///
/// Implementation contract:
///   - MUST dispose every AutomationElement retrieved from the UIA tree
///     (they hold COM references; undisposed = handle leak). (Codex C-1)
///   - MUST enforce a wall-clock budget so a pathological tree can't block
///     the vision pipeline (Codex M-2).
///   - MUST NOT throw for extraction failures — return empty list.
/// </summary>
internal interface IUiaElementExtractor
{
    /// <summary>
    /// Walks the UIA tree of the window identified by <paramref name="screen"/>.Hwnd
    /// and returns up to <paramref name="maxElements"/> visible UI elements.
    /// </summary>
    Task<IReadOnlyList<VisualElement>> ExtractAsync(
        ScreenBytes screen,
        int maxElements,
        CancellationToken ct);
}
