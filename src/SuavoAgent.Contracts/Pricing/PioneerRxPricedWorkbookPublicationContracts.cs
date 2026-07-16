using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Fixed local-only contract for returning the completed package-cost workbook
/// to the interactive user's Documents folder. Callers cannot choose a path,
/// filename, schema, or row count.
/// </summary>
public static class PioneerRxPricedWorkbookPublicationContract
{
    public const int CurrentVersion = 1;
    public const long MaximumWorkbookBytes = 16 * 1024 * 1024;
    public const int MaximumChunkBytes = 24 * 1024;
    public const int ExpectedDataRows = PioneerRxTop500ReportRecipe.TopCount;
    public const string DestinationLabel = PioneerRxTop500ReportRecipe.DestinationLabel;

    public static readonly IReadOnlyList<string> ExpectedHeaders = Array.AsReadOnly(
    [
        "Rank",
        "Drug",
        "Strength",
        "NDC",
        "Cheapest Supplier",
        "Cost",
    ]);

    public static bool IsCanonicalJobId(string value) =>
        Guid.TryParseExact(value, "D", out _);

    public static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record PioneerRxPricedWorkbookBeginRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("workbookSha256")] string WorkbookSha256,
    [property: JsonPropertyName("workbookBytes")] long WorkbookBytes)
{
    public bool IsValid() =>
        ContractVersion == PioneerRxPricedWorkbookPublicationContract.CurrentVersion &&
        PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(JobId) &&
        PioneerRxPricedWorkbookPublicationContract.IsLowerHex(WorkbookSha256, 64) &&
        WorkbookBytes is > 0 and <=
            PioneerRxPricedWorkbookPublicationContract.MaximumWorkbookBytes;
}

public sealed record PioneerRxPricedWorkbookBeginResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("uploadToken")] string? UploadToken,
    [property: JsonPropertyName("published")] bool Published,
    [property: JsonPropertyName("nextOffset")] long NextOffset,
    [property: JsonPropertyName("destinationLabel")] string DestinationLabel,
    [property: JsonPropertyName("workbookSha256")] string? WorkbookSha256,
    [property: JsonPropertyName("workbookBytes")] long? WorkbookBytes)
{
    public static PioneerRxPricedWorkbookBeginResult Failed(string jobId, string code) => new(
        PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
        jobId,
        false,
        code,
        null,
        false,
        0,
        PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
        null,
        null);
}

public sealed record PioneerRxPricedWorkbookChunkRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("uploadToken")] string UploadToken,
    [property: JsonPropertyName("expectedSha256")] string ExpectedSha256,
    [property: JsonPropertyName("expectedBytes")] long ExpectedBytes,
    [property: JsonPropertyName("offset")] long Offset,
    [property: JsonPropertyName("chunkBase64")] string ChunkBase64)
{
    public bool IsValid() =>
        ContractVersion == PioneerRxPricedWorkbookPublicationContract.CurrentVersion &&
        PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(JobId) &&
        PioneerRxPricedWorkbookPublicationContract.IsLowerHex(UploadToken, 32) &&
        PioneerRxPricedWorkbookPublicationContract.IsLowerHex(ExpectedSha256, 64) &&
        ExpectedBytes is > 0 and <=
            PioneerRxPricedWorkbookPublicationContract.MaximumWorkbookBytes &&
        Offset >= 0 && Offset < ExpectedBytes &&
        ChunkBase64 is { Length: > 0 and <= 40_000 };
}

public sealed record PioneerRxPricedWorkbookChunkResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("nextOffset")] long NextOffset)
{
    public static PioneerRxPricedWorkbookChunkResult Failed(string jobId, string code) =>
        new(PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
            jobId, false, code, 0);
}

public sealed record PioneerRxPricedWorkbookCommitRequest(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("uploadToken")] string UploadToken,
    [property: JsonPropertyName("expectedSha256")] string ExpectedSha256,
    [property: JsonPropertyName("expectedBytes")] long ExpectedBytes)
{
    public bool IsValid() =>
        ContractVersion == PioneerRxPricedWorkbookPublicationContract.CurrentVersion &&
        PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(JobId) &&
        PioneerRxPricedWorkbookPublicationContract.IsLowerHex(UploadToken, 32) &&
        PioneerRxPricedWorkbookPublicationContract.IsLowerHex(ExpectedSha256, 64) &&
        ExpectedBytes is > 0 and <=
            PioneerRxPricedWorkbookPublicationContract.MaximumWorkbookBytes;
}

public sealed record PioneerRxPricedWorkbookCommitResult(
    [property: JsonPropertyName("contractVersion")] int ContractVersion,
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("destinationLabel")] string DestinationLabel,
    [property: JsonPropertyName("workbookSha256")] string? WorkbookSha256,
    [property: JsonPropertyName("workbookBytes")] long? WorkbookBytes,
    [property: JsonPropertyName("dataRows")] int? DataRows)
{
    public static PioneerRxPricedWorkbookCommitResult Failed(string jobId, string code) => new(
        PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
        jobId,
        false,
        code,
        PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
        null,
        null,
        null);
}

public static class PioneerRxPricedWorkbookPublicationCodes
{
    public const string UploadReady = "priced_workbook_upload_ready";
    public const string ChunkAccepted = "priced_workbook_chunk_accepted";
    public const string Published = "priced_workbook_published";
    public const string InvalidRequest = "priced_workbook_invalid_request";
    public const string DestinationUnavailable = "priced_workbook_destination_unavailable";
    public const string UploadUnavailable = "priced_workbook_upload_unavailable";
    public const string IntegrityMismatch = "priced_workbook_integrity_mismatch";
    public const string SchemaMismatch = "priced_workbook_schema_mismatch";
    public const string PublicationCollision = "priced_workbook_publication_collision";
    public const string PublicationFailed = "priced_workbook_publication_failed";
}
