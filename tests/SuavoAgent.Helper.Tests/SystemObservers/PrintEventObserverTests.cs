using System.Threading.Channels;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class PrintEventObserverTests
{
    [Fact]
    public async Task Notification_EmitsOnlyHmacJobIdentity_AndDeduplicates()
    {
        var received = new List<BehavioralEvent>();
        using var buffer = CreateBuffer(received);
        using var logger = new LoggerConfiguration().CreateLogger();
        var source = new ControlledPrintNotificationSource();
        using var observer = new PrintEventObserver(buffer, "daily-salt", source, logger);
        using var cancellation = new CancellationTokenSource();

        var run = observer.RunAsync(cancellation.Token);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(2));
        var rawPrinterName = "Front Pharmacy Printer";
        await source.PublishAsync(PrintMonitorSignal.JobAdded(rawPrinterName, 17));
        await source.PublishAsync(PrintMonitorSignal.JobAdded(rawPrinterName, 17));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await buffer.FlushAsync();

        var behavioralEvent = Assert.Single(
            received,
            item => item.Type == BehavioralEventType.Interaction);
        Assert.Equal("print_job", behavioralEvent.Subtype);
        Assert.Equal("print", behavioralEvent.ElementId);
        Assert.NotNull(behavioralEvent.NameHash);
        Assert.DoesNotContain(rawPrinterName, behavioralEvent.NameHash!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            received,
            item => ContainsRawValue(item, rawPrinterName));
        Assert.Equal(1, observer.PrintEventCount);
        Assert.True(observer.IsAvailable);
        Assert.NotNull(observer.LastSuccessfulNotificationUtc);
    }

    [Fact]
    public async Task RunAsync_CapturesJobThatExistsOnlyBetweenLegacyPollIntervals()
    {
        var received = new List<BehavioralEvent>();
        using var buffer = CreateBuffer(received);
        using var logger = new LoggerConfiguration().CreateLogger();
        var source = new ControlledPrintNotificationSource();
        using var observer = new PrintEventObserver(buffer, "daily-salt", source, logger);
        using var cancellation = new CancellationTokenSource();

        var run = observer.RunAsync(cancellation.Token);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(2));

        // The job is created and removed before the old ten-second polling window.
        // The observer sees the add notification, not a later queue snapshot.
        source.TransientJobExists = true;
        await source.PublishAsync(PrintMonitorSignal.JobAdded("Fast Queue", 91));
        source.TransientJobExists = false;

        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await buffer.FlushAsync();

        Assert.False(source.TransientJobExists);
        Assert.Single(received, item => item.Subtype == "print_job");
        Assert.Equal(1, observer.PrintEventCount);
    }

    [Fact]
    public async Task PrinterFailure_DoesNotStopJobsFromAnotherPrinter()
    {
        var received = new List<BehavioralEvent>();
        using var buffer = CreateBuffer(received);
        using var logger = new LoggerConfiguration().CreateLogger();
        var source = new ControlledPrintNotificationSource();
        using var observer = new PrintEventObserver(buffer, "daily-salt", source, logger);
        using var cancellation = new CancellationTokenSource();

        var run = observer.RunAsync(cancellation.Token);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(2));
        await source.PublishAsync(PrintMonitorSignal.PrinterFailure("winspool_5"));
        await source.PublishAsync(PrintMonitorSignal.JobAdded("Healthy Queue", 28));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await buffer.FlushAsync();

        Assert.Contains(
            received,
            item => item.Type == BehavioralEventType.ObserverStatus
                && item.ElementId == "degraded_winspool_5");
        Assert.Single(received, item => item.Subtype == "print_job");
        Assert.True(observer.IsAvailable);
        Assert.Equal(1, observer.FailureCount);
    }

    [Fact]
    public async Task SourceFailure_ResubscribesAndEmitsRecovery()
    {
        var received = new List<BehavioralEvent>();
        using var buffer = CreateBuffer(received);
        using var logger = new LoggerConfiguration().CreateLogger();
        var source = new FailOncePrintNotificationSource();
        using var observer = new PrintEventObserver(buffer, "daily-salt", source, logger);
        using var cancellation = new CancellationTokenSource();

        var run = observer.RunAsync(cancellation.Token);
        await source.Recovered.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        await buffer.FlushAsync();

        Assert.True(source.AttemptCount >= 2);
        Assert.Contains(
            received,
            item => item.Type == BehavioralEventType.ObserverStatus
                && item.ElementId == "winspool_5");
        Assert.Contains(
            received,
            item => item.Type == BehavioralEventType.ObserverStatus
                && item.ElementId == "recovered");
        Assert.Single(received, item => item.Subtype == "print_job");
        Assert.True(observer.IsAvailable);
    }

    [Fact]
    public async Task Dispose_UnblocksActiveNotificationWait()
    {
        var received = new List<BehavioralEvent>();
        using var buffer = CreateBuffer(received);
        using var logger = new LoggerConfiguration().CreateLogger();
        var source = new ControlledPrintNotificationSource();
        using var observer = new PrintEventObserver(buffer, "daily-salt", source, logger);

        var run = observer.RunAsync(CancellationToken.None);
        await source.Started.WaitAsync(TimeSpan.FromSeconds(2));
        observer.Dispose();

        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(source.CancellationObserved);
    }

    private static BehavioralEventBuffer CreateBuffer(List<BehavioralEvent> received) =>
        new(
            20,
            20,
            events =>
            {
                received.AddRange(events);
                return Task.CompletedTask;
            });

    private static bool ContainsRawValue(BehavioralEvent item, string rawValue) =>
        new[]
        {
            item.Subtype,
            item.TreeHash,
            item.ElementId,
            item.ControlType,
            item.ClassName,
            item.NameHash,
            item.BoundingRect,
        }.Any(value => value?.Contains(rawValue, StringComparison.Ordinal) == true);

    private sealed class ControlledPrintNotificationSource : IPrintJobNotificationSource
    {
        private readonly Channel<QueuedSignal> _signals =
            Channel.CreateUnbounded<QueuedSignal>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
        private readonly TaskCompletionSource<bool> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSupported => true;
        public Task Started => _started.Task;
        public bool TransientJobExists { get; set; }
        public bool CancellationObserved { get; private set; }

        public async Task ObserveAsync(
            Action<PrintMonitorSignal> onSignal,
            CancellationToken cancellationToken)
        {
            onSignal(PrintMonitorSignal.Ready());
            _started.TrySetResult(true);
            try
            {
                await foreach (var queued in _signals.Reader.ReadAllAsync(cancellationToken))
                {
                    onSignal(queued.Signal);
                    queued.Processed.TrySetResult(true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public async Task PublishAsync(PrintMonitorSignal signal)
        {
            var processed = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await _signals.Writer.WriteAsync(new QueuedSignal(signal, processed));
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        private sealed record QueuedSignal(
            PrintMonitorSignal Signal,
            TaskCompletionSource<bool> Processed);
    }

    private sealed class FailOncePrintNotificationSource : IPrintJobNotificationSource
    {
        private readonly TaskCompletionSource<bool> _recovered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attemptCount;

        public bool IsSupported => true;
        public int AttemptCount => Volatile.Read(ref _attemptCount);
        public Task Recovered => _recovered.Task;

        public async Task ObserveAsync(
            Action<PrintMonitorSignal> onSignal,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attemptCount) == 1)
                throw new PrintSpoolerException(5);

            onSignal(PrintMonitorSignal.Ready());
            onSignal(PrintMonitorSignal.JobAdded("Recovered Queue", 37));
            _recovered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
