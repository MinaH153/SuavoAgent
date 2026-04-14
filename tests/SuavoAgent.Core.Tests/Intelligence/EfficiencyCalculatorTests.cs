using SuavoAgent.Core.Intelligence;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class EfficiencyCalculatorTests
{
    [Fact]
    public void ComputeReport_ProducesValidReport()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-eff-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var calc = new EfficiencyCalculator(db);
            var report = calc.ComputeReport("pharmacy");

            Assert.Equal("pharmacy", report.Industry);
            Assert.NotEmpty(report.Metrics);
            Assert.NotEqual(default, report.ReportedAt);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void CompareAgainstBenchmarks_ComputesPercentiles()
    {
        var report = new LocalEfficiencyReport(
            "pharmacy",
            new Dictionary<string, double> { ["fillTimeMin"] = 18.0 },
            new Dictionary<string, double>(),
            new List<LocalDocumentSchema>(),
            DateTimeOffset.UtcNow);

        var benchmarks = new List<EfficiencyBenchmark>
        {
            new("pharmacy", "fillTimeMin", 12.0, 18.0, 25.0, 35.0, "minutes", 50)
        };

        var result = EfficiencyCalculator.CompareAgainstBenchmarks(report, benchmarks);
        Assert.Equal(50, result["fillTimeMin"]); // 18.0 == P50
    }

    [Fact]
    public void CompareAgainstBenchmarks_TopQuartile()
    {
        var report = new LocalEfficiencyReport(
            "pharmacy",
            new Dictionary<string, double> { ["fillTimeMin"] = 10.0 },
            new Dictionary<string, double>(),
            new List<LocalDocumentSchema>(),
            DateTimeOffset.UtcNow);

        var benchmarks = new List<EfficiencyBenchmark>
        {
            new("pharmacy", "fillTimeMin", 12.0, 18.0, 25.0, 35.0, "minutes", 50)
        };

        var result = EfficiencyCalculator.CompareAgainstBenchmarks(report, benchmarks);
        Assert.Equal(25, result["fillTimeMin"]); // 10.0 < P25 = top quartile
    }
}
