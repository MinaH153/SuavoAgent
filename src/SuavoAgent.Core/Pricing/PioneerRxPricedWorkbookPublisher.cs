using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.Pricing;

public sealed record PricedWorkbookPublicationResult(
    bool Published,
    string Code,
    string? WorkbookSha256 = null,
    long? WorkbookBytes = null);

public interface IPricedWorkbookPublisher
{
    Task<PricedWorkbookPublicationResult> PublishAsync(
        string commandId,
        string localWorkbookPath,
        CancellationToken ct);
}

/// <summary>
/// Returns a completed v3 package-cost workbook to the authenticated Helper in
/// bounded frames. Only integrity metadata and workbook bytes cross the local
/// pipe; the interactive user's filesystem path remains Helper-local.
/// </summary>
public sealed class PioneerRxPricedWorkbookPublisher : IPricedWorkbookPublisher
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IIpcCommandClient _commandClient;
    private readonly ILogger<PioneerRxPricedWorkbookPublisher> _logger;

    public PioneerRxPricedWorkbookPublisher(
        IIpcCommandClient commandClient,
        ILogger<PioneerRxPricedWorkbookPublisher> logger)
    {
        _commandClient = commandClient ?? throw new ArgumentNullException(nameof(commandClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PricedWorkbookPublicationResult> PublishAsync(
        string commandId,
        string localWorkbookPath,
        CancellationToken ct)
    {
        if (!PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(commandId) ||
            !TryValidateLocalWorkbook(localWorkbookPath, out var length))
            return Failed(PioneerRxPricedWorkbookPublicationCodes.SchemaMismatch);

        string sha256;
        try
        {
            sha256 = await HashAsync(localWorkbookPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.priced_workbook.hash_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return Failed(PioneerRxPricedWorkbookPublicationCodes.IntegrityMismatch);
        }

        var beginRequest = new PioneerRxPricedWorkbookBeginRequest(
            PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
            commandId,
            sha256,
            length);
        var begin = await SendAsync<PioneerRxPricedWorkbookBeginRequest,
                PioneerRxPricedWorkbookBeginResult>(
                IpcCommands.PioneerRxPricedWorkbookBegin,
                beginRequest,
                ct)
            .ConfigureAwait(false);
        if (!BeginIsExact(begin, beginRequest))
            return Failed(begin?.Code ??
                PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
        if (begin!.Published)
            return new PricedWorkbookPublicationResult(true, begin.Code, sha256, length);

        var uploadToken = begin.UploadToken!;
        long offset = begin.NextOffset;
        var buffer = new byte[PioneerRxPricedWorkbookPublicationContract.MaximumChunkBytes];
        try
        {
            await using var stream = new FileStream(
                localWorkbookPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = offset;
            while (offset < length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, checked((int)Math.Min(
                        buffer.Length,
                        length - offset))), ct)
                    .ConfigureAwait(false);
                if (count <= 0)
                    return Failed(PioneerRxPricedWorkbookPublicationCodes.IntegrityMismatch);
                var chunkRequest = new PioneerRxPricedWorkbookChunkRequest(
                    PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                    commandId,
                    uploadToken,
                    sha256,
                    length,
                    offset,
                    Convert.ToBase64String(buffer, 0, count));
                var chunk = await SendAsync<PioneerRxPricedWorkbookChunkRequest,
                        PioneerRxPricedWorkbookChunkResult>(
                        IpcCommands.PioneerRxPricedWorkbookChunk,
                        chunkRequest,
                        ct)
                    .ConfigureAwait(false);
                var expectedNextOffset = offset + count;
                if (chunk is not
                    {
                        ContractVersion:
                            PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                        Success: true,
                        Code: PioneerRxPricedWorkbookPublicationCodes.ChunkAccepted,
                    } ||
                    !string.Equals(chunk.JobId, commandId, StringComparison.Ordinal) ||
                    chunk.NextOffset != expectedNextOffset)
                    return Failed(chunk?.Code ??
                        PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
                offset = expectedNextOffset;
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.priced_workbook.upload_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return Failed(PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }

        var commitRequest = new PioneerRxPricedWorkbookCommitRequest(
            PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
            commandId,
            uploadToken,
            sha256,
            length);
        var commit = await SendAsync<PioneerRxPricedWorkbookCommitRequest,
                PioneerRxPricedWorkbookCommitResult>(
                IpcCommands.PioneerRxPricedWorkbookCommit,
                commitRequest,
                ct)
            .ConfigureAwait(false);
        return CommitIsExact(commit, commitRequest)
            ? new PricedWorkbookPublicationResult(true, commit!.Code, sha256, length)
            : Failed(commit?.Code ??
                PioneerRxPricedWorkbookPublicationCodes.PublicationFailed);
    }

    private async Task<TResult?> SendAsync<TRequest, TResult>(
        string command,
        TRequest payload,
        CancellationToken ct)
    {
        var request = new IpcRequest(
            Guid.NewGuid().ToString("N"),
            command,
            PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
            JsonSerializer.SerializeToElement(payload));
        var response = await _commandClient.SendAsync(request, RequestTimeout, ct)
            .ConfigureAwait(false);
        if (response is not
            {
                Status: IpcStatus.Ok,
                Error: null,
                Data: not null,
            } ||
            !string.Equals(response.Id, request.Id, StringComparison.Ordinal) ||
            !string.Equals(response.Command, request.Command, StringComparison.Ordinal))
            return default;
        try
        {
            return JsonSerializer.Deserialize<TResult>(response.Data.Value);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "core.priced_workbook.response_invalid exception_type={ExceptionType}",
                exception.GetType().Name);
            return default;
        }
    }

    private static bool BeginIsExact(
        PioneerRxPricedWorkbookBeginResult? result,
        PioneerRxPricedWorkbookBeginRequest request) =>
        result is
        {
            ContractVersion:
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
            Success: true,
            WorkbookSha256: not null,
            WorkbookBytes: not null,
        } &&
        string.Equals(result.JobId, request.JobId, StringComparison.Ordinal) &&
        string.Equals(result.DestinationLabel,
            PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
            StringComparison.Ordinal) &&
        string.Equals(result.WorkbookSha256, request.WorkbookSha256,
            StringComparison.Ordinal) &&
        result.WorkbookBytes == request.WorkbookBytes &&
        (result.Published
            ? result.Code == PioneerRxPricedWorkbookPublicationCodes.Published &&
              result.UploadToken is null &&
              result.NextOffset == request.WorkbookBytes
            : result.Code == PioneerRxPricedWorkbookPublicationCodes.UploadReady &&
              PioneerRxPricedWorkbookPublicationContract.IsLowerHex(
                  result.UploadToken, 32) &&
              result.NextOffset >= 0 &&
              result.NextOffset <= request.WorkbookBytes);

    private static bool CommitIsExact(
        PioneerRxPricedWorkbookCommitResult? result,
        PioneerRxPricedWorkbookCommitRequest request) =>
        result is
        {
            ContractVersion:
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
            Success: true,
            Code: PioneerRxPricedWorkbookPublicationCodes.Published,
            DataRows: PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows,
            WorkbookSha256: not null,
            WorkbookBytes: not null,
        } &&
        string.Equals(result.JobId, request.JobId, StringComparison.Ordinal) &&
        string.Equals(result.DestinationLabel,
            PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
            StringComparison.Ordinal) &&
        string.Equals(result.WorkbookSha256, request.ExpectedSha256,
            StringComparison.Ordinal) &&
        result.WorkbookBytes == request.ExpectedBytes;

    private static bool TryValidateLocalWorkbook(string path, out long length)
    {
        length = 0;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            if (!info.Exists ||
                !string.Equals(info.Extension, ".xlsx", StringComparison.OrdinalIgnoreCase) ||
                info.Length is <= 0 or >
                    PioneerRxPricedWorkbookPublicationContract.MaximumWorkbookBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            if (!PioneerRxPricedWorkbookSchemaValidator.IsExact(fullPath)) return false;
            length = info.Length;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static PricedWorkbookPublicationResult Failed(string code) =>
        new(false, code);
}
