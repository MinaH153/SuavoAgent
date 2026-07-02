using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Reads the PioneerRx Pricing / Supplier-Catalog grid BY SIGHT — parses the OCR
/// <see cref="TextRegion"/>s of a captured grid image into supplier rows and ranks the cheapest,
/// so SuavoAgent can determine "the one on top" the way Nadim does visually, not only via the UIA
/// element tree or SQL. Pure + UI-free so it is unit-testable with synthetic regions (the capture +
/// Tesseract OCR run only on the box).
///
/// The vision read is the PRIMARY driver; the exact cost is CONFIRMED against the UIA read by
/// <see cref="VisionExactReconciler"/> so an OCR misread never writes wrong pricing (money safety).
///
/// Column semantics: Tesseract emits one region per text LINE, so a grid row can arrive either as a
/// single region ("Mckesson 8.42 0.0099 Available") or as several per-cell regions on the same Y.
/// We flatten each visual row (regions grouped by Y-overlap, ordered by X) to a token stream and
/// classify: leading alphabetic tokens = supplier, decimal-point tokens = candidate costs, a trailing
/// keyword = status. The row's per-unit cost is the SMALLEST positive decimal on the row (per-unit is
/// always ≤ pack cost), which gives a consistent RANKING across rows; the exact value of the winner is
/// then supplied by UIA at the verify step.
/// </summary>
public static class VisionSupplierGridParser
{
    /// <summary>One parsed grid row. <see cref="CostPerUnit"/> is null when no decimal cost was read.
    /// <see cref="HasId"/> is true when the row carries an item id (NDC/UPC) — the signature of a real
    /// PioneerRx supplier row, used to drop the top-of-window pricing panel from the ranking.</summary>
    public readonly record struct ParsedRow(
        string Supplier, decimal? CostPerUnit, string Status, double Confidence, int Y, bool HasId = false);

    /// <summary>The cheapest supplier the vision read found, with the min confidence of the rows that
    /// fed the ranking (conservative) and how many usable rows were seen.</summary>
    public sealed record VisionGridReading(
        string Supplier, decimal CostPerUnit, double Confidence, int UsableRowCount);

    // A cost cell must carry a decimal point — that excludes NDCs ("60505-0829-01"), UPCs, and integer
    // quantities ("500"), which would otherwise be mistaken for a price.
    private static bool IsCostToken(string token) =>
        token.Contains('.', StringComparison.Ordinal)
        && PricingGridReader.TryParseCost(token, out var c)
        && c > 0m;

    /// <summary>
    /// Ranks the cheapest usable supplier from the grid's OCR regions, or null when no row qualifies
    /// (empty grid, all discontinued, nothing parsed above the confidence floor).
    /// </summary>
    public static VisionGridReading? ReadCheapest(
        IReadOnlyList<TextRegion> regions, double minRowConfidence = 0.5)
    {
        var rows = ParseRows(regions);

        // On the wide real-PioneerRx grid every supplier row carries the item's NDC/UPC id. When we see
        // any such row, treat it as the real Supplier Catalog and drop rows WITHOUT an id — the pricing
        // panel above the grid (AWP Source / Max AWP / NADAC / Average Received Cost …) OCRs as
        // decimal-bearing lines that would otherwise be ranked as phantom "suppliers". A narrow grid
        // (the sim / synthetic tests, no ids anywhere) keeps every row, preserving the original behaviour.
        var wideGrid = rows.Any(r => r.HasId);
        var priced = rows.Where(r => r.CostPerUnit is > 0m && (!wideGrid || r.HasId)).ToList();
        var candidates = priced
            .Where(r => !string.IsNullOrWhiteSpace(r.Supplier) && r.Confidence >= minRowConfidence)
            .Select(r => new PricingGridReader.SupplierRow(r.Supplier, r.CostPerUnit!.Value, r.Status))
            .ToList();

        var cheapest = PricingGridReader.SelectCheapest(candidates);
        if (cheapest is null) return null;

        // Conservative confidence: the lowest row confidence among priced rows — one fuzzy row anywhere
        // in the ranking makes the whole read less trustworthy, which the reconciler can gate on.
        var confidence = priced.Select(r => r.Confidence).DefaultIfEmpty(0).Min();
        return new VisionGridReading(cheapest.Value.supplier, cheapest.Value.costPerUnit, confidence, candidates.Count);
    }

    /// <summary>Groups regions into visual rows (by Y-overlap) and classifies each into supplier / cost /
    /// status. Exposed for tests + telemetry.</summary>
    public static List<ParsedRow> ParseRows(IReadOnlyList<TextRegion> regions)
    {
        var result = new List<ParsedRow>();
        if (regions.Count == 0) return result;

        // Cluster into rows by vertical overlap. Sort by Y-center; a region starts a new row when its
        // center falls below the current row's band (half the median region height as the join gap).
        var ordered = regions
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .OrderBy(r => r.Bounds.Y + r.Bounds.Height / 2.0)
            .ToList();
        if (ordered.Count == 0) return result;

        var medianH = Median(ordered.Select(r => (double)Math.Max(1, r.Bounds.Height)).ToList());
        var joinGap = Math.Max(4.0, medianH * 0.6);

        var current = new List<TextRegion>();
        double bandCenter = double.NaN;
        foreach (var r in ordered)
        {
            var c = r.Bounds.Y + r.Bounds.Height / 2.0;
            if (current.Count == 0 || Math.Abs(c - bandCenter) <= joinGap)
            {
                current.Add(r);
                bandCenter = current.Average(x => x.Bounds.Y + x.Bounds.Height / 2.0);
            }
            else
            {
                result.Add(ClassifyRow(current));
                current = new List<TextRegion> { r };
                bandCenter = c;
            }
        }
        if (current.Count > 0) result.Add(ClassifyRow(current));

        // Drop header/preamble rows that carry no cost — they can't be supplier rows.
        return result;
    }

