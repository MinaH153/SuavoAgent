using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class PricingUploadChannelRetirementTests
{
    [Fact]
    public void Production_wiring_retries_results_without_registering_workbook_intake()
    {
        var source = ReadSource("src", "SuavoAgent.Core", "Program.cs");

        Assert.Contains("AddHostedService<PricingResultOutboxWorker>()", source);
        Assert.Contains("AddSingleton<PricingTerminalAckOutbox>()", source);
        Assert.Contains("AddHostedService<PricingTerminalAckOutboxWorker>()", source);
        Assert.DoesNotContain("AddSingleton<IPricingUploadCloudClient>", source);
        Assert.DoesNotContain("AddSingleton<PricingUploadInbox>", source);
        Assert.DoesNotContain("AddHostedService<PricingUploadWorker>", source);
    }

    private static string ReadSource(params string[] segments)
    {
        for (var cursor = new DirectoryInfo(AppContext.BaseDirectory);
             cursor is not null;
             cursor = cursor.Parent)
        {
            var candidate = Path.Combine([cursor.FullName, .. segments]);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(string.Join('/', segments));
    }
}
