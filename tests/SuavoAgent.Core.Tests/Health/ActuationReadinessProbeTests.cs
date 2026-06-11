using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

/// <summary>
/// The actuation-readiness probe is the strand detector: the live box (2026-06-11) reported
/// health_composite=healthy while the Helper's command pipe was half-open — connected but deaf
/// — because every legacy signal reads the EVENT pipe. These tests pin the probe's safety
/// properties: fail-closed on every unknown, never a false "ready", never a false alarm while
/// a live job legitimately holds the pipe, and a strand streak that only counts conclusive
/// strand-class verdicts (the self-heal trigger must never fire on busy skips or Core bugs).
/// </summary>
public class ActuationReadinessProbeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement PingPayload(uint helperSession, uint activeConsole) =>
        JsonSerializer.SerializeToElement(new HelperPingInfo(4321, helperSession, activeConsole,
            helperSession != 0 && helperSession == activeConsole));

    private static IpcResponse PingOk(uint helperSession, uint activeConsole) =>
        new("id", IpcStatus.Ok, IpcCommands.Ping, PingPayload(helperSession, activeConsole), null);

    private static (ActuationReadinessProbe Probe, ActuationReadinessTracker Tracker, ProbeFakeIpc Ipc) Build()
    {
        var ipc = new ProbeFakeIpc();
        var tracker = new ActuationReadinessTracker();
        var probe = new ActuationReadinessProbe(ipc, tracker, NullLogger<ActuationReadinessProbe>.Instance);
        return (probe, tracker, ipc);
    }

    [Fact]
    public async Task HealthyHelper_ReportsReady_WithSessionDiagnostics()
    {
        var (probe, tracker, ipc) = Build();
        ipc.NextResponse = _ => PingOk(helperSession: 2, activeConsole: 2);

        var s = await probe.ProbeOnceAsync(T0, default);

        Assert.True(s.Ready);
        Assert.True(s.CommandPipeResponsive);
        Assert.True(s.IsConsoleInteractive);
        Assert.Equal(2u, s.HelperSessionId);
        Assert.Equal(2u, s.ActiveConsoleSessionId);
        Assert.Equal(4321, s.HelperPid);
        Assert.Null(s.FailureCode);
        Assert.Equal(T0, s.LastConclusiveCheckAtUtc);
        Assert.Equal(0, s.ConsecutiveStrandFailures);
        Assert.Same(s, tracker.Current);
    }

    [Fact]
    public async Task StrandedPipe_ReportsNotReady_AndStreakAccumulatesAcrossProbes()
    {
        var (probe, _, ipc) = Build();
        ipc.NextResponse = _ => null; // the strand: connected, ping never answered

        var s1 = await probe.ProbeOnceAsync(T0, default);
        var s2 = await probe.ProbeOnceAsync(T0.AddMinutes(1), default);
        var s3 = await probe.ProbeOnceAsync(T0.AddMinutes(2), default);

        Assert.False(s3.Ready);
        Assert.False(s3.CommandPipeResponsive);
        Assert.Equal(HelperPreflightCodes.PingUnanswered, s3.FailureCode);
        Assert.Equal(1, s1.ConsecutiveStrandFailures);
        Assert.Equal(2, s2.ConsecutiveStrandFailures);
        Assert.Equal(3, s3.ConsecutiveStrandFailures);
        // Retry-once means each probe sends two pings when the first is unanswered.
        Assert.Equal(6, ipc.SendCount);
    }

    [Fact]
    public async Task TransientNullThenAnswer_RetryOnceRecovers_NoFalseAlarm()
    {
        // A Helper that restarted underneath a stale-connected client yields exactly one null
        // (the failed send tears the pipe down); the fresh connect+ping succeeds. The probe
        // must report READY, not strand.
        var (probe, _, ipc) = Build();
        var first = true;
        ipc.NextResponse = _ =>
        {
            if (first) { first = false; return null; }
            return PingOk(1, 1);
        };

        var s = await probe.ProbeOnceAsync(T0, default);

        Assert.True(s.Ready);
        Assert.Equal(0, s.ConsecutiveStrandFailures);
        Assert.Equal(2, ipc.SendCount);
    }

    [Fact]
    public async Task BusyPipe_Skips_CarriesPriorVerdict_AndNeverBumpsStreak()
    {
        var (probe, _, ipc) = Build();
        ipc.NextResponse = _ => PingOk(1, 1);
        var conclusive = await probe.ProbeOnceAsync(T0, default);

        ipc.IsBusyValue = true; // a pricing lookup holds the client
        var skipped = await probe.ProbeOnceAsync(T0.AddMinutes(1), default);

        Assert.True(skipped.Ready); // prior verdict carried — a busy pipe is not a strand
        Assert.Equal("pipe_busy", skipped.SkippedReason);
        Assert.Equal(conclusive.LastConclusiveCheckAtUtc, skipped.LastConclusiveCheckAtUtc); // verdict not refreshed
        Assert.Equal(T0.AddMinutes(1), skipped.LastProbeAttemptAtUtc);
        Assert.Equal(0, skipped.ConsecutiveStrandFailures);
        Assert.Equal(1, ipc.SendCount); // no ping was sent while busy
    }

    [Fact]
    public async Task BusyPipe_WithNoPriorVerdict_ReportsNotReadyUnknown()
    {
        var (probe, _, ipc) = Build();
        ipc.IsBusyValue = true;

        var s = await probe.ProbeOnceAsync(T0, default);

        Assert.False(s.Ready); // fail-closed: no conclusive verdict yet
        Assert.Equal("pipe_busy", s.SkippedReason);
        Assert.Null(s.LastConclusiveCheckAtUtc);
    }

    [Fact]
    public async Task WrongSessionHelper_PipeResponsive_ButNotReady_AndStreakResets()
    {
        var (probe, _, ipc) = Build();

        // Build a strand streak first…
        ipc.NextResponse = _ => null;
        await probe.ProbeOnceAsync(T0, default);
        await probe.ProbeOnceAsync(T0.AddMinutes(1), default);

        // …then the Helper answers, but from a non-active session (post-CRD-switch survivor).
        ipc.NextResponse = _ => PingOk(helperSession: 3, activeConsole: 1);
        var s = await probe.ProbeOnceAsync(T0.AddMinutes(2), default);

        Assert.False(s.Ready);
        Assert.True(s.CommandPipeResponsive);   // the pipe WORKS — that's the truth
        Assert.False(s.IsConsoleInteractive);
        Assert.Equal(HelperPreflightCodes.NotInteractive, s.FailureCode);
        Assert.Equal(3u, s.HelperSessionId);
        Assert.Equal(1u, s.ActiveConsoleSessionId);
        // Different failure class, different owner (Broker session kill) — strand streak resets.
        Assert.Equal(0, s.ConsecutiveStrandFailures);
    }

    [Fact]
    public async Task UnreachablePipe_NotReady_ButNotStrandClass()
    {
        var (probe, _, ipc) = Build();
        ipc.NextResponse = _ => null;
        await probe.ProbeOnceAsync(T0, default); // streak 1

        ipc.IsConnectedValue = false;
        ipc.ConnectShouldSucceed = false; // Helper fully down — the Broker's relaunch loop owns this
        var s = await probe.ProbeOnceAsync(T0.AddMinutes(1), default);

        Assert.False(s.Ready);
        Assert.Equal(HelperPreflightCodes.PipeUnreachable, s.FailureCode);
        Assert.Equal(0, s.ConsecutiveStrandFailures); // never self-heal toward a dead-Helper state
    }

    [Fact]
    public async Task SendThrows_FailsClosed_WithoutCountingTowardSelfHeal()
    {
        var (probe, _, ipc) = Build();
        ipc.ThrowOnSend = new InvalidOperationException("boom");

        var s = await probe.ProbeOnceAsync(T0, default);

        Assert.False(s.Ready);
        Assert.Equal(ActuationReadinessProbe.CodeProbeError, s.FailureCode);
        Assert.Equal(0, s.ConsecutiveStrandFailures); // a Core bug must never thrash the Helper
    }

    [Fact]
    public async Task NullIpcClient_FailsClosed()
    {
        var tracker = new ActuationReadinessTracker();
        var probe = new ActuationReadinessProbe(null, tracker, NullLogger<ActuationReadinessProbe>.Instance);

        var s = await probe.ProbeOnceAsync(T0, default);

        Assert.False(s.Ready);
        Assert.Equal(HelperPreflightCodes.IpcNotConfigured, s.FailureCode);
    }

    [Fact]
    public async Task GenuineCancellation_Propagates()
    {
        var (probe, _, ipc) = Build();
        using var cts = new CancellationTokenSource();
        ipc.NextResponseAsync = async _ =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, cts.Token);
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.ProbeOnceAsync(T0, cts.Token));
    }

    private sealed class ProbeFakeIpc : IIpcCommandClient
    {
        public bool IsConnectedValue { get; set; } = true;
        public bool ConnectShouldSucceed { get; set; } = true;
        public bool IsBusyValue { get; set; }
        public Exception? ThrowOnSend { get; set; }
        public Func<IpcRequest, IpcResponse?> NextResponse { get; set; } = _ => null;
        public Func<IpcRequest, Task<IpcResponse?>>? NextResponseAsync { get; set; }
        public int SendCount { get; private set; }

        public bool IsConnected => IsConnectedValue;
        public bool IsBusy => IsBusyValue;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (ConnectShouldSucceed) IsConnectedValue = true;
            return Task.FromResult(ConnectShouldSucceed);
        }

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            SendCount++;
            if (ThrowOnSend is not null) throw ThrowOnSend;
            if (NextResponseAsync is not null) return NextResponseAsync(request);
            return Task.FromResult(NextResponse(request));
        }
    }
}
