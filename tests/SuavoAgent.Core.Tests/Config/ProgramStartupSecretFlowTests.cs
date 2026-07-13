using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public sealed class ProgramStartupSecretFlowTests
{
    [Fact]
    public void Program_loads_protected_store_override_before_hmac_clients_are_registered()
    {
        var source = ReadProgramSource();
        var bootstrapIndex = source.IndexOf(
            "credentialBootstrap = CloudCredentialBootstrapper.LoadOrMigrate(",
            StringComparison.Ordinal);
        var cloudClientIndex = source.IndexOf("new SuavoCloudClient(agentOpts)", StringComparison.Ordinal);
        var configClientIndex = source.IndexOf("new AgentConfigClient(", StringComparison.Ordinal);

        Assert.NotEqual(-1, bootstrapIndex);
        Assert.NotEqual(-1, cloudClientIndex);
        Assert.NotEqual(-1, configClientIndex);
        Assert.True(bootstrapIndex < cloudClientIndex);
        Assert.True(bootstrapIndex < configClientIndex);
        Assert.DoesNotContain("SealSecretsFile(", source, StringComparison.Ordinal);
        Assert.Contains("cloud_credential_migrated", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Core_startup_verifies_but_cannot_mutate_its_privileged_acl_boundary()
    {
        var source = ReadProgramSource();

        Assert.Contains("InstalledDataRootVerifier.IsSafe(dataDir)", source, StringComparison.Ordinal);
        Assert.Contains("core.acl_boundary_verified", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetAccessControl", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectorySecurity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FileSystemAccessRule", source, StringComparison.Ordinal);
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
