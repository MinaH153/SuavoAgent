using Serilog;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

/// <summary>
/// Binary native-messaging mode. Program calls this before constructing any
/// console sink so Chromium's length-prefixed stdin/stdout channel cannot be
/// corrupted by human-readable logs.
/// </summary>
internal static class BrowserNativeMessagingEntryPoint
{
    public static bool IsCandidate(IReadOnlyList<string> arguments) =>
        arguments.Count >= 1 &&
        arguments[0].StartsWith("chrome-extension://", StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Stream nativeInput,
        Stream nativeOutput,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !BrowserHostLaunchContext.TryParse(arguments, out var launchContext))
            return 2;

        var trust = BrowserConnectorTrustStore.LoadProduction(DateTimeOffset.UtcNow);
        if (!trust.Valid || trust.Authority is null)
            return 3;

        // Deliberately has no sinks. In native-host mode stdout belongs solely
        // to Chrome/Edge framing, and stderr is not a PHI-safe telemetry path.
        using var silentLogger = new LoggerConfiguration().CreateLogger();
        try
        {
            IBrowserParentVerifier parentVerifier = new WindowsBrowserParentVerifier();
            IBrowserNativeChannelVerifier channelVerifier =
                new WindowsBrowserNativeChannelVerifier();
            var relayIdentity = new WindowsBrowserRelayPeerIdentityVerifier();
            var transport = new NamedPipeBrowserRelayClientTransport(
                BrowserRelayPipeName.ForCurrentProcess(),
                relayIdentity);
            var adapterDirectory = Path.Combine(AppContext.BaseDirectory, "adapters");
            var industryAdapter = SuavoAgent.Core.Config.IndustryAdapter.LoadForIndustry(
                "pharmacy",
                adapterDirectory);
            return await RunVerifiedAsync(
                launchContext,
                trust.Authority,
                channelVerifier,
                parentVerifier,
                transport,
                industryAdapter.ClassifyDomain,
                silentLogger,
                nativeInput,
                nativeOutput,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch
        {
            return 5;
        }
    }

    internal static async Task<int> RunVerifiedAsync(
        BrowserHostLaunchContext launchContext,
        VerifiedBrowserConnectorAuthority authority,
        IBrowserNativeChannelVerifier channelVerifier,
        IBrowserParentVerifier parentVerifier,
        IBrowserRelayClientTransport relayTransport,
        Func<string, string?> domainClassifier,
        ILogger logger,
        Stream nativeInput,
        Stream nativeOutput,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(launchContext);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(channelVerifier);
        ArgumentNullException.ThrowIfNull(parentVerifier);
        ArgumentNullException.ThrowIfNull(relayTransport);
        ArgumentNullException.ThrowIfNull(domainClassifier);
        ArgumentNullException.ThrowIfNull(logger);

        var clock = timeProvider ?? TimeProvider.System;
        if (authority.ExpiresAt <= clock.GetUtcNow() ||
            !authority.TryAuthorize(launchContext.Origin, out var exactCaller))
            return 4;

        BrowserNativeChannelVerification channel;
        try
        {
            channel = await channelVerifier.VerifyAsync(
                exactCaller,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return 4;
        }
        if (!channel.Trusted)
            return 4;

        BrowserParentVerification caller;
        try
        {
            caller = await parentVerifier.VerifyAsync(
                exactCaller,
                launchContext.ParentWindowHandle,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return 4;
        }
        if (!caller.Trusted)
            return 4;

        // Only after exact signed authority, kernel-backed native-channel
        // proof, and corroborative parent-browser verification may the second
        // Helper instance request the lease-derived domain-hash key.
        await using var relay = await BrowserObservationRelayClient.ConnectAsync(
            relayTransport,
            cancellationToken).ConfigureAwait(false);
        using var host = new BrowserNativeMessagingHost(
            authority,
            parentVerifier,
            relay,
            domainClassifier,
            relay.DomainHashKey.Span,
            logger,
            timeProvider: clock);
        var result = await host.RunAsync(
            nativeInput,
            nativeOutput,
            launchContext,
            cancellationToken).ConfigureAwait(false);
        return result.Connected ||
               string.Equals(
                   result.ReasonCode,
                   BrowserConnectorReasonCodes.Disconnected,
                   StringComparison.Ordinal)
            ? 0
            : 4;
    }
}
