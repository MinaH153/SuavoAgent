namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Normalizes NDC (National Drug Code) strings to HIPAA 11-digit form (5-4-2).
///
/// NDC source formats in the wild:
///   • 4-4-2 hyphenated  (e.g. "0006-0734-60")  → pad labeler to 5     → "00060-0734-60" → "00060073460"
///   • 5-3-2 hyphenated  (e.g. "50242-041-21")  → pad product to 4     → "50242-0041-21" → "50242004121"
///   • 5-4-2 hyphenated  (e.g. "50242-0041-21") → pass-through         → "50242004121"
///   • 10-digit unhyphenated: AMBIGUOUS — could be any of the above, cannot disambiguate alone
///   • 11-digit unhyphenated: already normalized HIPAA form, pass-through
///
/// The prior implementation (<c>raw.Replace("-", "").PadLeft(11, '0')</c>) silently produced
/// WRONG results for 4-4-2 and 5-3-2 formats. Example:
///   "0006-0734-60" → "0006073460" → "00006073460"   (WRONG — correct is "00060073460")
///   "50242-041-21" → "5024204121" → "05024204121"   (WRONG — correct is "50242004121")
/// The wrong values fail to match PioneerRx's stored NDC and the agent reports "no supplier rows"
/// for rows that actually have data.
/// </summary>
public static class NdcNormalizer
{
    public enum NdcShape
    {
        Invalid,
        Format442,
        Format532,
        Format542,
        Digits10,
        Digits11,
    }

    public readonly record struct NormalizeOutcome(
        bool Ok,
        string? Canonical11,
        NdcShape Shape,
        string? Reason)
    {
        public static NormalizeOutcome Fail(string reason, NdcShape shape = NdcShape.Invalid)
            => new(false, null, shape, reason);
    }

    /// <summary>
    /// Normalizes an NDC to 11-digit HIPAA form. Returns the canonical value and the detected shape.
    /// If the input is ambiguous (10 unhyphenated digits) returns Fail — caller must request
    /// source format clarification rather than guess.
    /// </summary>
    public static NormalizeOutcome Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return NormalizeOutcome.Fail("Empty NDC");

        var trimmed = input.Trim();

        if (trimmed.Contains('-'))
            return NormalizeHyphenated(trimmed);

        if (!trimmed.All(char.IsDigit))
            return NormalizeOutcome.Fail($"Non-digit characters: '{trimmed}'");

        return trimmed.Length switch
        {
            11 => new NormalizeOutcome(true, trimmed, NdcShape.Digits11, null),
            10 => NormalizeOutcome.Fail(
                "Ambiguous 10-digit NDC — source format (4-4-2 / 5-3-2 / 5-4-2) unknown. " +
                "Store the on-label hyphenated form or confirm the format.",
                NdcShape.Digits10),
            _ => NormalizeOutcome.Fail($"Unexpected length {trimmed.Length} for unhyphenated NDC"),
        };
    }

    private static NormalizeOutcome NormalizeHyphenated(string trimmed)
    {
        var parts = trimmed.Split('-');
        if (parts.Length != 3)
            return NormalizeOutcome.Fail($"Hyphenated NDC must have 3 segments, got {parts.Length}");

        if (parts.Any(p => p.Length == 0 || !p.All(char.IsDigit)))
            return NormalizeOutcome.Fail($"Hyphenated NDC segments must be digits: '{trimmed}'");

        var a = parts[0].Length;
        var b = parts[1].Length;
        var c = parts[2].Length;

        (string labeler, string product, string package, NdcShape shape) = (a, b, c) switch
        {
            (4, 4, 2) => ("0" + parts[0], parts[1],         parts[2], NdcShape.Format442),
            (5, 3, 2) => (parts[0],        "0" + parts[1], parts[2], NdcShape.Format532),
            (5, 4, 2) => (parts[0],        parts[1],        parts[2], NdcShape.Format542),
            _ => (string.Empty, string.Empty, string.Empty, NdcShape.Invalid),
        };

        if (shape == NdcShape.Invalid)
            return NormalizeOutcome.Fail(
                $"Unsupported NDC segment lengths {a}-{b}-{c} — expected 4-4-2, 5-3-2, or 5-4-2");

        return new NormalizeOutcome(true, labeler + product + package, shape, null);
    }

    /// <summary>
    /// Best-effort variant: returns null instead of Fail for callers that want a simple API.
    /// Prefer <see cref="Normalize"/> when error reporting matters.
    /// </summary>
    public static string? TryNormalize(string? input)
        => Normalize(input).Canonical11;
}
