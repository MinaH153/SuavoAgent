using System.Collections.Frozen;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

public interface ITopDispensedWorklistProgressBuilder : ITopDispensedWorklistBuilder
{
    Task<TopDispensedWorklistBuildResult> BuildAsync(
        string commandId,
        Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Production v3 worklist source. It asks the authenticated interactive Helper
/// to drive the fixed Rx Binoculars recipe, receives an opaque artifact receipt,
/// pulls the workbook through bounded chunks without ever receiving the user's
/// filesystem path, then publishes a protected Core-local input for pricing.
/// </summary>
public sealed class PioneerRxExportTopDispensedWorklistBuilder : ITopDispensedWorklistProgressBuilder
{
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(20);

    private readonly IIpcCommandClient _commandClient;
    private readonly ExcelPricingReader _reader;
    private readonly ILogger<PioneerRxExportTopDispensedWorklistBuilder> _logger;
    private readonly string _dataRoot;
    private readonly TimeProvider _timeProvider;
    private readonly PioneerRxTop500ProgressRelay? _progressRelay;

    public PioneerRxExportTopDispensedWorklistBuilder(
        IIpcCommandClient commandClient,
        ExcelPricingReader reader,
        ILogger<PioneerRxExportTopDispensedWorklistBuilder> logger,
        PioneerRxTop500ProgressRelay? progressRelay = null)
        : this(
            commandClient,
            reader,
            logger,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent"),
            TimeProvider.System,
            progressRelay)
    {
    }

    internal PioneerRxExportTopDispensedWorklistBuilder(
        IIpcCommandClient commandClient,
        ExcelPricingReader reader,
        ILogger<PioneerRxExportTopDispensedWorklistBuilder> logger,
        string dataRoot,
        TimeProvider? timeProvider = null,
        PioneerRxTop500ProgressRelay? progressRelay = null)
    {
        _commandClient = commandClient ?? throw new ArgumentNullException(nameof(commandClient));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dataRoot = Path.GetFullPath(dataRoot ?? throw new ArgumentNullException(nameof(dataRoot)));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progressRelay = progressRelay;
    }

    public async Task<TopDispensedWorklistBuildResult> BuildAsync(
        string commandId,
        CancellationToken cancellationToken) => await BuildCoreAsync(
            commandId,
            reportProgress: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<TopDispensedWorklistBuildResult> BuildAsync(
        string commandId,
        Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask> reportProgress,
        CancellationToken cancellationToken) => await BuildCoreAsync(
            commandId,
            (Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask>?)reportProgress,
            cancellationToken).ConfigureAwait(false);

    private async Task<TopDispensedWorklistBuildResult> BuildCoreAsync(
        string commandId,
        Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(commandId, "D", out _))
            return TopDispensedWorklistBuildResult.Fail("pricing_worklist_validation_failed");

        if (!TryPrepareOutput(commandId, out var outputPath))
            return TopDispensedWorklistBuildResult.Fail("pricing_worklist_source_unavailable");
        if (File.Exists(outputPath))
        {
            var cached = ValidatePublished(
                outputPath,
                DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime));
            if (cached.Ok) return cached;

            // A crash can leave a corrupt file, while a retry after midnight
            // makes yesterday's otherwise-valid report stale. The path is a
            // canonical command-id file inside the ACL-verified data root, so
            // delete only that invalid cache and regenerate it fail-closed.
            TryDelete(outputPath);
            if (File.Exists(outputPath)) return cached;
        }

        using var relayLease = reportProgress is null || _progressRelay is null
            ? null
            : _progressRelay.TryRegister(commandId, reportProgress);
        if (reportProgress is not null && _progressRelay is not null && relayLease is null)
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_generation_failed");
        if (_progressRelay is null)
        {
            await ReportProgressAsync(
                reportProgress,
                commandId,
                PioneerRxTop500ExportStages.GeneratingReportSequence,
                PioneerRxTop500ExportStages.GeneratingReport,
                cancellationToken).ConfigureAwait(false);
        }
        var export = await RequestExportAsync(commandId, cancellationToken).ConfigureAwait(false);
        if (export.Receipt is null)
            return TopDispensedWorklistBuildResult.Fail(
                export.ErrorCode ?? "pricing_worklist_generation_failed");
        var receipt = export.Receipt;

        if (_progressRelay is null)
        {
            await ReportProgressAsync(
                reportProgress,
                commandId,
                PioneerRxTop500ExportStages.ExportingReportSequence,
                PioneerRxTop500ExportStages.ExportingReport,
                cancellationToken).ConfigureAwait(false);
        }
        var bytes = await ReadArtifactAsync(receipt, cancellationToken).ConfigureAwait(false);
        if (bytes is null || !ValidateBytes(bytes, receipt))
            return TopDispensedWorklistBuildResult.Fail("pricing_worklist_validation_failed");

        if (!await WriteAtomicallyAsync(outputPath, bytes, cancellationToken).ConfigureAwait(false))
            return TopDispensedWorklistBuildResult.Fail("pricing_worklist_generation_failed");
        if (!DateOnly.TryParseExact(
                receipt.CompletedOnThrough,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var receiptRunDate))
            return TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_validation_failed");
        return ValidatePublished(outputPath, receiptRunDate);
    }

    private async ValueTask ReportProgressAsync(
        Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask>? reportProgress,
        string commandId,
        int sequence,
        string stage,
        CancellationToken ct)
    {
        if (reportProgress is null) return;
        try
        {
            await reportProgress(
                new PioneerRxTop500ExportProgress(
                    commandId,
                    sequence,
                    stage,
                    0,
                    0,
                    0,
                    _timeProvider.GetUtcNow()),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.top500_export.progress_failed stage={Stage} exception_type={ExceptionType}",
                stage,
                exception.GetType().Name);
        }
    }

    private async Task<ExportRequestResult> RequestExportAsync(
        string commandId,
        CancellationToken ct)
    {
        var payload = new PioneerRxTop500ExportRequest(
            PioneerRxTop500ExportRequest.CurrentContractVersion,
            commandId);
        var request = new IpcRequest(
            Guid.NewGuid().ToString("N"),
            IpcCommands.PioneerRxTop500Export,
            PioneerRxTop500ExportRequest.CurrentContractVersion,
            JsonSerializer.SerializeToElement(payload));
        var response = await _commandClient.SendAsync(request, ExportTimeout, ct)
            .ConfigureAwait(false);
        if (!ResponseMatches(response, request))
            return ExportRequestResult.Failed("pricing_worklist_generation_failed");

        try
        {
            var receipt = JsonSerializer.Deserialize<PioneerRxTop500ExportResult>(
                response!.Data!.Value);
            if (ReceiptIsExact(receipt, commandId))
                return ExportRequestResult.Ready(receipt!);
            if (FailureReceiptIsExact(receipt, commandId))
                return ExportRequestResult.Failed(MapExportFailure(receipt!.Code));
            return ExportRequestResult.Failed("pricing_worklist_validation_failed");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.top500_export.receipt_invalid exception_type={ExceptionType}",
                exception.GetType().Name);
            return ExportRequestResult.Failed("pricing_worklist_validation_failed");
        }
    }

