using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class SetupConfigTests
{
    [Fact]
    public void Load_rejects_legacy_setup_without_cloud_agent_id()
    {
        var ex = Assert.Throws<Exception>(() => SetupConfig.Load(new[]
        {
            "--pharmacy-id", "PH-test",
            "--api-key", "sagent_test",
            "--cloud-url", "https://suavollc.com",
            "--release-tag", "v3.13.13",
        }));

        Assert.Contains("AgentId must be a cloud UUID", ex.Message);
        Assert.Contains("Legacy local setup.json installs are disabled", ex.Message);
    }

    [Fact]
    public void Load_accepts_dashboard_config_with_cloud_agent_id()
    {
        var config = SetupConfig.Load(new[]
        {
            "--pharmacy-id", "PH-test",
            "--api-key", "sagent_test",
            "--agent-id", "2a492d97-9b8c-4217-a5b1-142f8fa36602",
            "--cloud-url", "https://suavollc.com",
            "--release-tag", "v3.13.13",
        });

        Assert.NotNull(config);
        Assert.Equal("2a492d97-9b8c-4217-a5b1-142f8fa36602", config.AgentId);
    }
}
