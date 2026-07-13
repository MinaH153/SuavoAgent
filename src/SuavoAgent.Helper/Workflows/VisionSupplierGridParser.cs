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
/// Money semantics are fail-closed. Vision may rank a row only when OCR exposes exactly one recognized
/// <c>Cost Per Unit</c> header cell and an independently bounded numeric cell aligned to that header.
/// A line-wide OCR region cannot prove which decimal is Cost, Rebate, AWP, MAC, or Cost Per Unit and is
/// therefore never used as money. The exact value of the winner is still confirmed by UIA before use.
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

        var costColumn = ResolveCostPerUnitColumn(regions);

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
                result.Add(ClassifyRow(current, costColumn));
                current = new List<TextRegion> { r };
                bandCenter = c;
            }
        }
        if (current.Count > 0) result.Add(ClassifyRow(current, costColumn));

        // Drop header/preamble rows that carry no cost — they can't be supplier rows.
        return result;
    }

    private static ParsedRow ClassifyRow(
        List<TextRegion> rowRegions,
        HorizontalBand? costColumn)
    {
        // Flatten the row to a token stream ordered left-to-right (region X, then token order).
        var tokens = rowRegions
            .OrderBy(r => r.Bounds.X)
            .SelectMany(r => r.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var y = (int)rowRegions.Average(r => r.Bounds.Y);
        var confidence = rowRegions.Select(r => r.Confidence).DefaultIfEmpty(0).Average();

        // Never infer money from "the smallest decimal on the row". PioneerRx places Cost, Rebate,
        // AWP, and MAC beside Cost Per Unit, and any of them may be smaller. Only an independently
        // bounded whole numeric cell whose center lies in the exact recognized header's x-band is
        // admissible. Missing/ambiguous headers and multiple aligned numbers reject the row.
        var perUnit = ReadCostPerUnitCell(rowRegions, costColumn);

        // The real PioneerRx grid puts Linked / Inventory Group / Name / NDC / UPC BEFORE the Supplier
        // column, so a naive "leading alpha before first cost" read swallows all of them. When the row
        // carries an id (NDC/UPC), anchor on it: the supplier is the first alphabetic run after the id
        // columns. On a narrow grid (no id — the sim / synthetic rows) fall back to leading-alpha.
        var hasId = tokens.Any(IsIdToken);
        var supplier = hasId ? SupplierAfterId(tokens) : "";
        // Fall back to leading-alpha when there is no id, or when the id-anchored read finds nothing after
        // the id (a layout whose supplier PRECEDES the id — e.g. a narrow grid that still carries an NDC).
        if (string.IsNullOrEmpty(supplier)) supplier = LeadingAlphaSupplier(tokens);

        // Status is a positive observation, never a token substring. Per-cell OCR must expose an
        // entire cell equal to Available/Active. The legacy one-region-per-line shape may use only
        // an exact final token and is rejected first when any negated/ineligible phrase is present.
        // This prevents "Not Available" from being tokenized into a false eligible observation.
        var status = ReadEligibleStatus(rowRegions, tokens);

        return new ParsedRow(supplier, perUnit, status, confidence, y, hasId);
    }

    private readonly record struct HorizontalBand(int Left, int Right, int HeaderBottom);

    private static HorizontalBand? ResolveCostPerUnitColumn(IReadOnlyList<TextRegion> regions)
    {
        var exactHeaders = regions
            .Where(region => IsExactCostPerUnitHeader(region.Text))
            .ToList();
        if (exactHeaders.Count > 1) return null;
        if (exactHeaders.Count == 1)
            return HeaderBand(exactHeaders[0]);

        // Production Tesseract pricing extraction is word-granular so the
        // parser can prove column membership. Reconstruct only the exact
        // adjacent header phrase from three bounded words on one visual row.
        // A line-wide region, a cross-column phrase, or duplicate matches
        // remains ambiguous and fails closed.
        var ordered = regions
            .Where(region => region.Bounds.Width > 0 && region.Bounds.Height > 0)
            .OrderBy(region => region.Bounds.Y + region.Bounds.Height / 2.0)
            .ThenBy(region => region.Bounds.X)
            .ToArray();
        var candidates = new List<HorizontalBand>();
        for (var index = 0; index + 2 < ordered.Length; index++)
        {
            var cost = ordered[index];
            var per = ordered[index + 1];
            var unit = ordered[index + 2];
            if (!HeaderWord(cost.Text, "cost") ||
                !HeaderWord(per.Text, "per") ||
                !HeaderWord(unit.Text, "unit") ||
                !SameHeaderRow(cost, per, unit) ||
                !AdjacentHeaderWords(cost, per) ||
                !AdjacentHeaderWords(per, unit))
                continue;
            candidates.Add(new HorizontalBand(
                cost.Bounds.X,
                checked(unit.Bounds.X + unit.Bounds.Width),
                Math.Max(
                    checked(cost.Bounds.Y + cost.Bounds.Height),
                    Math.Max(
                        checked(per.Bounds.Y + per.Bounds.Height),
                        checked(unit.Bounds.Y + unit.Bounds.Height)))));
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static HorizontalBand? HeaderBand(TextRegion header)
    {
        if (header.Bounds.Width <= 0 || header.Bounds.Height <= 0) return null;
        return new HorizontalBand(
            header.Bounds.X,
            checked(header.Bounds.X + header.Bounds.Width),
            checked(header.Bounds.Y + header.Bounds.Height));
    }

    private static bool HeaderWord(string value, string expected) =>
        value.Trim().Trim('(', ')', '[', ']', ':', '.', '|')
            .Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool SameHeaderRow(
        TextRegion first,
        TextRegion second,
        TextRegion third)
    {
        var centers = new[]
        {
            first.Bounds.Y + first.Bounds.Height / 2.0,
            second.Bounds.Y + second.Bounds.Height / 2.0,
            third.Bounds.Y + third.Bounds.Height / 2.0,
        };
        var tolerance = Math.Max(
            4.0,
            new[] { first.Bounds.Height, second.Bounds.Height, third.Bounds.Height }
                .Average() * 0.6);
        return centers.Max() - centers.Min() <= tolerance;
    }

    private static bool AdjacentHeaderWords(TextRegion left, TextRegion right)
    {
        var gap = right.Bounds.X - checked(left.Bounds.X + left.Bounds.Width);
        var maximumGap = Math.Max(
            16,
            Math.Max(left.Bounds.Height, right.Bounds.Height) * 3);
        return gap is >= -2 && gap <= maximumGap;
    }

    private static bool IsExactCostPerUnitHeader(string text)
    {
        var normalized = string.Join(' ', text
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Equals("Cost Per Unit", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Cost (per unit)", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ReadCostPerUnitCell(
        IReadOnlyList<TextRegion> rowRegions,
        HorizontalBand? costColumn)
    {
        if (costColumn is not { } band) return null;

        decimal? value = null;
        foreach (var region in rowRegions)
        {
            if (region.Bounds.Y < band.HeaderBottom) continue;
            var centerX = region.Bounds.X + region.Bounds.Width / 2;
            if (centerX < band.Left || centerX > band.Right) continue;

            var cell = region.Text.Trim();
            // Whole-cell identity is required. Whitespace means this is a line/compound region, not
            // a bounded money cell, even if one token happens to parse as a decimal.
            if (cell.Any(char.IsWhiteSpace) || !IsCostToken(cell)) continue;
            PricingGridReader.TryParseCost(cell, out var parsed);
            if (value is not null) return null;
            value = parsed;
        }
        return value;
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

    private static string ReadEligibleStatus(
        IReadOnlyList<TextRegion> rowRegions,
        IReadOnlyList<string> tokens)
    {
        var wholeRow = string.Join(' ', rowRegions
            .OrderBy(region => region.Bounds.X)
            .Select(region => region.Text));
        if (ContainsIneligibleStatusPhrase(wholeRow))
            return "";

        foreach (var region in rowRegions)
        {
            var cell = TrimStatusPunctuation(region.Text);
            if (IsExactEligibleStatus(cell)) return cell;
        }

        // Some OCR providers return the entire visual row as one region. Preserve that supported
        // shape without scanning arbitrary tokens: only an exact trailing field is admissible.
        if (rowRegions.Count != 1)
            return "";

        var trailing = tokens.Count == 0 ? "" : TrimStatusPunctuation(tokens[^1]);
        return IsExactEligibleStatus(trailing) ? trailing : "";
    }

    private static bool IsExactEligibleStatus(string value) =>
        value.Equals("Available", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Active", StringComparison.OrdinalIgnoreCase);

    private static string TrimStatusPunctuation(string value) =>
        value.Trim().Trim(',', ';', ':', '.', '|', '[', ']', '(', ')');

    private static bool ContainsIneligibleStatusPhrase(string value)
    {
        var normalized = string.Join(' ', value
            .Replace('-', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var compact = normalized.Replace(" ", "", StringComparison.Ordinal);
        return normalized.Contains("not available", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("not active", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("do not use", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("notavailable", StringComparison.OrdinalIgnoreCase)
            || compact.Contains("donotuse", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("inactive", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("discontinued", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStatusKeyword(string token) =>
        token.Equals("active", StringComparison.OrdinalIgnoreCase)
        || token.Contains("available", StringComparison.OrdinalIgnoreCase)
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
