using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public class InstallOrchestratorVerifyTests
{
    [Fact]
    public void Phase_enum_has_Verify_between_InstallServices_and_Done()
    {
        Assert.True((int)InstallOrchestrator.Phase.InstallServices < (int)InstallOrchestrator.Phase.Verify);
        Assert.True((int)InstallOrchestrator.Phase.Verify < (int)InstallOrchestrator.Phase.Done);
    }
}
