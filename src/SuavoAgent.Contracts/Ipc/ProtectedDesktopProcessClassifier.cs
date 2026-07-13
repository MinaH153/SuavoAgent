using System.Collections.Frozen;
using System.Text;

namespace SuavoAgent.Contracts.Ipc;

/// <summary>
/// Immutable process-identity floor for the sandbox boundary.  A process that can
/// carry pharmacy/clinical data, browse arbitrary PHI, edit office documents, or
/// execute/administer the workstation is never a zero-blast-radius sandbox app.
///
/// Callers should pass every identity signal they possess (requested process name,
/// resolved image path, product name, file description, signer subject).  This
/// makes a simple executable rename insufficient when the original vendor/product
/// metadata or install directory still identifies the application.  The Helper
/// additionally authenticates the resolved PID's canonical path and Authenticode
/// signer before any sandbox input is emitted.
/// </summary>
public static class ProtectedDesktopProcessClassifier
{
    private static readonly FrozenSet<string> ExactProcessStems = new[]
    {
        // Pharmacy management systems represented by current/declared adapters.
        "pioneerpharmacy", "pioneerpharmacyhost", "pioneerrx", "computerrx", "rx30",
        "bestrx", "qs1", "qs1nexgen", "nexgen", "liberty", "libertyrx", "libertysoftware",
        "frameworkltc", "frameworkecm", "scriptpro", "pharmaserv", "mckessonpharmaserv",

        // General-purpose surfaces that can display or export PHI.
        "chrome", "chromium", "msedge", "msedgewebview2", "firefox", "iexplore",
        "brave", "bravebrowser", "opera", "vivaldi", "arc", "safari",
        "notepad", "excel", "winword", "outlook", "olk", "msaccess", "powerpnt", "onenote",
        "visio", "mspub", "acrord32", "acrobat", "teams", "ms-teams",

        // Shell/admin surfaces are never sandbox applications.
        "cmd", "conhost", "powershell", "powershellise", "pwsh", "windowsterminal", "wt",
        "wscript", "cscript", "mshta", "regedit", "rundll32", "regsvr32", "taskmgr",
        "mmc", "services", "explorer", "bash", "wsl", "wslhost", "ssh", "python",
        "pythonw", "node", "deno", "java", "javaw",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Strong product/path tokens.  These are intentionally conservative: a false
    // positive merely keeps an app out of the sandbox path and routes it to a
    // purpose-built, higher-assurance integration.
    private static readonly FrozenSet<string> ProtectedTokens = new[]
    {
        "pioneerpharmacy", "pioneerrx", "computerrx", "liberty", "libertyrx", "libertysoftware",
        "qs1nexgen", "frameworkltc", "frameworkecm", "scriptpro", "pharmaserv", "mckesson",
        "pharmacy", "prescription", "dispensing", "patient", "clinical", "medicalrecord",
        "electronichealthrecord", "electronicmedicalrecord",

        // Product/version metadata closes the signed-binary rename case for browsers,
        // Office, PDF readers and command surfaces. Keep tokens specific enough that
        // an unrelated application is merely routed away from sandbox, never mis-actuated.
        "googlechrome", "chromium", "microsoftedge", "mozilla firefox", "mozillafirefox",
        "bravesoftware", "bravebrowser", "operasoftware", "vivaldi", "microsoftoffice",
        "microsoftword", "microsoftexcel", "microsoftoutlook", "microsoftpowerpoint",
        "microsoftonenote", "microsoftvisio", "adobeacrobat", "windowspowershell",
        "windowscommandprocessor", "windowsterminal", "microsoftwindowsscripthost",
    }.Select(Normalize).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when any supplied identity signal describes a protected/non-sandbox process.</summary>
    public static bool IsProtectedIdentity(params string?[] identityParts)
    {
        if (identityParts is null) return false;

        foreach (var raw in identityParts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var leaf = LeafName(raw);
            var stem = StripExeAndGlob(leaf);
            var normalizedStem = Normalize(stem);
            if (ExactProcessStems.Contains(normalizedStem)) return true;

            var normalizedFull = Normalize(raw);
            foreach (var token in ProtectedTokens)
            {
                if (normalizedFull.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Canonical process stem for scope keys and exact comparisons: strips a path,
    /// trailing glob markers and <c>.exe</c>, then retains only ASCII letters/digits.
    /// </summary>
    public static string CanonicalProcessStem(string? identity)
        => Normalize(StripExeAndGlob(LeafName(identity)));

    private static string LeafName(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return string.Empty;
        var value = identity.Trim().Trim('"');
        var slash = value.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : value;
    }

    private static string StripExeAndGlob(string value)
    {
        var trimmed = value.Trim().TrimEnd('*', '?');
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') result.Append(ch);
            else if (ch is >= 'A' and <= 'Z') result.Append(char.ToLowerInvariant(ch));
        }
        return result.ToString();
    }
}
