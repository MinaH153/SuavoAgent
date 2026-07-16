using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class CoreServiceIdentityTests
{
    [Fact]
    public void ExactWindowsServiceIdentityIsStableAcrossEveryProcess()
    {
        Assert.Equal("SuavoAgent.Core", CoreServiceIdentity.ServiceName);
        Assert.Equal("SuavoAgent.Core.exe", CoreServiceIdentity.ExecutableName);
        Assert.Equal(@"NT AUTHORITY\LocalService", CoreServiceIdentity.AccountName);
        Assert.Equal(
            "S-1-5-80-3161787503-2860973704-3751597344-303720228-1013404410",
            CoreServiceIdentity.ServiceSid);
    }
}
