using FlaUI.Core.AutomationElements;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Helper.Actuation;

namespace SuavoAgent.Helper.Workflows;

public interface IPioneerRxTop500ExportProgressSink
{
    void Report(PioneerRxTop500ExportProgress progress);
}

/// <summary>
/// Fixed PioneerRx UI workflow for Nadim's year-to-date Top-500 generic,
/// non-controlled, Rx-only dispensing report. It exposes no caller-provided
/// selectors or filter values and fails closed if any field cannot be set and
/// read back exactly.
/// </summary>
public sealed partial class PioneerRxTop500ExportWorkflow
{
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(2);

    private readonly PioneerRxUiaEngine _engine;
    private readonly ActuationGate _actuationGate;
    private readonly ILogger _logger;
    private readonly SendInputDriver? _pointerDriver;
    private readonly TimeProvider _timeProvider;
    private readonly StableXlsxExportWatcher? _watcher;
    private readonly PioneerRxTop500ArtifactStore? _artifactStore;
    private readonly IPioneerRxTop500ExportProgressSink? _progressSink;

    public PioneerRxTop500ExportWorkflow(
        PioneerRxUiaEngine engine,
        ActuationGate actuationGate,
        ILogger logger,
        SendInputDriver? pointerDriver = null,
        TimeProvider? timeProvider = null,
        IPioneerRxTop500ExportProgressSink? progressSink = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _actuationGate = actuationGate ?? throw new ArgumentNullException(nameof(actuationGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pointerDriver = pointerDriver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progressSink = progressSink;

        var downloads = StableXlsxExportWatcher.ResolveDefaultDownloadsDirectory();
        if (!string.IsNullOrWhiteSpace(downloads))
            _watcher = new StableXlsxExportWatcher(downloads, _timeProvider);

        var stagingRoot = PioneerRxTop500ArtifactStore.ResolveDefaultStagingRootDirectory();
        if (!string.IsNullOrWhiteSpace(stagingRoot))
            _artifactStore = new PioneerRxTop500ArtifactStore(
                stagingRoot,
                Path.Combine("SuavoAgent", "Report Staging"));
    }

    internal PioneerRxTop500ExportWorkflow(
        PioneerRxUiaEngine engine,
        ActuationGate actuationGate,
        ILogger logger,
        StableXlsxExportWatcher watcher,
        PioneerRxTop500ArtifactStore artifactStore,
        TimeProvider? timeProvider = null,
        IPioneerRxTop500ExportProgressSink? progressSink = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _actuationGate = actuationGate ?? throw new ArgumentNullException(nameof(actuationGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progressSink = progressSink;
    }

    public async Task<PioneerRxTop500ExportResult> RunAsync(
        PioneerRxTop500ExportRequest request,
        CancellationToken ct)
    {
        var runDate = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var startDate = PioneerRxTop500ReportRecipe.StartFor(runDate);
        if (request is null || !request.IsValid())
            return PioneerRxTop500ExportResult.Failed(
                request?.JobId ?? string.Empty,
                PioneerRxTop500ExportCodes.InvalidRequest,
                startDate,
                runDate);

        if (_watcher is null || _artifactStore is null ||
            !_artifactStore.TryPrepare() ||
            !_watcher.TryCaptureBaseline(out _))
        {
            return PioneerRxTop500ExportResult.Failed(
                request.JobId,
                PioneerRxTop500ExportCodes.ExportDirectoryUnavailable,
                startDate,
                runDate);
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            EnsureLiveActuation();
            var mainWindow = _engine.MainWindow;
            if (mainWindow is null)
                return Failed(request, PioneerRxTop500ExportCodes.PioneerRxUnavailable, startDate, runDate);

            BringPmsToForeground(mainWindow);
            Narrate("Preparing", "dispensing report");

            using var automation = new UIA2Automation();
            var reportOpen = OpenReportWindow(mainWindow, automation, ct);
            if (!reportOpen.NavigationSucceeded)
                return Failed(request, PioneerRxTop500ExportCodes.ReportNavigationUnavailable, startDate, runDate);
            var reportSurface = reportOpen.Surface;
            if (reportSurface is null)
                return Failed(request, PioneerRxTop500ExportCodes.ReportWindowUnavailable, startDate, runDate);

            BringPmsToForeground(mainWindow);
            if (!ApplyFixedRecipe(reportSurface, automation, startDate, runDate, ct))
                return Failed(request, PioneerRxTop500ExportCodes.FilterSurfaceUnavailable, startDate, runDate);
            if (!VerifyFixedRecipe(reportSurface, automation, startDate, runDate))
                return Failed(request, PioneerRxTop500ExportCodes.FilterVerificationFailed, startDate, runDate);

            // Never export through a report viewer that predates this run. A
            // same-day stale viewer has the same title/page anchors and its
            // workbook can otherwise satisfy every date-based semantic check.
            if (!CloseExistingReportViewers(automation, ct))
                return Failed(request, PioneerRxTop500ExportCodes.ReportViewUnavailable, startDate, runDate);
            BringPmsToForeground(mainWindow);
            var parametersOpen = OpenReportParameters(mainWindow, automation, ct);
            if (!parametersOpen.NavigationSucceeded)
                return Failed(request, PioneerRxTop500ExportCodes.ReportNavigationUnavailable, startDate, runDate);
            var parameters = parametersOpen.Surface;
            if (parameters is null)
                return Failed(request, PioneerRxTop500ExportCodes.ReportWindowUnavailable, startDate, runDate);
            if (!ApplyReportParameters(parameters, automation))
                return Failed(request, PioneerRxTop500ExportCodes.FilterSurfaceUnavailable, startDate, runDate);

            Narrate("Building", "dispensing report");
            ReportProgress(
                request.JobId,
                PioneerRxTop500ExportStages.GeneratingReportSequence,
                PioneerRxTop500ExportStages.GeneratingReport);
            if (!ClickFixedButton(
                    parameters,
                    automation,
                    PioneerRxTop500ReportSurface.ViewButtonId,
                    PioneerRxTop500ReportSurface.ViewButtonName))
                return Failed(request, PioneerRxTop500ExportCodes.ReportViewUnavailable, startDate, runDate);
            var reportViewer = WaitForReportViewer(automation, ct);
            if (reportViewer is null)
                return Failed(request, PioneerRxTop500ExportCodes.ReportViewUnavailable, startDate, runDate);

            if (!_watcher.TryCaptureBaseline(out var baseline) || baseline is null)
                return Failed(request, PioneerRxTop500ExportCodes.ExportDirectoryUnavailable, startDate, runDate);
            var saveAsDialogBaseline = CaptureSaveAsDialogBaseline(automation);

            Narrate("Saving", "Top 500 report");
            ReportProgress(
                request.JobId,
                PioneerRxTop500ExportStages.ExportingReportSequence,
                PioneerRxTop500ExportStages.ExportingReport);
            var exportStartedAt = _timeProvider.GetUtcNow();
            if (!ClickViewerExcel(reportViewer, automation))
                return Failed(request, PioneerRxTop500ExportCodes.ExportControlUnavailable, startDate, runDate);

            var uniqueSavePath = BuildUniqueSavePath(
                baseline.RootDirectory,
                request.JobId);
            using var exportCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watchTask = _watcher.WaitAsync(
                baseline,
                exportStartedAt,
                ExportTimeout,
                exportCts.Token,
                export => PioneerRxTop500ExportWorkbookValidator.IsExact(
                    export.FullPath,
                    runDate));
            var saveAsOutcome = await MonitorOptionalSaveAsAsync(
                watchTask,
                automation,
                saveAsDialogBaseline,
                uniqueSavePath,
                exportCts.Token).ConfigureAwait(false);
            if (saveAsOutcome is SaveAsMonitorOutcome.ForeignProcessRejected or
                SaveAsMonitorOutcome.InvalidTrustedDialog)
            {
                exportCts.Cancel();
                try { _ = await watchTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                var code = saveAsOutcome == SaveAsMonitorOutcome.ForeignProcessRejected
                    ? PioneerRxTop500ExportCodes.ExportSaveDialogUntrusted
                    : PioneerRxTop500ExportCodes.ExportSaveDialogInvalid;
                return PioneerRxTop500ExportResult.Failed(
                    request.JobId,
                    code,
                    startDate,
                    runDate,
                    code);
            }

            var watched = await watchTask.ConfigureAwait(false);
            if (!watched.Success || watched.Export is null)
            {
                return Failed(
                    request,
                    watched.InvalidStableFileObserved
                        ? PioneerRxTop500ExportCodes.ExportInvalid
                        : PioneerRxTop500ExportCodes.ExportTimedOut,
                    startDate,
                    runDate);
            }

            var published = await _artifactStore.PublishAsync(
                watched.Export,
                runDate,
                ct).ConfigureAwait(false);
            if (published is null)
                return Failed(request, PioneerRxTop500ExportCodes.ExportInvalid, startDate, runDate);

            _logger.Information(
                "PioneerRx Top-500 workflow completed code={Code} bytes={Bytes}",
                PioneerRxTop500ExportCodes.ExportReady,
                published.Length);
            Narrate("Done", "report ready");
            return new PioneerRxTop500ExportResult(
                PioneerRxTop500ExportRequest.CurrentContractVersion,
                request.JobId,
                true,
                PioneerRxTop500ExportCodes.ExportReady,
                null,
                published.Token,
                PioneerRxTop500ReportRecipe.RawArtifactLabel,
                published.Sha256,
                published.Length,
                PioneerRxTop500ReportRecipe.FormatDate(startDate),
                PioneerRxTop500ReportRecipe.FormatDate(runDate),
                PioneerRxTop500ReportRecipe.TopCount);
        }
        catch (Top500ActuationBlockedException ex)
        {
            _logger.Warning(
                "PioneerRx Top-500 workflow halted code={Code}",
                ex.BlockerCode);
            return PioneerRxTop500ExportResult.Failed(
                request.JobId,
                PioneerRxTop500ExportCodes.ActuationGateClosed,
                startDate,
                runDate,
                ex.BlockerCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Failed(request, PioneerRxTop500ExportCodes.Cancelled, startDate, runDate);
        }
        catch (Exception ex)
        {
            _logger.Error(
                "PioneerRx Top-500 workflow failed locally type={ExceptionType}",
                ex.GetType().Name);
            return Failed(request, PioneerRxTop500ExportCodes.UnexpectedFailure, startDate, runDate);
        }
    }

    public async Task<PioneerRxTop500ArtifactReadResult> ReadArtifactAsync(
        PioneerRxTop500ArtifactReadRequest request,
        CancellationToken ct)
    {
        if (request is null || !request.IsValid())
            return PioneerRxTop500ArtifactReadResult.Failed(
                request?.JobId ?? string.Empty,
                PioneerRxTop500ArtifactReadCodes.InvalidRequest);
        if (_artifactStore is null)
            return PioneerRxTop500ArtifactReadResult.Failed(
                request.JobId,
                PioneerRxTop500ArtifactReadCodes.Unavailable);

        var artifact = await _artifactStore.ReadAsync(request, ct).ConfigureAwait(false);
        return artifact is null
            ? PioneerRxTop500ArtifactReadResult.Failed(
                request.JobId,
                PioneerRxTop500ArtifactReadCodes.IntegrityMismatch)
            : new PioneerRxTop500ArtifactReadResult(
                PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
                request.JobId,
                true,
                PioneerRxTop500ArtifactReadCodes.Ready,
                Convert.ToBase64String(artifact.Bytes),
                artifact.Offset,
                artifact.NextOffset,
                artifact.Complete,
                artifact.Sha256,
                artifact.Length);
    }

    private static PioneerRxTop500ExportResult Failed(
        PioneerRxTop500ExportRequest request,
        string code,
        DateOnly startDate,
        DateOnly runDate) => PioneerRxTop500ExportResult.Failed(
            request.JobId,
            code,
            startDate,
            runDate);

    internal static string BuildUniqueSavePath(string rootDirectory, string jobId)
    {
        if (!Guid.TryParseExact(jobId, "D", out var parsedJobId))
            throw new ArgumentException("Job id must be canonical.", nameof(jobId));
        return Path.Combine(
            Path.GetFullPath(rootDirectory),
            $"SuavoAgent-Top500-{parsedJobId:N}-{Guid.NewGuid():N}.xlsx");
    }

    private void EnsureLiveActuation()
    {
        var rejection = _actuationGate.CheckLiveOrReject();
        if (rejection is not null)
            throw new Top500ActuationBlockedException(
                rejection.RejectionCode ?? ActuationRejectionCodes.GateDisabled);

        var trust = _engine.VerifyAttachedProcessIdentity();
        if (!trust.Trusted)
            throw new Top500ActuationBlockedException(
                ActuationRejectionCodes.ProcessIdentityUntrusted);
    }

    private void ExecuteLiveMutation(Action mutation)
    {
        var identityRejected = false;
        var rejection = _actuationGate.ExecuteLiveMutationOrReject(() =>
        {
            if (!_engine.VerifyAttachedProcessIdentity().Trusted)
            {
                identityRejected = true;
                return;
            }
            mutation();
        });
        if (rejection is not null)
            throw new Top500ActuationBlockedException(
                rejection.RejectionCode ?? ActuationRejectionCodes.GateDisabled);
        if (identityRejected)
            throw new Top500ActuationBlockedException(
                ActuationRejectionCodes.ProcessIdentityUntrusted);
    }

    private void Narrate(string action, string fixedCaption) =>
        _pointerDriver?.NarratePresence(action, fixedCaption);

    internal void ReportProgress(
        string jobId,
        int sequence,
        string stage)
    {
        if (_progressSink is null) return;
        try
        {
            _progressSink.Report(new PioneerRxTop500ExportProgress(
                jobId,
                sequence,
                stage,
                0,
                0,
                0,
                _timeProvider.GetUtcNow()));
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "PioneerRx Top-500 progress signal failed stage={Stage} type={ExceptionType}",
                stage,
                ex.GetType().Name);
        }
    }

    private sealed class Top500ActuationBlockedException(string blockerCode) : Exception
    {
        public string BlockerCode { get; } = blockerCode;
    }
}
