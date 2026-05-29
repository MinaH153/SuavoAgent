using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class DeviceCodePairingTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    private sealed class FakeService : IDeviceCodeService
    {
        private readonly Queue<DeviceCodePollResult> _polls;
        private readonly DeviceCodeCreateResult _create;
        public int CreateCalls { get; private set; }
        public int PollCalls { get; private set; }
        public Func<int, DeviceCodePollResult>? PollOverride { get; init; }

        public FakeService(DeviceCodeCreateResult create, params DeviceCodePollResult[] polls)
        {
            _create = create;
            _polls = new Queue<DeviceCodePollResult>(polls);
        }

        public Task<DeviceCodeCreateResult> CreateAsync(string fingerprint, string version, CancellationToken ct)
        {
            CreateCalls++;
            return Task.FromResult(_create);
        }

        public Task<DeviceCodePollResult> PollAsync(string deviceCode, CancellationToken ct)
        {
            PollCalls++;
            if (PollOverride != null) return Task.FromResult(PollOverride(PollCalls));
            return Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : new DeviceCodePollResult("pending"));
        }
    }

    private static DeviceCodeCreateResult Created(int expiresIn = 900, int poll = 5) =>
        new("ABCD-2345", "https://suavollc.com/admin/agents/pair?code=ABCD-2345", expiresIn, poll);

    [Fact]
    public async Task PendingThenAuthorized_ReturnsConfigFromDashboardCreds()
    {
        var svc = new FakeService(
            Created(),
            new DeviceCodePollResult("pending"),
            new DeviceCodePollResult("pending"),
            new DeviceCodePollResult("authorized", "sagent_live", "agent-uuid", "pharm-uuid", "Queen"));
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.NotNull(result.Config);
        Assert.Equal("pharm-uuid", result.Config!.PharmacyId);
        Assert.Equal("sagent_live", result.Config.ApiKey);
        Assert.Equal("agent-uuid", result.Config.AgentId);
        Assert.Equal("https://suavollc.com", result.Config.CloudUrl);
        Assert.Equal(3, svc.PollCalls);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("denied")]
    public async Task TerminalNonAuthorized_FailsWithThatReason(string status)
    {
        var svc = new FakeService(Created(), new DeviceCodePollResult(status));
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.False(result.Authorized);
        Assert.Equal(status, result.Reason);
        Assert.Null(result.Config);
    }

    [Fact]
    public async Task TransientHttpError_IsRetried_NotFatal()
    {
        var svc = new FakeService(Created())
        {
            PollOverride = call => call < 2
                ? throw new HttpRequestException("429 / network blip")
                : new DeviceCodePollResult("authorized", "sagent_x", "a", "p", "Queen"),
        };
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.True(svc.PollCalls >= 2);
    }

    [Fact]
    public async Task NeverApproved_ExpiresAtDeadline()
    {
        // 20s deadline, 5s poll -> ~4 polls then expire. Always pending.
        var svc = new FakeService(Created(expiresIn: 20, poll: 5));
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.False(result.Authorized);
        Assert.Equal("expired", result.Reason);
        Assert.Equal(4, svc.PollCalls);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var svc = new FakeService(Created());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pairing.RunAsync("fp", "3.15.0", null, cts.Token));
    }

    [Fact]
    public async Task ReportsProgress_WithCodeAndVerificationUrl()
    {
        var svc = new FakeService(Created(),
            new DeviceCodePollResult("authorized", "sagent_x", "a", "p", "Queen"));
        var reports = new List<PairingProgress>();
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        await pairing.RunAsync("fp", "3.15.0", new Progress<PairingProgress>(reports.Add), CancellationToken.None);

        // Progress is reported on a captured context; give it a tick to flush.
        await Task.Delay(20);
        Assert.Contains(reports, r => r.DeviceCode == "ABCD-2345" && r.VerificationUrl.Contains("ABCD-2345"));
    }
}
