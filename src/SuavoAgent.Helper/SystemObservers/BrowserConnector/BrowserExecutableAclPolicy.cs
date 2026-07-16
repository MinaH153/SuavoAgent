using System.Security.AccessControl;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal enum BrowserAclObjectKind
{
    Directory,
    File,
}

internal enum BrowserAclRuleEffect
{
    Allow,
    Deny,
    Unknown,
}

internal readonly record struct BrowserAclRuleEvidence(
    string IdentitySid,
    BrowserAclRuleEffect Effect,
    uint AccessMask,
    bool IsInherited,
    bool ContainerInherit,
    bool ObjectInherit,
    bool InheritOnly,
    bool NoPropagateInherit);

internal sealed record BrowserAclObjectEvidence(
    string OwnerSid,
    BrowserAclObjectKind Kind,
    int Depth,
    IReadOnlyList<BrowserAclRuleEvidence> Rules);

/// <summary>
/// Pure effective-ACE decision for the Program Files-to-browser path. Raw
/// generic rights and inheritance flags are retained; an inherit-only ACE is
/// not effective on the object whose DACL carries it.
/// </summary>
internal static class BrowserExecutableAclPolicy
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const uint GenericAll = 0x10000000;
    public const uint GenericWrite = 0x40000000;

    public static bool IsProtectedChain(
        IReadOnlyList<BrowserAclObjectEvidence>? objects)
    {
        if (objects is null or { Count: 0 })
            return false;

        var expectedDepth = 0;
        var sawFile = false;
        foreach (var evidence in objects)
        {
            if (evidence is null ||
                evidence.Depth != expectedDepth++ ||
                !IsPrivileged(evidence.OwnerSid) ||
                evidence.Rules is null ||
                sawFile)
            {
                return false;
            }
            sawFile = evidence.Kind == BrowserAclObjectKind.File;

            foreach (var rule in evidence.Rules)
            {
                if (!HasWriteCapability(rule.AccessMask))
                    continue;

                var applicability = AppliesToCurrentObject(rule, evidence);
                if (applicability == BrowserAclApplicability.DoesNotApply ||
                    rule.Effect == BrowserAclRuleEffect.Deny)
                {
                    continue;
                }
                if (applicability == BrowserAclApplicability.Unknown ||
                    rule.Effect == BrowserAclRuleEffect.Unknown ||
                    !IsPrivileged(rule.IdentitySid))
                {
                    return false;
                }
            }
        }

        return sawFile;
    }

    internal static bool HasWriteCapability(uint accessMask) =>
        (accessMask & (SpecificWriteRights | GenericWrite | GenericAll)) != 0;

    private static BrowserAclApplicability AppliesToCurrentObject(
        BrowserAclRuleEvidence rule,
        BrowserAclObjectEvidence evidence)
    {
        if (!Enum.IsDefined(evidence.Kind) || evidence.Depth < 0)
            return BrowserAclApplicability.Unknown;
        if (!rule.InheritOnly)
            return BrowserAclApplicability.Applies;

        // IsInherited and NoPropagateInherit describe how/far the ACE was
        // materialized, not whether it applies to this object. Every object in
        // the chain is queried independently, so no propagation is inferred.
        // INHERIT_ONLY means this ACE exists solely to seed descendants. It is
        // therefore not effective on the current Program Files directory (or
        // on a file, which has no descendants). The inheritance flags must
        // identify a real propagation target; otherwise the raw ACE is
        // malformed/ambiguous and an effective write fails closed.
        if (!rule.ContainerInherit && !rule.ObjectInherit)
            return BrowserAclApplicability.Unknown;
        return BrowserAclApplicability.DoesNotApply;
    }

    private static bool IsPrivileged(string? sid) =>
        string.Equals(sid, SystemSid, StringComparison.Ordinal) ||
        string.Equals(sid, AdministratorsSid, StringComparison.Ordinal) ||
        string.Equals(sid, TrustedInstallerSid, StringComparison.Ordinal);

    private const uint SpecificWriteRights = unchecked((uint)(int)(
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership));

    private enum BrowserAclApplicability
    {
        Applies,
        DoesNotApply,
        Unknown,
    }
}
