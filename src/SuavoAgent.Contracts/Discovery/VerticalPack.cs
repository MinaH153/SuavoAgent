namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// A signed, versioned bundle of sector-specific priors that specializes
/// the universal file-discovery mechanism for one industry cluster —
/// pharmacy, accounting, legal, dental, restaurant, field-service,
/// generic-SMB, or any cluster that emerges from fleet observation.
///
/// <para>
/// Packs are the narrower gates referenced in the architecture: the
/// universal foundation (enumerate → score → sample → rank) is frozen
/// and vertical-neutral; packs provide the priors that make a generic
/// operator's agent feel tailored. New verticals ship as new packs —
/// no code change, no agent redeploy.
/// </para>
///
/// <para>
/// In v3.13 packs live as C# static instances under
/// <c>Core/Verticals/&lt;Sector&gt;/</c>. In v3.14 the same records will
/// load at runtime from signed JSON at
/// <c>%ProgramData%\SuavoAgent\packs\&lt;id&gt;.v&lt;n&gt;.pack.json</c>
/// via a <c>PackLoader</c>. The <see cref="VerticalPack"/> record shape
/// stays identical across both delivery modes — only the loader changes.
/// </para>
///
/// <para>
/// The self-healing / pack-learning loop (v3.14+) generates pack
/// proposals from aggregated fleet observations. When operators in a
/// cluster consistently pick files matching certain hints or NDC-like
/// regex variants, the cloud learning worker drafts a new pack version,
/// routes it through the same Pending → Shadow → Approved flow as
/// auto-rules, and OTA-distributes the signed pack to matching agents.
/// </para>
/// </summary>
/// <param name="Id">Stable identifier (e.g. <c>"pharmacy_rx"</c>, <c>"accounting_qbo"</c>, <c>"generic_smb"</c>).</param>
/// <param name="Version">Semver. Different versions allow drift-healing without breaking existing operators.</param>
/// <param name="DisplayName">Human-readable name for portal/audit surfaces.</param>
/// <param name="NameHintPriors">Keywords that commonly appear in filenames for this sector's target files.</param>
/// <param name="CommonPrimaryKeyPatterns">Regex patterns for record identifiers (NDC for pharmacy, case# for legal, invoice# for accounting, etc.).</param>
/// <param name="CommonExtensions">File extensions the sector typically uses (with leading dot).</param>
/// <param name="BucketPriorAdjustments">
/// Optional per-<see cref="FileLocationBucket"/> overrides for the scorer's universal priors.
/// Reserved for v3.14 pack-learning — the scorer currently uses its baked-in defaults and ignores
/// this field. Defined now so the pack shape is stable when the scorer starts consuming it.
/// </param>
public sealed record VerticalPack(
    string Id,
    string Version,
    string DisplayName,
    IReadOnlyList<string> NameHintPriors,
    IReadOnlyList<ExpectedColumnPattern> CommonPrimaryKeyPatterns,
    IReadOnlyList<string> CommonExtensions,
    IReadOnlyDictionary<FileLocationBucket, double>? BucketPriorAdjustments = null);
