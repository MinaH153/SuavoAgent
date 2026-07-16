using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Fixed request for the reviewed PioneerRx Top-500 export workflow. The caller
/// cannot supply selectors, captions, filter values, a date range, or an output
/// path. Helper derives the current local run date and applies the only approved
/// recipe below.
/// </summary>
public sealed record PioneerRxTop500ExportRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId)
{
    public const int CurrentContractVersion = 1;

    public bool IsValid() =>
        ContractVersion == CurrentContractVersion &&
        Guid.TryParse(JobId, out _);
}

/// <summary>
/// Local-only receipt returned across the authenticated Core-to-Helper pipe.
/// The filesystem path remains inside Helper; Core receives only an opaque
/// artifact token plus integrity metadata. No report contents, screen text,
/// patient identifiers, or filter result rows cross this boundary.
/// </summary>
public sealed record PioneerRxTop500ExportResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("blockerCode")] string? BlockerCode,
    [property: JsonPropertyName("artifactToken")] string? ArtifactToken,
    [property: JsonPropertyName("destinationLabel")] string DestinationLabel,
    [property: JsonPropertyName("workbookSha256")] string? WorkbookSha256,
    [property: JsonPropertyName("workbookBytes")] long? WorkbookBytes,
    [property: JsonPropertyName("completedOnFrom")] string CompletedOnFrom,
    [property: JsonPropertyName("completedOnThrough")] string CompletedOnThrough,
    [property: JsonPropertyName("topCount")] int TopCount)
{
    public static PioneerRxTop500ExportResult Failed(
        string jobId,
        string code,
        DateOnly start,
        DateOnly end,
        string? blockerCode = null) => new(
            PioneerRxTop500ExportRequest.CurrentContractVersion,
            jobId,
            false,
            code,
            blockerCode,
            null,
            PioneerRxTop500ReportRecipe.RawArtifactLabel,
            null,
            null,
            PioneerRxTop500ReportRecipe.FormatDate(start),
            PioneerRxTop500ReportRecipe.FormatDate(end),
            PioneerRxTop500ReportRecipe.TopCount);
}

/// <summary>
/// The video-confirmed, non-configurable report recipe. These values are kept
/// in the shared contract so Core, Helper, the simulator, and tests cannot
/// silently drift onto different filters.
/// </summary>
public static class PioneerRxTop500ReportRecipe
{
    public const string DrugClass = "Rx";
    public const string BrandGeneric = "Generic";
    public const string DeaSchedule = "No Schedule";
    public const string RxTransaction = "Removed From Inventory";
    public const string ReportType = "Top X Most Dispensed";
    public const int TopCount = 500;
    public const string RawArtifactLabel = "Protected local staging";
    public const string DestinationLabel = "Documents/SuavoAgent Reports";

    public static readonly IReadOnlyList<string> IncludedStatuses = Array.AsReadOnly(
    [
        "Completed",
        "Out for Delivery",
        "To Be Put in Bin",
        "Waiting for Central Fill",
        "Waiting for Check",
        "Waiting for Delivery",
        "Waiting for Fill",
        "Waiting for Pick up",
    ]);

    public static DateOnly StartFor(DateOnly runDate) => new(runDate.Year, 1, 1);

    public static string FormatDate(DateOnly value) =>
        value.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
}

public static class PioneerRxTop500ExportCodes
{
    public const string ExportReady = "top500_export_ready";
    public const string InvalidRequest = "top500_invalid_request";
    public const string ActuationGateClosed = "top500_actuation_gate_closed";
    public const string PioneerRxUnavailable = "top500_pioneerrx_unavailable";
    public const string ReportNavigationUnavailable = "top500_report_navigation_unavailable";
    public const string ReportWindowUnavailable = "top500_report_window_unavailable";
    public const string FilterSurfaceUnavailable = "top500_filter_surface_unavailable";
    public const string FilterVerificationFailed = "top500_filter_verification_failed";
    public const string ReportViewUnavailable = "top500_report_view_unavailable";
    public const string ExportControlUnavailable = "top500_export_control_unavailable";
    public const string ExportSaveDialogUntrusted = "top500_export_save_dialog_untrusted";
    public const string ExportSaveDialogInvalid = "top500_export_save_dialog_invalid";
    public const string ExportDirectoryUnavailable = "top500_export_directory_unavailable";
    public const string ExportTimedOut = "top500_export_timed_out";
    public const string ExportInvalid = "top500_export_invalid";
    public const string Cancelled = "top500_cancelled";
    public const string UnexpectedFailure = "top500_unexpected_failure";
}

