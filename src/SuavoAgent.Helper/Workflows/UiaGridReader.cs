using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using Serilog;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// The reusable PioneerRx grid read — the proven, virtualization-safe primitives extracted from the
/// pricing workflow so EVERY grid-reading feature (pricing, reorder, invoice-recon, short-dated,
/// will-call) shares the exact same battle-tested read instead of reinventing it. Behavior is identical
/// to what passed the live CONFORMANCE rehearsal:
///   - find the grid (Table ?? DataGrid),
///   - wait for a lazily-loaded / virtualized DevExpress grid to STABILIZE (quiet-settle so the grid's
///     own UI thread isn't starved, then scroll + poll until the row count stops growing),
///   - resolve columns BY HEADER NAME (fail closed if a required column is missing/renamed — never read
///     by hardcoded ordinal, which would silently write wrong cells),
///   - read each cell's FULL text (Value / LegacyIAccessible, not the truncated Name).
///
/// The feature keeps only its own judgment (which columns it needs + what to do with the rows). Windows
/// UIA (FlaUI); the grid-read behavior is verified live by the pricing rehearsal, so a change here must
/// re-pass it.
/// </summary>
public sealed class UiaGridReader
{
    private readonly ILogger _logger;
    private readonly TimeSpan _gridLoadTimeout;
    private readonly int _settleMs;

    public UiaGridReader(ILogger logger, TimeSpan gridLoadTimeout, int settleMs = 900)
    {
        _logger = logger;
        _gridLoadTimeout = gridLoadTimeout;
        _settleMs = settleMs;
    }

    /// <summary>A logical column to resolve by header name — any of <see cref="Aliases"/> (case-insensitive)
    /// matches. A <see cref="Required"/> column that isn't found fails the whole resolve closed.</summary>
    public readonly record struct ColumnSpec(string Key, string[] Aliases, bool Required);

