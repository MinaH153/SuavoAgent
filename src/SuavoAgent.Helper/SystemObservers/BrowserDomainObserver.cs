using System.Text.RegularExpressions;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class BrowserDomainObserver
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "iexplore"
    };

    private static readonly Regex DomainRegex = new(
        @"^(?:https?://)?([^/:?\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly Func<string, string?> _domainClassifier;
    private readonly ILogger _logger;
    private string? _lastDomainHash;

    public int ObservationCount { get; private set; }

    public BrowserDomainObserver(
        BehavioralEventBuffer buffer, string pharmacySalt,
        Func<string, string?> domainClassifier, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _domainClassifier = domainClassifier;
        _logger = logger;
    }

    public static bool IsBrowserProcess(string processName) =>
        BrowserProcesses.Contains(processName);

    /// <summary>
    /// Called with a pre-extracted domain (already sanitized by the caller).
    /// The raw window title should NEVER reach this method — hash or extract domain at the call site.
    /// </summary>
    public void OnDomainDetected(string domain)
    {
        if (string.IsNullOrEmpty(domain)) return;

        var domainHash = UiaPropertyScrubber.HmacHash(domain, _pharmacySalt);
        if (domainHash == _lastDomainHash) return;
        _lastDomainHash = domainHash;

        var category = _domainClassifier(domain);

        var evt = BehavioralEvent.Interaction(
            subtype: "browser_domain",
            treeHash: null,
            elementId: category ?? "unknown",
            controlType: "browser",
            className: null,
            nameHash: category == null ? domainHash : null
        );

        _buffer.Enqueue(evt);
        ObservationCount++;
    }

    public static string? ExtractDomain(string input)
    {
        var match = DomainRegex.Match(input);
        if (match.Success)
        {
            var domain = match.Groups[1].Value.ToLowerInvariant();
            if (domain.Contains('.') && !domain.All(char.IsDigit))
                return domain;
        }
        return null;
    }
}
