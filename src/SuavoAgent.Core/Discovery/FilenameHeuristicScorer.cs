using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Default scorer. Weighted mix of five signals, each returned in [0,1]
/// via <see cref="ScoreDetail"/>:
///
/// <list type="bullet">
///   <item><b>NameScore</b> — substring match of each <i>valid</i> hint
///     (length ≥ 3, non-whitespace) against the filename <i>tokenized</i>
///     on <c>[-_. ]</c>. Saturates at 3 hits.</item>
///   <item><b>RecencyScore</b> — linear decay over
///     <c>spec.RecentDaysBoost</c>. Files modified today or in the future
///     (clock skew) score 1.0; files older than the window score 0.0.</item>
///   <item><b>ExtensionScore</b> — case-insensitive exact match against
///     the spec's extension list.</item>
///   <item><b>BucketScore</b> — priors on where humans keep in-progress
///     work; <see cref="FileLocationBucket.ExcelMru"/> /
///     <see cref="FileLocationBucket.WindowsRecent"/> additionally decay
///     when the candidate's <c>LastOpenedUtc</c> is stale or missing.</item>
///   <item><b>SizeScore</b> — whether raw bytes fit the tabular row-count
///     band (when the spec's <see cref="ShapeExpectation"/> is tabular
///     and supplies bounds); neutral otherwise.</item>
/// </list>
///
/// Weights sum to 1.0 so the total stays in [0,1] without re-normalization.
/// </summary>
public sealed class FilenameHeuristicScorer : IFilenameHeuristicScorer
{
    // Weights tuned so a perfect-name-match in Desktop/MRU, correct
    // extension, recent edit, size-in-band scores ≳ 0.95. Mismatch on any
    // single signal drops the score enough that the locator surfaces the
    // file as "maybe" rather than auto-use.
    private const double WName = 0.45;
    private const double WRecent = 0.20;
    private const double WExt = 0.15;
    private const double WBucket = 0.15;
    private const double WSize = 0.05;

    // Short substrings false-positive against unrelated filenames
    // ("rx" would match "box.xlsx"). Hints below this length are silently
    // dropped during scoring.
    private const int MinHintLength = 3;

    // Tokenization delimiters — how filenames are split into words before
    // substring matching. Matches the separators humans use when naming
    // files: "ndc-report-march.xlsx", "generic_top_500.xlsx", "rx.list.xlsx".
    private static readonly char[] FilenameTokenDelimiters = { '-', '_', '.', ' ', '(', ')' };

    public ScoreDetail Score(FileDiscoverySpec spec, FileCandidate candidate, DateTimeOffset nowUtc)
    {
        var name = NameScore(spec.NameHints, candidate.FileName);
        var recent = RecencyScore(candidate.LastModifiedUtc, nowUtc, spec.RecentDaysBoost ?? 90);
        var ext = ExtensionScore(spec.Extensions, candidate.FileName);
        var bucket = BucketScore(candidate.Bucket, candidate.LastOpenedUtc, nowUtc);
        var size = SizeScore(spec.Shape, candidate.SizeBytes);

        var total = WName * name + WRecent * recent + WExt * ext + WBucket * bucket + WSize * size;
        total = Math.Clamp(total, 0.0, 1.0);

        return new ScoreDetail(
            Total: total,
            NameScore: name,
            RecencyScore: recent,
            ExtensionScore: ext,
            BucketScore: bucket,
            SizeScore: size);
    }

    private static double NameScore(IReadOnlyList<string>? hints, string fileName)
    {
        // Normalize: trim whitespace entries, drop anything shorter than
        // MinHintLength. This protects against empty-string spec entries
        // and false-positive-prone tiny hints.
        var validHints = NormalizeHints(hints);
        if (validHints.Count == 0)
        {
            // True "no hints given" — scorer has no preference either way.
            return 0.5;
        }

        // Tokenize the filename (without extension) so we match on whole
        // words. "generics_top_500.xlsx" tokenizes to
        // ["generics", "top", "500"] and hints ["generic", "top"] both
        // substring-match one of those tokens.
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var tokens = stem.Split(FilenameTokenDelimiters, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return 0.0; // Pathological filename, no usable tokens.
        }

        int hits = 0;
        foreach (var h in validHints)
        {
            var hint = h.ToLowerInvariant();
            foreach (var tok in tokens)
            {
                if (tok.Contains(hint))
                {
                    hits++;
                    break; // Don't double-count a single hint across multiple tokens.
                }
            }
        }

        // Hints were given but none matched — strong negative signal, not
        // neutral. Distinguishes this case from "no hints given" (0.5).
        if (hits == 0) return 0.1;

        // Saturating curve: 1 → 0.6, 2 → 0.85, 3+ → 1.0.
        return hits switch
        {
            1 => 0.6,
            2 => 0.85,
            _ => 1.0,
        };
    }

