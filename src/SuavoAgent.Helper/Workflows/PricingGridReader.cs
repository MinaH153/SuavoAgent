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
    /// <summary>
    /// A Supplier Catalog row. <see cref="CostPerUnit"/> is the RANKING cost — Nadim's rule is
    /// "cheapest = argmin over <b>Cost Per Unit</b>, NOT raw Cost" (a 500-count pack can win on
    /// per-unit while losing on pack cost). Production feeds the grid's "Cost Per Unit" column here;
    /// the SQL path ranks by the per-unit column too. Never populate this with raw pack Cost.
    /// </summary>
    public readonly record struct SupplierRow(string Supplier, decimal CostPerUnit, string Status);

    public static bool TryParseCost(string? text, out decimal cost)
    {
        cost = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // PioneerRx/DevExpress currency columns render "$3.28" / "$0.0099". InvariantCulture's
        // currency symbol is "¤" (not "$"), so NumberStyles.Any rejects a "$"-prefixed cell and
        // the whole supplier batch parses to nothing → false "no supplier rows". Keep only the
        // numeric glyphs (digits, sign, decimal, thousands, accounting parens) before parsing.
        Span<char> buf = stackalloc char[text.Length];
        int n = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch) || ch is '.' or ',' or '-' or '+' or '(' or ')')
                buf[n++] = ch;
        }
        if (n == 0) return false;

        return decimal.TryParse(
            buf[..n],
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out cost);
    }

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
    /// Cheapest usable supplier by <b>Cost Per Unit</b> ascending (Nadim's rule: argmin over
    /// Cost Per Unit, NOT raw pack Cost — never trust top-row position), skipping blank suppliers,
    /// non-positive per-unit costs, and discontinued/unavailable rows. Returns null when no row
    /// qualifies.
    /// </summary>
    public static (string supplier, decimal costPerUnit)? SelectCheapest(IEnumerable<SupplierRow> rows)
    {
        string? bestSupplier = null;
        decimal bestCostPerUnit = decimal.MaxValue;

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Supplier)) continue;
            if (row.CostPerUnit <= 0) continue;
            if (!IsUsableStatus(row.Status)) continue;

            if (row.CostPerUnit < bestCostPerUnit)
            {
                bestCostPerUnit = row.CostPerUnit;
                bestSupplier = row.Supplier.Trim();
            }
        }

        return bestSupplier == null ? null : (bestSupplier, bestCostPerUnit);
    }
}
