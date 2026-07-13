using SuavoAgent.Setup;
using System.Text.Json;
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
        public int AbortCalls { get; private set; }
        public Func<int, DeviceCodePollResult>? PollOverride { get; init; }
        public Func<int, DeviceCodeCreateResult>? CreateOverride { get; init; }

        public FakeService(DeviceCodeCreateResult create, params DeviceCodePollResult[] polls)
        {
            _create = create;
            _polls = new Queue<DeviceCodePollResult>(polls);
        }

        public Task<DeviceCodeCreateResult> CreateAsync(string fingerprint, string version, CancellationToken ct)
        {
            CreateCalls++;
            if (CreateOverride != null) return Task.FromResult(CreateOverride(CreateCalls));
            return Task.FromResult(_create);
        }

        public Task<DeviceCodePollResult> PollAsync(
            string deviceCode, string deviceSecret, CancellationToken ct)
        {
            PollCalls++;
            if (PollOverride != null) return Task.FromResult(PollOverride(PollCalls));
            return Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : new DeviceCodePollResult("pending"));
        }

        public void AbortPendingKey(string fingerprint, string expectedKeyId)
        {
            Assert.Equal("fp", fingerprint);
            Assert.Equal("device-key-id", expectedKeyId);
            AbortCalls++;
        }
    }

    private static DeviceCodeCreateResult Created(int expiresIn = 900, int poll = 5) =>
        new(
            "ABCD-2345",
            "https://suavollc.com/pharmacy/agent/pair?code=ABCD-2345",
            expiresIn,
            poll,
            "agent-only-secret",
            "device-key-id",
            "device-key-name",
            new string('A', 43));

    private static DeviceCodePollResult Authorized(
        string apiKey = "sagent_x",
        string agentId = "a",
        string pharmacyId = "p")
    {
        var dto = new VerticalConfigDto(
            "pharmacy",
            "hipaa",
            "pioneerrx",
            "PioneerRx",
            "phi-v1",
            new VerticalFraming("SuavoAgent", "PioneerRx", "pharmacy", "NPI"),
            new VerticalCompliance(true, "hipaa-ba-v1"));
        return new DeviceCodePollResult(
            "authorized",
            apiKey,
            agentId,
            pharmacyId,
            "Queen",
            VerticalConfigRaw: JsonSerializer.Serialize(dto),
            VerticalConfig: dto,
            VerticalConfigSignature: "signed-envelope",
            VerticalConfigKeyId: "vertical-v1");
    }

    [Fact]
    public async Task PendingThenAuthorized_ReturnsConfigFromDashboardCreds()
    {
        var svc = new FakeService(
            Created(),
            new DeviceCodePollResult("pending"),
            new DeviceCodePollResult("pending"),
            Authorized("sagent_live", "agent-uuid", "pharm-uuid"));
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.NotNull(result.Config);
        Assert.Equal("pharm-uuid", result.Config!.PharmacyId);
        Assert.Equal("sagent_live", result.Config.ApiKey);
        Assert.Equal("agent-uuid", result.Config.AgentId);
        Assert.Equal("https://suavollc.com", result.Config.CloudUrl);
        Assert.True(result.Config.LearningMode);
        Assert.Equal("device-key-id", result.Config.DeviceKeyId);
        Assert.Equal("device-key-name", result.Config.DeviceKeyName);
        Assert.Equal(new string('A', 43), result.Config.DeviceChallenge);
        Assert.Equal(3, svc.PollCalls);
        Assert.Equal(0, svc.AbortCalls);
    }

    [Fact]
    public async Task Authorized_without_signed_profile_is_rejected_and_pending_key_is_aborted()
    {
        var svc = new FakeService(
            Created(),
            new DeviceCodePollResult(
                "authorized",
                "sagent_live",
                "agent-uuid",
                "pharm-uuid",
                "Queen"));
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.False(result.Authorized);
        Assert.Equal("signed_profile_unavailable", result.Reason);
        Assert.Null(result.Config);
        Assert.Equal(1, svc.AbortCalls);
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
        Assert.Equal(1, svc.AbortCalls);
    }

    [Fact]
    public async Task TransientHttpError_IsRetried_NotFatal()
    {
        var svc = new FakeService(Created())
        {
            PollOverride = call => call < 2
                ? throw new HttpRequestException("429 / network blip")
                : Authorized(),
        };
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.True(svc.PollCalls >= 2);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("malformed-json")]
    [InlineData("malformed-shape")]
    public async Task TransientTimeoutOrMalformedPollIsRetried(string failure)
    {
        var svc = new FakeService(Created())
        {
            PollOverride = call => call == 1
                ? failure switch
                {
                    "timeout" => throw new TaskCanceledException("HTTP timeout"),
                    "malformed-json" => throw new JsonException("gateway HTML"),
                    _ => throw new InvalidOperationException("missing status"),
                }
                : Authorized("sagent_x", "agent", "pharmacy"),
        };
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.Equal(2, svc.PollCalls);
    }

    [Fact]
    public async Task ConsecutiveTransientFailuresStopAtBoundedRetryBudget()
    {
        var svc = new FakeService(Created(expiresIn: 900, poll: 1))
        {
            PollOverride = _ => throw new TaskCanceledException("HTTP timeout"),
        };
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.False(result.Authorized);
        Assert.Equal("transient_retry_exhausted", result.Reason);
        Assert.Equal(5, svc.PollCalls);
        Assert.Equal(1, svc.AbortCalls);
    }

    [Fact]
    public async Task TransientCodeCreationResponseIsRetriedWithSamePendingKeyFlow()
    {
        var svc = new FakeService(
            Created(),
            Authorized("sagent_x", "agent", "pharmacy"))
        {
            CreateOverride = call => call == 1
                ? throw new DeviceCodeTransientException(
                    "bad gateway response",
                    new JsonException("HTML"))
                : Created(),
        };
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        var result = await pairing.RunAsync("fp", "3.15.0", null, CancellationToken.None);

        Assert.True(result.Authorized);
        Assert.Equal(2, svc.CreateCalls);
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
        Assert.Equal(1, svc.AbortCalls);
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
        Assert.Equal(0, svc.AbortCalls);
    }

    [Fact]
    public async Task ReportsProgress_WithCodeAndVerificationUrl()
    {
        var svc = new FakeService(Created(),
            Authorized());
        var reports = new List<PairingProgress>();
        var pairing = new DeviceCodePairing(svc, "https://suavollc.com", NoDelay);

        await pairing.RunAsync("fp", "3.15.0", new Progress<PairingProgress>(reports.Add), CancellationToken.None);

        // Progress is reported on a captured context; give it a tick to flush.
        await Task.Delay(20);
        Assert.Contains(reports, r => r.DeviceCode == "ABCD-2345" && r.VerificationUrl.Contains("ABCD-2345"));
    }
}