/// <summary>
/// Fixed structural identifiers for the reviewed report surface. Exact visible
/// names remain the fallback for PioneerRx builds that do not expose IDs.
/// </summary>
public static class PioneerRxTop500ReportSurface
{
    public const string SurfaceHeader = "Rx Transaction Search";
    public const string DirectOpenReport = "Find Rx";
    public const string GlobalSearchMenu = "Search";
    public const string OpenReportMenu = "Rx Binoculars";
    public const string RxTab = "Rx";
    public const string RxTabId = "tabRx";
    public const string DispensedItemTab = "Dispensed Item";
    public const string DispensedItemTabId = "tabDispensedItem";
    public const string CompletedFromId = "dtCompletedOnFrom";
    public const string CompletedFromHelp = "Completed On From";
    public const string CompletedThroughId = "dtCompletedOnThrough";
    public const string CompletedThroughHelp = "Completed On Through";
    public const string DrugClassId = "cboDrugClass";
    public const string DrugClassHelp = "Drug Class";
    public const string BrandGenericId = "cboBrandGeneric";
    public const string BrandGenericHelp = "Brand/Generic";
    public const string DeaScheduleId = "cboDeaSchedule";
    public const string DeaScheduleHelp = "DEA Schedule";
    public const string RxTransactionId = "cboRxTransaction";
    public const string RxTransactionHelp = "Rx Transaction";
    public const string StatusGroupId = "grpRxStatuses";
    public const string StatusGroupName = "Rx Status";
    public const string ReportsMenu = "Reports";
    public const string ReportEntry = "Top X Most Dispensed";
    public const string ParametersTitle = "Report Parameters";
    public const string TopCountId = "txtTopCount";
    public const string TopCountHelp = "Top X";
    public const string ViewButtonId = "btnView";
    public const string ViewButtonName = "View - F12";
    public const string ViewerTitle = "Top X Most Dispensed Report";
    public const string ViewerContentTitle = "Top 500 Most Dispensed Rx Items";
    public const string ViewerFirstPage = "1/18";
    public const string ExcelButtonId = "btnExcel";
    public const string ExcelButtonName = "Excel";
    public const string SaveAsTitle = "Save As";
    public const string SaveAsFileNameId = "1001";
    public const string SaveAsFileNameHelp = "File name:";
    public const string SaveAsButtonId = "1";
    public const string SaveAsButtonName = "Save";
}

public sealed record PioneerRxTop500ExportProgress(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("processed")] int Processed,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("needsReview")] int NeedsReview,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt);

public sealed record PioneerRxTop500ProgressReceipt(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("accepted")] bool Accepted);

public static class PioneerRxTop500ExportStages
{
    // Sequence 1 (waiting_to_start) is emitted by Core when the signed command
    // is admitted. Helper owns the next two truthful UI boundaries.
    public const int GeneratingReportSequence = 2;
    public const string GeneratingReport = "generating_report";
    public const int ExportingReportSequence = 3;
    public const string ExportingReport = "exporting_report";
}

/// <summary>
/// Second, authenticated local IPC step. Core returns the opaque receipt to
/// Helper and asks for the bounded workbook bytes. The user-visible filesystem
/// path never crosses IPC and can never enter cloud payloads or logs.
/// </summary>
public sealed record PioneerRxTop500ArtifactReadRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("artifactToken")] string ArtifactToken,
    [property: JsonPropertyName("expectedSha256")] string ExpectedSha256,
    [property: JsonPropertyName("expectedBytes")] long ExpectedBytes,
    [property: JsonPropertyName("offset")] long Offset)
{
    public const int CurrentContractVersion = 1;
    public const long MaximumWorkbookBytes = 16 * 1024 * 1024;

    public bool IsValid() =>
        ContractVersion == CurrentContractVersion &&
        Guid.TryParse(JobId, out _) &&
        ArtifactToken is { Length: 32 } &&
        ArtifactToken.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        ExpectedSha256 is { Length: 64 } &&
        ExpectedSha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        ExpectedBytes is > 0 and <= MaximumWorkbookBytes &&
        Offset >= 0 && Offset < ExpectedBytes;
}

public sealed record PioneerRxTop500ArtifactReadResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("chunkBase64")] string? ChunkBase64,
    [property: JsonPropertyName("offset")] long Offset,
    [property: JsonPropertyName("nextOffset")] long NextOffset,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("workbookSha256")] string? WorkbookSha256,
    [property: JsonPropertyName("workbookBytes")] long? WorkbookBytes)
{
    public static PioneerRxTop500ArtifactReadResult Failed(string jobId, string code) =>
        new(PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
            jobId, false, code, null, 0, 0, false, null, null);
}

public static class PioneerRxTop500ArtifactReadCodes
{
    public const string Ready = "top500_artifact_ready";
    public const string InvalidRequest = "top500_artifact_invalid_request";
    public const string NotFound = "top500_artifact_not_found";
    public const string IntegrityMismatch = "top500_artifact_integrity_mismatch";
    public const string Unavailable = "top500_artifact_unavailable";
}
