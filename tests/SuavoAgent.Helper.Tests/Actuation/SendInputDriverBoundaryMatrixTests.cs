using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// Exercises input validation and target-binding failures before any Win32
/// injection can occur. Live gates are used only with a required target kind
/// and no established target, proving the driver refuses blind keystrokes.
/// </summary>
public sealed class SendInputDriverBoundaryMatrixTests
{
    [Fact]
    public void ConstructorRejectsMissingSafetyDependencies()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var config = Config(enabled: true, dryRun: true);
        var gate = new ActuationGate(config, logger);

        Assert.Throws<ArgumentNullException>(() =>
            new SendInputDriver(null!, config, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new SendInputDriver(gate, null!, logger));
        Assert.Throws<ArgumentNullException>(() =>
            new SendInputDriver(gate, config, null!));
    }

    [Fact]
    public async Task NullRequestsAreRejectedByArgumentContract()
    {
        var driver = Build(enabled: true, dryRun: true);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.TypeTextAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.PressKeysAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            driver.LaunchSandboxAppAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task NullTextIsMalformedBeforePhiOrGateChecks()
    {
        var driver = Build(enabled: true, dryRun: false);

        var result = await driver.TypeTextAsync(
            new TypeTextRequest(null!, false, 0),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.MalformedRequest, result.RejectionCode);
        Assert.False(result.DryRun);
    }

    [Theory]
    [InlineData("123-45-6789")]
    [InlineData("patient@example.com")]
    [InlineData("(858) 555-1212")]
    public async Task PotentialPhiIsRejectedBeforeGateAndNeverEchoed(string text)
    {
        var driver = Build(enabled: true, dryRun: false);

        var result = await driver.TypeTextAsync(
            new TypeTextRequest(text, false, 0),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.PhiPatternDetected, result.RejectionCode);
        Assert.DoesNotContain(text, result.RejectionReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedGateRejectsSafeTextWithEffectiveRequestDryRun()
    {
        var driver = Build(enabled: false, dryRun: false);

        var result = await driver.TypeTextAsync(
            new TypeTextRequest("safe", false, 0, DryRun: true),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task LiveTypeRequiresAnEstablishedTargetForEveryBoundScope()
    {
        var driver = Build(enabled: true, dryRun: false);

        var sandbox = await driver.TypeTextAsync(
            new TypeTextRequest(
                "safe", false, 0, ProcessName: "calculator"),
            CancellationToken.None,
            SendInputDriver.TargetTrustKind.Sandbox);
        var pms = await driver.TypeTextAsync(
            new TypeTextRequest(
                "safe", false, 0, ProcessName: "PioneerPharmacy"),
            CancellationToken.None,
            SendInputDriver.TargetTrustKind.PioneerRx);

        Assert.Equal(ActuationRejectionCodes.ForegroundNotTarget, sandbox.RejectionCode);
        Assert.Equal(ActuationRejectionCodes.ForegroundNotTarget, pms.RejectionCode);
        Assert.False(sandbox.DryRun);
        Assert.False(pms.DryRun);
    }

    [Theory]
    [MemberData(nameof(InvalidChordLists))]
    public async Task InvalidChordListsFailBeforeGate(
        IReadOnlyList<string>? chords,
        string expectedCode)
    {
        var driver = Build(enabled: true, dryRun: false);

        var result = await driver.PressKeysAsync(
            new PressKeysRequest(chords!, 0),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.RejectionCode);
        Assert.False(result.DryRun);
    }

    public static IEnumerable<object[]> InvalidChordLists()
    {
        yield return [null!, ActuationRejectionCodes.MalformedRequest];
        yield return [Array.Empty<string>(), ActuationRejectionCodes.MalformedRequest];
        yield return [new[] { "Ctrl+DefinitelyNotAKey" }, ActuationRejectionCodes.ChordParseFailure];
        yield return [new[] { "Enter", "" }, ActuationRejectionCodes.ChordParseFailure];
    }

    [Fact]
    public async Task ClosedGateRejectsValidChordsWithEffectiveRequestDryRun()
    {
        var driver = Build(enabled: false, dryRun: false);

        var result = await driver.PressKeysAsync(
            new PressKeysRequest(["Ctrl+A"], 0, DryRun: true),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task LivePressRequiresAnEstablishedTargetForEveryBoundScope()
    {
        var driver = Build(enabled: true, dryRun: false);

        var sandbox = await driver.PressKeysAsync(
            new PressKeysRequest(["Enter"], 0, ProcessName: "calculator"),
            CancellationToken.None,
            SendInputDriver.TargetTrustKind.Sandbox);
        var pms = await driver.PressKeysAsync(
            new PressKeysRequest(["Enter"], 0, ProcessName: "PioneerPharmacy"),
            CancellationToken.None,
            SendInputDriver.TargetTrustKind.PioneerRx);

        Assert.Equal(ActuationRejectionCodes.ForegroundNotTarget, sandbox.RejectionCode);
        Assert.Equal(ActuationRejectionCodes.ForegroundNotTarget, pms.RejectionCode);
    }

    [Fact]
    public async Task LiveClickRejectsUnprovenSandboxAndPmsIdentityBeforePointerInput()
    {
        var driver = Build(enabled: true, dryRun: false);

        var sandbox = await driver.ClickAtAsync(
            1,
            1,
            dryRun: false,
            CancellationToken.None,
            expectedPid: int.MaxValue,
            expectedProcess: "calculator",
            targetTrustKind: SendInputDriver.TargetTrustKind.Sandbox);
        var pms = await driver.ClickAtAsync(
            1,
            1,
            dryRun: false,
            CancellationToken.None,
            expectedPid: int.MaxValue,
            expectedProcess: "PioneerPharmacy",
            targetTrustKind: SendInputDriver.TargetTrustKind.PioneerRx);

        Assert.Equal(ActuationRejectionCodes.ProcessIdentityUntrusted, sandbox.RejectionCode);
        Assert.Equal(ActuationRejectionCodes.ProcessIdentityUntrusted, pms.RejectionCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task LaunchRequiresNonBlankAppKey(string appKey)
    {
        var driver = Build(enabled: true, dryRun: false);

        var result = await driver.LaunchSandboxAppAsync(
            new LaunchSandboxAppRequest(appKey),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.MalformedRequest, result.RejectionCode);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("powershell")]
    [InlineData("PioneerPharmacy")]
    public async Task LaunchRejectsEveryAppOutsideImmutableAllowlist(string appKey)
    {
        var driver = Build(enabled: true, dryRun: false);

        var result = await driver.LaunchSandboxAppAsync(
            new LaunchSandboxAppRequest(appKey),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.AppNotInAllowlist, result.RejectionCode);
    }

    [Fact]
    public async Task ClosedGateRejectsAllowlistedLaunchBeforePathResolution()
    {
        var driver = Build(enabled: false, dryRun: false);

        var result = await driver.LaunchSandboxAppAsync(
            new LaunchSandboxAppRequest("calculator", DryRun: true),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Theory]
    [InlineData("calculator", "calc.exe", true)]
    [InlineData("CALCULATOR", "CalculatorApp", true)]
    [InlineData("calc", "calculator", true)]
    [InlineData("calculator", "notepad", false)]
    [InlineData("", "calc", false)]
    public void TargetIdentityUsesPackagedAliasesAndExactStems(
        string expected,
        string established,
        bool matches)
    {
        Assert.Equal(
            matches,
            SendInputDriver.TargetIdentityMatches(expected, established));
    }

    [Fact]
    public void EvidenceHashIsStableBoundedAndVerbBound()
    {
        var first = SendInputDriver.ComputeEvidenceHash("type_text", "safe");
        var repeat = SendInputDriver.ComputeEvidenceHash("type_text", "safe");
        var otherVerb = SendInputDriver.ComputeEvidenceHash("press_keys", "safe");

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, otherVerb);
        Assert.Equal(16, first.Length);
        Assert.All(first, character => Assert.True(
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'));
    }

    private static SendInputDriver Build(bool enabled, bool dryRun)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var config = Config(enabled, dryRun);
        return new SendInputDriver(
            new ActuationGate(config, logger),
            config,
            logger);
    }

    private static ActuationConfig Config(bool enabled, bool dryRun) => new()
    {
        Enabled = enabled,
        DryRun = dryRun,
        DefaultPerKeyDelayMs = 0,
        DefaultInterChordDelayMs = 0,
        UserInputPauseWindow = TimeSpan.FromSeconds(60),
    };
}
