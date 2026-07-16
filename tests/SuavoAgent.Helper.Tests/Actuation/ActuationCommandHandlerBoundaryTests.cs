using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// Exercises the command boundary before any Windows input primitive is
/// allowed to run. These are the fail-closed paths that protect a workstation
/// when Core sends missing, malformed, out-of-scope, or dry-run requests.
/// </summary>
public sealed class ActuationCommandHandlerBoundaryTests
{
    [Fact]
    public async Task UnknownCommand_IsRejectedWithoutTouchingDesktop()
    {
        using var fixture = new Fixture();

        var result = await fixture.Handler.HandleAsync(
            "actuation.unreviewed_command",
            data: null,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.MalformedRequest, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Theory]
    [InlineData(ActuationIpcCommands.ClickByLabel)]
    [InlineData(ActuationIpcCommands.ClickBySignature)]
    [InlineData(ActuationIpcCommands.TypeText)]
    [InlineData(ActuationIpcCommands.PressKeys)]
    [InlineData(ActuationIpcCommands.LaunchSandboxApp)]
    [InlineData(ActuationIpcCommands.AssertElement)]
    [InlineData(ActuationIpcCommands.DiscoverElements)]
    public async Task MissingData_IsRejectedWithoutDesktopAccess(string command)
    {
        using var fixture = new Fixture();

        var result = await fixture.Handler.HandleAsync(
            command,
            data: null,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.MalformedRequest, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task ReloadAllowlist_RemoteMutationIsAlwaysDenied()
    {
        using var fixture = new Fixture();

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.ReloadAllowlist,
            JsonSerializer.SerializeToElement(new ReloadAllowlistRequest()),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(
            ActuationRejectionCodes.RemotePolicyMutationDenied,
            result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Theory]
    [MemberData(nameof(ProtectedProcessRequests))]
    public async Task ProtectedOrUndeclaredProcess_IsRejectedBeforeResolution(
        string command,
        JsonElement data)
    {
        using var fixture = new Fixture();

        var result = await fixture.Handler.HandleAsync(
            command,
            data,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.ProcessNotAllowed, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    public static IEnumerable<object[]> ProtectedProcessRequests()
    {
        yield return
        [
            ActuationIpcCommands.ClickByLabel,
            JsonSerializer.SerializeToElement(new ClickByLabelRequest(
                "Save", "PioneerPharmacy.exe", "exact", 1, DryRun: true)),
        ];
        yield return
        [
            ActuationIpcCommands.ClickBySignature,
            JsonSerializer.SerializeToElement(new ClickBySignatureRequest(
                "Button", "SaveButton", null, "chrome.exe", 1, DryRun: true)),
        ];
        yield return
        [
            ActuationIpcCommands.TypeText,
            JsonSerializer.SerializeToElement(new TypeTextRequest(
                "safe", false, 0, DryRun: true, ProcessName: "notepad.exe")),
        ];
        yield return
        [
            ActuationIpcCommands.PressKeys,
            JsonSerializer.SerializeToElement(new PressKeysRequest(
                ["Enter"], 0, DryRun: true, ProcessName: "powershell.exe")),
        ];
        yield return
        [
            ActuationIpcCommands.AssertElement,
            JsonSerializer.SerializeToElement(new AssertElementRequest(
                "msedge.exe", "done", "exact", 1,
                AutomationId: "Result", DryRun: true)),
        ];
        yield return
        [
            ActuationIpcCommands.DiscoverElements,
            JsonSerializer.SerializeToElement(new DiscoverElementsRequest(
                "explorer.exe", Max: 1, DryRun: true)),
        ];
    }

    [Fact]
    public async Task ClosedGate_RejectsValidLabelClickBeforeUiaResolution()
    {
        using var fixture = new Fixture(enabled: false, dryRun: false);
        var data = JsonSerializer.SerializeToElement(new ClickByLabelRequest(
            "Equals", "calculator", "contains_ci", 1));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.ClickByLabel,
            data,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, result.RejectionCode);
        Assert.False(result.DryRun);
    }

    [Fact]
    public async Task ClosedGate_RejectsValidSignatureClickBeforeUiaResolution()
    {
        using var fixture = new Fixture(enabled: false, dryRun: false);
        var data = JsonSerializer.SerializeToElement(new ClickBySignatureRequest(
            "Button", "num7Button", null, "calc.exe", 1));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.ClickBySignature,
            data,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, result.RejectionCode);
        Assert.False(result.DryRun);
    }

    [Fact]
    public async Task DryRunTypeText_ReachesDriverButInjectsNoInput()
    {
        using var fixture = new Fixture(enabled: true, dryRun: false);
        var data = JsonSerializer.SerializeToElement(new TypeTextRequest(
            "safe arithmetic", false, 0, DryRun: true,
            ProcessName: "calculator"));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.TypeText,
            data,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Null(result.RejectionCode);
    }

    [Fact]
    public async Task DryRunPressKeys_ReachesDriverButInjectsNoInput()
    {
        using var fixture = new Fixture(enabled: true, dryRun: false);
        var data = JsonSerializer.SerializeToElement(new PressKeysRequest(
            ["Ctrl+A", "Backspace"], 0, DryRun: true,
            ProcessName: "calc.exe"));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.PressKeys,
            data,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Null(result.RejectionCode);
    }

    [Fact]
    public async Task DryRunLaunch_ReachesDriverButStartsNoProcess()
    {
        using var fixture = new Fixture(enabled: true, dryRun: false);
        var data = JsonSerializer.SerializeToElement(new LaunchSandboxAppRequest(
            ActuationAllowlistedSandboxApps.Calculator,
            DryRun: true));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.LaunchSandboxApp,
            data,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Null(result.RejectionCode);
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("contains_ci")]
    [InlineData("normalized")]
    public async Task DryRunAssertion_IsAProofNoOpWithoutReadingDesktop(
        string matchMode)
    {
        using var fixture = new Fixture(enabled: true, dryRun: false);
        var data = JsonSerializer.SerializeToElement(new AssertElementRequest(
            "calculator",
            "12",
            matchMode,
            1,
            AutomationId: "CalculatorResults",
            DryRun: true));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.AssertElement,
            data,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Equal("assert_element_dryrun", result.EvidenceHash);
    }

    [Theory]
    [InlineData(null, "expected", "Button")]
    [InlineData("", "expected", "Button")]
    [InlineData("calculator", "", "Button")]
    [InlineData("calculator", "expected", null)]
    public async Task AssertionRequiresProcessExpectedAndLocator(
        string? processName,
        string expected,
        string? controlType)
    {
        using var fixture = new Fixture();
        var data = JsonSerializer.SerializeToElement(new AssertElementRequest(
            processName!, expected, "normalized", 1,
            ControlType: controlType,
            DryRun: true));

        var result = await fixture.Handler.HandleAsync(
            ActuationIpcCommands.AssertElement,
            data,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.MalformedRequest, result.RejectionCode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ILogger _logger;
        private readonly UiaLabelResolver _resolver;
        private readonly UiaSignatureResolver _signatureResolver;

        public Fixture(bool enabled = true, bool dryRun = true)
        {
            _logger = new LoggerConfiguration().CreateLogger();
            var config = new ActuationConfig
            {
                Enabled = enabled,
                DryRun = dryRun,
                UserInputPauseWindow = TimeSpan.FromSeconds(60),
                DefaultUiaTimeout = TimeSpan.FromMilliseconds(1),
                DefaultPerKeyDelayMs = 0,
                DefaultInterChordDelayMs = 0,
            };
            var gate = new ActuationGate(config, _logger);
            _resolver = new UiaLabelResolver(_logger);
            _signatureResolver = new UiaSignatureResolver(_logger);
            var driver = new SendInputDriver(gate, config, _logger);
            Handler = new ActuationCommandHandler(
                gate,
                driver,
                _resolver,
                config,
                _logger,
                _signatureResolver);
        }

        public ActuationCommandHandler Handler { get; }

        public void Dispose()
        {
            _signatureResolver.Dispose();
            _resolver.Dispose();
            (_logger as IDisposable)?.Dispose();
        }
    }
}
