using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Builds the privacy-safe <see cref="FileCandidateForRanker"/> projection
/// from a <see cref="FileCandidateSample"/>. Lives in Core so the scrubber
/// dependency can be injected in session 3 (LLM tier) without changing
/// Contracts.
///
/// <para>
/// v3.13: no scrubber required — the heuristic ranker is fully local and
/// header values never leave the agent. v3.14+: when <c>LlmFileRanker</c>
/// sends headers to a cloud LLM, the factory overload with a
/// <c>IPhiScrubber</c> parameter replaces headers that match PHI patterns
/// with <c>&lt;PII&gt;</c> tokens before the ranker sees them.
/// </para>
/// </summary>
public static class FileCandidateProjection
{
    /// <summary>
    /// Projects a sampled candidate into the ranker-facing record. The
    /// <paramref name="candidateId"/> is the opaque handle the locator
    /// uses to correlate ranker verdicts back to this sample.
    /// </summary>
    public static FileCandidateForRanker FromSample(
        string candidateId,
        FileCandidateSample sample,
        double heuristicScore,
        DateTimeOffset nowUtc)
    {
        var tab = sample.Shape as TabularShapeSample;
        var outcome = sample switch
        {
            { Shape: not null } => SampleOutcome.Sampled,
            { ErrorMessage: not null } => SampleOutcome.SampleFailed,
            _ => SampleOutcome.NotSampled,
        };

        return new FileCandidateForRanker(
            CandidateId: candidateId,
            DirectoryDepth: ComputeDepth(sample.Candidate.AbsolutePath),
            Size: BandSize(sample.Candidate.SizeBytes),
            Recency: BandRecency(sample.Candidate.LastModifiedUtc, nowUtc),
            Extension: Path.GetExtension(sample.Candidate.FileName).ToLowerInvariant(),
            Bucket: sample.Candidate.Bucket,
            ColumnHeaderCount: tab?.ColumnHeaders.Count ?? 0,
            // v3.13: headers pass through unchanged since only the local
            // heuristic ranker consumes them. v3.14 LLM path will scrub.
            ScrubbedColumnHeaders: tab?.ColumnHeaders ?? Array.Empty<string>(),
            RowCount: tab?.RowCount ?? 0,
            HasPrimaryKeyShape: tab is { PrimaryKeyColumnIndex: >= 0 },
            StructureMatchesHints: tab?.StructureMatchesHints ?? false,
            HeuristicScore: heuristicScore,
            SampleOutcome: outcome);
    }

    /// <summary>Count how many path segments live below the user-profile
    /// root. Useful structural signal without leaking the raw path.</summary>
    public static int ComputeDepth(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return 0;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile)
            && absolutePath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            var tail = absolutePath.Substring(userProfile.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return tail.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
        }
        return absolutePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
    }

    public static SizeBand BandSize(long bytes) => bytes switch
    {
        < 10_000 => SizeBand.Tiny,
        < 500_000 => SizeBand.Small,
        < 5_000_000 => SizeBand.Medium,
        < 50_000_000 => SizeBand.Large,
        _ => SizeBand.Huge,
    };

    public static RecencyBand BandRecency(DateTimeOffset modified, DateTimeOffset nowUtc)
    {
        var ageDays = (nowUtc - modified).TotalDays;
        return ageDays switch
        {
            < 1 => RecencyBand.Today,
            < 7 => RecencyBand.ThisWeek,
            < 30 => RecencyBand.ThisMonth,
            < 90 => RecencyBand.ThisQuarter,
            _ => RecencyBand.Older,
        };
    }
}
