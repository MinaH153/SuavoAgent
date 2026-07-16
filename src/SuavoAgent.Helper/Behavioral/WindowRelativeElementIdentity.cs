using System.Security.Cryptography;
using System.Text;
using FlaUI.Core.AutomationElements;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Resolves PHI-free interaction identities. AutomationId remains the primary
/// identity. Anonymous controls require a window-relative sibling path or a
/// UIA RuntimeId; class-only placeholders are rejected as non-unique.
/// </summary>
internal static class WindowRelativeElementIdentity
{
    private const int MaximumAncestryDepth = 16;

    internal static string? Resolve(
        AutomationElement element,
        Window? subscribedWindow,
        RawElementProperties raw)
    {
        if (StructuralIdentifierSanitizer.IsAllowed(raw.AutomationId))
            return raw.AutomationId;

        if (!StructuralIdentifierSanitizer.IsAllowed(raw.ClassName))
            return null;

        // Never walk Parent/FindAllChildren from a UIA callback. That ancestry
        // traversal can synchronously block the provider callback thread. A
        // single RuntimeId read is the bounded identity attempt; if unavailable
        // the anonymous element is rejected below.
        var runtimeId = TryGetRuntimeId(element);
        _ = subscribedWindow;

        return BuildAnonymousIdentity(
            raw.ControlType,
            raw.ClassName,
            windowRelativeSiblingPath: null,
            runtimeId);
    }

    internal static string? BuildAnonymousIdentity(
        string? controlType,
        string? className,
        IReadOnlyList<int>? windowRelativeSiblingPath,
        IReadOnlyList<int>? runtimeId)
    {
        if (!StructuralIdentifierSanitizer.IsAllowed(className)) return null;

        var hasPath = windowRelativeSiblingPath is { Count: > 0 }
            && windowRelativeSiblingPath.Count <= MaximumAncestryDepth
            && windowRelativeSiblingPath.All(index => index >= 0);
        var hasRuntimeId = runtimeId is { Count: > 0 };
        if (!hasPath && !hasRuntimeId) return null;

        var pathPart = hasPath
            ? string.Join('.', windowRelativeSiblingPath!)
            : "unavailable";
        var runtimePart = hasRuntimeId
            ? HashRuntimeId(runtimeId!)
            : "unavailable";
        var shapePart = HashStructuralShape(controlType, className!);

        // Hash the structural shape and put bounded disambiguators first. A
        // hostile or unusually long ClassName can never push the sibling/
        // runtime discriminator beyond the transport's ElementId bound.
        return $"anon:path:{pathPart}:rid:{runtimePart}:shape:{shapePart}";
    }

    private static int[]? TryGetRuntimeId(AutomationElement element)
    {
        try
        {
            var runtimeId = element.Properties.RuntimeId.ValueOrDefault;
            return runtimeId is { Length: > 0 } ? runtimeId : null;
        }
        catch
        {
            return null;
        }
    }

    private static string HashRuntimeId(IReadOnlyList<int> runtimeId)
    {
        // RuntimeId is structural, not user text. Hashing avoids exposing the
        // provider's raw identifier while retaining within-session identity.
        var canonical = string.Join(',', runtimeId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string HashStructuralShape(string? controlType, string className)
    {
        var canonical = $"{controlType ?? "Unknown"}|{className}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
