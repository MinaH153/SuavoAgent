namespace PioneerRxSim;

/// <summary>
/// Adversarial seed variants. Each one perturbs EXACTLY ONE property of the automation
/// surface so a rehearsal failure is attributable to a single workflow assumption.
/// </summary>
public enum SimVariant
{
    /// <summary>The surface PricingWorkflow asserts (screenshots-derived). Happy-path baseline.</summary>
    Faithful,

    /// <summary>"Cost Per Unit" header renamed to "Unit Cost" — must trip the fail-closed
    /// header-name resolution ("Pricing grid schema not recognized"), never ordinal fallback.</summary>
    RenamedCost,

    /// <summary>Supplier rows arrive in two batches 2.2s / 3.4s after the Pricing tab opens.
    /// Probes WaitForStableRows: its quiet window is 2 consecutive equal reads (~500ms), so a
    /// 1.2s inter-batch gap can satisfy "stable" on the partial set — known-weakness probe.</summary>
    SlowGrid,

    /// <summary>Rows arrive after 6.5s — beyond GridLoadTimeout (5s). The workflow must fail
    /// the row closed ("Pricing grid has no rows"), not return a partial/empty-derived price.</summary>
    GlacialGrid,

    /// <summary>Menu uses the STOCK WPF Menu control type (UIA ControlType.Menu) instead of the
    /// MenuBar type the workflow asserts. If real PioneerRx exposes a WPF-native menu, step 1
    /// of every lookup fails — this variant demonstrates that failure mode pre-Nadim.</summary>
    WpfMenu,

    /// <summary>Cost Per Unit cells render "$0.3719". PricingGridReader.TryParseCost uses
    /// InvariantCulture whose currency symbol is "¤", so "$" fails to parse — if the real grid
    /// shows currency symbols, every row dies as "No usable supplier rows". Gap probe.</summary>
    CurrencyCells,

    /// <summary>40 suppliers for the multi-supplier NDC, true cheapest at sorted index ~33,
    /// grid sized to realize only ~8 rows. Answers the UIA2 virtualization question empirically:
    /// does FindAllChildren(DataItem) see unrealized rows (and their cells) or silently miss
    /// the true minimum?</summary>
    VirtualDepth,
}

public sealed record SimOptions(
    SimVariant Variant,
    /// <summary>True = Quick Search box is cleared after a SUCCESSFUL item load (one plausible
    /// real-PioneerRx behavior). False (default) = typed text stays in the box, which makes
    /// PricingWorkflow.VerifyLoadedNdc tautological (the box itself contains the typed NDC and
    /// is scanned FIRST) — the adversarial truth this rehearsal exists to surface.</summary>
    bool ClearSearchAfterLoad,
    /// <summary>True (default) = a reopened Edit Rx Item window shows the LAST loaded item
    /// (PioneerRx-style item persistence). Combined with a no-match NDC this is the
    /// stale-grid wrong-data probe.</summary>
    bool PersistLastItem,
    int? GridBatch1MsOverride,
    int? GridBatch2MsOverride)
{
    public (int Batch1Ms, int Batch2Ms) GridTiming => Variant switch
    {
        SimVariant.SlowGrid => (GridBatch1MsOverride ?? 2200, GridBatch2MsOverride ?? 3400),
        SimVariant.GlacialGrid => (GridBatch1MsOverride ?? 6500, GridBatch2MsOverride ?? 6500),
        _ => (GridBatch1MsOverride ?? 450, GridBatch2MsOverride ?? 850),
    };

    public string CostPerUnitHeader =>
        Variant == SimVariant.RenamedCost ? "Unit Cost" : "Cost Per Unit";

    public string FormatCost(decimal costPerUnit) =>
        Variant == SimVariant.CurrencyCells
            ? "$" + costPerUnit.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
            : costPerUnit.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture);

    public static SimOptions Parse(string[] args)
    {
        var variant = SimVariant.Faithful;
        bool clearSearch = false;
        bool persistLast = true;
        int? b1 = null, b2 = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--variant" when i + 1 < args.Length:
                    variant = ParseVariant(args[++i]);
                    break;
                case "--clear-search-after-load":
                    clearSearch = true;
                    break;
                case "--no-persist-last-item":
                    persistLast = false;
                    break;
                case "--grid-batch1-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out var v1):
                    b1 = v1; i++;
                    break;
                case "--grid-batch2-ms" when i + 1 < args.Length && int.TryParse(args[i + 1], out var v2):
                    b2 = v2; i++;
                    break;
            }
        }

        return new SimOptions(variant, clearSearch, persistLast, b1, b2);
    }

    public static SimVariant ParseVariant(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "faithful" or "" => SimVariant.Faithful,
        "renamed-cost" => SimVariant.RenamedCost,
        "slow-grid" => SimVariant.SlowGrid,
        "glacial-grid" => SimVariant.GlacialGrid,
        "wpf-menu" => SimVariant.WpfMenu,
        "currency-cells" => SimVariant.CurrencyCells,
        "virtual-depth" => SimVariant.VirtualDepth,
        _ => throw new ArgumentException(
            $"Unknown variant '{raw}'. Valid: faithful, renamed-cost, slow-grid, glacial-grid, wpf-menu, currency-cells, virtual-depth"),
    };

    public string VariantFlag => Variant switch
    {
        SimVariant.Faithful => "faithful",
        SimVariant.RenamedCost => "renamed-cost",
        SimVariant.SlowGrid => "slow-grid",
        SimVariant.GlacialGrid => "glacial-grid",
        SimVariant.WpfMenu => "wpf-menu",
        SimVariant.CurrencyCells => "currency-cells",
        SimVariant.VirtualDepth => "virtual-depth",
        _ => "faithful",
    };
}