    /// <summary>Finds the data grid under <paramref name="root"/> (Table, else DataGrid), polling up to
    /// the load timeout. Null when no grid appears.</summary>
    public AutomationElement? FindGrid(AutomationElement root, ConditionFactory cf)
    {
        var deadline = DateTime.UtcNow + _gridLoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var grid = root.FindFirstDescendant(cf.ByControlType(ControlType.Table))
                ?? root.FindFirstDescendant(cf.ByControlType(ControlType.DataGrid));
            if (grid != null) return grid;
            Thread.Sleep(200);
        }
        return null;
    }

    /// <summary>
    /// Polls the grid until its realized-row set stabilizes (or the load timeout elapses), returning the
    /// rows. DevExpress/WPF grids virtualize + lazy-load: a single read can catch a partial set and miss
    /// the true winner. <paramref name="expectedRowCount"/> is the grid's logical count (-1 if none), so
    /// the caller can fail closed on a still-partial read.
    /// </summary>
    public AutomationElement[] WaitForStableRows(AutomationElement grid, ConditionFactory cf, out int expectedRowCount)
    {
        expectedRowCount = TryGetGridRowCount(grid); // -1 when the grid exposes no logical count

        var deadline = DateTime.UtcNow + _gridLoadTimeout;
        var scroll = grid.Patterns.Scroll.PatternOrDefault;
        bool canScroll = false;
        try { canScroll = scroll != null && scroll.VerticallyScrollable.ValueOrDefault; } catch { }

        // Accumulate realized rows across scroll positions, keyed by RuntimeId. A virtualized grid only
        // exposes RENDERED rows via UIA, so a single-viewport read misses off-screen rows — and the true
        // winner can be one of them. Scroll to the bottom in increments, unioning rows, until we've
        // realized the logical row count (or growth stops / the budget elapses).
        var byId = new Dictionary<string, AutomationElement>();
        void Harvest()
        {
            int i = 0;
            foreach (var r in grid.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
            {
                var rid = TryGetRuntimeId(r);
                byId[rid is { Length: > 0 } ? string.Join(",", rid) : "ord:" + i] = r;
                i++;
            }
        }

        // Quiet settle: let a lazily-loaded grid finish materializing its rows BEFORE we start hammering
        // it with UIA queries. The grid loads rows on ITS OWN UI thread; our rapid cross-process
        // FindAllChildren calls can starve that thread so late-loading rows never appear during the read.
        // A brief UIA-free pause lets the grid's own loading complete; the loop then confirms stability.
        Thread.Sleep(_settleMs);
        Harvest();
        int lastCount = byId.Count, stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (expectedRowCount > 0 && byId.Count >= expectedRowCount) break;

            bool scrolled = false;
            if (canScroll)
            {
                double pct = 100;
                try { pct = scroll!.VerticalScrollPercent.ValueOrDefault; } catch { }
                if (pct < 99.9)
                {
                    try { scroll!.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement); scrolled = true; } catch { }
                }
            }

            Thread.Sleep(500);
            Harvest();

            if (byId.Count == lastCount)
            {
                // A lazily-loaded grid materializes rows in TIMED BATCHES (the true winner can be in a
                // LATER batch). Require a longer no-growth window (~1.75s) so a late batch has time to land
                // before we conclude the set is complete. Still bounded by the load timeout.
                if (++stable >= 3 && !scrolled) break;
            }
            else { stable = 0; lastCount = byId.Count; }
        }
        _logger.Debug("UiaGridReader: harvested {Count} rows (canScroll={Scroll}, expectedCount={Expected})",
            byId.Count, canScroll, expectedRowCount);
        return byId.Values.ToArray();
    }

    /// <summary>
    /// Resolves each column's index by header name (WPF DataGrid exposes headers as Header/HeaderItem).
    /// A header cell matches at most one spec (first spec whose alias matches, in spec order — mirrors the
    /// original else-if chain); each spec takes its first matching cell. Returns null (fail closed) if the
    /// grid has no header, no header items, OR any Required column is unresolved — the caller then errors
    /// rather than reading by ordinal and writing the wrong cell.
    /// </summary>
    public Dictionary<string, int>? ResolveColumns(AutomationElement grid, ConditionFactory cf, IReadOnlyList<ColumnSpec> specs)
    {
        try
        {
            var header = grid.FindFirstDescendant(cf.ByControlType(ControlType.Header));
            if (header == null)
            {
                _logger.Warning("UiaGridReader: no Header found in grid — failing closed (no ordinal fallback)");
                return null;
            }

            var headerCells = header.FindAllDescendants(cf.ByControlType(ControlType.HeaderItem));
            if (headerCells.Length == 0)
            {
                _logger.Warning("UiaGridReader: Header has no HeaderItems — failing closed (no ordinal fallback)");
                return null;
            }

            var idx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < headerCells.Length; i++)
            {
                var name = headerCells[i].Name?.Trim() ?? "";
                foreach (var spec in specs)
                {
                    if (idx.ContainsKey(spec.Key)) continue;
                    if (spec.Aliases.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase)))
                    {
                        idx[spec.Key] = i;
                        break; // this cell matched a spec — don't let it match another
                    }
                }
            }

            var missing = specs.Where(s => s.Required && !idx.ContainsKey(s.Key)).Select(s => s.Key).ToList();
            if (missing.Count > 0)
            {
                _logger.Warning("UiaGridReader: required columns not resolved by header name: [{Missing}] — failing closed",
                    string.Join(", ", missing));
                return null;
            }

            _logger.Debug("UiaGridReader: resolved columns [{Resolved}]",
                string.Join(", ", idx.Select(kv => $"{kv.Key}=col {kv.Value}")));
            return idx;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "UiaGridReader: column resolution error — failing closed (no ordinal fallback)");
            return null;
        }
    }

    /// <summary>The cell elements of a data row (Custom + DataItem children, left-to-right).</summary>
    public static AutomationElement[] RowCells(AutomationElement row, ConditionFactory cf) =>
        row.FindAllChildren(cf.ByControlType(ControlType.Custom))
            .Concat(row.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
            .ToArray();

    /// <summary>Reads a cell's FULL value rather than its rendered Name — grid cells truncate long text in
    /// Name ("Mckesson Geri…"); the ValuePattern / LegacyIAccessible value carries the complete string.
    /// Falls back to Name when no value pattern exists.</summary>
    public static string GetCellText(AutomationElement el)
    {
        try
        {
            var text = el.AsTextBox()?.Text;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { /* not a value-bearing element */ }

        try
        {
            var legacy = el.Patterns.LegacyIAccessible.PatternOrDefault;
            var v = legacy?.Value?.ValueOrDefault;
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        catch { /* legacy pattern unsupported */ }

        return el.Name?.Trim() ?? "";
    }

    /// <summary>Logical row count from the grid's Grid pattern, or -1 when unavailable — tells a fully-
    /// realized read from a partial (still-virtualized) one so ranking can fail closed.</summary>
    public static int TryGetGridRowCount(AutomationElement grid)
    {
        try { return grid.Patterns.Grid.PatternOrDefault?.RowCount.ValueOrDefault ?? -1; }
        catch { return -1; }
    }

    /// <summary>RuntimeId of <paramref name="el"/>, or null if unavailable or the read throws.</summary>
    public static int[]? TryGetRuntimeId(AutomationElement? el)
    {
        if (el == null) return null;
        try { return el.Properties.RuntimeId.ValueOrDefault; }
        catch { return null; }
    }
}
