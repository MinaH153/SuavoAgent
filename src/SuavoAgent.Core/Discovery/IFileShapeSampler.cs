using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Opens a candidate file read-only and produces a <see cref="ShapeSample"/>
/// describing its structure. File-type-specific — dispatches by extension
/// internally (tabular for .xlsx/.csv/.tsv first-landed; Document/
/// Email samplers plug in as siblings).
///
/// Opens are non-locking: callers can sample a file while it's open in
/// Excel. Failures surface as <see cref="FileCandidateSample.ErrorMessage"/>
/// rather than exceptions so the locator can continue with other candidates.
/// </summary>
public interface IFileShapeSampler
{
    Task<FileCandidateSample> SampleAsync(
        FileCandidate candidate,
        FileDiscoverySpec spec,
        CancellationToken ct);
}