    private static List<string> NormalizeHints(IReadOnlyList<string>? hints)
    {
        if (hints is null) return new List<string>(capacity: 0);
        var result = new List<string>(capacity: hints.Count);
        foreach (var raw in hints)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (trimmed.Length < MinHintLength) continue;
            result.Add(trimmed);
        }
        return result;
    }

    private static double RecencyScore(DateTimeOffset lastModified, DateTimeOffset nowUtc, int recentDaysBoost)
    {
        // Guard nonsense spec (negative / zero window) — treat as "no
        // recency preference", score neutral rather than divide by zero.
        if (recentDaysBoost <= 0) return 0.5;

        var ageDays = (nowUtc - lastModified).TotalDays;
        if (ageDays <= 0) return 1.0; // Edited now or in future (clock skew).
        if (ageDays >= recentDaysBoost) return 0.0;
        return 1.0 - (ageDays / recentDaysBoost);
    }

    private static double ExtensionScore(IReadOnlyList<string>? extensions, string fileName)
    {
        // Normalize: drop blank entries. An extensions list with only
        // whitespace means "no preference," not "nothing matches."
        var valid = new List<string>();
        if (extensions is not null)
        {
            foreach (var e in extensions)
            {
                if (string.IsNullOrWhiteSpace(e)) continue;
                valid.Add(e.Trim());
            }
        }
        if (valid.Count == 0) return 0.5;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        foreach (var e in valid)
        {
            if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return 1.0;
        }
        return 0.0;
    }

    private static double BucketScore(FileLocationBucket bucket, DateTimeOffset? lastOpenedUtc, DateTimeOffset nowUtc)
    {
        // Base prior per bucket.
        var basePrior = bucket switch
        {
            FileLocationBucket.ExcelMru => 1.0,
            FileLocationBucket.WindowsRecent => 0.9,
            FileLocationBucket.Desktop => 0.85,
            FileLocationBucket.Downloads => 0.8,
            FileLocationBucket.OneDrive => 0.65,
            FileLocationBucket.Dropbox => 0.65,
            FileLocationBucket.Documents => 0.55,
            _ => 0.3,
        };

        // MRU / Recent entries carry their full prior only when the
        // last-opened signal is fresh. A stale MRU entry from 6 months ago
        // shouldn't outrank a file freshly-saved to Desktop today.
        if (bucket is FileLocationBucket.ExcelMru or FileLocationBucket.WindowsRecent)
        {
            if (lastOpenedUtc is null)
            {
                // No open-time signal available — trust the bucket but
                // decay modestly so we don't lock in a wrong prior.
                return Math.Max(basePrior * 0.75, 0.65);
            }

            var openedAgeDays = (nowUtc - lastOpenedUtc.Value).TotalDays;
            if (openedAgeDays <= 7) return basePrior;         // fresh MRU
            if (openedAgeDays <= 30) return basePrior * 0.85; // recent but not today
            return basePrior * 0.7;                            // stale MRU, still better than Documents
        }

        return basePrior;
    }

    private static double SizeScore(ShapeExpectation? shape, long sizeBytes)
    {
        // Non-tabular shapes don't have row-count priors yet (Document/Email
        // samplers will add their own bounds when implemented). Neutral for now.
        if (shape is not TabularExpectation tab) return 0.5;
        if (tab.MinRows is null && tab.MaxRows is null) return 0.5;

        // Clamp pathological spec inputs (negatives) before use.
        var minRows = Math.Max(0, tab.MinRows ?? 0);
        var maxRows = Math.Max(minRows, tab.MaxRows ?? int.MaxValue);

        // Rough estimate: typical xlsx row with ~10 columns ≈ 150-800 bytes
        // after OOXML compression (depends heavily on formatting, images,
        // shared strings, formulas). This is a coarse filter — the sampler
        // verifies actual row count once the file is opened.
        const long bytesPerRowLow = 150;
        const long bytesPerRowHigh = 800;

        var minBytes = (long)minRows * bytesPerRowLow;
        var maxBytes = maxRows == int.MaxValue ? long.MaxValue : (long)maxRows * bytesPerRowHigh;

        if (sizeBytes >= minBytes && sizeBytes <= maxBytes) return 1.0;
        return 0.3;
    }
}
