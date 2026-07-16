using Serilog;
using Serilog.Core;
using Serilog.Events;
using SuavoAgent.Core.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Diagnostics;

public sealed class CoreDiagnosticRedactionEnricherTests
{
    private const string Sentinel = "PHI-SENTINEL-987654321";

    public static IEnumerable<object[]> SensitiveProperties()
    {
        foreach (var name in new[]
                 {
                     "Rx", "RxHash", "Ndc", "Medication", "Supplier", "Cost",
                     "Patient", "Workbook", "File", "FileName", "Path", "Dir",
                     "Url", "Uri", "Query", "JobId", "SessionId", "PharmacyId",
                     "CorrelationKey", "Nonce", "Reason", "Error", "Message",
                     "CommandId", "TaskId", "RunId", "Digest", "Hash", "Name",
                 })
        {
            yield return new object[] { name };
        }
    }

    [Theory]
    [MemberData(nameof(SensitiveProperties))]
    public void SensitiveStructuredValues_AreRedactedBeforeSink(string propertyName)
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration()
            .SanitizeCoreDiagnostics()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger
            .ForContext(propertyName, Sentinel)
            .Information("core.test");

        var logEvent = Assert.Single(sink.Events);
        var rendered = logEvent.RenderMessage();
        Assert.DoesNotContain(Sentinel, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, string.Join('|', logEvent.Properties.Values), StringComparison.Ordinal);
        Assert.Equal(CoreDiagnosticRedactionEnricher.RedactedValue,
            Assert.IsType<ScalarValue>(logEvent.Properties[propertyName]).Value);
    }

    [Fact]
    public void HostileValues_AreRedacted_WhileBooleansCountsAndEnumsSurvive()
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration()
            .SanitizeCoreDiagnostics()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "unsafe={JobId} reason={Reason} count={Count} enabled={Enabled} state={State}",
            Sentinel,
            Sentinel,
            7,
            true,
            SampleState.Ready);

        var logEvent = Assert.Single(sink.Events);
        Assert.DoesNotContain(Sentinel, logEvent.RenderMessage(), StringComparison.Ordinal);
        Assert.Equal(CoreDiagnosticRedactionEnricher.RedactedValue,
            Assert.IsType<ScalarValue>(logEvent.Properties["JobId"]).Value);
        Assert.Equal(7, Assert.IsType<ScalarValue>(logEvent.Properties["Count"]).Value);
        Assert.Equal(true, Assert.IsType<ScalarValue>(logEvent.Properties["Enabled"]).Value);
        Assert.Equal(nameof(SampleState.Ready),
            Assert.IsType<ScalarValue>(logEvent.Properties["State"]).Value);
    }

    [Fact]
    public void EventCodeAndExceptionType_RequireStrictStructuralTokens()
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration()
            .SanitizeCoreDiagnostics()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "event={EventCode} type={ExceptionType} hostile={HostileType}",
            "core.worker.failed",
            "InvalidOperationException",
            Sentinel);

        var logEvent = Assert.Single(sink.Events);
        Assert.Equal("core.worker.failed",
            Assert.IsType<ScalarValue>(logEvent.Properties["EventCode"]).Value);
        Assert.Equal("InvalidOperationException",
            Assert.IsType<ScalarValue>(logEvent.Properties["ExceptionType"]).Value);
        Assert.DoesNotContain(Sentinel, logEvent.RenderMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public void StructuredObjectsAndSequences_AreReplacedAsAWhole()
    {
        var sink = new CaptureSink();
        using var logger = new LoggerConfiguration()
            .SanitizeCoreDiagnostics()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information(
            "payload={@Payload} values={Values}",
            new { PatientName = Sentinel, Medication = Sentinel },
            new[] { Sentinel });

        var logEvent = Assert.Single(sink.Events);
        Assert.DoesNotContain(Sentinel, logEvent.RenderMessage(), StringComparison.Ordinal);
        Assert.Equal(CoreDiagnosticRedactionEnricher.RedactedValue,
            Assert.IsType<ScalarValue>(logEvent.Properties["Payload"]).Value);
        Assert.Equal(CoreDiagnosticRedactionEnricher.RedactedValue,
            Assert.IsType<ScalarValue>(logEvent.Properties["Values"]).Value);
    }

    private enum SampleState
    {
        Ready,
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
