using System.Text.Json.Serialization;

namespace SuavoAgent.Core.Intelligence;

/// <summary>
/// Anonymized intelligence templates that transfer between businesses.
/// No business-specific data — only patterns, benchmarks, and templates.
/// </summary>

/// <summary>
/// An app usage pattern observed across multiple businesses in an industry.
/// E.g., "pharmacy businesses average 47 PMS->Excel transitions/day"
/// </summary>
public sealed record AppUsagePattern(
    [property: JsonPropertyName("industry")] string Industry,
    [property: JsonPropertyName("appName")] string AppName,
    [property: JsonPropertyName("avgFocusMinPerDay")] double AvgFocusMinPerDay,
    [property: JsonPropertyName("avgTransitionsPerDay")] int AvgTransitionsPerDay,
    [property: JsonPropertyName("businessCount")] int BusinessCount,
    [property: JsonPropertyName("confidence")] double Confidence);

/// <summary>
/// Efficiency benchmark — percentile bands per industry per metric.
/// E.g., "top quartile fill time = 15 min"
/// </summary>
public sealed record EfficiencyBenchmark(
    [property: JsonPropertyName("industry")] string Industry,
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("p25")] double P25,
    [property: JsonPropertyName("p50")] double P50,
    [property: JsonPropertyName("p75")] double P75,
    [property: JsonPropertyName("p95")] double P95,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("sampleSize")] int SampleSize);

/// <summary>
/// Document schema template — column patterns found across businesses.
/// E.g., "inventory spreadsheets in pharmacies have 8-12 columns matching [pattern]"
/// </summary>
public sealed record DocumentSchemaTemplate(
    [property: JsonPropertyName("industry")] string Industry,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("columnPatterns")] IReadOnlyList<string> ColumnPatterns,
    [property: JsonPropertyName("avgColumnCount")] int AvgColumnCount,
    [property: JsonPropertyName("businessCount")] int BusinessCount);

/// <summary>
/// Aggregated collective intelligence response from the cloud.
/// Contains anonymized templates — no individual business data.
/// </summary>
public sealed record CollectiveIntelligencePacket(
    [property: JsonPropertyName("industry")] string Industry,
    [property: JsonPropertyName("appPatterns")] IReadOnlyList<AppUsagePattern> AppPatterns,
    [property: JsonPropertyName("benchmarks")] IReadOnlyList<EfficiencyBenchmark> Benchmarks,
    [property: JsonPropertyName("documentTemplates")] IReadOnlyList<DocumentSchemaTemplate> DocumentTemplates,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt);

/// <summary>
/// Local efficiency scores computed by the agent for contribution to the collective.
/// Sent to cloud via heartbeat — cloud aggregates across fleet.
/// </summary>
public sealed record LocalEfficiencyReport(
    [property: JsonPropertyName("industry")] string Industry,
    [property: JsonPropertyName("metrics")] Dictionary<string, double> Metrics,
    [property: JsonPropertyName("appDistribution")] Dictionary<string, double> AppDistribution,
    [property: JsonPropertyName("documentSchemas")] IReadOnlyList<LocalDocumentSchema> DocumentSchemas,
    [property: JsonPropertyName("reportedAt")] DateTimeOffset ReportedAt);

public sealed record LocalDocumentSchema(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("columnCount")] int ColumnCount,
    [property: JsonPropertyName("schemaFingerprint")] string SchemaFingerprint);
