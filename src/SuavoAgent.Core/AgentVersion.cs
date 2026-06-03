using System.Reflection;

namespace SuavoAgent.Core;

/// <summary>
/// Resolves the agent's reported version. Source of truth = the STAMPED ASSEMBLY, NOT appsettings.json.
///
/// Why: the OTA self-update (<see cref="Cloud.SelfUpdater"/>) swaps the .exe binaries in place but never
/// rewrites <c>appsettings.json</c>'s <c>Agent.Version</c>. The agent used to read its version from that
/// config field, so a freshly-OTA'd binary kept reporting its stale install-time version EVERYWHERE —
/// the startup log, the cloud <c>agent_version</c> column, the dashboard, the per-pilot 7-day readiness
/// gate, and rollout tracking. That is the long-standing "agent_version is stale post-OTA" symptom
/// (field-confirmed on Mina's box, 2026-06-03: a v3.19.0 binary reporting "3.18.4").
///
/// <c>Directory.Build.props</c> stamps <c>InformationalVersion = $(Version)</c>, and the release workflow
/// builds with <c>-p:Version=&lt;tag&gt;</c>, so a shipped binary's assembly version is authoritative.
/// Local developer builds default <c>$(Version)</c> to <c>"0.0.0"</c>, so those fall back to the
/// configured value (e.g. the dev "x.y.z-dev" in appsettings) for convenience.
/// </summary>
public static class AgentVersion
{
    /// <summary>The stamped assembly InformationalVersion, with any "+&lt;git-sha&gt;" build metadata stripped, or null.</summary>
    public static string? FromAssembly() =>
        Normalize(Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string? Normalize(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return null;
        var plus = informational.IndexOf('+'); // SourceLink appends "+<git-sha>" build metadata
        return plus >= 0 ? informational[..plus] : informational;
    }

    /// <summary>
    /// Prefer the stamped release assembly version; fall back to <paramref name="configured"/> only for
    /// local dev (unstamped "0.0.0") builds.
    /// </summary>
    public static string Resolve(string? configured) => Resolve(FromAssembly(), configured);

    /// <summary>Pure overload for deterministic testing — no assembly reflection.</summary>
    internal static string Resolve(string? assemblyVersion, string? configured)
    {
        var isRealRelease = !string.IsNullOrWhiteSpace(assemblyVersion) && assemblyVersion != "0.0.0";
        if (isRealRelease) return assemblyVersion!;
        return string.IsNullOrWhiteSpace(configured) ? "unknown" : configured!;
    }
}
