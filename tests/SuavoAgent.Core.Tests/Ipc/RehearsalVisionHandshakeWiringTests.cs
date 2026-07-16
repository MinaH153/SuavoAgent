using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public sealed class RehearsalVisionHandshakeWiringTests
{
    [Fact]
    public void PioneerRx_rehearsal_loads_registry_identity_for_every_command_client()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "PioneerRxRehearsal",
            "Program.cs"));

        Assert.Contains("VisionConfigurationRegistry.Load", source, StringComparison.Ordinal);
        Assert.Contains("if (!visionState.IsValid)", source, StringComparison.Ordinal);
        Assert.Equal(2, Count(source, "new IpcCommandClient("));
        Assert.True(Count(source, "visionHandshake") >= 3);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SuavoAgent.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("SuavoAgent repository root not found.");
    }
}
