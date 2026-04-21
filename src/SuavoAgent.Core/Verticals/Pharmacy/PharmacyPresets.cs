using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Verticals.Pharmacy;

/// <summary>
/// Pharmacy-vertical preset factories. Each factory composes a
/// <see cref="FileDiscoverySpec"/> by materializing the
/// <see cref="PharmacyRxPack"/>'s priors into spec fields, so callers
/// (portal, HeartbeatWorker, tests) don't need to know about packs
/// directly.
///
/// In v3.14 the factories will load the pack at runtime from a signed
/// JSON file via <c>PackLoader.Load("pharmacy_rx")</c> instead of
/// referencing the static instance — the spec construction logic stays
/// identical, only the data source changes.
/// </summary>
public static class PharmacyPresets
{
    /// <summary>
    /// Preset for the NDC pricing batch job — the file Nadim described in
    /// his April 4 audio: top-N most-dispensed generics with NDCs to look
    /// up in PioneerRx and write supplier + cost back to.
    /// </summary>
    public static FileDiscoverySpec NdcPricingList() => FromPack(
        PharmacyRxPack.Instance,
        description:
            "Top-N most-dispensed generics NDC list with one NDC per row, " +
            "for the pricing lookup job.",
        additionalNameHints: null,
        minRows: 50,
        maxRows: 2000);

    /// <summary>
    /// Materializes a vertical pack into a discovery spec. Hints come from
    /// the pack; the primary key pattern is the first in the pack's
    /// <see cref="VerticalPack.CommonPrimaryKeyPatterns"/>; extensions and
    /// tabular column hints are taken as-is. The pack reference is stashed
    /// in <see cref="FileDiscoverySpec.SourcePack"/> for attribution.
    ///
    /// Additional operator-supplied name hints append to the pack's priors
    /// (not replace), so a per-pharmacy spec can layer local vocabulary on
    /// top of the sector pack.
    /// </summary>
    public static FileDiscoverySpec FromPack(
        VerticalPack pack,
        string description,
        IReadOnlyList<string>? additionalNameHints = null,
        int? minRows = null,
        int? maxRows = null)
    {
        var hints = MergeHints(pack.NameHintPriors, additionalNameHints);
        var primaryKey = pack.CommonPrimaryKeyPatterns.Count > 0
            ? pack.CommonPrimaryKeyPatterns[0]
            : null;

        // Tabular-shape is the first-landed shape. When Document/Email
        // packs ship, FromPack dispatches by the pack's primary shape.
        var shape = new TabularExpectation(
            ColumnHints: InferColumnHints(pack),
            MinRows: minRows,
            MaxRows: maxRows,
            PrimaryKeyPattern: primaryKey);

        return new FileDiscoverySpec(
            Description: description,
            NameHints: hints,
            Extensions: pack.CommonExtensions,
            Shape: shape,
            SourcePack: pack);
    }

    // Pack priors first, operator additions second. De-dupes while
    // preserving order (pack hints win the ordinal for ties).
    private static IReadOnlyList<string> MergeHints(
        IReadOnlyList<string> packHints,
        IReadOnlyList<string>? additional)
    {
        if (additional is null || additional.Count == 0) return packHints;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(packHints.Count + additional.Count);
        foreach (var h in packHints)
            if (seen.Add(h)) merged.Add(h);
        foreach (var h in additional)
            if (seen.Add(h)) merged.Add(h);
        return merged;
    }

    // Column hints come from the pack's primary-key pattern name + its
    // display hints. Kept intentionally narrow — the sampler can still
    // detect the shape even when headers are unusual.
    private static IReadOnlyList<string> InferColumnHints(VerticalPack pack)
    {
        var hints = new List<string>();
        foreach (var pk in pack.CommonPrimaryKeyPatterns)
        {
            if (!string.IsNullOrWhiteSpace(pk.Name)) hints.Add(pk.Name);
        }
        // Pharmacy-specific additions kept out of the pack itself for now;
        // these come from operator-observed header vocabulary in v3.14.
        if (pack.Id == "pharmacy_rx")
        {
            hints.AddRange(new[] { "Generic", "Drug", "Name" });
        }
        return hints;
    }
}
