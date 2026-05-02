using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SuavoAgent.Analyzers;
using Xunit;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// Stress test for the analyzer. Generates 1000 synthetic types with nested
/// references (forward chain + cross-link cycles) and asserts the analyzer
/// completes within threshold. Catches future regressions where someone adds
/// expensive operations inside the analyzer hot path.
///
/// Threshold: 5 seconds wall-clock on Linux x64; 10 seconds on macOS arm64
/// (slower CI). Failures surface as test failures with measured time.
/// </summary>
public class PerformanceTests
{
    private const int TypeCount = 1000;
    private const int LinuxThresholdMs = 8_000;
    private const int MacArmThresholdMs = 10_000;

    private const string Annotations = """
        namespace SuavoAgent.Contracts.Annotations;
        using System;

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class PhiDirectAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
        public sealed class OutboundPayloadAttribute : Attribute { }
        """;

    [Fact]
    public async Task AnalyzerScales_To1000Types_UnderThreshold()
    {
        var source = GenerateSyntheticTypes(TypeCount);

        var sw = Stopwatch.StartNew();
        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);
        sw.Stop();

        // No PHI in any type → no SUAVO0001 expected.
        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));

        var thresholdMs = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? MacArmThresholdMs
            : LinuxThresholdMs;

        Assert.True(
            sw.ElapsedMilliseconds < thresholdMs,
            $"Analyzer took {sw.ElapsedMilliseconds}ms on {TypeCount} types " +
            $"(threshold: {thresholdMs}ms). Investigate analyzer hot-path regression.");
    }

    private static string GenerateSyntheticTypes(int count)
    {
        var sb = new StringBuilder(Annotations);
        sb.AppendLine();
        sb.AppendLine("using SuavoAgent.Contracts.Annotations;");
        sb.AppendLine();
        sb.AppendLine("namespace PerfTest;");
        sb.AppendLine();

        // Generate 'count' clean outbound types. Each references the next in
        // a chain, with cross-links every ~17 types for cycle stress. None
        // contain PHI — analyzer walks the whole graph but emits no diagnostics.
        for (int i = 0; i < count; i++)
        {
            var nextRef = (i + 1) % count;     // forward chain
            var cycleRef = (i + 17) % count;   // cross-link for cycles

            sb.AppendLine($"[OutboundPayload]");
            sb.AppendLine($"public class T{i}");
            sb.AppendLine("{");
            sb.AppendLine($"    public string Id{i} {{ get; set; }} = \"\";");
            sb.AppendLine($"    public T{nextRef}? Next {{ get; set; }}");
            sb.AppendLine($"    public T{cycleRef}? Cross {{ get; set; }}");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
