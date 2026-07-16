using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class MultiAppUiaObserverTests
{
    [Fact]
    public async Task OnAppFocused_EmitsProviderTreeHash_NotSyntheticTitleHash()
    {
        IReadOnlyList<BehavioralEvent>? captured = null;
        using var buffer = new BehavioralEventBuffer(
            capacity: 10,
            batchSize: 10,
            flushAction: events =>
            {
                captured = events;
                return Task.CompletedTask;
            });
        using var logger = new LoggerConfiguration().CreateLogger();
        const string providerHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var observer = new MultiAppUiaObserver(
            buffer,
            new StubSnapshotProvider(new WindowStructureSnapshot(
                true, providerHash, 12, false, null)),
            logger);

        observer.OnAppFocused("PioneerPharmacy.exe", 42);
        await WaitUntilAsync(() => observer.SnapshotCount == 1);
        await buffer.FlushAsync();

        Assert.NotNull(captured);
        var snapshot = Assert.Single(captured!, item =>
            item.Type == BehavioralEventType.TreeSnapshot);
        Assert.Equal(providerHash, snapshot.TreeHash);
        Assert.Equal(12, snapshot.OccurrenceCount);
        Assert.Equal("complete", snapshot.ElementId);
    }

    [Fact]
    public async Task CaptureFailure_EmitsVisibleObserverStatus_NotFakeTree()
    {
        IReadOnlyList<BehavioralEvent>? captured = null;
        using var buffer = new BehavioralEventBuffer(
            capacity: 10,
            batchSize: 10,
            flushAction: events =>
            {
                captured = events;
                return Task.CompletedTask;
            });
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new MultiAppUiaObserver(
            buffer,
            new StubSnapshotProvider(new WindowStructureSnapshot(
                false, null, 0, false, "window_unavailable")),
            logger);

        observer.OnAppFocused("notepad", 42);
        await WaitUntilAsync(() => observer.FailureCount == 1);
        await buffer.FlushAsync();

        var behavioralEvent = Assert.Single(captured!);
        Assert.Equal(BehavioralEventType.ObserverStatus, behavioralEvent.Type);
        Assert.Equal("multi_app_uia", behavioralEvent.Subtype);
        Assert.Equal("window_unavailable", behavioralEvent.ElementId);
        Assert.Null(behavioralEvent.TreeHash);
    }

    [Fact]
    public async Task FailureCountPublishesOnlyAfterObserverStatusIsBuffered()
    {
        IReadOnlyList<BehavioralEvent>? captured = null;
        using var buffer = new BehavioralEventBuffer(
            capacity: 10,
            batchSize: 10,
            flushAction: events =>
            {
                captured = events;
                return Task.CompletedTask;
            });
        using var logger = new LoggerConfiguration().CreateLogger();
        var observer = new MultiAppUiaObserver(
            buffer,
            new StubSnapshotProvider(new WindowStructureSnapshot(
                false, null, 0, false, "unused")),
            logger);
        var stateGate = Assert.IsType<object>(typeof(MultiAppUiaObserver)
            .GetField("_stateLock", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(observer));
        using var callerStarted = new ManualResetEventSlim();
        Task? reportTask = null;

        Monitor.Enter(stateGate);
        try
        {
            reportTask = Task.Run(() =>
            {
                callerStarted.Set();
                observer.OnAppFocused("", 42);
            });
            Assert.True(callerStarted.Wait(TimeSpan.FromSeconds(1)));

            // While the state transaction is blocked, no completion counter may become visible.
            // The old ordering incremented first, then waited here before enqueueing its event.
            Assert.False(SpinWait.SpinUntil(
                () => observer.FailureCount != 0,
                TimeSpan.FromMilliseconds(250)));
            Assert.False(reportTask.IsCompleted);
        }
        finally
        {
            Monitor.Exit(stateGate);
        }

        await reportTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, observer.FailureCount);
        await buffer.FlushAsync();

        var behavioralEvent = Assert.Single(captured!);
        Assert.Equal(BehavioralEventType.ObserverStatus, behavioralEvent.Type);
        Assert.Equal("invalid_window", behavioralEvent.ElementId);
    }

    [Fact]
    public async Task TimedOutCapture_CanRecoverOnNextFocusForSameWindow()
    {
        var captured = new ConcurrentQueue<BehavioralEvent>();
        using var buffer = new BehavioralEventBuffer(
            capacity: 20,
            batchSize: 20,
            flushAction: events =>
            {
                foreach (var behavioralEvent in events)
                    captured.Enqueue(behavioralEvent);
                return Task.CompletedTask;
            });
        using var logger = new LoggerConfiguration().CreateLogger();
        const string providerHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        var provider = new SequencedSnapshotProvider(
            new WindowStructureSnapshot(false, null, 0, false, "provider_timeout"),
            new WindowStructureSnapshot(true, providerHash, 7, true, null));
        var observer = new MultiAppUiaObserver(buffer, provider, logger);

        observer.OnAppFocused("notepad", 42);
        await WaitUntilAsync(() => observer.FailureCount == 1);
        observer.OnAppFocused("notepad", 42);
        await WaitUntilAsync(() => observer.SnapshotCount == 1);
        await buffer.FlushAsync();

        var events = captured.ToArray();
        Assert.Contains(events, item =>
            item.Type == BehavioralEventType.ObserverStatus
            && item.ElementId == "provider_timeout");
        Assert.Contains(events, item =>
            item.Type == BehavioralEventType.TreeSnapshot
            && item.TreeHash == providerHash
            && item.ElementId == "truncated"
            && item.OccurrenceCount == 7);
        Assert.Contains(events, item =>
            item.Type == BehavioralEventType.ObserverStatus
            && item.ElementId == "recovered");
        Assert.Equal(2, provider.CaptureCount);
    }

    [Fact]
    public void IsolatedProvider_TerminatesHungWorker_AndAcceptsFutureCapture()
    {
        const string providerHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var successfulPayload = JsonSerializer.Serialize(new WindowStructureSnapshot(
            true,
            providerHash,
            3,
            false,
            null,
            WindowHandle: 42,
            ProcessId: 777));
        var worker = new SequencedWorkerProcess(
            new WindowSnapshotWorkerExecution(
                Started: true,
                TimedOut: true,
                Terminated: true,
                ExitCode: -1,
                StandardOutput: null,
                FailureCode: "provider_timeout"),
            new WindowSnapshotWorkerExecution(
                Started: true,
                TimedOut: false,
                Terminated: true,
                ExitCode: 0,
                StandardOutput: successfulPayload,
                FailureCode: null));
        var provider = new IsolatedWindowStructureSnapshotProvider(
            worker,
            executablePath: "signed-helper.exe",
            timeout: TimeSpan.FromMilliseconds(25),
            requireWindows: false);

        var timedOut = provider.Capture(42, expectedProcessId: 777);
        var recovered = provider.Capture(42, expectedProcessId: 777);

        Assert.False(timedOut.Success);
        Assert.Equal("provider_timeout", timedOut.FailureCode);
        Assert.True(recovered.Success);
        Assert.Equal(providerHash, recovered.TreeHash);
        Assert.Equal(2, worker.ExecutionCount);
    }

    [Theory]
    [InlineData(43, 777)]
    [InlineData(42, 778)]
    public void IsolatedProvider_RejectsWorkerResultForWrongHwndOrPid(
        long workerWindowHandle,
        int workerProcessId)
    {
        const string providerHash = "123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0";
        var payload = JsonSerializer.Serialize(new WindowStructureSnapshot(
            true,
            providerHash,
            3,
            false,
            null,
            workerWindowHandle,
            workerProcessId));
        var worker = new SequencedWorkerProcess(new WindowSnapshotWorkerExecution(
            Started: true,
            TimedOut: false,
            Terminated: true,
            ExitCode: 0,
            StandardOutput: payload,
            FailureCode: null));
        var provider = new IsolatedWindowStructureSnapshotProvider(
            worker,
            executablePath: "signed-helper.exe",
            timeout: TimeSpan.FromMilliseconds(25),
            requireWindows: false);

        var result = provider.Capture(42, expectedProcessId: 777);

        Assert.False(result.Success);
        Assert.Equal("worker_invalid_snapshot", result.FailureCode);
    }

    [Fact]
    public void IsolatedProvider_RejectsHwndWhosePidChangesAfterWorkerCapture()
    {
        const string providerHash = "23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef01";
        var payload = JsonSerializer.Serialize(new WindowStructureSnapshot(
            true,
            providerHash,
            3,
            false,
            null,
            WindowHandle: 42,
            ProcessId: 777));
        var worker = new SequencedWorkerProcess(new WindowSnapshotWorkerExecution(
            Started: true,
            TimedOut: false,
            Terminated: true,
            ExitCode: 0,
            StandardOutput: payload,
            FailureCode: null));
        var processIdentity = new SequencedProcessIdentity(777, 778);
        var provider = new IsolatedWindowStructureSnapshotProvider(
            worker,
            executablePath: "signed-helper.exe",
            timeout: TimeSpan.FromMilliseconds(25),
            requireWindows: false,
            processIdentity: processIdentity);

        var result = provider.Capture(42, expectedProcessId: 777);

        Assert.False(result.Success);
        Assert.Equal("window_process_changed", result.FailureCode);
    }

    [Fact]
    public void WorkerMode_InvalidHandle_ReturnsBoundedFailurePayload()
    {
        using var output = new StringWriter();

        var handled = UiaSnapshotWorkerMode.TryRun(
            new[] { UiaSnapshotWorkerMode.Switch, "--window-handle", "not-a-handle" },
            output,
            out var exitCode);
        var snapshot = JsonSerializer.Deserialize<WindowStructureSnapshot>(output.ToString());

        Assert.True(handled);
        Assert.Equal(2, exitCode);
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Success);
        Assert.Equal("invalid_window", snapshot.FailureCode);
    }

    [Fact]
    public void WorkerMode_DispatchesAfterCrashHooksAndBeforeConsoleLogging()
    {
        var program = File.ReadAllText(FindRepoFile("src/SuavoAgent.Helper/Program.cs"));
        var wire = program.IndexOf(
            "Wire.AttachUnhandledHooks(WireComponent.Helper",
            StringComparison.Ordinal);
        var worker = program.IndexOf(
            "UiaSnapshotWorkerMode.TryRun(args, Console.Out",
            StringComparison.Ordinal);
        var logging = program.IndexOf(
            "Log.Logger = new LoggerConfiguration",
            StringComparison.Ordinal);

        Assert.True(wire >= 0);
        Assert.True(worker > wire);
        Assert.True(logging > worker);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(predicate());
    }

    private sealed class StubSnapshotProvider : IWindowStructureSnapshotProvider
    {
        private readonly WindowStructureSnapshot _snapshot;

        public StubSnapshotProvider(WindowStructureSnapshot snapshot) => _snapshot = snapshot;

        public WindowStructureSnapshot Capture(
            nint windowHandle,
            int? expectedProcessId = null,
            WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp) =>
            _snapshot;
    }

    private sealed class SequencedSnapshotProvider : IWindowStructureSnapshotProvider
    {
        private readonly WindowStructureSnapshot[] _snapshots;
        private int _captureCount;

        internal SequencedSnapshotProvider(params WindowStructureSnapshot[] snapshots) =>
            _snapshots = snapshots;

        internal int CaptureCount => Volatile.Read(ref _captureCount);

        public WindowStructureSnapshot Capture(
            nint windowHandle,
            int? expectedProcessId = null,
            WindowStructureCaptureProfile profile = WindowStructureCaptureProfile.MultiApp)
        {
            var index = Interlocked.Increment(ref _captureCount) - 1;
            return _snapshots[Math.Min(index, _snapshots.Length - 1)];
        }
    }

    private sealed class SequencedWorkerProcess : IWindowSnapshotWorkerProcess
    {
        private readonly WindowSnapshotWorkerExecution[] _executions;
        private int _executionCount;

        internal SequencedWorkerProcess(params WindowSnapshotWorkerExecution[] executions) =>
            _executions = executions;

        internal int ExecutionCount => Volatile.Read(ref _executionCount);

        public WindowSnapshotWorkerExecution Execute(
            string executablePath,
            nint windowHandle,
            int expectedProcessId,
            WindowStructureCaptureProfile profile,
            TimeSpan timeout)
        {
            var index = Interlocked.Increment(ref _executionCount) - 1;
            return _executions[Math.Min(index, _executions.Length - 1)];
        }
    }

    private sealed class SequencedProcessIdentity : IWindowProcessIdentityResolver
    {
        private readonly int[] _processIds;
        private int _readCount;

        internal SequencedProcessIdentity(params int[] processIds) =>
            _processIds = processIds;

        public bool TryGetProcessId(nint windowHandle, out int processId)
        {
            var index = Interlocked.Increment(ref _readCount) - 1;
            processId = _processIds[Math.Min(index, _processIds.Length - 1)];
            return windowHandle != 0;
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }
}
