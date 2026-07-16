using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Companion;
using Xunit;

namespace SuavoAgent.Helper.Tests.Companion;

public sealed class CompanionStatusTextTests
{
    [Fact]
    public void EveryStateHasFixedPrivacySafeCopy()
    {
        foreach (var state in Enum.GetValues<CompanionState>())
        {
            foreach (var dryRun in new[] { false, true })
            {
                var title = CompanionStatusText.Title(state);
                var status = CompanionStatusText.Status(state, dryRun);

                Assert.False(string.IsNullOrWhiteSpace(title));
                Assert.False(string.IsNullOrWhiteSpace(status));
                Assert.False(PhiPatternGuard.ContainsPotentialPhi(title, out var titlePattern), titlePattern);
                Assert.False(PhiPatternGuard.ContainsPotentialPhi(status, out var statusPattern), statusPattern);
            }
        }
    }

    [Fact]
    public void StatusApiAcceptsNoRuntimeTextThatCouldContainPhi()
    {
        var parameters = typeof(CompanionStatusText)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .SelectMany(method => method.GetParameters())
            .ToArray();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(string));
    }
}