    private async Task<byte[]?> ReadArtifactAsync(
        PioneerRxTop500ExportResult receipt,
        CancellationToken ct)
    {
        await using var output = new MemoryStream(checked((int)receipt.WorkbookBytes!.Value));
        long offset = 0;
        while (offset < receipt.WorkbookBytes.Value)
        {
            var payload = new PioneerRxTop500ArtifactReadRequest(
                PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
                receipt.JobId,
                receipt.ArtifactToken!,
                receipt.WorkbookSha256!,
                receipt.WorkbookBytes.Value,
                offset);
            var request = new IpcRequest(
                Guid.NewGuid().ToString("N"),
                IpcCommands.PioneerRxTop500ReadArtifact,
                PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
                JsonSerializer.SerializeToElement(payload));
            var response = await _commandClient.SendAsync(request, ChunkTimeout, ct)
                .ConfigureAwait(false);
            if (!ResponseMatches(response, request)) return null;

            PioneerRxTop500ArtifactReadResult? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<PioneerRxTop500ArtifactReadResult>(
                    response!.Data!.Value);
            }
            catch
            {
                return null;
            }
            if (!ChunkIsExact(chunk, receipt, offset)) return null;

            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(chunk!.ChunkBase64!);
            }
            catch
            {
                return null;
            }
            if (decoded.Length == 0 || offset + decoded.Length != chunk.NextOffset)
                return null;
            await output.WriteAsync(decoded, ct).ConfigureAwait(false);
            offset = chunk.NextOffset;
            if (chunk.Complete != (offset == receipt.WorkbookBytes.Value))
                return null;
        }
        return output.ToArray();
    }

    private bool TryPrepareOutput(string commandId, out string outputPath)
    {
        outputPath = string.Empty;
        try
        {
            if (!InstalledDataRootVerifier.IsSafe(_dataRoot)) return false;
            var directory = Path.Combine(_dataRoot, "pricing", "generated");
            Directory.CreateDirectory(directory);
            if (DirectoryTreeContainsReparsePoint(_dataRoot, directory)) return false;
            outputPath = Path.Combine(directory, $"{commandId}.xlsx");
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.top500_export.directory_unavailable exception_type={ExceptionType}",
                exception.GetType().Name);
            return false;
        }
    }

    private async Task<bool> WriteAtomicallyAsync(
        string outputPath,
        byte[] bytes,
        CancellationToken ct)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileNameWithoutExtension(outputPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, outputPath, overwrite: false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception exception)
        {
            TryDelete(temporaryPath);
            _logger.LogWarning(
                "core.top500_export.publish_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return File.Exists(outputPath);
        }
    }

    private TopDispensedWorklistBuildResult ValidatePublished(
        string path,
        DateOnly expectedRunDate)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return TopDispensedWorklistBuildResult.Fail("pricing_worklist_validation_failed");
            if (!PioneerRxTop500ExportWorkbookValidator.IsExact(path, expectedRunDate))
                return TopDispensedWorklistBuildResult.Fail("pricing_worklist_validation_failed");
            var read = _reader.Read(path);
            return read.Success &&
                   read.Invalid.Count == 0 &&
                   read.Rows.Count == PioneerRxTop500ReportRecipe.TopCount
                ? TopDispensedWorklistBuildResult.Success(path, read.Rows.Count)
                : TopDispensedWorklistBuildResult.Fail("pricing_worklist_validation_failed");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.top500_export.validation_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return TopDispensedWorklistBuildResult.Fail("pricing_worklist_validation_failed");
        }
    }

    private bool ReceiptIsExact(PioneerRxTop500ExportResult? receipt, string commandId)
    {
        if (receipt is not
            {
                ContractVersion: PioneerRxTop500ExportRequest.CurrentContractVersion,
                Success: true,
                Code: PioneerRxTop500ExportCodes.ExportReady,
                BlockerCode: null,
                TopCount: PioneerRxTop500ReportRecipe.TopCount,
                ArtifactToken.Length: 32,
                WorkbookSha256.Length: 64,
                WorkbookBytes: > 0 and <= PioneerRxTop500ArtifactReadRequest.MaximumWorkbookBytes,
            } ||
            !string.Equals(receipt.JobId, commandId, StringComparison.Ordinal) ||
            !string.Equals(
                receipt.DestinationLabel,
                PioneerRxTop500ReportRecipe.RawArtifactLabel,
                StringComparison.Ordinal) ||
            !IsLowerHex(receipt.ArtifactToken) ||
            !IsLowerHex(receipt.WorkbookSha256))
            return false;

        if (!DateOnly.TryParseExact(
                receipt.CompletedOnThrough,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end) ||
            !DateOnly.TryParseExact(
                receipt.CompletedOnFrom,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start) ||
            start != PioneerRxTop500ReportRecipe.StartFor(end))
            return false;

        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        return end >= today.AddDays(-1) && end <= today.AddDays(1);
    }

    private bool FailureReceiptIsExact(
        PioneerRxTop500ExportResult? receipt,
        string commandId)
    {
        if (receipt is not
            {
                ContractVersion: PioneerRxTop500ExportRequest.CurrentContractVersion,
                Success: false,
                ArtifactToken: null,
                WorkbookSha256: null,
                WorkbookBytes: null,
                TopCount: PioneerRxTop500ReportRecipe.TopCount,
            } ||
            !KnownExportFailureCodes.Contains(receipt.Code) ||
            !string.Equals(receipt.JobId, commandId, StringComparison.Ordinal) ||
            !string.Equals(
                receipt.DestinationLabel,
                PioneerRxTop500ReportRecipe.RawArtifactLabel,
                StringComparison.Ordinal) ||
            !FailureBlockerIsExact(receipt.Code, receipt.BlockerCode))
            return false;

        if (!DateOnly.TryParseExact(
                receipt.CompletedOnThrough,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end) ||
            !DateOnly.TryParseExact(
                receipt.CompletedOnFrom,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start) ||
            start != PioneerRxTop500ReportRecipe.StartFor(end))
            return false;

        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        return end >= today.AddDays(-1) && end <= today.AddDays(1);
    }

    private static bool FailureBlockerIsExact(string code, string? blockerCode) => code switch
    {
        PioneerRxTop500ExportCodes.ActuationGateClosed =>
            blockerCode is not null && KnownActuationBlockers.Contains(blockerCode),
        PioneerRxTop500ExportCodes.ExportSaveDialogUntrusted or
        PioneerRxTop500ExportCodes.ExportSaveDialogInvalid =>
            string.Equals(blockerCode, code, StringComparison.Ordinal),
        _ => blockerCode is null,
    };

    private static string MapExportFailure(string code) => code switch
    {
        PioneerRxTop500ExportCodes.ActuationGateClosed =>
            "pricing_report_permission_blocked",
        PioneerRxTop500ExportCodes.PioneerRxUnavailable =>
            "pricing_pioneerrx_not_open",
        PioneerRxTop500ExportCodes.ReportNavigationUnavailable or
        PioneerRxTop500ExportCodes.ReportWindowUnavailable =>
            "pricing_report_open_failed",
        PioneerRxTop500ExportCodes.FilterSurfaceUnavailable or
        PioneerRxTop500ExportCodes.FilterVerificationFailed =>
            "pricing_report_filters_failed",
        PioneerRxTop500ExportCodes.ReportViewUnavailable or
        PioneerRxTop500ExportCodes.UnexpectedFailure =>
            "pricing_report_generation_failed",
        PioneerRxTop500ExportCodes.ExportControlUnavailable or
        PioneerRxTop500ExportCodes.ExportTimedOut =>
            "pricing_report_export_failed",
        PioneerRxTop500ExportCodes.ExportSaveDialogUntrusted or
        PioneerRxTop500ExportCodes.ExportSaveDialogInvalid =>
            "pricing_report_save_dialog_blocked",
        PioneerRxTop500ExportCodes.ExportDirectoryUnavailable =>
            "pricing_report_storage_unavailable",
        PioneerRxTop500ExportCodes.ExportInvalid =>
            "pricing_report_validation_failed",
        PioneerRxTop500ExportCodes.Cancelled =>
            "pricing_report_cancelled",
        _ => "pricing_worklist_validation_failed",
    };

    private static readonly FrozenSet<string> KnownExportFailureCodes = new[]
    {
        PioneerRxTop500ExportCodes.InvalidRequest,
        PioneerRxTop500ExportCodes.ActuationGateClosed,
        PioneerRxTop500ExportCodes.PioneerRxUnavailable,
        PioneerRxTop500ExportCodes.ReportNavigationUnavailable,
        PioneerRxTop500ExportCodes.ReportWindowUnavailable,
        PioneerRxTop500ExportCodes.FilterSurfaceUnavailable,
        PioneerRxTop500ExportCodes.FilterVerificationFailed,
        PioneerRxTop500ExportCodes.ReportViewUnavailable,
        PioneerRxTop500ExportCodes.ExportControlUnavailable,
        PioneerRxTop500ExportCodes.ExportSaveDialogUntrusted,
        PioneerRxTop500ExportCodes.ExportSaveDialogInvalid,
        PioneerRxTop500ExportCodes.ExportDirectoryUnavailable,
        PioneerRxTop500ExportCodes.ExportTimedOut,
        PioneerRxTop500ExportCodes.ExportInvalid,
        PioneerRxTop500ExportCodes.Cancelled,
        PioneerRxTop500ExportCodes.UnexpectedFailure,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> KnownActuationBlockers = new[]
    {
        ActuationRejectionCodes.GateStateUnavailable,
        ActuationRejectionCodes.GateDisabled,
        ActuationRejectionCodes.GatePaused,
        ActuationRejectionCodes.GateDryRun,
        ActuationRejectionCodes.KillSwitchTripped,
        ActuationRejectionCodes.CompromiseDetected,
        ActuationRejectionCodes.PhiPatternDetected,
        ActuationRejectionCodes.LabelNotFound,
        ActuationRejectionCodes.ProcessNotAllowed,
        ActuationRejectionCodes.ProcessIdentityUntrusted,
        ActuationRejectionCodes.ForegroundNotTarget,
        ActuationRejectionCodes.TypeNotVerified,
        ActuationRejectionCodes.AppNotInAllowlist,
        ActuationRejectionCodes.MalformedRequest,
        ActuationRejectionCodes.ChordParseFailure,
        ActuationRejectionCodes.ExecutionException,
        ActuationRejectionCodes.CapabilityUnavailable,
        ActuationRejectionCodes.RemotePolicyMutationDenied,
        ActuationRejectionCodes.ElementNotFound,
        ActuationRejectionCodes.AssertMismatch,
    }.ToFrozenSet(StringComparer.Ordinal);

    private sealed record ExportRequestResult(
        PioneerRxTop500ExportResult? Receipt,
        string? ErrorCode)
    {
        internal static ExportRequestResult Ready(PioneerRxTop500ExportResult receipt) =>
            new(receipt, null);

        internal static ExportRequestResult Failed(string errorCode) =>
            new(null, errorCode);
    }

    private static bool ChunkIsExact(
        PioneerRxTop500ArtifactReadResult? chunk,
        PioneerRxTop500ExportResult receipt,
        long expectedOffset) => chunk is
        {
            ContractVersion: PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
            Success: true,
            Code: PioneerRxTop500ArtifactReadCodes.Ready,
            ChunkBase64: not null,
            WorkbookBytes: not null,
            WorkbookSha256: not null,
        } &&
        string.Equals(chunk.JobId, receipt.JobId, StringComparison.Ordinal) &&
        string.Equals(chunk.WorkbookSha256, receipt.WorkbookSha256, StringComparison.Ordinal) &&
        chunk.WorkbookBytes == receipt.WorkbookBytes &&
        chunk.Offset == expectedOffset &&
        chunk.NextOffset > expectedOffset &&
        chunk.NextOffset <= receipt.WorkbookBytes &&
        chunk.ChunkBase64.Length <= 40_000;

    private static bool ValidateBytes(byte[] bytes, PioneerRxTop500ExportResult receipt)
    {
        if (bytes.LongLength != receipt.WorkbookBytes) return false;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, receipt.WorkbookSha256, StringComparison.Ordinal)) return false;
        return DateOnly.TryParseExact(
                   receipt.CompletedOnThrough,
                   "MM/dd/yyyy",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out var runDate) &&
               PioneerRxTop500ExportWorkbookValidator.IsExact(bytes, runDate);
    }

    private static bool ResponseMatches(IpcResponse? response, IpcRequest request) =>
        response is
        {
            Status: IpcStatus.Ok,
            Data: not null,
            Error: null,
        } &&
        string.Equals(response.Id, request.Id, StringComparison.Ordinal) &&
        string.Equals(response.Command, request.Command, StringComparison.Ordinal);

    private static bool IsLowerHex(string value) =>
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool DirectoryTreeContainsReparsePoint(string root, string leaf)
    {
        var current = new DirectoryInfo(leaf);
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    rootPath,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                return false;
            current = current.Parent;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
