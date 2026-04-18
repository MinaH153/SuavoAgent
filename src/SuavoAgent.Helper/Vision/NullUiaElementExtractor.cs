using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// No-op UIA extractor. Used on non-Windows platforms and as a safe
/// fallback when UIA query fails at construction time.
/// </summary>
internal sealed class NullUiaElementExtractor : IUiaElementExtractor
{
    public Task<IReadOnlyList<VisualElement>> ExtractAsync(
        ScreenBytes screen,
        int maxElements,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VisualElement>>(Array.Empty<VisualElement>());
}
