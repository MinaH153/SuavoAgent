using System;

namespace SuavoAgent.Diagnostics.Phi;

/// <summary>
/// Post-regex validators shared by both scrub surfaces. NonBacktracking cannot express a
/// checksum, so the shape is matched by regex and confirmed here before redaction.
/// </summary>
public static class PhiValidators
{
    /// <summary>Resolve a <see cref="PhiPostValidator"/> to its predicate, or null for None.</summary>
    public static Func<string, bool>? Resolve(PhiPostValidator validator) => validator switch
    {
        PhiPostValidator.DeaChecksum => DeaChecksumValid,
        _ => null,
    };

    /// <summary>
    /// DEA number checksum: (d1+d3+d5) + 2*(d2+d4+d6) mod 10 must equal d7.
    /// </summary>
    public static bool DeaChecksumValid(string deaCandidate)
    {
        if (deaCandidate.Length != 9) return false;
        var digits = deaCandidate.AsSpan(2);
        if (digits.Length != 7) return false;
        for (int i = 0; i < 7; i++)
        {
            if (!char.IsDigit(digits[i])) return false;
        }
        var n1 = digits[0] - '0';
        var n2 = digits[1] - '0';
        var n3 = digits[2] - '0';
        var n4 = digits[3] - '0';
        var n5 = digits[4] - '0';
        var n6 = digits[5] - '0';
        var n7 = digits[6] - '0';
        var checksum = (n1 + n3 + n5) + 2 * (n2 + n4 + n6);
        return checksum % 10 == n7;
    }
}