    private static ParsedRow ClassifyRow(List<TextRegion> rowRegions)
    {
        // Flatten the row to a token stream ordered left-to-right (region X, then token order).
        var tokens = rowRegions
            .OrderBy(r => r.Bounds.X)
            .SelectMany(r => r.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var y = (int)rowRegions.Average(r => r.Bounds.Y);
        var confidence = rowRegions.Select(r => r.Confidence).DefaultIfEmpty(0).Average();

        // per-unit cost = the smallest decimal-bearing cost on the row (per-unit ≤ pack cost).
        decimal? perUnit = null;
        foreach (var t in tokens)
        {
            if (!IsCostToken(t)) continue;
            PricingGridReader.TryParseCost(t, out var c);
            if (perUnit is null || c < perUnit) perUnit = c;
        }

        // The real PioneerRx grid puts Linked / Inventory Group / Name / NDC / UPC BEFORE the Supplier
        // column, so a naive "leading alpha before first cost" read swallows all of them. When the row
        // carries an id (NDC/UPC), anchor on it: the supplier is the first alphabetic run after the id
        // columns. On a narrow grid (no id — the sim / synthetic rows) fall back to leading-alpha.
        var hasId = tokens.Any(IsIdToken);
        var supplier = hasId ? SupplierAfterId(tokens) : "";
        // Fall back to leading-alpha when there is no id, or when the id-anchored read finds nothing after
        // the id (a layout whose supplier PRECEDES the id — e.g. a narrow grid that still carries an NDC).
        if (string.IsNullOrEmpty(supplier)) supplier = LeadingAlphaSupplier(tokens);

        // status = any status keyword anywhere on the row; empty when none (treated as usable upstream).
        var status = tokens.FirstOrDefault(IsStatusKeyword) ?? "";

        return new ParsedRow(supplier, perUnit, status, confidence, y, hasId);
    }

    // supplier = leading alphabetic tokens up to (but not including) the first cost token; skip a token
    // that is itself a status keyword so "Discontinued" never becomes the supplier name. (Narrow grid.)
    private static string LeadingAlphaSupplier(List<string> tokens)
    {
        var parts = new List<string>();
        foreach (var t in tokens)
        {
            if (IsCostToken(t)) break;
            if (IsStatusKeyword(t)) continue;
            if (t.Any(char.IsLetter)) parts.Add(t);
        }
        return string.Join(' ', parts).Trim();
    }

    // supplier = the first contiguous alphabetic run AFTER the row's id columns (NDC then UPC). Skips the
    // id tokens and any punctuation/pipes Tesseract reads from the grid separators; the run ends at the
    // next non-alpha token (the Supplier Item Number), a cost, or the shipping-size text. This lands on
    // the Supplier cell and never on the drug Name (before the id) or the Manufacturer (after the item #).
    private static string SupplierAfterId(List<string> tokens)
    {
        var idx = tokens.FindIndex(IsIdToken);
        if (idx < 0) return "";

        var parts = new List<string>();
        for (var i = idx + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (IsCostToken(t)) break;                       // reached the numeric columns
            var alpha = t.Any(char.IsLetter);
            if (parts.Count == 0)
            {
                if (IsIdToken(t) || IsStatusKeyword(t)) continue; // skip UPC / stray status
                if (IsShippingKeyword(t)) break;
                if (!alpha) continue;                         // skip pipes / punctuation junk
                parts.Add(t);
            }
            else
            {
                if (!alpha || IsStatusKeyword(t) || IsShippingKeyword(t)) break;
                parts.Add(t);
            }
        }
        return string.Join(' ', parts).Trim();
    }

    // NDC (#####-####-##, tolerant of OCR digit-count wobble) or a long unbroken digit run (UPC / item
    // number). A price is excluded (it carries a dot); a short quantity ("500", "40") is excluded (< 7).
    private static bool IsIdToken(string token)
    {
        var parts = token.Split('-');
        if (parts.Length == 3 && parts[0].Length is >= 4 and <= 6
            && parts.All(p => p.Length > 0 && p.All(char.IsDigit)))
            return true;
        return token.Length >= 7 && token.All(char.IsDigit);
    }

    private static bool IsShippingKeyword(string token) =>
        token.Equals("Stock", StringComparison.OrdinalIgnoreCase)
        || token.Equals("Package", StringComparison.OrdinalIgnoreCase);

    private static bool IsStatusKeyword(string token) =>
        token.Contains("available", StringComparison.OrdinalIgnoreCase)
        || token.Contains("discontinued", StringComparison.OrdinalIgnoreCase)
        || token.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
        || token.Contains("inactive", StringComparison.OrdinalIgnoreCase);

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 1;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2.0 : values[mid];
    }
}
