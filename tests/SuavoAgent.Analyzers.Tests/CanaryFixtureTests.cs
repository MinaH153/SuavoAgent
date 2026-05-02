using System.Linq;
using System.Threading.Tasks;
using SuavoAgent.Analyzers;
using Xunit;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// The canary fixture. Asserts the gate works on a representative
/// HIPAA-violation. If <see cref="CanaryViolation_GateRejects"/> ever passes
/// silently (i.e., no diagnostic emitted), the gate is broken — investigate
/// immediately.
/// </summary>
public class CanaryFixtureTests
{
    private const string Annotations = """
        namespace SuavoAgent.Contracts.Annotations;
        using System;

        [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
        public sealed class PhiDirectAttribute : Attribute { }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
        public sealed class OutboundPayloadAttribute : Attribute { }
        """;

    [Fact]
    public async Task CanaryViolation_GateRejects()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace SuavoAgent.Canary;

            [OutboundPayload]
            public class CanaryLeak
            {
                [PhiDirect]
                public string PatientName { get; set; } = "";

                [PhiDirect]
                public string DateOfBirth { get; set; } = "";
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        var violations = diagnostics.Where(d => d.Id == "SUAVO0001").ToArray();
        Assert.Equal(2, violations.Length);
        Assert.Contains(violations, d => d.GetMessage().Contains("CanaryLeak.PatientName"));
        Assert.Contains(violations, d => d.GetMessage().Contains("CanaryLeak.DateOfBirth"));
    }

    [Fact]
    public async Task CanaryClean_GateSilent()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace SuavoAgent.Canary;

            [OutboundPayload]
            public class CleanCanary
            {
                public string RxHash { get; set; } = "";
                public string MedicationCode { get; set; } = "";
                public int Quantity { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }
}
