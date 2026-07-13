using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public sealed class NativeInstallHealthMilestoneTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-native-health-milestone-" + Guid.NewGuid().ToString("N"));

    public NativeInstallHealthMilestoneTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NonPositiveTimeout_IsRejectedBeforeProbe()
    {
        var called = false;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            NativeInstallHealthMilestone.WaitAsync(
                _root,
                _root,
                TimeSpan.Zero,
                CancellationToken.None,
                () => { called = true; return Task.FromResult(Outcome()); }));

        Assert.False(called);
    }

    [Fact]
    public async Task DefinitiveNonTransientFailure_ReturnsImmediately()
    {
        var probes = 0;
        var expected = Outcome(
            new GateResult("Cloud auth", GateState.Fail, "rejected"));

        var result = await NativeInstallHealthMilestone.WaitAsync(
            Path.Combine(_root, "install"),
            Path.Combine(_root, "data"),
            TimeSpan.FromSeconds(30),
            CancellationToken.None,
            () =>
            {
                probes++;
                return Task.FromResult(expected);
            });

        Assert.Same(expected, result);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task PipeAndServiceFailures_RemainTransientUntilCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var transient = Outcome(
            new GateResult("Pipe", GateState.Fail, "starting"),
            new GateResult("Services", GateState.Fail, "starting"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeInstallHealthMilestone.WaitAsync(
                Path.Combine(_root, "install"),
                Path.Combine(_root, "data"),
                TimeSpan.FromSeconds(30),
                cancellation.Token,
                () => Task.FromResult(transient)));
    }

    [Fact]
    public async Task ExpiredWindow_AddsExplicitActivationFailure()
    {
        var result = await NativeInstallHealthMilestone.WaitAsync(
            Path.Combine(_root, "install"),
            Path.Combine(_root, "data"),
            TimeSpan.FromTicks(1),
            CancellationToken.None,
            () => Task.FromResult(Outcome()));

        Assert.False(result.Passed);
        var gate = Assert.Single(result.Gates);
        Assert.Equal("Activation", gate.Name);
        Assert.Equal(GateState.Fail, gate.State);
        Assert.Contains("timed out", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static VerifyOutcome Outcome(params GateResult[] gates) =>
        new(false, gates, "test outcome");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
