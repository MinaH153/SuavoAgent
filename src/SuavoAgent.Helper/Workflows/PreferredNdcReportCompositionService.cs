using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// The single native in-process Feature-B Excel composition path: admit a private read-only snapshot,
/// evaluate every pair, and write a fresh report. It is intentionally not registered as a CLI, cloud
/// command, dashboard action, or PioneerRx mutation surface.
/// </summary>
public sealed class PreferredNdcReportCompositionService
{
    private readonly ILogger<PreferredNdcReportWriter> _writerLogger;
    private readonly TimeProvider _timeProvider;

    public PreferredNdcReportCompositionService(
        ILogger<PreferredNdcReportWriter> writerLogger,
        TimeProvider? timeProvider = null)
    {
        _writerLogger = writerLogger ?? throw new ArgumentNullException(nameof(writerLogger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PreferredNdcCompositionResult> ComposeAsync(
        string inputWorkbookPath,
        string outputDirectory,
        CancellationToken ct)
    {
        if (!PreferredNdcWorkbookAdmission.TryAdmit(
                inputWorkbookPath,
                out var admitted,
                out var admissionCode))
            return PreferredNdcCompositionResult.Failed(admissionCode);

        var lease = admitted!;
        using (lease)
        {
            ct.ThrowIfCancellationRequested();
            var jobId = $"preferred-ndc-{lease.SourceSha256[..16]}";
            var requests = lease.Reader.Pairs
                .Select((pair, index) => new PreferredNdcRequest(
                    jobId,
                    index,
                    pair.DrugGroupKey,
                    pair.PlanId))
                .ToArray();
            var runner = new PreferredNdcReportRunner(lease.Reader, _timeProvider);
            var rows = await runner.RunAsync(requests, ct).ConfigureAwait(false);

            var timestamp = _timeProvider.GetUtcNow().ToString(
                "yyyyMMdd-HHmmss",
                System.Globalization.CultureInfo.InvariantCulture);
            var write = new PreferredNdcReportWriter(_writerLogger)
                .Write(outputDirectory, rows, timestamp);
            if (!write.Success || string.IsNullOrWhiteSpace(write.OutputPath))
                return PreferredNdcCompositionResult.Failed(
                    write.Error ?? PreferredNdcReportWriter.WriteFailedError);

            var code = write.FailRows == 0
                ? "report_written"
                : "report_written_with_manual_review";
            return new PreferredNdcCompositionResult(
                Success: true,
                Code: code,
                OutputPath: write.OutputPath,
                PairCount: rows.Count,
                RecommendationCount: write.OkRows,
                ManualReviewCount: write.FailRows,
                SourceSha256: lease.SourceSha256);
        }
    }
}

public sealed record PreferredNdcCompositionResult(
    bool Success,
    string Code,
    string? OutputPath,
    int PairCount,
    int RecommendationCount,
    int ManualReviewCount,
    string? SourceSha256)
{
    public static PreferredNdcCompositionResult Failed(string code) =>
        new(false, code, null, 0, 0, 0, null);
}
