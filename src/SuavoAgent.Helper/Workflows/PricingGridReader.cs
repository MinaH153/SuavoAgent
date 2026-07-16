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
    /// A Supplier Catalog row. <see cref="CostPerUnit"/> is the current engine's admitted ranking
    /// basis: a dedicated "Cost Per Unit" field, never a relabeled raw pack Cost. The pharmacy PIC's
    /// final cost-basis decision remains a field gate; until then, an unresolved basis halts execution.
    /// </summary>
    public readonly record struct SupplierRow(string Supplier, decimal CostPerUnit, string Status);

    /// <summary>
    /// One package-cost row from the exact PioneerRx Supplier Catalog columns.
    /// This is intentionally a separate type so a package amount can never be
    /// passed to the cost-per-unit selector by accident.
    /// </summary>
    public readonly record struct PackageSupplierRow(
        string Supplier,
        decimal PackageCost,
        string Status,
        bool Linked,
        string InventoryGroup,
        bool Discontinued);

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
    /// Whether a row's Status keeps it eligible. Eligibility is an allowlist,
    /// not a denylist: only the two PioneerRx states proved safe for selection
    /// are accepted. A blank or unfamiliar status is never inferred usable.
    /// </summary>
    public static bool IsUsableStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;

        var value = status.Trim();
        return value.Equals("Available", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Active", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryParseLinked(string? text, out bool linked)
    {
        linked = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value.Equals("True", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Linked", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Checked", StringComparison.OrdinalIgnoreCase) ||
            value == "1")
        {
            linked = true;
            return true;
        }
        if (value.Equals("False", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("No", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Unchecked", StringComparison.OrdinalIgnoreCase) ||
            value == "0")
            return true;
        return false;
    }

    /// <summary>True if the text marks a PioneerRx "(Do Not Use)" item.</summary>
    public static bool LooksLikeDoNotUse(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Replace(" ", string.Empty).Contains("donotuse", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Cheapest usable supplier under the current <b>Cost Per Unit</b> engine contract (never trust
    /// top-row position), skipping blank suppliers, non-positive per-unit costs, and any row without
    /// an explicitly eligible status. Returns null when no row qualifies.
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

    /// <summary>
    /// Cheapest exact package Cost among rows explicitly linked to an Rx item,
    /// in an eligible status, and not marked discontinued.
    /// </summary>
    public static (string supplier, decimal packageCost)? SelectCheapestPackage(
        IEnumerable<PackageSupplierRow> rows)
    {
        string? bestSupplier = null;
        decimal bestPackageCost = decimal.MaxValue;
        foreach (var row in rows)
        {
            if (!row.Linked || row.Discontinued ||
                !row.InventoryGroup.Trim().Equals("Rx", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(row.Supplier) ||
                row.PackageCost <= 0 ||
                !IsUsableStatus(row.Status))
                continue;
            if (row.PackageCost < bestPackageCost)
            {
                bestPackageCost = row.PackageCost;
                bestSupplier = row.Supplier.Trim();
            }
        }
        return bestSupplier is null ? null : (bestSupplier, bestPackageCost);
    }
}
