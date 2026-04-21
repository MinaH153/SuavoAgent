using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Walks common filesystem locations (Desktop/Downloads/Documents/OneDrive/
/// Dropbox) and yields matching <see cref="FileCandidate"/>s. Extension
/// filter is applied at enumeration time for cheap rejection; the ranker
/// handles everything else. Swallows access-denied errors silently so a
/// single locked directory can't kill the whole pass.
/// </summary>
public interface IFileEnumerator
{
    IReadOnlyList<FileCandidate> Enumerate(
        FileDiscoverySpec spec,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
}

/// <summary>
/// One root path to walk, tagged with which <see cref="FileLocationBucket"/>
/// files found under it belong to. Injectable so tests point at temp dirs.
/// </summary>
public sealed record EnumerationRoot(string Path, FileLocationBucket Bucket);
