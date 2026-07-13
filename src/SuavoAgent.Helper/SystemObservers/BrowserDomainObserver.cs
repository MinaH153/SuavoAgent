using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;

namespace SuavoAgent.Helper.SystemObservers;

public sealed class BrowserDomainObserver : IBrowserConnectorSink
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "iexplore"
    };

    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly object _stateLock = new();
    private string? _lastObservationFingerprint;

    public int ObservationCount { get; private set; }
    public int ConnectorUnavailableCount { get; private set; }
    public string ConnectorStatus { get; private set; } = "not_connected";

    public BrowserDomainObserver(
        BehavioralEventBuffer buffer, string pharmacySalt,
        Func<string, string?> domainClassifier, ILogger logger)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _ = pharmacySalt ?? throw new ArgumentNullException(nameof(pharmacySalt));
        _ = domainClassifier ?? throw new ArgumentNullException(nameof(domainClassifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public static bool IsBrowserProcess(string processName) =>
        BrowserProcesses.Contains(processName);

    public void OnStatus(BrowserConnectorStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var safeStatus = status.State switch
        {
            BrowserConnectorState.Ready => BrowserConnectorReasonCodes.Ready,
            BrowserConnectorState.HandshakePending => BrowserConnectorReasonCodes.HandshakePending,
            BrowserConnectorState.Disconnected => BrowserConnectorReasonCodes.Disconnected,
            BrowserConnectorState.Degraded when BrowserConnectorReasonCodes.IsSafe(status.ReasonCode) =>
                status.ReasonCode,
            _ => BrowserConnectorReasonCodes.InternalFailure,
        };

        lock (_stateLock)
        {
            if (string.Equals(ConnectorStatus, safeStatus, StringComparison.Ordinal))
                return;
            ConnectorStatus = safeStatus;
            _buffer.Enqueue(BehavioralEvent.ObserverStatus("browser_domain", safeStatus));
        }
    }

    public void OnObservation(BrowserDomainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!IsSafeCategory(observation.Category) ||
            (observation.HostnameHash is not null && !IsLowerHexSha256(observation.HostnameHash)) ||
            (observation.Category == "unknown") != (observation.HostnameHash is not null) ||
            observation.Counter <= 0)
        {
            _logger.Warning("Browser observation rejected ({ReasonCode})",
                BrowserConnectorReasonCodes.MessageInvalid);
            OnStatus(new BrowserConnectorStatus(
                BrowserConnectorState.Degraded,
                BrowserConnectorReasonCodes.MessageInvalid,
                DateTimeOffset.UtcNow));
            return;
        }

        var fingerprint = observation.HostnameHash ?? observation.Category;
        lock (_stateLock)
        {
            if (string.Equals(_lastObservationFingerprint, fingerprint, StringComparison.Ordinal))
                return;
            _lastObservationFingerprint = fingerprint;
            _buffer.Enqueue(BehavioralEvent.Interaction(
                subtype: "browser_domain",
                treeHash: null,
                elementId: observation.Category,
                controlType: "browser",
                className: observation.Browser.ToString().ToLowerInvariant(),
                nameHash: observation.HostnameHash));
            ObservationCount++;
        }
    }

    public void OnBrowserFocusedWithoutConnector()
    {
        lock (_stateLock)
        {
            ConnectorUnavailableCount++;
            if (ConnectorStatus == BrowserConnectorReasonCodes.Ready)
                return;
            if (ConnectorStatus != "connector_unavailable")
                _buffer.Enqueue(BehavioralEvent.ObserverStatus("browser_domain", "connector_unavailable"));
            ConnectorStatus = "connector_unavailable";
        }
    }

    private static bool IsSafeCategory(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || !char.IsAsciiLetterLower(value[0]))
            return false;
        return value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character is '_' or ':' or '-');
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
