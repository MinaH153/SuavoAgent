namespace SuavoAgent.Diagnostics;

/// Build-time metadata for diagnostic events. <c>GitSha</c> and
/// <c>BuildTimestampUtc</c> are emitted as constants by the MSBuild
/// <c>EmbedGitSha</c> target in <c>SuavoAgent.Diagnostics.csproj</c>
/// (partial class extends this one with the generated values).
/// Fall-back defaults below ensure the type is usable even if the build
/// target somehow doesn't run (e.g., design-time IntelliSense).
public static partial class BuildContext
{
    // The fields below are intentionally NOT marked const so that the
    // MSBuild-generated partial can supply 'const' versions of the same
    // names. If the generated partial doesn't exist (design-time only),
    // these defaults answer.
    static BuildContext()
    {
        DefaultGitSha = "unknown";
        DefaultBuildTimestampUtc = "0001-01-01T00:00:00Z";
    }

    public static string DefaultGitSha { get; }
    public static string DefaultBuildTimestampUtc { get; }
}
