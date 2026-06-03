using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Cloud;

public sealed class PricingJobCloudUploader
{
    private const string SuppressedPricingWarning = "pricing_lookup_failed";
    private static readonly Regex SensitiveWarningPattern = new(
        @"\b(patient|dob|date\s+of\s+birth|phone|address|ssn|email|mrn|rx\s*(number|#)|prescription)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhonePattern = new(
        @"(?<!\d)(?:\+?1[-.\s]?)?(?:\(?\d{3}\)?[-.\s]?)\d{3}[-.\s]?\d{4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocalPathPattern = new(
        @"([A-Z]:\\|\\\\|/Users/|/home/|/var/folders/)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IPostSigner _postSigner;
    private readonly AgentStateDb _db;
    private readonly ILogger<PricingJobCloudUploader> _logger;

    public PricingJobCloudUploader(
        IPostSigner postSigner,
        AgentStateDb db,
        ILogger<PricingJobCloudUploader> logger)
    {
        _postSigner = postSigner;
        _db = db;
        _logger = logger;
    }

    public async Task UploadAsync(
        PricingJobSpec spec,
        PricingJobExecutionResult execution,
        string? commandId,
        CancellationToken ct)
    {
        try
        {
            var results = _db.GetPricingResults(spec.JobId);
            var source = SafeSource(execution.Mode);
            var payload = new
            {
                commandId,
                status = execution.Progress.Status,
                mode = source,
                totalItems = execution.Progress.TotalItems,
                completedItems = execution.Progress.CompletedItems,
                failedItems = execution.Progress.FailedItems,
                items = results.Select(r => new
                {
                    rowIndex = r.RowIndex,
                    ndc = r.Ndc,
                    found = r.Found,
                    supplierName = r.SupplierName,
                    costPerUnit = r.CostPerUnit,
                    status = r.Found ? "found" : "not_found",
                    warning = r.Found ? null : SafeWarning(r.ErrorMessage),
                    confidence = r.Found ? 0.95m : 0.35m,
                    source,
                    localEvidenceId = $"pricing:{spec.JobId}:{r.RowIndex}",
                    selectorObservations = MapObservations(r.Observations),
                }).ToArray(),
            };

            await _postSigner.PostSignedAsync(
                $"/api/agent/pricing-jobs/{spec.JobId}/results",
                payload,
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pricing job cloud upload failed for {JobId}", spec.JobId);
        }
    }

    // M2a: GREEN-tier selector telemetry. Each field is ControlType / AutomationId / ClassName
    // or an enum name — PHI-negative by type (SelectorObservation/ObservedElement cannot carry
    // Name / Value / Text / a free-text string), so no scrub is needed or possible here.
    private static object[]? MapObservations(
        IReadOnlyList<SuavoAgent.Contracts.Learning.SelectorObservation>? observations)
    {
        if (observations is not { Count: > 0 }) return null;
        return observations.Select(o => (object)new
        {
            stepId = o.StepId.ToString(),
            resolvedVia = o.ResolvedVia.ToString(),
            outcome = o.Outcome.ToString(),
            failureKind = o.FailureKind.ToString(),
            attempted = MapElement(o.Attempted),
            observedCandidates = o.ObservedCandidates.Select(MapElement).ToArray(),
        }).ToArray();
    }

    private static object? MapElement(SuavoAgent.Contracts.Learning.ObservedElement? e) =>
        e is null ? null : new { controlType = e.ControlType, automationId = e.AutomationId, className = e.ClassName };

    private static string? SafeWarning(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (SensitiveWarningPattern.IsMatch(trimmed) ||
            EmailPattern.IsMatch(trimmed) ||
            PhonePattern.IsMatch(trimmed) ||
            LocalPathPattern.IsMatch(trimmed))
        {
            return SuppressedPricingWarning;
        }

        return trimmed.Length <= 240 ? trimmed : trimmed[..240];
    }

    private static string SafeSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "sql";
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "sql" || normalized.Contains("sql", StringComparison.Ordinal)) return "sql";
        if (normalized == "uia" || normalized == "ui" || normalized.Contains("uia", StringComparison.Ordinal)) return "uia";
        if (normalized == "manual") return "manual";
        return "manual";
    }
}
