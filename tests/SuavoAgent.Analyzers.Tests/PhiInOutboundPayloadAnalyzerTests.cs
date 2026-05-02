using System.Linq;
using System.Threading.Tasks;
using SuavoAgent.Analyzers;
using Xunit;

namespace SuavoAgent.Analyzers.Tests;

public class PhiInOutboundPayloadAnalyzerTests
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
    public async Task DirectViolation_PhiPropertyOnOutboundType_EmitsDiagnostic()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class LeakyPayload
            {
                [PhiDirect]
                public string PatientName { get; set; } = "";
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        var diag = AnalyzerTestHelper.SingleDiagnostic(diagnostics, "SUAVO0001");
        Assert.Contains("LeakyPayload.PatientName", diag.GetMessage());
        Assert.Contains("TestNs.LeakyPayload", diag.GetMessage());
    }

    [Fact]
    public async Task NestedViolation_PhiInChildType_EmitsDiagnostic()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            public class PatientFields
            {
                [PhiDirect]
                public string Address1 { get; set; } = "";
            }

            [OutboundPayload]
            public class NestedLeakyPayload
            {
                public PatientFields Patient { get; set; } = new();
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        var diag = AnalyzerTestHelper.SingleDiagnostic(diagnostics, "SUAVO0001");
        Assert.Contains("NestedLeakyPayload.Patient.Address1", diag.GetMessage());
    }

    [Fact]
    public async Task CyclicTypes_DoesNotInfiniteLoop()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class A
            {
                public B? Other { get; set; }
                public string Name { get; set; } = "";
            }

            public class B
            {
                public A? Back { get; set; }
                public int Count { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        // No PHI in the graph; analyzer should terminate without diagnostics.
        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }

    [Fact]
    public async Task CleanOutboundType_NoPhiFields_NoDiagnostic()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class CleanPayload
            {
                public string RxHash { get; set; } = "";
                public int Quantity { get; set; }
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }

    [Fact]
    public async Task NonOutboundTypeWithPhi_NoDiagnostic()
    {
        // PHI-Direct in a local-only type (no [OutboundPayload]) is fine.
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            public class LocalOnlyPatient
            {
                [PhiDirect]
                public string PatientName { get; set; } = "";
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }

    [Fact]
    public async Task MultipleViolations_ReportsAll()
    {
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public class MultiLeak
            {
                [PhiDirect]
                public string PatientName { get; set; } = "";

                [PhiDirect]
                public string DateOfBirth { get; set; } = "";
            }
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        var matching = diagnostics.Where(d => d.Id == "SUAVO0001").ToArray();
        Assert.Equal(2, matching.Length);
        Assert.Contains(matching, d => d.GetMessage().Contains("PatientName"));
        Assert.Contains(matching, d => d.GetMessage().Contains("DateOfBirth"));
    }
}
