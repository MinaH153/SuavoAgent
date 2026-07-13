using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests;

public sealed class IpcServiceSidIsolationTests
{
    [Fact]
    public void CommandPipeAllowsOnlySystemAndExactCoreServiceSid()
    {
        Assert.Equal(
            ["S-1-5-18", CoreServiceIdentity.ServiceSid],
            IpcCommandServer.CommandPipeAllowedSidValues());
        Assert.DoesNotContain("S-1-5-19", IpcCommandServer.CommandPipeAllowedSidValues());
        Assert.DoesNotContain("S-1-5-20", IpcCommandServer.CommandPipeAllowedSidValues());
        Assert.DoesNotContain("S-1-5-4", IpcCommandServer.CommandPipeAllowedSidValues());
    }

    [Fact]
    public void TokenGroupMustBeExactAndEnabled()
    {
        Assert.True(IpcCommandServer.IsRequiredCoreServiceGroup(
            CoreServiceIdentity.ServiceSid,
            0x00000004));
        Assert.False(IpcCommandServer.IsRequiredCoreServiceGroup(
            CoreServiceIdentity.ServiceSid,
            0));
        Assert.False(IpcCommandServer.IsRequiredCoreServiceGroup("S-1-5-19", 0x00000004));
        Assert.False(IpcCommandServer.IsRequiredCoreServiceGroup("S-1-5-18", 0x00000004));
    }

    [Fact]
    public void BinaryIdentityRequiresExactCoreExecutableBesideHelper()
    {
        var root = Path.Combine(Path.GetTempPath(), "suavo-agent-ipc");
        var expected = Path.Combine(root, CoreServiceIdentity.ExecutableName);

        Assert.True(IpcCommandServer.IsExpectedCoreExecutablePath(expected, root));
        Assert.False(IpcCommandServer.IsExpectedCoreExecutablePath(
            Path.Combine(root, "SuavoAgent.Core-copy.exe"), root));
        Assert.False(IpcCommandServer.IsExpectedCoreExecutablePath(
            Path.Combine(Path.GetTempPath(), "other", CoreServiceIdentity.ExecutableName), root));
    }
}
