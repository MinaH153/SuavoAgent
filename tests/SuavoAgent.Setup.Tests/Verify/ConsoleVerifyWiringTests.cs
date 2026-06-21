using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class ConsoleVerifyWiringTests
{
    [Fact]
    public async Task Default_verifier_composes_the_four_named_gates()
    {
        // The single production gate set shared by the GUI orchestrator AND the console installer.
        // We assert the gate NAMES (not their states — on a box without a running agent the gates
        // legitimately return Fail/Warn), which is what guarantees both paths verify the same things.
        var outcome = await VerifierFactory.BuildDefault().RunAsync(CancellationToken.None);
        var names = new HashSet<string>();
        foreach (var g in outcome.Gates) names.Add(g.Name);

        Assert.Contains("Services", names);
        Assert.Contains("Pipe", names);
        Assert.Contains("Brain", names);
        Assert.Contains("Cloud auth", names);
        Assert.Equal(4, outcome.Gates.Count);
    }
}
