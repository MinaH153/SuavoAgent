using System.Text.RegularExpressions;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

public enum BrowserFamily
{
    Chrome,
    Edge,
}

public enum BrowserConnectorState
{
    HandshakePending,
    Ready,
    Degraded,
    Disconnected,
}

/// <summary>
/// Privacy-safe connector state. Reason codes are a closed vocabulary and
/// never contain origins, extension IDs, hostnames, URLs, paths, or exception
/// messages.
/// </summary>
public sealed record BrowserConnectorStatus(
    BrowserConnectorState State,
    string ReasonCode,
    DateTimeOffset Timestamp);

/// <summary>
/// The only browser observation allowed to leave the native-host boundary.
/// Known domains become a coarse local category. Unknown domains become a
/// keyed hash. A raw hostname is deliberately not representable here.
/// </summary>
public sealed record BrowserDomainObservation(
    string Category,
    string? HostnameHash,
    BrowserFamily Browser,
    long Counter,
    DateTimeOffset Timestamp);

public interface IBrowserConnectorSink
{
    void OnStatus(BrowserConnectorStatus status);

    void OnObservation(BrowserDomainObservation observation);
}

public interface IBrowserParentVerifier
{
    ValueTask<BrowserParentVerification> VerifyAsync(
        BrowserConnectorAuthorityEntry authorization,
        nint parentWindowHandle,
        CancellationToken cancellationToken);
}

/// <summary>
/// Proves that the native host's real standard input and output handles are
/// connected to the expected browser-owned native-messaging pipe endpoints.
/// The production implementation obtains those handles from the process; no
/// handle or peer process identifier is accepted from the launch arguments.
/// </summary>
public interface IBrowserNativeChannelVerifier
{
    ValueTask<BrowserNativeChannelVerification> VerifyAsync(
        BrowserConnectorAuthorityEntry authorization,
        CancellationToken cancellationToken);
}

public readonly record struct BrowserParentVerification(bool Trusted, string ReasonCode)
{
    public static BrowserParentVerification Allow() => new(true, BrowserConnectorReasonCodes.Ready);

    public static BrowserParentVerification Deny(string reasonCode) => new(false, reasonCode);
}

public readonly record struct BrowserNativeChannelVerification(bool Trusted, string ReasonCode)
{
    public static BrowserNativeChannelVerification Allow() =>
        new(true, BrowserConnectorReasonCodes.Ready);

    public static BrowserNativeChannelVerification Deny() =>
        new(false, BrowserConnectorReasonCodes.NativeChannelUntrusted);
}

public static class BrowserConnectorReasonCodes
{
    public const string HandshakePending = "handshake_pending";
    public const string Ready = "ready";
    public const string Disconnected = "connector_disconnected";
    public const string AuthorityInvalid = "authority_invalid";
    public const string OriginRejected = "origin_rejected";
    public const string ParentBrowserMismatch = "parent_browser_mismatch";
    public const string ParentBrowserUntrusted = "parent_browser_untrusted";
    public const string NativeChannelUntrusted = "native_channel_untrusted";
    public const string UnsupportedPlatform = "unsupported_platform";
    public const string SessionExpired = "session_expired";
    public const string FrameOversize = "frame_oversize";
    public const string FrameTruncated = "frame_truncated";
    public const string FrameInvalid = "frame_invalid";
    public const string MessageInvalid = "message_invalid";
    public const string ReplayRejected = "replay_rejected";
    public const string ChallengeRejected = "challenge_rejected";
    public const string AuthenticationRejected = "authentication_rejected";
    public const string HostnameRejected = "hostname_rejected";
    public const string CategoryRejected = "category_rejected";
    public const string InternalFailure = "internal_failure";

    private static readonly Regex SafeCode = new(
        "^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsSafe(string? code) =>
        code is not null && SafeCode.IsMatch(code);
}
