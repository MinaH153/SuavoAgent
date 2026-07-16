using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public sealed class ApprovedPmsForegroundWindowCaptureTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void LiveBootstrapBuildsOnlyApprovedWindowCapture()
    {
        var capture = VisionBootstrap.BuildLivePmsCapture(Options(), Log);

        Assert.IsType<ApprovedPmsForegroundWindowCapture>(capture);
        Assert.DoesNotContain(
            typeof(VisionBootstrap).Assembly.GetTypes(),
            type => string.Equals(type.Name, "GdiScreenCapture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_ResolveFailure_NeverConstructsPixelCapture()
    {
        var authority = new FakeAuthority { ResolveAllowed = false };
        var factoryCalled = false;
        var capture = NewCapture(authority, (_, _) =>
        {
            factoryCalled = true;
            return new FakeCapture((ScreenBytes?)null);
        });

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.False(factoryCalled);
        Assert.Equal(1, authority.ResolveCount);
        Assert.Equal(0, authority.RevalidateCount);
    }

    [Fact]
    public async Task Capture_RequiredWorkflowWindowMismatch_NeverConstructsPixelCapture()
    {
        var authority = new FakeAuthority();
        var factoryCalled = false;
        var capture = NewCapture(
            authority,
            (_, _) =>
            {
                factoryCalled = true;
                return new FakeCapture((ScreenBytes?)null);
            },
            requiredHwnd: new IntPtr(0x9999),
            requiredPid: authority.Target.ProcessId);

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.False(factoryCalled);
        Assert.Equal(0, authority.RevalidateCount);
    }

    [Fact]
    public async Task Capture_RevalidatesExactTargetBeforeAndAfterPrintWindow()
    {
        var authority = new FakeAuthority();
        ApprovedPmsWindowTarget observedTarget = default;
        var capture = NewCapture(authority, (target, authorize) =>
        {
            observedTarget = target;
            return new FakeCapture(() =>
            {
                if (!authorize(target.Hwnd, target.ProcessId)) return null;
                return Bytes(target.Hwnd);
            });
        });

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(authority.Target, observedTarget);
        Assert.Equal(authority.Target.Hwnd.ToInt64(), result.Value.Hwnd);
        Assert.Equal(2, authority.RevalidateCount);
    }

    [Fact]
    public async Task Capture_PrePrintWindowTrustChange_ReturnsNull()
    {
        var authority = new FakeAuthority(false);
        var capture = NewCapture(authority, (target, authorize) =>
            new FakeCapture(() => authorize(target.Hwnd, target.ProcessId)
                ? Bytes(target.Hwnd)
                : null));

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, authority.RevalidateCount);
    }

    [Fact]
    public async Task Capture_PostPrintWindowTrustChange_DiscardsPixels()
    {
        var authority = new FakeAuthority(true, false);
        var capture = NewCapture(authority, (target, authorize) =>
            new FakeCapture(() =>
            {
                Assert.True(authorize(target.Hwnd, target.ProcessId));
                return Bytes(target.Hwnd);
            }));

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(2, authority.RevalidateCount);
    }

    [Fact]
    public async Task Capture_WrongHwndFromPixelCapture_IsRejected()
    {
        var authority = new FakeAuthority(true);
        var capture = NewCapture(authority, (target, authorize) =>
            new FakeCapture(() =>
            {
                Assert.True(authorize(target.Hwnd, target.ProcessId));
                return Bytes(new IntPtr(target.Hwnd.ToInt64() + 1));
            }));

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, authority.RevalidateCount);
    }

    [Fact]
    public async Task Capture_ExceptionMessageAndObjectAreNotLogged()
    {
        const string sensitive = @"Jane Doe C:\Patients\rx-1234.txt";
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var capture = NewCapture(
            new FakeAuthority(),
            (_, _) => throw new InvalidOperationException(sensitive),
            logger);

        var result = await capture.CapturePrimaryAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.NotEmpty(sink.Events);
        Assert.All(sink.Events, entry => Assert.Null(entry.Exception));
        Assert.DoesNotContain(
            sensitive,
            string.Join('\n', sink.Events.Select(entry => entry.RenderMessage())),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Authority_StableExactWindowPidAndTrust_Allows()
    {
        var hwnd = new IntPtr(0x1234);
        var authority = NewAuthority(
            getForeground: () => hwnd,
            getPid: _ => 42,
            verify: _ => new PmsProcessTrustEvidence(true, "trust-a", "trusted"));

        Assert.True(authority.TryResolveForeground(out var target, out var resolveCode));
        Assert.Equal("approved", resolveCode);
        Assert.True(authority.Revalidate(target, out var revalidateCode));
        Assert.Equal("approved", revalidateCode);
    }

    [Fact]
    public void Authority_ForegroundHwndChanges_FailsClosed()
    {
        var hwnd = new IntPtr(0x1234);
        var authority = NewAuthority(
            getForeground: () => hwnd,
            getPid: _ => 42,
            verify: _ => new PmsProcessTrustEvidence(true, "trust-a", "trusted"));
        Assert.True(authority.TryResolveForeground(out var target, out _));

        hwnd = new IntPtr(0x9999);

        Assert.False(authority.Revalidate(target, out var code));
        Assert.Equal("foreground_window_changed", code);
    }

    [Fact]
    public void Authority_WindowOwnerPidChanges_FailsClosed()
    {
        var pid = 42;
        var authority = NewAuthority(
            getForeground: () => new IntPtr(0x1234),
            getPid: _ => pid,
            verify: _ => new PmsProcessTrustEvidence(true, "trust-a", "trusted"));
        Assert.True(authority.TryResolveForeground(out var target, out _));

        pid = 77;

        Assert.False(authority.Revalidate(target, out var code));
        Assert.Equal("foreground_process_changed", code);
    }

    [Fact]
    public void Authority_ProcessTrustBindingChanges_FailsClosed()
    {
        var binding = "trust-a";
        var authority = NewAuthority(
            getForeground: () => new IntPtr(0x1234),
            getPid: _ => 42,
            verify: _ => new PmsProcessTrustEvidence(true, binding, "trusted"));
        Assert.True(authority.TryResolveForeground(out var target, out _));

        binding = "trust-b";

        Assert.False(authority.Revalidate(target, out var code));
        Assert.Equal("pms_process_trust_changed", code);
    }

    [Fact]
    public void Authority_ProcessTrustRevoked_FailsClosed()
    {
        var trusted = true;
        var authority = NewAuthority(
            getForeground: () => new IntPtr(0x1234),
            getPid: _ => 42,
            verify: _ => trusted
                ? new PmsProcessTrustEvidence(true, "trust-a", "trusted")
                : PmsProcessTrustEvidence.Denied("approval_revoked"));
        Assert.True(authority.TryResolveForeground(out var target, out _));

        trusted = false;

        Assert.False(authority.Revalidate(target, out var code));
        Assert.Equal("approval_revoked", code);
    }

    [Fact]
    public void Authority_ProbeException_FailsClosedWithoutExceptionText()
    {
        var authority = NewAuthority(
            getForeground: () => throw new InvalidOperationException(@"Jane Doe C:\Patients\rx.txt"),
            getPid: _ => 42,
            verify: _ => new PmsProcessTrustEvidence(true, "trust-a", "trusted"));

        Assert.False(authority.TryResolveForeground(out _, out var code));
        Assert.Equal("pms_window_probe_failed", code);
    }

    [Theory]
    [InlineData(@"C:\Patients\Jane Doe", "pms_capture_authorization_failed")]
    [InlineData("approval_revoked", "approval_revoked")]
    [InlineData("", "pms_capture_authorization_failed")]
    public void FailureCodeSanitizer_NeverEmitsPathOrFreeText(string input, string expected)
    {
        Assert.Equal(expected, WindowsApprovedPmsWindowAuthority.SafeCode(input));
    }

    private static ApprovedPmsForegroundWindowCapture NewCapture(
        FakeAuthority authority,
        ApprovedPmsForegroundWindowCapture.WindowCaptureFactory factory,
        ILogger? logger = null,
        IntPtr requiredHwnd = default,
        int requiredPid = 0) => new(
            Options(), logger ?? Log, authority, factory, requiredHwnd, requiredPid);

    private static WindowsApprovedPmsWindowAuthority NewAuthority(
        Func<IntPtr> getForeground,
        Func<IntPtr, int> getPid,
        Func<int, PmsProcessTrustEvidence> verify) => new(
            isSupported: true,
            getForegroundWindow: getForeground,
            isWindowVisible: _ => true,
            getWindowProcessId: getPid,
            verifyProcess: verify);

    private static IOptions<AgentOptions> Options() => Microsoft.Extensions.Options.Options.Create(
        new AgentOptions
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                MinIntervalMs = 0,
            },
        });

    private static ScreenBytes Bytes(IntPtr hwnd) => new(
        [1, 2, 3],
        100,
        80,
        DateTimeOffset.UtcNow,
        hwnd.ToInt64());

    private sealed class FakeAuthority : IApprovedPmsWindowAuthority
    {
        private readonly Queue<bool> _revalidationResults;

        public FakeAuthority(params bool[] revalidationResults)
        {
            _revalidationResults = new Queue<bool>(revalidationResults);
        }

        public bool IsSupported => true;
        public bool ResolveAllowed { get; init; } = true;
        public ApprovedPmsWindowTarget Target { get; } = new(new IntPtr(0x1234), 42, "trust-a");
        public int ResolveCount { get; private set; }
        public int RevalidateCount { get; private set; }

        public bool TryResolveForeground(out ApprovedPmsWindowTarget target, out string code)
        {
            ResolveCount++;
            target = Target;
            code = ResolveAllowed ? "approved" : "foreground_window_missing";
            return ResolveAllowed;
        }

        public bool Revalidate(ApprovedPmsWindowTarget target, out string code)
        {
            RevalidateCount++;
            var allowed = _revalidationResults.Count == 0 || _revalidationResults.Dequeue();
            code = allowed ? "approved" : "pms_process_trust_changed";
            return allowed;
        }
    }

    private sealed class FakeCapture : IScreenCapture
    {
        private readonly Func<ScreenBytes?> _capture;

        public FakeCapture(ScreenBytes? screen) : this(() => screen) { }

        public FakeCapture(Func<ScreenBytes?> capture) => _capture = capture;

        public bool IsAvailable => true;

        public Task<ScreenBytes?> CapturePrimaryAsync(CancellationToken ct) =>
            Task.FromResult(_capture());
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
