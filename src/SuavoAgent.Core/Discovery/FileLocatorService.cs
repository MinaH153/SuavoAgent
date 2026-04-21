using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Orchestrates the universal file-discovery pipeline:
/// <c>Enumerate → Score → Take-MaxForRanker → Sample-Top-K → Project → Rank → Map → Band → Cap</c>.
///
/// <para>
/// The locator owns the privacy boundary: it constructs
/// <see cref="FileCandidateForRanker"/> projections before the ranker
/// sees anything, and translates <see cref="RankerVerdict"/> outputs back
/// to <see cref="CandidateRanking"/> using the raw
/// <see cref="FileCandidateSample"/> map kept locally. Rankers therefore
/// can't leak raw filenames or paths even if they forward their input
/// verbatim to an LLM.
/// </para>
/// </summary>
public sealed class FileLocatorService
{
    private readonly IFileEnumerator _enumerator;
    private readonly IFilenameHeuristicScorer _scorer;
    private readonly IFileShapeSampler _sampler;
    private readonly IFileRanker _ranker;
    private readonly FileLocatorOptions _options;
    private readonly ILogger<FileLocatorService>? _logger;

    public FileLocatorService(
        IFileEnumerator enumerator,
        IFilenameHeuristicScorer scorer,
        IFileShapeSampler sampler,
        IFileRanker ranker,
        FileLocatorOptions? options = null,
        ILogger<FileLocatorService>? logger = null)
    {
        _enumerator = enumerator;
        _scorer = scorer;
        _sampler = sampler;
        _ranker = ranker;
        _options = options ?? new FileLocatorOptions();
        _logger = logger;
    }

    public async Task<FileDiscoveryResult> LocateAsync(
        FileDiscoverySpec spec,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var candidates = _enumerator.Enumerate(spec, nowUtc, ct);
        if (candidates.Count == 0)
        {
            return Empty();
        }

        // Score every candidate. Cheap string math, no IO.
        var scored = new List<(FileCandidate Candidate, ScoreDetail Detail)>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var detail = _scorer.Score(spec, candidate, nowUtc);
            scored.Add((candidate with { HeuristicScore = detail.Total }, detail));
        }

        // Sort by heuristic total desc, stable on path.
        scored.Sort((a, b) =>
        {
            var cmp = b.Detail.Total.CompareTo(a.Detail.Total);
            if (cmp != 0) return cmp;
            return string.CompareOrdinal(a.Candidate.AbsolutePath, b.Candidate.AbsolutePath);
        });

        // Take at most MaxCandidatesForRanker forward. Sample only the
        // top SampleDepth — the remainder go to the ranker as NotSampled.
        var forwarded = scored.Take(_options.MaxCandidatesForRanker).ToList();
        var toSample = forwarded.Take(_options.SampleDepth).ToList();
        var samples = await SampleWithFaultIsolation(toSample, spec, ct);

        // Remaining forwarded candidates get a null-shape sample entry so
        // the ranker still sees them (at heuristic-only strength).
        var allSamples = new List<FileCandidateSample>(forwarded.Count);
        allSamples.AddRange(samples);
        for (int i = toSample.Count; i < forwarded.Count; i++)
        {
            allSamples.Add(new FileCandidateSample(
                Candidate: forwarded[i].Candidate,
                Shape: null,
                ErrorMessage: null)); // NotSampled, distinct from SampleFailed
        }

        // Project into privacy-safe ranker input + keep raw-sample map.
        var rankerInputs = new List<FileCandidateForRanker>(allSamples.Count);
        var samplesById = new Dictionary<string, FileCandidateSample>(allSamples.Count);
        for (int i = 0; i < allSamples.Count; i++)
        {
            var sample = allSamples[i];
            var detail = forwarded[i].Detail;
            var id = StableCandidateId(sample.Candidate, i);

            rankerInputs.Add(FileCandidateProjection.FromSample(id, sample, detail.Total, nowUtc));
            samplesById[id] = sample;

            // NotSampled path doesn't have Shape but also isn't an error;
            // override the projection's outcome.
            if (i >= toSample.Count)
            {
                rankerInputs[i] = rankerInputs[i] with { SampleOutcome = SampleOutcome.NotSampled };
            }
        }

