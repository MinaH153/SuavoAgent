using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserDomainObserverTests
{
    [Fact]
    public async Task AuthenticatedObservation_EmitsCategoryWithoutRawHostname()
    {
        var events = new List<BehavioralEvent>();
        using var buffer = Buffer(events);
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new BrowserDomainObserver(
            buffer,
            "daily-salt",
            domain => domain == "example.com" ? "business_portal" : null,
            logger);

        observer.OnObservation(new BrowserDomainObservation(
            "business_portal",
            null,
            BrowserFamily.Chrome,
            1,
            DateTimeOffset.UtcNow));
        await buffer.FlushAsync();

        var behavioralEvent = Assert.Single(events);
        Assert.Equal("browser_domain", behavioralEvent.Subtype);
        Assert.Equal("business_portal", behavioralEvent.ElementId);
        Assert.Null(behavioralEvent.NameHash);
        Assert.Equal("chrome", behavioralEvent.ClassName);
    }

    [Fact]
    public async Task RawDomainOrWindowTitle_CannotCrossObservationContract()
    {
        var events = new List<BehavioralEvent>();
        using var buffer = Buffer(events);
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new BrowserDomainObserver(buffer, "daily-salt", _ => null, logger);

        observer.OnObservation(new BrowserDomainObservation(
            "Prescription Queue - example.com - Google Chrome",
            null,
            BrowserFamily.Chrome,
            1,
            DateTimeOffset.UtcNow));
        await buffer.FlushAsync();

        var status = Assert.Single(events);
        Assert.Equal(BehavioralEventType.ObserverStatus, status.Type);
        Assert.Equal(BrowserConnectorReasonCodes.MessageInvalid, status.ElementId);
    }

    [Fact]
    public async Task UnknownHostname_RequiresOnlyLowercaseKeyedSha256()
    {
        var events = new List<BehavioralEvent>();
        using var buffer = Buffer(events);
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new BrowserDomainObserver(buffer, "daily-salt", _ => null, logger);
        var hash = new string('a', 64);

        observer.OnObservation(new BrowserDomainObservation(
            "unknown",
            hash,
            BrowserFamily.Edge,
            1,
            DateTimeOffset.UtcNow));
        await buffer.FlushAsync();

        var behavioralEvent = Assert.Single(events);
        Assert.Equal("unknown", behavioralEvent.ElementId);
        Assert.Equal(hash, behavioralEvent.NameHash);
        Assert.Equal("edge", behavioralEvent.ClassName);
    }

    [Fact]
    public async Task MissingConnector_IsVisibleOnceInsteadOfGuessingFromTitle()
    {
        var events = new List<BehavioralEvent>();
        using var buffer = Buffer(events);
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new BrowserDomainObserver(buffer, "daily-salt", _ => null, logger);

        observer.OnBrowserFocusedWithoutConnector();
        observer.OnBrowserFocusedWithoutConnector();
        await buffer.FlushAsync();

        var status = Assert.Single(events);
        Assert.Equal(BehavioralEventType.ObserverStatus, status.Type);
        Assert.Equal("browser_domain", status.Subtype);
        Assert.Equal("connector_unavailable", status.ElementId);
        Assert.Equal(2, observer.ConnectorUnavailableCount);
    }

    [Fact]
    public async Task ReadyConnector_IsNotDowngradedByLegacyFocusCallback()
    {
        var events = new List<BehavioralEvent>();
        using var buffer = Buffer(events);
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new BrowserDomainObserver(buffer, "daily-salt", _ => null, logger);

        observer.OnStatus(new BrowserConnectorStatus(
            BrowserConnectorState.Ready,
            BrowserConnectorReasonCodes.Ready,
            DateTimeOffset.UtcNow));
        observer.OnBrowserFocusedWithoutConnector();
        await buffer.FlushAsync();

        var status = Assert.Single(events);
        Assert.Equal(BrowserConnectorReasonCodes.Ready, status.ElementId);
        Assert.Equal(BrowserConnectorReasonCodes.Ready, observer.ConnectorStatus);
    }

    private static BehavioralEventBuffer Buffer(ICollection<BehavioralEvent> output) =>
        new(
            20,
            20,
            events =>
            {
                foreach (var behavioralEvent in events) output.Add(behavioralEvent);
                return Task.CompletedTask;
            });
}
