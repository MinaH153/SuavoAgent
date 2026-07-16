using System.Text;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

/// <summary>
/// Platform-independent lexical policy for device-bound Windows browser paths.
/// Filesystem canonicalization, reparse, and ACL proof are separate runtime
/// requirements because a syntactically valid signed path is not yet trusted.
/// </summary>
internal static class BrowserExecutablePathPolicy
{
    public const int MaximumPathCharacters = 512;
    public const int MaximumPathUtf8Bytes = 1_024;

    public static bool IsValidAuthorityPath(
        string? path,
        BrowserFamily browser)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > MaximumPathCharacters ||
            Encoding.UTF8.GetByteCount(path) > MaximumPathUtf8Bytes ||
            path.Length < 4 ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            path[2] != '\\' ||
            path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Contains('/') ||
            path.Contains('%') ||
            path.Contains('~') ||
            path.AsSpan(2).Contains(':') ||
            path.Any(character =>
                char.IsControl(character) || character is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        var segments = path[3..].Split('\\');
        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.EndsWith(' ') ||
                segment.EndsWith('.')))
        {
            return false;
        }

        return string.Equals(
            segments[^1],
            ExpectedFileName(browser),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUnderExpectedMachineVendorPath(
        string canonicalPath,
        BrowserFamily browser,
        string programFiles,
        string programFilesX86)
    {
        if (!IsValidAuthorityPath(canonicalPath, browser))
            return false;

        var vendorSuffix = browser switch
        {
            BrowserFamily.Chrome => @"Google\Chrome\Application",
            BrowserFamily.Edge => @"Microsoft\Edge\Application",
            _ => string.Empty,
        };
        if (vendorSuffix.Length == 0)
            return false;

        return IsUnderVendorRoot(canonicalPath, programFiles, vendorSuffix) ||
               IsUnderVendorRoot(canonicalPath, programFilesX86, vendorSuffix);
    }

    public static string ExpectedFileName(BrowserFamily browser) => browser switch
    {
        BrowserFamily.Chrome => "chrome.exe",
        BrowserFamily.Edge => "msedge.exe",
        _ => string.Empty,
    };

    private static bool IsUnderVendorRoot(
        string canonicalPath,
        string root,
        string vendorSuffix)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        var normalizedRoot = root.Replace('/', '\\').TrimEnd('\\');
        if (normalizedRoot.Length < 3 ||
            !char.IsAsciiLetter(normalizedRoot[0]) ||
            normalizedRoot[1] != ':' ||
            normalizedRoot[2] != '\\')
        {
            return false;
        }

        var vendorRoot = normalizedRoot + "\\" + vendorSuffix;
        return canonicalPath.StartsWith(
            vendorRoot + "\\",
            StringComparison.OrdinalIgnoreCase);
    }
}
