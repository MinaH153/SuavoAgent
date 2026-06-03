using System;
using System.Collections.Generic;

namespace SuavoAgent.Contracts.Adapters;

/// <summary>
/// Per-adapter PHI column guard. HIPAA invariant: every registered adapter MUST
/// carry a non-empty policy (a no-guard adapter is a HIPAA hole) — enforced by
/// <see cref="Create"/> and re-checked at registry construction.
///
/// <see cref="IsPhiColumn"/> is a verbatim generalization of the original
/// PioneerRx column classifier: an OrdinalIgnoreCase blocklist match OR a
/// lowercased substring pattern match. The blocklist's case-insensitivity is a
/// property of the set passed in (use <see cref="StringComparer.OrdinalIgnoreCase"/>).
/// </summary>
public sealed record PhiPolicy
{
    public required IReadOnlySet<string> ColumnBlocklist { get; init; }
    public required IReadOnlyList<string> ColumnPatterns { get; init; }

    public bool IsPhiColumn(string columnName)
    {
        if (ColumnBlocklist.Contains(columnName))
            return true;

        var lower = columnName.ToLowerInvariant();
        foreach (var pattern in ColumnPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }

        return false;
    }

    public static PhiPolicy Create(IReadOnlySet<string> blocklist, IReadOnlyList<string> patterns)
        => blocklist.Count == 0 && patterns.Count == 0
            ? throw new ArgumentException("PHI policy must define at least one blocklist column or pattern.")
            : new PhiPolicy { ColumnBlocklist = blocklist, ColumnPatterns = patterns };
}
