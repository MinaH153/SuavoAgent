using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class PricingLogRedactionTests
{
    [Fact]
    public void PricingPipelineSources_DoNotLogWorkbookPathTemplates()
    {
        foreach (var relativePath in new[]
        {
            Path.Combine("src", "SuavoAgent.Core", "Pricing", "ExcelPricingReader.cs"),
            Path.Combine("src", "SuavoAgent.Core", "Pricing", "ExcelPricingWriter.cs"),
            Path.Combine("src", "SuavoAgent.Core", "Pricing", "PricingJobRunner.cs"),
            Path.Combine("src", "SuavoAgent.Core", "Pricing", "SqlPricingJobRunner.cs"),
        })
        {
            var source = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

            Assert.DoesNotContain("{Path}", source);
            Assert.DoesNotContain("OutputPath ??", source);
            Assert.DoesNotContain("LogError(ex", source);
        }
    }
}
