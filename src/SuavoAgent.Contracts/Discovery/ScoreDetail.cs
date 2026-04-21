namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Per-signal breakdown from <c>IFilenameHeuristicScorer</c>. Surfaces the
/// individual contributions so operators can audit "why did this file
/// score 0.42 instead of 0.88?" without rerunning the scorer. Each
/// component is in [0,1] before weighting; <see cref="Total"/> is the
/// final weighted-and-clamped value.
/// </summary>
public sealed record ScoreDetail(
    double Total,
    double NameScore,
    double RecencyScore,
    double ExtensionScore,
    double BucketScore,
    double SizeScore);
