using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Covers the GUI wrapper around DeviceCodePairing: it must surface the code,
/// hand a config back on approval, and fall into a retryable failed state on
/// expiry/denial — without real network or waits.
/// </summary>
public sealed class DeviceCodePairingViewModelTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay =
        (_, _) => Task.CompletedTask;

    private sealed class FakeService : IDeviceCodeService
    {
        private readonly Queue<DeviceCodePollResult> _polls;
        private readonly DeviceCodeCreateResult _create;

        public FakeService(DeviceCodeCreateResult create, params DeviceCodePollResult[] polls)
        {
            _create = create;
            _polls = new Queue<DeviceCodePollResult>(polls);
        }

        public Task<DeviceCodeCreateResult> CreateAsync(string fingerprint, string version, CancellationToken ct)
            => Task.FromResult(_create);

        public Task<DeviceCodePollResult> PollAsync(string deviceCode, CancellationToken ct)
            => Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : new DeviceCodePollResult("pending"));
    }

    private static DeviceCodeCreateResult Created() =>
        new("ABCD-2345", "https://suavollc.com/pharmacy/agent/pair?code=ABCD-2345", 900, 5);

    private static DeviceCodePairingViewModel NewVm(
        IDeviceCodeService service,
        Action<SetupConfig>? onPaired = null,
        Action? onCancelled = null,
        Action<string>? openUrl = null) =>
        new(
            service,
            "https://suavollc.com",
            fingerprint: "fp-123",
            version: "3.15.0",
            onPaired: onPaired ?? (_ => { }),
            onCancelled: onCancelled ?? (() => { }),
            delay: NoDelay,
            openUrl: openUrl);

    [Fact]
    public async Task Authorized_surfaces_code_and_returns_config()
    {
        SetupConfig? paired = null;
        var svc = new FakeService(
            Created(),
            new DeviceCodePollResult("pending"),
            new DeviceCodePollResult("authorized", "sagent_live", "agent-uuid", "pharm-uuid", "Better Life"));
        var vm = NewVm(svc, onPaired: c => paired = c);

        await vm.StartAsync();

        Assert.Equal("ABCD-2345", vm.DeviceCode);
        Assert.False(vm.IsFailed);
        Assert.NotNull(paired);
        Assert.Equal("pharm-uuid", paired!.PharmacyId);
        Assert.Equal("sagent_live", paired.ApiKey);
    }

    [Fact]
    public async Task Expired_enters_failed_state_and_allows_retry()
    {
        var paired = false;
        var svc = new FakeService(
            new DeviceCodeCreateResult("ABCD-2345", "https://suavollc.com/x", 0, 5),
            new DeviceCodePollResult("expired"));
        var vm = NewVm(svc, onPaired: _ => paired = true);

        await vm.StartAsync();

        Assert.True(vm.IsFailed);
        Assert.False(vm.ShowCode);
        Assert.False(paired);
        Assert.Contains("expired", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.RetryCommand.CanExecute(null));
    }

    [Fact]
    public async Task Denied_enters_failed_state_without_pairing()
    {
        var paired = false;
        var svc = new FakeService(Created(), new DeviceCodePollResult("denied"));
        var vm = NewVm(svc, onPaired: _ => paired = true);

        await vm.StartAsync();

        Assert.True(vm.IsFailed);
        Assert.False(paired);
    }

    [Fact]
    public async Task OpenBrowser_opens_the_verification_url()
    {
        string? opened = null;
        var svc = new FakeService(
            Created(),
            new DeviceCodePollResult("authorized", "k", "a", "p", "Q"));
        var vm = NewVm(svc, openUrl: u => opened = u);

        await vm.StartAsync();
        vm.OpenBrowserCommand.Execute(null);

        Assert.Equal("https://suavollc.com/pharmacy/agent/pair?code=ABCD-2345", opened);
    }

    [Fact]
    public void Cancel_invokes_the_cancel_callback()
    {
        var cancelled = false;
        var svc = new FakeService(Created(), new DeviceCodePollResult("pending"));
        var vm = NewVm(svc, onCancelled: () => cancelled = true);

        vm.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
