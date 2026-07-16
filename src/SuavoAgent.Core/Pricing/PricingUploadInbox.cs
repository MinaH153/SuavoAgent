using System.Text.Json;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

internal sealed record PricingUploadClaim(
    Guid Id,
    string WorkbookPath,
    string Sha256,
    long SizeBytes);

internal sealed record PricingUploadInboxReceipt(
    int Version,
    Guid UploadId,
    string Sha256,
    long SizeBytes,
    string State,
    string? TerminalStatus,
    string? ReasonCode,
    DateTimeOffset UpdatedAtUtc,
    string? ResultJobId = null,
    string? ResultPayloadDigest = null);

/// <summary>
/// Crash-safe opaque local intake. Filenames and receipts contain only UUIDs,
/// hashes, sizes, fixed states, and fixed reason codes; workbook text is never
/// serialized or logged.
/// </summary>
internal sealed class PricingUploadInbox
{
    private const int ReceiptVersion = 2;
    private const int MaxReceiptBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly string _root;
    private readonly Func<string, bool> _deleteFile;
    private readonly Func<Guid, AgentStateDb.PricingResultOutboxEntry?>? _findResultOutbox;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal PricingUploadInbox(
        string root,
        Func<string, bool>? deleteFile = null,
        Func<Guid, AgentStateDb.PricingResultOutboxEntry?>? findResultOutbox = null)
    {
        _root = Path.GetFullPath(root);
        _deleteFile = deleteFile ?? DeleteFile;
        _findResultOutbox = findResultOutbox;
        Directory.CreateDirectory(_root);
        if (OperatingSystem.IsWindows() &&
            (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("pricing_intake_reparse_forbidden");
        RecoverInterruptedClaims();
    }

    internal async Task ReconcileTemporaryFilesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temporaryFiles = Directory.EnumerateFiles(
                    _root, "*.tmp", SearchOption.TopDirectoryOnly)
                .Take(513)
                .ToArray();
            if (temporaryFiles.Length > 512)
                throw new IOException("pricing_upload_temp_count_exceeded");
            var staleBefore = DateTime.UtcNow - TimeSpan.FromMinutes(2);
            foreach (var path in temporaryFiles)
            {
                ct.ThrowIfCancellationRequested();
                if (File.GetLastWriteTimeUtc(path) > staleBefore) continue;
                DeleteRequired(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task StageAsync(
        IPricingUploadCloudClient cloud,
        PricingUploadDescriptor descriptor,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var receipt = ReadReceipt(descriptor.Id);
            if (receipt is not null)
            {
                AssertDescriptor(receipt, descriptor);
                if (receipt.State is "fetched" && File.Exists(ReadyPath(descriptor.Id)))
                {
                    AssertWorkbook(ReadyPath(descriptor.Id), descriptor);
                    await cloud.AckFetchedAsync(descriptor, ct).ConfigureAwait(false);
                }
                return;
            }

            var ready = ReadyPath(descriptor.Id);
            if (File.Exists(ready))
            {
                AssertWorkbook(ready, descriptor);
            }
            else
            {
                var temporary = Path.Combine(_root, $"{descriptor.Id:D}.{Guid.NewGuid():N}.tmp");
                await cloud.DownloadAsync(descriptor, temporary, ct).ConfigureAwait(false);
                File.Move(temporary, ready, overwrite: false);
            }

            WriteReceipt(new(
                ReceiptVersion,
                descriptor.Id,
                descriptor.Sha256,
                descriptor.SizeBytes,
                "fetched",
                null,
                null,
                DateTimeOffset.UtcNow,
                null,
                null));
            await cloud.AckFetchedAsync(descriptor, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<PricingUploadClaim?> TryClaimAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var receiptPath in Directory.EnumerateFiles(
                         _root, "*.receipt.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
            {
                var receipt = ReadReceiptPath(receiptPath);
                if (receipt.State != "fetched") continue;
                var ready = ReadyPath(receipt.UploadId);
                if (!File.Exists(ready))
                {
                    WriteTerminal(receipt, "failed", "pricing_execution_failed");
                    continue;
                }
                try
                {
                    AssertWorkbook(ready, Descriptor(receipt));
                }
                catch (PricingWorkbookContentException)
                {
                    WriteTerminal(receipt, "failed", "native_validation_failed");
                    continue;
                }
                var processing = ProcessingPath(receipt.UploadId);
                File.Move(ready, processing, overwrite: false);
                WriteReceipt(receipt with
                {
                    State = "processing",
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
                return new(receipt.UploadId, processing, receipt.Sha256, receipt.SizeBytes);
            }
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RecordOutcomeAsync(
        PricingUploadClaim claim,
        bool consumed,
        CancellationToken ct)
    {
        if (consumed)
            throw new InvalidOperationException(
                "pricing_result_acceptance_must_be_completed_from_outbox");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var receipt = ReadReceipt(claim.Id) ??
                throw new InvalidDataException("pricing_upload_receipt_missing");
            if (receipt.State != "processing" || receipt.Sha256 != claim.Sha256 ||
                receipt.SizeBytes != claim.SizeBytes)
                throw new InvalidDataException("pricing_upload_receipt_conflict");
            WriteTerminal(
                receipt,
                "failed",
                "pricing_execution_failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Binds a successfully executed workbook to its immutable result outbox
    /// row while cloud acceptance is pending. This state is never claimable for
    /// execution, so retries cannot rerun PioneerRx or replace the exact payload.
    /// </summary>
    internal async Task ReturnForResultSyncRetryAsync(
        PricingUploadClaim claim,
        string resultJobId,
        string resultPayloadDigest,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var receipt = ReadReceipt(claim.Id) ??
                throw new InvalidDataException("pricing_upload_receipt_missing");
            if (receipt.State != "processing" || receipt.Sha256 != claim.Sha256 ||
                receipt.SizeBytes != claim.SizeBytes)
                throw new InvalidDataException("pricing_upload_receipt_conflict");

            AssertWorkbook(ProcessingPath(claim.Id), Descriptor(receipt));
            AssertResultBinding(resultJobId, resultPayloadDigest);
            WriteReceipt(receipt with
            {
                State = "result_sync_pending",
                TerminalStatus = null,
                ReasonCode = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ResultJobId = resultJobId,
                ResultPayloadDigest = resultPayloadDigest,
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task CompleteAcceptedResultSyncAsync(
        Guid uploadId,
        string resultJobId,
        string resultPayloadDigest,
        bool executionOk,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AssertResultBinding(resultJobId, resultPayloadDigest);
            var outbox = _findResultOutbox?.Invoke(uploadId) ??
                throw new InvalidOperationException("pricing_result_acceptance_not_durable");
            if (outbox.State != "accepted" || outbox.JobId != resultJobId ||
                !string.Equals(
                    outbox.PayloadSha256, resultPayloadDigest, StringComparison.Ordinal))
                throw new InvalidOperationException("pricing_result_acceptance_conflict");

            var receipt = ReadReceipt(uploadId) ??
                throw new InvalidDataException("pricing_upload_receipt_missing");
            var expectedStatus = executionOk ? "consumed" : "failed";
            if (receipt.State == "terminal_pending" &&
                receipt.TerminalStatus == expectedStatus &&
                receipt.ResultJobId == resultJobId &&
                receipt.ResultPayloadDigest == resultPayloadDigest)
                return;
            if (receipt.State is not ("processing" or "result_sync_pending"))
                throw new InvalidDataException("pricing_upload_receipt_conflict");
            if (receipt.ResultJobId is not null && receipt.ResultJobId != resultJobId ||
                receipt.ResultPayloadDigest is not null &&
                receipt.ResultPayloadDigest != resultPayloadDigest)
                throw new InvalidDataException("pricing_upload_receipt_conflict");
            WriteTerminal(
                receipt with
                {
                    ResultJobId = resultJobId,
                    ResultPayloadDigest = resultPayloadDigest,
                },
                expectedStatus,
                executionOk ? null : "pricing_execution_failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task FlushTerminalReceiptsAsync(
        IPricingUploadCloudClient cloud,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var receiptPath in Directory.EnumerateFiles(
                         _root, "*.receipt.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
            {
                var receipt = ReadReceiptPath(receiptPath);
                if (receipt.State != "terminal_pending") continue;
                if (receipt.TerminalStatus == "consumed" ||
                    receipt.ResultJobId is not null ||
                    receipt.ResultPayloadDigest is not null)
                {
                    AssertResultBinding(
                        receipt.ResultJobId ?? "",
                        receipt.ResultPayloadDigest ?? "");
                    var outbox = _findResultOutbox?.Invoke(receipt.UploadId) ??
                        throw new InvalidOperationException(
                            "pricing_result_acceptance_not_durable");
                    if (outbox.State != "accepted" ||
                        outbox.JobId != receipt.ResultJobId ||
                        outbox.PayloadSha256 != receipt.ResultPayloadDigest)
                        throw new InvalidOperationException(
                            "pricing_result_acceptance_conflict");
                }
                DeleteRequired(ReadyPath(receipt.UploadId));
                DeleteRequired(ProcessingPath(receipt.UploadId));
                foreach (var derived in Directory.EnumerateFiles(
                             _root,
                             $"{receipt.UploadId:D}.processing-priced-*.xlsx",
                             SearchOption.TopDirectoryOnly))
                    DeleteRequired(derived);
                await cloud.AckLifecycleAsync(
                    receipt.UploadId,
                    receipt.TerminalStatus == "consumed",
                    receipt.ReasonCode,
                    ct).ConfigureAwait(false);
                DeleteRequired(receiptPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RecoverInterruptedClaims()
    {
        foreach (var receiptPath in Directory.EnumerateFiles(
                     _root, "*.receipt.json", SearchOption.TopDirectoryOnly))
        {
            var receipt = ReadReceiptPath(receiptPath);
            if (receipt.State != "processing") continue;
            var outbox = _findResultOutbox?.Invoke(receipt.UploadId);
            if (outbox is not null)
            {
                WriteReceipt(receipt with
                {
                    State = "result_sync_pending",
                    TerminalStatus = null,
                    ReasonCode = null,
                    ResultJobId = outbox.JobId,
                    ResultPayloadDigest = outbox.PayloadSha256,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                });
                continue;
            }

            var processing = ProcessingPath(receipt.UploadId);
            var ready = ReadyPath(receipt.UploadId);
            if (!File.Exists(processing))
            {
                WriteTerminal(receipt, "failed", "pricing_execution_failed");
                continue;
            }
            File.Move(processing, ready, overwrite: false);
            WriteReceipt(receipt with
            {
                State = "fetched",
                TerminalStatus = null,
                ReasonCode = null,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
    }

    private void WriteTerminal(
        PricingUploadInboxReceipt receipt,
        string status,
        string? reasonCode) =>
        WriteReceipt(receipt with
        {
            State = "terminal_pending",
            TerminalStatus = status,
            ReasonCode = reasonCode,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

    private void AssertWorkbook(string path, PricingUploadDescriptor descriptor)
    {
        var validation = PricingWorkbookContentPolicy.Validate(path);
        if (validation.Sha256 != descriptor.Sha256 ||
            validation.SizeBytes != descriptor.SizeBytes)
            throw new PricingWorkbookContentException("pricing_upload_native_validation_mismatch");
    }

    private static void AssertDescriptor(
        PricingUploadInboxReceipt receipt,
        PricingUploadDescriptor descriptor)
    {
        if (receipt.UploadId != descriptor.Id || receipt.Sha256 != descriptor.Sha256 ||
            receipt.SizeBytes != descriptor.SizeBytes)
            throw new InvalidDataException("pricing_upload_receipt_conflict");
    }

    private static PricingUploadDescriptor Descriptor(PricingUploadInboxReceipt receipt) =>
        new(receipt.UploadId, "Pricing workbook", receipt.SizeBytes, receipt.Sha256,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 1);

    private PricingUploadInboxReceipt? ReadReceipt(Guid id)
    {
        var path = ReceiptPath(id);
        return File.Exists(path) ? ReadReceiptPath(path) : null;
    }

    private static PricingUploadInboxReceipt ReadReceiptPath(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaxReceiptBytes)
            throw new InvalidDataException("pricing_upload_receipt_invalid");
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("pricing_upload_receipt_invalid");
            var names = root.EnumerateObject().Select(property => property.Name).ToHashSet();
            var v1Names = new[]
            {
                "version", "uploadId", "sha256", "sizeBytes", "state",
                "terminalStatus", "reasonCode", "updatedAtUtc",
            };
            var v2Names = v1Names.Concat(new[]
            {
                "resultJobId", "resultPayloadDigest",
            });
            if (!((names.Count == 8 && names.SetEquals(v1Names)) ||
                  (names.Count == 10 && names.SetEquals(v2Names))))
                throw new InvalidDataException("pricing_upload_receipt_invalid");
            var receipt = JsonSerializer.Deserialize<PricingUploadInboxReceipt>(bytes, JsonOptions) ??
                throw new InvalidDataException("pricing_upload_receipt_invalid");
            if (receipt.Version is not (1 or ReceiptVersion) || receipt.UploadId == Guid.Empty ||
                receipt.Sha256.Length != 64 ||
                receipt.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                receipt.SizeBytes is <= 0 or > PricingWorkbookContentPolicy.MaxArchiveBytes ||
                receipt.State is not ("fetched" or "processing" or "result_sync_pending" or "terminal_pending") ||
                (receipt.State == "terminal_pending" && receipt.TerminalStatus is not ("consumed" or "failed")) ||
                (receipt.TerminalStatus == "consumed" && receipt.ReasonCode is not null) ||
                (receipt.State == "result_sync_pending" &&
                    !IsResultBinding(receipt.ResultJobId, receipt.ResultPayloadDigest)) ||
                (receipt.TerminalStatus == "consumed" &&
                    !IsResultBinding(receipt.ResultJobId, receipt.ResultPayloadDigest)) ||
                (receipt.TerminalStatus == "failed" && receipt.ReasonCode is not
                    ("native_validation_failed" or "pricing_execution_failed")))
                throw new InvalidDataException("pricing_upload_receipt_invalid");
            return receipt;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("pricing_upload_receipt_invalid");
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private void WriteReceipt(PricingUploadInboxReceipt receipt)
    {
        receipt = receipt with { Version = ReceiptVersion };
        var destination = ReceiptPath(receipt.UploadId);
        var temporary = Path.Combine(_root, $"{receipt.UploadId:D}.{Guid.NewGuid():N}.receipt.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
            using (var stream = new FileStream(
                       temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            Array.Clear(bytes);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) DeleteRequired(temporary);
        }
    }

    private static void AssertResultBinding(string jobId, string payloadDigest)
    {
        if (!IsResultBinding(jobId, payloadDigest))
            throw new InvalidDataException("pricing_result_binding_invalid");
    }

    private static bool IsResultBinding(string? jobId, string? payloadDigest) =>
        !string.IsNullOrWhiteSpace(jobId) && jobId.Length <= 128 &&
        payloadDigest is { Length: 64 } &&
        payloadDigest.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private string ReadyPath(Guid id) => Path.Combine(_root, $"{id:D}.ready.xlsx");
    private string ProcessingPath(Guid id) => Path.Combine(_root, $"{id:D}.processing.xlsx");
    private string ReceiptPath(Guid id) => Path.Combine(_root, $"{id:D}.receipt.json");

    private void DeleteRequired(string path)
    {
        if (!_deleteFile(path) || File.Exists(path))
            throw new IOException("pricing_upload_local_cleanup_pending");
    }

    private static bool DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
