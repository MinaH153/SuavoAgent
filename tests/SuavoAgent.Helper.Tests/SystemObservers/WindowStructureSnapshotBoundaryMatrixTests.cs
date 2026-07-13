using System.Text.Json;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class WindowStructureSnapshotBoundaryMatrixTests
{
    private const long Hwnd = 42;
    private const int Pid = 777;
    private const string Hash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ProviderRejectsUnsupportedPlatformInvalidWindowAndMissingExecutable()
    {
        var worker = new StubWorker(Success());

        if (!OperatingSystem.IsWindows())
        {
            var unsupported = new IsolatedWindowStructureSnapshotProvider(
                worker,
                "helper.exe",
                TimeSpan.FromSeconds(1),
                requireWindows: true).Capture((nint)Hwnd, Pid);
            Assert.Equal("unsupported_platform", unsupported.FailureCode);
        }
        var invalidWindow = Provider(worker).Capture(nint.Zero, Pid);
        var missingExecutable = new IsolatedWindowStructureSnapshotProvider(
            worker,
            " ",
            TimeSpan.FromSeconds(1),
            requireWindows: false).Capture((nint)Hwnd, Pid);

        Assert.Equal("invalid_window", invalidWindow.FailureCode);
        Assert.Equal("worker_executable_unavailable", missingExecutable.FailureCode);
        Assert.Equal(0, worker.ExecutionCount);
    }

    [Fact]
    public void ProviderBindsWindowToProcessBeforeStartingWorker()
    {
        var worker = new StubWorker(Success());
        var unavailable = Provider(worker, new StubIdentity(false, 0))
            .Capture((nint)Hwnd);
        var mismatch = Provider(worker, new StubIdentity(true, Pid + 1))
            .Capture((nint)Hwnd, Pid);

        Assert.Equal("window_process_unavailable", unavailable.FailureCode);
        Assert.Equal("window_process_mismatch", mismatch.FailureCode);
        Assert.Equal(0, worker.ExecutionCount);
    }

    [Theory]
    [MemberData(nameof(ExecutionFailures))]
    public void WorkerExecutionFailureHasClosedReason(
        object executionValue,
        string expectedCode)
    {
        var execution = Assert.IsType<WindowSnapshotWorkerExecution>(executionValue);
        var result = Provider(new StubWorker(execution)).Capture((nint)Hwnd, Pid);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Null(result.TreeHash);
        Assert.Equal(0, result.ElementCount);
    }

    public static IEnumerable<object[]> ExecutionFailures()
    {
        yield return
        [
            new WindowSnapshotWorkerExecution(true, true, true, -1, null, "provider_timeout"),
            "provider_timeout",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(true, true, false, -1, null, "provider_timeout"),
            "provider_timeout_unterminated",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(false, false, true, -1, null, null),
            "worker_start_failed",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(false, false, true, -1, null, "launch_denied"),
            "launch_denied",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(true, false, true, 9, null, null),
            "worker_failed",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(true, false, true, 0, null, "worker_channel_failed"),
            "worker_channel_failed",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(true, false, true, 0, " ", null),
            "worker_empty_response",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(true, false, true, 0, "not-json", null),
            "worker_invalid_response",
        ];
        yield return
        [
            new WindowSnapshotWorkerExecution(true, false, true, 0, "null", null),
            "worker_invalid_response",
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void InvalidWorkerSnapshotIsRejected(
        object snapshotValue,
        int profileValue,
        string expectedCode)
    {
        var snapshot = Assert.IsType<WindowStructureSnapshot>(snapshotValue);
        var profile = (WindowStructureCaptureProfile)profileValue;
        var result = Provider(new StubWorker(Output(snapshot)))
            .Capture((nint)Hwnd, Pid, profile);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.FailureCode);
    }

    public static IEnumerable<object[]> InvalidSnapshots()
    {
        yield return
        [
            new WindowStructureSnapshot(false, null, 0, false, "Provider Failed / Raw"),
            (int)WindowStructureCaptureProfile.MultiApp,
            "provider_failed___raw",
        ];
        yield return
        [
            new WindowStructureSnapshot(false, null, 0, false, null),
            (int)WindowStructureCaptureProfile.MultiApp,
            "capture_failed",
        ];
        yield return
        [
            Valid() with { TreeHash = null },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { TreeHash = "abcd" },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { TreeHash = new string('z', 64) },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { ElementCount = 0 },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { ElementCount = 513 },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { ElementCount = 5001 },
            (int)WindowStructureCaptureProfile.Pms,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { WindowHandle = Hwnd + 1 },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
        yield return
        [
            Valid() with { ProcessId = Pid + 1 },
            (int)WindowStructureCaptureProfile.MultiApp,
            "worker_invalid_snapshot",
        ];
    }

    [Fact]
    public void ValidSnapshotUsesResolvedPidAndRechecksIdentityAfterWorker()
    {
        var worker = new StubWorker(Success());
        var identity = new SequencedIdentity((true, Pid), (true, Pid));
        var result = Provider(worker, identity).Capture((nint)Hwnd);

        Assert.True(result.Success);
        Assert.Null(result.FailureCode);
        Assert.Equal(Pid, worker.LastExpectedProcessId);
        Assert.Equal(2, identity.Calls);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, Pid + 1)]
    public void FinalWindowIdentityChangeRejectsOtherwiseValidSnapshot(
        bool finalAvailable,
        int finalPid)
    {
        var identity = new SequencedIdentity(
            (true, Pid),
            (finalAvailable, finalPid));

        var result = Provider(new StubWorker(Success()), identity)
            .Capture((nint)Hwnd, Pid);

        Assert.False(result.Success);
        Assert.Equal("window_process_changed", result.FailureCode);
    }

    [Fact]
    public void ProfileLimitsAreExplicitAndPmsIsNotSilentlyTruncatedToMultiApp()
    {
        Assert.Equal(
            512,
            FlaUiWindowStructureSnapshotProvider.MaximumElements(
                WindowStructureCaptureProfile.MultiApp));
        Assert.Equal(
            5000,
            FlaUiWindowStructureSnapshotProvider.MaximumElements(
                WindowStructureCaptureProfile.Pms));
    }

    [Fact]
    public void WorkerModeRejectsEveryMalformedProtocolAxis()
    {
        Assert.False(UiaSnapshotWorkerMode.TryRun(
            Array.Empty<string>(),
            TextWriter.Null,
            out var nonCandidateExit));
        Assert.Equal(0, nonCandidateExit);

        var invalidExpected = RunWorker(
            UiaSnapshotWorkerMode.Switch,
            "--window-handle", "42",
            "--expected-process-id", "0",
            "--capture-profile", "multi-app");
        var missingExpected = RunWorker(
            UiaSnapshotWorkerMode.Switch,
            "--window-handle", "42",
            "--capture-profile", "multi-app");
        var invalidProfile = RunWorker(
            UiaSnapshotWorkerMode.Switch,
            "--window-handle", "42",
            "--expected-process-id", "777",
            "--capture-profile", "wide-open");

        Assert.Equal("invalid_expected_process", invalidExpected.Snapshot.FailureCode);
        Assert.Equal(2, invalidExpected.ExitCode);
        Assert.Equal("invalid_expected_process", missingExpected.Snapshot.FailureCode);
        Assert.Equal("invalid_capture_profile", invalidProfile.Snapshot.FailureCode);
        Assert.Equal(2, invalidProfile.ExitCode);
    }

    [Theory]
    [InlineData("multi-app")]
    [InlineData("pms")]
    public void WorkerModeAcceptsOnlyClosedProfilesThenFailsPlatformSafely(string profile)
    {
        if (OperatingSystem.IsWindows())
            return;

        var result = RunWorker(
            UiaSnapshotWorkerMode.Switch,
            "--window-handle", "42",
            "--expected-process-id", "777",
            "--capture-profile", profile);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Snapshot.Success);
        Assert.Equal("unsupported_platform", result.Snapshot.FailureCode);
    }

    [Fact]
    public void Win32IdentityResolverFailsClosedOffWindowsAndOnZeroHandle()
    {
        var resolver = new Win32WindowProcessIdentityResolver();

        Assert.False(resolver.TryGetProcessId(nint.Zero, out var zeroPid));
        Assert.Equal(0, zeroPid);
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(resolver.TryGetProcessId((nint)Hwnd, out var nonWindowsPid));
            Assert.Equal(0, nonWindowsPid);
        }
    }

    [Fact]
    public void SystemWorkerStartsBoundedExecutableAndCapturesItsOutput()
    {
        if (!File.Exists("/usr/bin/true"))
            return;
        var worker = new SystemWindowSnapshotWorkerProcess();

        var result = worker.Execute(
            "/usr/bin/true",
            (nint)Hwnd,
            Pid,
            WindowStructureCaptureProfile.MultiApp,
            TimeSpan.FromSeconds(2));

        Assert.True(result.Started);
        Assert.False(result.TimedOut);
        Assert.True(result.Terminated);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    private static IsolatedWindowStructureSnapshotProvider Provider(
        IWindowSnapshotWorkerProcess worker,
        IWindowProcessIdentityResolver? identity = null) => new(
            worker,
            "signed-helper.exe",
            TimeSpan.FromSeconds(1),
            requireWindows: false,
            processIdentity: identity);

    private static WindowSnapshotWorkerExecution Success() => Output(Valid());

    private static WindowSnapshotWorkerExecution Output(WindowStructureSnapshot snapshot) =>
        new(true, false, true, 0, JsonSerializer.Serialize(snapshot), null);

    private static WindowStructureSnapshot Valid() => new(
        true,
        Hash,
        3,
        false,
        null,
        Hwnd,
        Pid);

    private static (WindowStructureSnapshot Snapshot, int ExitCode) RunWorker(
        params string[] args)
    {
        using var output = new StringWriter();
        Assert.True(UiaSnapshotWorkerMode.TryRun(args, output, out var exitCode));
        var snapshot = JsonSerializer.Deserialize<WindowStructureSnapshot>(
            output.ToString());
        return (Assert.IsType<WindowStructureSnapshot>(snapshot), exitCode);
    }

    private sealed class StubWorker(WindowSnapshotWorkerExecution execution)
        : IWindowSnapshotWorkerProcess
    {
        public int ExecutionCount { get; private set; }
        public int LastExpectedProcessId { get; private set; }

        public WindowSnapshotWorkerExecution Execute(
            string executablePath,
            nint windowHandle,
            int expectedProcessId,
            WindowStructureCaptureProfile profile,
            TimeSpan timeout)
        {
            ExecutionCount++;
            LastExpectedProcessId = expectedProcessId;
            return execution;
        }
    }

    private sealed class StubIdentity(bool available, int processId)
        : IWindowProcessIdentityResolver
    {
        public bool TryGetProcessId(nint windowHandle, out int resolved)
        {
            resolved = processId;
            return available;
        }
    }

    private sealed class SequencedIdentity(params (bool Available, int Pid)[] results)
        : IWindowProcessIdentityResolver
    {
        private readonly Queue<(bool Available, int Pid)> _results = new(results);
        public int Calls { get; private set; }

        public bool TryGetProcessId(nint windowHandle, out int processId)
        {
            Calls++;
            var next = _results.Dequeue();
            processId = next.Pid;
            return next.Available;
        }
    }
}
