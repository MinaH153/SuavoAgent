using System.Collections.Concurrent;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.Behavioral;

public sealed class UiaTreeObserverIntegrityTests
{
    private const string ValidHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task EmptyTree_EmitsFailureWithoutSnapshotOrContextUpdate()
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        var contextUpdates = 0;
        var observer = new UiaTreeObserver(
            "unused-test-salt",
            buffer,
            logger,
            _ => Interlocked.Increment(ref contextUpdates));

        observer.PublishCapture(
            // Even a provider that incorrectly supplies a hash cannot turn a
            // zero-element result into current structural context.
            new UiaTreeCapture(true, ValidHash, 0, false, null),
            TimeSpan.Zero);
        await buffer.FlushAsync();

        var status = Assert.Single(delivered);
        Assert.Equal(BehavioralEventType.ObserverStatus, status.Type);
        Assert.Equal("uia_tree", status.Subtype);
        Assert.Equal("empty_tree", status.ElementId);
        Assert.DoesNotContain(delivered, item => item.Type == BehavioralEventType.TreeSnapshot);
        Assert.Equal(0, contextUpdates);
        Assert.Equal(0, observer.LastElementCount);
        Assert.Equal("empty_tree", observer.LastFailureCode);
    }

    [Fact]
    public async Task ValidCaptureAfterFailure_EmitsTruthAndRecovery_ThenUpdatesContext()
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        var updatedHashes = new ConcurrentQueue<string>();
        var observer = new UiaTreeObserver(
            "unused-test-salt",
            buffer,
            logger,
            updatedHashes.Enqueue);

        observer.PublishCapture(
            new UiaTreeCapture(false, null, 0, false, "provider_failure"),
            TimeSpan.FromMilliseconds(5));
        observer.PublishCapture(
            new UiaTreeCapture(true, ValidHash, 5000, true, null),
            TimeSpan.FromMilliseconds(20));
        await buffer.FlushAsync();

        var events = delivered.ToArray();
        Assert.Collection(
            events,
            failure =>
            {
                Assert.Equal(BehavioralEventType.ObserverStatus, failure.Type);
                Assert.Equal("provider_failure", failure.ElementId);
            },
            snapshot =>
            {
                Assert.Equal(BehavioralEventType.TreeSnapshot, snapshot.Type);
                Assert.Equal(ValidHash, snapshot.TreeHash);
                Assert.Equal("truncated", snapshot.ElementId);
                Assert.Equal(5000, snapshot.OccurrenceCount);
            },
            recovered =>
            {
                Assert.Equal(BehavioralEventType.ObserverStatus, recovered.Type);
                Assert.Equal("recovered", recovered.ElementId);
            });
        Assert.Equal(new[] { ValidHash }, updatedHashes.ToArray());
        Assert.Equal(5000, observer.LastElementCount);
        Assert.True(observer.LastSnapshotTruncated);
        Assert.Null(observer.LastFailureCode);
    }

    [Fact]
    public async Task IsolatedTimeout_EmitsFailure_ThenFutureCaptureRecovers()
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        var provider = new SequencedProvider(
            new WindowStructureSnapshot(false, null, 0, false, "provider_timeout"),
            SuccessSnapshot(windowHandle: 9001, processId: 77));
        var observer = CreateIsolatedObserver(buffer, logger, provider);

        observer.CaptureExpectedWindow();
        observer.CaptureExpectedWindow();
        await buffer.FlushAsync();

        var events = delivered.ToArray();
        Assert.Collection(
            events,
            failure => Assert.Equal("provider_timeout", failure.ElementId),
            snapshot => Assert.Equal(ValidHash, snapshot.TreeHash),
            recovered => Assert.Equal("recovered", recovered.ElementId));
        Assert.Equal(2, provider.CaptureCount);
        Assert.All(provider.Requests, request =>
        {
            Assert.Equal((nint)9001, request.WindowHandle);
            Assert.Equal(77, request.ExpectedProcessId);
            Assert.Equal(WindowStructureCaptureProfile.Pms, request.Profile);
        });
    }

    [Theory]
    [InlineData(9002, 77)]
    [InlineData(9001, 78)]
    public async Task WorkerResultForWrongHwndOrPid_IsRejected(
        long workerWindowHandle,
        int workerProcessId)
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        var provider = new SequencedProvider(
            SuccessSnapshot(workerWindowHandle, workerProcessId));
        var observer = CreateIsolatedObserver(buffer, logger, provider);

        observer.CaptureExpectedWindow();
        await buffer.FlushAsync();

        var failure = Assert.Single(delivered);
        Assert.Equal(BehavioralEventType.ObserverStatus, failure.Type);
        Assert.Equal("worker_identity_mismatch", failure.ElementId);
        Assert.DoesNotContain(delivered, item => item.Type == BehavioralEventType.TreeSnapshot);
    }

    [Fact]
    public async Task ConcurrentCapture_IsRejectedWithoutOverlappingWorker()
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var captureStarted = new ManualResetEventSlim();
        using var releaseCapture = new ManualResetEventSlim();
        var provider = new BlockingProvider(captureStarted, releaseCapture);
        var observer = CreateIsolatedObserver(buffer, logger, provider);

        var firstCapture = Task.Run(observer.CaptureExpectedWindow);
        Assert.True(captureStarted.Wait(TimeSpan.FromSeconds(1)));
        observer.CaptureExpectedWindow();
        Assert.Equal(1, provider.MaximumConcurrentCaptures);

        releaseCapture.Set();
        await firstCapture;
        await buffer.FlushAsync();

        Assert.Equal(1, provider.CaptureCount);
        Assert.Equal(1, provider.MaximumConcurrentCaptures);
        Assert.Contains(delivered, item => item.ElementId == "capture_busy");
        Assert.Contains(delivered, item => item.Type == BehavioralEventType.TreeSnapshot);
        Assert.Contains(delivered, item => item.ElementId == "recovered");
    }

    [Fact]
    public async Task ProcessAuthorityRevokedDuringWorkerCapture_RejectsSnapshot()
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        var provider = new SequencedProvider(SuccessSnapshot(9001, 77));
        var authorityChecks = 0;
        var observer = new UiaTreeObserver(
            "unused-test-salt",
            buffer,
            logger,
            expectedProcessId: () => 77,
            processTrusted: _ => Interlocked.Increment(ref authorityChecks) == 1,
            windowLocator: new StubWindowLocator(9001),
            snapshotProvider: provider);

        observer.CaptureExpectedWindow();
        await buffer.FlushAsync();

        var failure = Assert.Single(delivered);
        Assert.Equal(BehavioralEventType.ObserverStatus, failure.Type);
        Assert.Equal("process_authority_denied", failure.ElementId);
        Assert.Equal(2, authorityChecks);
        Assert.Equal(1, provider.CaptureCount);
        Assert.DoesNotContain(delivered, item => item.Type == BehavioralEventType.TreeSnapshot);
    }

    [Fact]
    public async Task ProcessAuthorityDeniedBeforeCapture_DoesNotLaunchWorker()
    {
        var delivered = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = CreateBuffer(delivered);
        using var logger = new LoggerConfiguration().CreateLogger();
        var provider = new SequencedProvider(SuccessSnapshot(9001, 77));
        var observer = new UiaTreeObserver(
            "unused-test-salt",
            buffer,
            logger,
            expectedProcessId: () => 77,
            processTrusted: _ => false,
            windowLocator: new StubWindowLocator(9001),
            snapshotProvider: provider);

        observer.CaptureExpectedWindow();
        await buffer.FlushAsync();

        Assert.Equal("process_authority_denied", Assert.Single(delivered).ElementId);
        Assert.Equal(0, provider.CaptureCount);
    }

    private static UiaTreeObserver CreateIsolatedObserver(
        BehavioralEventBuffer buffer,
        ILogger logger,
        IWindowStructureSnapshotProvider provider) =>
        new(
            "unused-test-salt",
            buffer,
            logger,
            expectedProcessId: () => 77,
            processTrusted: _ => true,
            windowLocator: new StubWindowLocator(9001),
            snapshotProvider: provider);

    private static WindowStructureSnapshot SuccessSnapshot(
        long windowHandle,
        int processId) =>
        new(
            true,
            ValidHash,
            42,
            false,
            null,
            windowHandle,
            processId);

    private static BehavioralEventBuffer CreateBuffer(
        ConcurrentQueue<BehavioralEvent> delivered) =>
        new(
            capacity: 20,
            batchSize: 20,
            flushAction: events =>
            {
                foreach (var behavioralEvent in events)
                    delivered.Enqueue(behavioralEvent);
                return Task.CompletedTask;
            });

    private sealed class StubWindowLocator : IPmsWindowLocator
    {
        private readonly nint _windowHandle;

        internal StubWindowLocator(nint windowHandle) => _windowHandle = windowHandle;

        public bool TryLocate(int expectedProcessId, out nint windowHandle)
        {
            windowHandle = _windowHandle;
            return expectedProcessId == 77;
        }
    }

    private sealed class SequencedProvider : IWindowStructureSnapshotProvider
    {
        private readonly WindowStructureSnapshot[] _snapshots;
        private readonly ConcurrentQueue<CaptureRequest> _requests = new();
        private int _captureCount;

        internal SequencedProvider(params WindowStructureSnapshot[] snapshots) =>
            _snapshots = snapshots;

        internal int CaptureCount => Volatile.Read(ref _captureCount);
        internal IReadOnlyList<CaptureRequest> Requests => _requests.ToArray();

        public WindowStructureSnapshot Capture(
            nint windowHandle,
            int? expectedProcessId = null,
            WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp)
        {
            _requests.Enqueue(new CaptureRequest(
                windowHandle,
                expectedProcessId.GetValueOrDefault(),
                profile));
            var index = Interlocked.Increment(ref _captureCount) - 1;
            return _snapshots[Math.Min(index, _snapshots.Length - 1)];
        }
    }

    private sealed class BlockingProvider : IWindowStructureSnapshotProvider
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;
        private int _captureCount;
        private int _active;
        private int _maximumConcurrentCaptures;

        internal BlockingProvider(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        internal int CaptureCount => Volatile.Read(ref _captureCount);
        internal int MaximumConcurrentCaptures => Volatile.Read(ref _maximumConcurrentCaptures);

        public WindowStructureSnapshot Capture(
            nint windowHandle,
            int? expectedProcessId = null,
            WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp)
        {
            Interlocked.Increment(ref _captureCount);
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximumConcurrentCaptures, active);
            _started.Set();
            _release.Wait(TimeSpan.FromSeconds(2));
            Interlocked.Decrement(ref _active);
            return SuccessSnapshot(windowHandle.ToInt64(), expectedProcessId.GetValueOrDefault());
        }
    }

    private sealed record CaptureRequest(
        nint WindowHandle,
        int ExpectedProcessId,
        WindowStructureCaptureProfile Profile);

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (candidate <= current) return;
                if (Interlocked.CompareExchange(ref target, candidate, current) == current) return;
            }
        }
    }
}
