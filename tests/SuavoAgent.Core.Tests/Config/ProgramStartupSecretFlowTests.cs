using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public sealed class ProgramStartupSecretFlowTests
{
    [Fact]
    public void Program_unprotects_captured_agent_options_before_hmac_clients_are_registered()
    {
        var source = ReadProgramSource();
        var unprotectIndex = source.IndexOf(
            "agentOpts.ApiKey = SuavoAgent.Core.Config.CredentialProtector.Unprotect(agentOpts.ApiKey);",
            StringComparison.Ordinal);
        var cloudClientIndex = source.IndexOf("new SuavoCloudClient(agentOpts)", StringComparison.Ordinal);
        var configClientIndex = source.IndexOf("new AgentConfigClient(", StringComparison.Ordinal);

        Assert.NotEqual(-1, unprotectIndex);
        Assert.NotEqual(-1, cloudClientIndex);
        Assert.NotEqual(-1, configClientIndex);
        Assert.True(unprotectIndex < cloudClientIndex);
        Assert.True(unprotectIndex < configClientIndex);
    }

    private static string ReadProgramSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "SuavoAgent.Core", "Program.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate src/SuavoAgent.Core/Program.cs from test output directory.");
    }
}
