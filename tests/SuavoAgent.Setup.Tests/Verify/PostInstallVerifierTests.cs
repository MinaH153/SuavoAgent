// tests/SuavoAgent.Setup.Tests/Verify/PostInstallVerifierTests.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class PostInstallVerifierTests
{
    private static Func<CancellationToken, Task<GateResult>> Gate(string name, GateState s) =>
        _ => Task.FromResult(new GateResult(name, s, $"{name} {s}"));

    [Fact]
    public async Task Passes_when_no_gate_fails()
    {
        var v = new PostInstallVerifier(new[] { Gate("Services", GateState.Ok), Gate("Brain", GateState.Skip), Gate("Cloud", GateState.Warn) });
        var outcome = await v.RunAsync(CancellationToken.None);
        Assert.True(outcome.Passed);
        Assert.Equal(3, outcome.Gates.Count);
    }

    [Fact]
    public async Task Fails_and_summary_names_first_failing_gate()
    {
        var v = new PostInstallVerifier(new[] { Gate("Services", GateState.Ok), Gate("Brain", GateState.Fail) });
        var outcome = await v.RunAsync(CancellationToken.None);
        Assert.False(outcome.Passed);
        Assert.Contains("Brain", outcome.Summary);
    }

    [Fact]
    public void ToJson_includes_each_gate_and_passed_flag()
    {
        var outcome = new VerifyOutcome(false,
            new[] { new GateResult("Brain", GateState.Fail, "broken") }, "Brain: broken");
        var json = PostInstallVerifier.ToJson(outcome);
        Assert.Contains("\"passed\"", json);
        Assert.Contains("Brain", json);
    }
}
