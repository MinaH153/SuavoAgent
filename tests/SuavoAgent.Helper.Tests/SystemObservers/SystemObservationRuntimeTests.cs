using System.Collections.Concurrent;
using Microsoft.Win32;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class SystemObservationRuntimeTests
{
    [Fact]
    public async Task StartEmitsReadinessAndDisposeRevokesActiveState()
    {
        var captured = new ConcurrentQueue<BehavioralEvent>();
        var buffer = Buffer(captured);
        using var logger = new LoggerConfiguration().CreateLogger();
        var activeTransitions = new ConcurrentQueue<bool>();
        await using var runtime = new SystemObservationRuntime(
            buffer,
            observationKey: "opaque-test-observation-key",
            domainClassifier: _ => "business_portal",
            currentLease: () => null,
            setObservationActive: activeTransitions.Enqueue,
            logger,
            CancellationToken.None);

        runtime.Start();
        await WaitUntilAsync(() =>
            captured.Any(item =>
                item.Type == BehavioralEventType.ObserverStatus &&
                item.Subtype == "system_liveness"));

        Assert.NotEmpty(activeTransitions);
        Assert.All(activeTransitions, Assert.True);
        Assert.Contains(captured, item => item.Type == BehavioralEventType.StationProfile);
        Assert.Contains(captured, item =>
            item.Type == BehavioralEventType.ObserverStatus &&
            item.Subtype == "browser_domain");
        Assert.Contains(captured, item =>
            item.Type == BehavioralEventType.ObserverStatus &&
            item.Subtype == "print");

        var error = Assert.Throws<InvalidOperationException>(runtime.Start);
        Assert.Equal("system_observers_already_started", error.Message);

        await runtime.DisposeAsync();
        Assert.False(activeTransitions.Last());

        // Idempotent shutdown must not emit another inactive edge or dispose
        // an already-disposed buffer twice in a way that escapes.
        await runtime.DisposeAsync();
        Assert.False(activeTransitions.Last());
    }

    [Fact]
    public async Task DisposeBeforeStartIsBoundedAndMarksInactive()
    {
        var captured = new ConcurrentQueue<BehavioralEvent>();
        var transitions = new List<bool>();
        using var logger = new LoggerConfiguration().CreateLogger();
        var runtime = new SystemObservationRuntime(
            Buffer(captured),
            "opaque-key",
            _ => null,
            () => null,
            transitions.Add,
            logger,
            CancellationToken.None);

        await runtime.DisposeAsync();

        Assert.Equal([false], transitions);
    }

    [Fact]
    public async Task ObserverTaskFaultRevokesHealthAndCannotBeRenewedByLiveness()
    {
        var transitions = new ConcurrentQueue<bool>();
        using var logger = new LoggerConfiguration().CreateLogger();
        await using var runtime = new SystemObservationRuntime(
            Buffer(new ConcurrentQueue<BehavioralEvent>()),
            "opaque-key",
            _ => null,
            () => null,
            transitions.Enqueue,
            logger,
            CancellationToken.None,
            foregroundRunner: _ => Task.FromException(
                new InvalidOperationException("synthetic-observer-fault")),
            printRunner: token => Task.Delay(Timeout.InfiniteTimeSpan, token));

        runtime.Start();
        await WaitUntilAsync(() => transitions.TryPeek(out _) && transitions.Last() == false);
        await Task.Delay(25);

        Assert.False(transitions.Last());
    }

    [Fact]
    public void ConstructorRejectsMissingCompositionDependencies()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var buffer = Buffer(new ConcurrentQueue<BehavioralEvent>());
        try
        {
            Assert.Throws<ArgumentNullException>(() => new SystemObservationRuntime(
                null!, "key", _ => null, () => null, _ => { }, logger,
                CancellationToken.None));
            Assert.Throws<ArgumentException>(() => new SystemObservationRuntime(
                buffer, " ", _ => null, () => null, _ => { }, logger,
                CancellationToken.None));
            Assert.Throws<ArgumentNullException>(() => new SystemObservationRuntime(
                buffer, "key", null!, () => null, _ => { }, logger,
                CancellationToken.None));
            Assert.Throws<ArgumentNullException>(() => new SystemObservationRuntime(
                buffer, "key", _ => null, null!, _ => { }, logger,
                CancellationToken.None));
            Assert.Throws<ArgumentNullException>(() => new SystemObservationRuntime(
                buffer, "key", _ => null, () => null, null!, logger,
                CancellationToken.None));
            Assert.Throws<ArgumentNullException>(() => new SystemObservationRuntime(
                buffer, "key", _ => null, () => null, _ => { }, null!,
                CancellationToken.None));
        }
        finally
        {
            buffer.Dispose();
        }
    }

    [Fact]
    public async Task ForegroundTrackerHonorsPreCancelledTokenWithoutPollingDesktop()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var buffer = Buffer(new ConcurrentQueue<BehavioralEvent>());
        using var tracker = new ForegroundTracker(buffer, "opaque-key", logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await tracker.RunAsync(cancellation.Token);

        Assert.Equal(0, tracker.TransitionCount);
    }

    [Fact]
    public async Task StationProfileContainsNoRawMachineName()
    {
        var captured = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = Buffer(captured);
        using var logger = new LoggerConfiguration().CreateLogger();
        var profiler = new StationProfiler(buffer, "opaque-key", logger);

        profiler.CaptureProfile();
        await WaitUntilAsync(() =>
            captured.Any(item => item.Type == BehavioralEventType.StationProfile));

        var profile = Assert.Single(
            captured,
            item => item.Type == BehavioralEventType.StationProfile);
        var serialized = System.Text.Json.JsonSerializer.Serialize(profile);
        Assert.DoesNotContain(Environment.MachineName, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionLogon, "logon")]
    [InlineData(SessionSwitchReason.SessionLogoff, "logoff")]
    [InlineData(SessionSwitchReason.SessionLock, "lock")]
    [InlineData(SessionSwitchReason.SessionUnlock, "unlock")]
    [InlineData(SessionSwitchReason.RemoteConnect, "rdp_connect")]
    [InlineData(SessionSwitchReason.RemoteDisconnect, "rdp_disconnect")]
    [InlineData(SessionSwitchReason.ConsoleConnect, "console_connect")]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, "console_disconnect")]
    public void SessionReasonsMapToClosedSafeLabels(
        SessionSwitchReason reason,
        string expected)
    {
        Assert.Equal(expected, UserSessionObserver.MapReason(reason));
    }

    [Fact]
    public void UnknownSessionReasonIsIgnored()
    {
        Assert.Null(UserSessionObserver.MapReason((SessionSwitchReason)int.MaxValue));
    }

    private static BehavioralEventBuffer Buffer(
        ConcurrentQueue<BehavioralEvent> captured) => new(
            capacity: 64,
            batchSize: 1,
            flushAction: events =>
            {
                foreach (var item in events)
                    captured.Enqueue(item);
                return Task.CompletedTask;
            });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }
}