        // Parallel score-detail map keyed by id (for verdict reconstruction).
        var detailsById = new Dictionary<string, ScoreDetail>(rankerInputs.Count);
        for (int i = 0; i < rankerInputs.Count; i++)
        {
            detailsById[rankerInputs[i].CandidateId] = forwarded[i].Detail;
        }

        var verdicts = await _ranker.RankAsync(spec, rankerInputs, ct);
        if (verdicts.Count == 0)
        {
            return Empty();
        }

        // Map verdicts → CandidateRankings using the raw samples we kept.
        var rankings = new List<CandidateRanking>(verdicts.Count);
        foreach (var v in verdicts)
        {
            if (!samplesById.TryGetValue(v.CandidateId, out var rawSample)) continue;
            rankings.Add(new CandidateRanking(
                Candidate: rawSample,
                Confidence: v.Confidence,
                Reason: v.Reason,
                Tier: v.Tier,
                SignalBreakdown: v.SignalBreakdown ?? detailsById.GetValueOrDefault(v.CandidateId)));
        }

        // Cap total rankings at spec.MaxCandidates.
        if (rankings.Count > spec.MaxCandidates)
        {
            rankings = rankings.Take(spec.MaxCandidates).ToList();
        }

        if (rankings.Count == 0) return Empty();

        var best = rankings[0];
        var alternatives = rankings.Skip(1).ToList();
        var resolution = BandConfidence(best.Confidence);

        _logger?.LogInformation(
            "FileLocator: {Resolution} best={File} confidence={Conf:F2} (tier={Tier})",
            resolution, best.Candidate.Candidate.FileName, best.Confidence, best.Tier);

        return new FileDiscoveryResult(best, alternatives, resolution);
    }

    /// <summary>
    /// Runs sample-open calls in parallel and catches per-task failures so
    /// one bad file doesn't poison the whole batch. Cancellation propagates
    /// via the shared <paramref name="ct"/>; other exception types turn
    /// into <see cref="FileCandidateSample.ErrorMessage"/> entries.
    /// </summary>
    private async Task<List<FileCandidateSample>> SampleWithFaultIsolation(
        IReadOnlyList<(FileCandidate Candidate, ScoreDetail _)> toSample,
        FileDiscoverySpec spec,
        CancellationToken ct)
    {
        var tasks = toSample
            .Select(pair => RunOneSample(pair.Candidate, spec, ct))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<FileCandidateSample> RunOneSample(
        FileCandidate candidate,
        FileDiscoverySpec spec,
        CancellationToken ct)
    {
        try
        {
            return await _sampler.SampleAsync(candidate, spec, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must propagate up through Task.WhenAll so the
            // whole discovery cancels together. Don't absorb it here.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Sampler task failed for {Path}", candidate.AbsolutePath);
            return new FileCandidateSample(
                Candidate: candidate,
                Shape: null,
                ErrorMessage: ex.GetType().Name);
        }
    }

    /// <summary>
    /// Stable, opaque candidate id. The ranker needs a handle it can round
    /// trip to us; the raw absolute path is PHI-adjacent, so we hash it
    /// into a short value that's stable across a single discovery but
    /// doesn't leak location info if the ranker logs it.
    /// </summary>
    private static string StableCandidateId(FileCandidate candidate, int index)
    {
        var hash = candidate.AbsolutePath.GetHashCode();
        return $"c{index}-{(uint)hash:x8}";
    }

    private FileDiscoveryResult Empty() => new(
        Best: null,
        Alternatives: Array.Empty<CandidateRanking>(),
        Resolution: FileDiscoveryResolution.NotFound);

    private FileDiscoveryResolution BandConfidence(double confidence)
    {
        if (confidence >= _options.AutoUseConfidence) return FileDiscoveryResolution.AutoUse;
        if (confidence >= _options.ConfirmFloor) return FileDiscoveryResolution.RequireConfirm;
        return FileDiscoveryResolution.Inconclusive;
    }
}
