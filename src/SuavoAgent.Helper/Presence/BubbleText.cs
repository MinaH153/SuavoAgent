namespace SuavoAgent.Helper.Presence;

/// <summary>Composes a one-line bubble caption from an action kind + optional
/// (already PHI-vetted) label. Null/empty label → "Clicking…"; present → "Clicking 7".</summary>
public static class BubbleText
{
    private const int MaxLabel = 48;

    public static string For(string actionKind, string? label)
    {
        var kind = string.IsNullOrWhiteSpace(actionKind) ? "Working" : actionKind.Trim();
        if (string.IsNullOrWhiteSpace(label)) return kind + "…";
        var l = label.Trim();
        if (l.Length > MaxLabel) l = l[..MaxLabel] + "…";
        return $"{kind} {l}";
    }
}
