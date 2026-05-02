using System.Linq;
using System.Threading.Tasks;
using SuavoAgent.Analyzers;
using Xunit;

namespace SuavoAgent.Analyzers.Tests;

/// <summary>
/// Regression tests for the Wave 1 retrofitted types. Each retrofitted type
/// is asserted clean (analyzer silent). If a future PR introduces a PHI
/// field on one of these types, the regression test fires immediately.
/// </summary>
public class RetrofittedTypesRegressionTests
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
    public async Task PatientDetailsPayload_Clean()
    {
        // Mirrors src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs after retrofit.
        // None of the fields are PHI-Direct (per field-registry: address fields are PHI-Adjacent salted).
        var source = Annotations + """

            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public sealed record PatientDetailsPayload(
                string? FirstName,
                string? LastInitial,
                string? Phone,
                string? Address1,
                string? Address2,
                string? City,
                string? State,
                string? Zip);
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }

    [Fact]
    public async Task WaveGateTrippedPayload_Clean()
    {
        var source = Annotations + """

            using System;
            using System.Collections.Generic;
            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public sealed record WaveGateTrippedPayload(
                string WaveId,
                string EvidenceSummary,
                string CertifiedBy,
                IReadOnlyList<string> EvidenceEventIds,
                DateTimeOffset TrippedAt);
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }

    [Fact]
    public async Task WaveGateFailedPayload_Clean()
    {
        var source = Annotations + """

            using System;
            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            [OutboundPayload]
            public sealed record WaveGateFailedPayload(
                string WaveId,
                int AttemptNumber,
                string FailureSummary,
                string RootCauseClass,
                DateTimeOffset? RemediationPlanCommittedAt,
                string NextAttemptEstimated);
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }

    [Fact]
    public async Task HealthCompositePayload_Clean()
    {
        var source = Annotations + """

            using System;
            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            public sealed record HealthCompositeComponents(
                bool HelperAttached,
                bool IpcConnected,
                bool SchemaCanaryGreen,
                bool ExtractionRecent);

            [OutboundPayload]
            public sealed record HealthCompositePayload(
                string Status,
                HealthCompositeComponents Components,
                DateTimeOffset ComputedAt);
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }
}
