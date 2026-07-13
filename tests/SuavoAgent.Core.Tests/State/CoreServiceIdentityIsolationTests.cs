using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class CoreServiceIdentityIsolationTests
{
    [Fact]
    public void StartupGuardAcceptsOnlyExactServiceSidAmongTokenGroups()
    {
        Assert.True(CoreServiceIdentityGuard.ContainsRequiredServiceSid(
            ["S-1-5-19", CoreServiceIdentity.ServiceSid]));
        Assert.False(CoreServiceIdentityGuard.ContainsRequiredServiceSid(["S-1-5-19"]));
        Assert.False(CoreServiceIdentityGuard.ContainsRequiredServiceSid(["S-1-5-20"]));
        Assert.False(CoreServiceIdentityGuard.ContainsRequiredServiceSid(["S-1-5-18"]));
    }

    [Fact]
    public void ProtectedStateRejectsSharedServiceAccounts()
    {
        Assert.True(ProductionAclBoundary.IsAllowedSidValue("S-1-5-18"));
        Assert.True(ProductionAclBoundary.IsAllowedSidValue("S-1-5-32-544"));
        Assert.True(ProductionAclBoundary.IsAllowedSidValue(CoreServiceIdentity.ServiceSid));
        Assert.False(ProductionAclBoundary.IsAllowedSidValue("S-1-5-19"));
        Assert.False(ProductionAclBoundary.IsAllowedSidValue("S-1-5-20"));
        Assert.False(ProductionAclBoundary.IsAllowedSidValue("S-1-5-4"));
    }

    [Fact]
    public void ObservationPipeAllowsOnlySystemAndInteractiveClients()
    {
        Assert.Equal(["S-1-5-18", "S-1-5-4"], IpcPipeServer.ObservationPipeAllowedSidValues());
        Assert.DoesNotContain("S-1-5-19", IpcPipeServer.ObservationPipeAllowedSidValues());
        Assert.DoesNotContain("S-1-5-20", IpcPipeServer.ObservationPipeAllowedSidValues());
    }

    [Fact]
    public void ProgramDemandsExactServiceIdentityBeforeProtectedInitialization()
    {
        var source = ReadRepoFile("src/SuavoAgent.Core/Program.cs");
        var guard = source.IndexOf(
            "CoreServiceIdentityGuard.DemandCurrentProcessHasServiceSid();",
            StringComparison.Ordinal);
        var bootstrap = source.IndexOf("// Bootstrap self-update", StringComparison.Ordinal);
        var host = source.IndexOf("Host.CreateApplicationBuilder", StringComparison.Ordinal);

        Assert.True(guard >= 0);
        Assert.True(guard < bootstrap);
        Assert.True(guard < host);
        Assert.DoesNotContain("WellKnownSidType.LocalServiceSid", source, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not locate {relativePath}");
    }
}
