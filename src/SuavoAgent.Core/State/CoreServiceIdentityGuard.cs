using System.Runtime.Versioning;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.State;

/// <summary>
/// Refuses to run the Windows Core process unless SCM supplied the exact
/// SuavoAgent.Core service SID. LocalService alone is a shared machine identity
/// and is never sufficient to open protected state or the Helper command pipe.
/// </summary>
internal static class CoreServiceIdentityGuard
{
    [SupportedOSPlatform("windows")]
    internal static void DemandCurrentProcessHasServiceSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var groupSids = identity.Groups?
            .Select(group => group.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray() ?? [];
        if (!ContainsRequiredServiceSid(groupSids))
        {
            throw new UnauthorizedAccessException(
                $"{CoreServiceIdentity.ServiceName} requires its exact Windows service SID. " +
                "Run the signed native repair before starting Core.");
        }
    }

    internal static bool ContainsRequiredServiceSid(IEnumerable<string> groupSidValues) =>
        groupSidValues.Any(value => string.Equals(
            value,
            CoreServiceIdentity.ServiceSid,
            StringComparison.Ordinal));
}
