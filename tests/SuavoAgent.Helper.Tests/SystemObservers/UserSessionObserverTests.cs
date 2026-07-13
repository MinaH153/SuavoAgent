using Microsoft.Win32;
using SuavoAgent.Helper.SystemObservers;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class UserSessionObserverTests
{
    [Theory]
    [InlineData(SessionSwitchReason.SessionLogon, "logon")]
    [InlineData(SessionSwitchReason.SessionLogoff, "logoff")]
    [InlineData(SessionSwitchReason.SessionLock, "lock")]
    [InlineData(SessionSwitchReason.SessionUnlock, "unlock")]
    [InlineData(SessionSwitchReason.RemoteConnect, "rdp_connect")]
    [InlineData(SessionSwitchReason.RemoteDisconnect, "rdp_disconnect")]
    [InlineData(SessionSwitchReason.ConsoleConnect, "console_connect")]
    [InlineData(SessionSwitchReason.ConsoleDisconnect, "console_disconnect")]
    public void MapReason_CoversLockLogoutAndRdpTransitions(
        SessionSwitchReason reason,
        string expected)
    {
        Assert.Equal(expected, UserSessionObserver.MapReason(reason));
    }
}
