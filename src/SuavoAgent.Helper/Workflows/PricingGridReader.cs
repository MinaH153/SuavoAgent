using System.Globalization;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Pure (UI-free) helpers for interpreting the PioneerRx Pricing grid, extracted
/// from <see cref="PricingWorkflow"/> so the selection logic is unit-testable
/// without a live UIA tree.
///
/// Hardenings driven by field screenshots (Apr 4, 2026):
///  - The cheapest supplier is the min Cost across ALL rows — the grid sort is
///    user-toggleable, so never trust row 1; compute the min (SelectCheapest).
///  - Honor "Include Discontinued = No" even if the grid filter wasn't pinned,
///    by skipping rows whose Status marks them unusable (IsUsableStatus).
///  - The NDC quick-search dropdown returns red "(Do Not Use)" duplicates next
///    to the green active item; never price a Do-Not-Use item (LooksLikeDoNotUse).
/// </summary>
public static class PricingGridReader
{
    public readonly record struct SupplierRow(string Supplier, decimal Cost, string Status);

    public static bool TryParseCost(string? text, out decimal cost) =>
        decimal.TryParse(
            text ?? string.Empty,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out cost);

    /// <summary>
    /// Whether a row's Status keeps it eligible. An empty status (no Status
    /// column resolved) is treated as usable so we never over-filter when the
    /// column is absent — the grid's own "Include Discontinued = No" default
    /// already excludes them in that case.
    /// </summary>
    public static bool IsUsableStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        var s = status.Trim().ToLowerInvariant();
        return !(s.Contains("discontinued")
            || s.Contains("unavailable")
            || s.Contains("inactive")
            || s.Contains("do not use"));
    }

    /// <summary>True if the text marks a PioneerRx "(Do Not Use)" item.</summary>
    public static bool LooksLikeDoNotUse(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Replace(" ", string.Empty).Contains("donotuse", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cheapest usable supplier by Cost (ascending), skipping blank suppliers,
    /// non-positive costs, and discontinued/unavailable rows. Returns null when
    /// no row qualifies.
    /// </summary>
    public static (string supplier, decimal cost)? SelectCheapest(IEnumerable<SupplierRow> rows)
    {
        string? bestSupplier = null;
        decimal bestCost = decimal.MaxValue;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Supplier)) continue;
            if (row.Cost <= 0) continue;
            if (!IsUsableStatus(row.Status)) continue;

            if (row.Cost < bestCost)
            {
                bestCost = row.Cost;
                bestSupplier = row.Supplier.Trim();
            }
        }

        return bestSupplier == null ? null : (bestSupplier, bestCost);
    }
}
