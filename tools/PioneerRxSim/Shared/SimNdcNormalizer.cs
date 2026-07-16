namespace PioneerRxSim;

/// <summary>
/// Normalizes Quick Search input to the canonical 11-digit NDC key used by the
/// simulator catalog. This intentionally mirrors the production
/// SuavoAgent.Core.Pricing.NdcNormalizer rule: only unambiguous hyphenated
/// 4-4-2, 5-3-2, 5-4-2, or already-canonical 11-digit input is accepted.
/// Unhyphenated 10-digit input remains rejected because its missing segment is
/// unknowable without the original hyphen positions.
/// </summary>
public static class SimNdcNormalizer
{
    public static string? TryNormalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        if (!trimmed.Contains('-'))
            return trimmed.Length == 11 && trimmed.All(char.IsDigit)
                ? trimmed
                : null;

        var parts = trimmed.Split('-');
        if (parts.Length != 3 ||
            parts.Any(part => part.Length == 0 || !part.All(char.IsDigit)))
            return null;

        return (parts[0].Length, parts[1].Length, parts[2].Length) switch
        {
            (4, 4, 2) => "0" + parts[0] + parts[1] + parts[2],
            (5, 3, 2) => parts[0] + "0" + parts[1] + parts[2],
            (5, 4, 2) => parts[0] + parts[1] + parts[2],
            _ => null,
        };
    }
}
