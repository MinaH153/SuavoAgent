using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingUploadInboxTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_pricing_inbox_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public PricingUploadInboxTests()
    {
        Directory.CreateDirectory(_root);
        _db = new AgentStateDb(Path.Combine(_root, "state.db"));
    }

    [Fact]
    public async Task StageClaimConsume_UsesOpaqueFilesAndDurableReceipts()
    {
        var source = Workbook();
        var descriptor = Describe(source);
        var cloud = new FakeCloud(source);
        var inbox = Inbox(Path.Combine(_root, "intake"));

        await inbox.StageAsync(cloud, descriptor, CancellationToken.None);

        Assert.Equal(1, cloud.FetchedAcks);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(_root, "intake")),
            path => path.Contains(Path.GetFileName(source), StringComparison.Ordinal));
        var claim = await inbox.TryClaimAsync(CancellationToken.None);
        Assert.NotNull(claim);
        Assert.EndsWith(".processing.xlsx", claim!.WorkbookPath, StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(
                Path.GetDirectoryName(claim.WorkbookPath)!,
                $"{descriptor.Id:D}.processing-priced-20260712-180000.xlsx"),
            "derived");

        var outbox = _db.StagePricingResultPayload(
            "job-accepted", null, claim.Id,
            CompletedEmptyPayload(), 0, true);
        _db.MarkPricingResultPayloadAccepted(
            outbox.JobId, outbox.PayloadSha256, 0, "pricing_result_upload_accepted",
            $"{{\"accepted\":true,\"jobId\":\"{outbox.JobId}\",\"recorded\":0}}",
            RemoteCommandTrust.CommandV1KeyId,
            Convert.ToBase64String(new byte[64]));
        await inbox.CompleteAcceptedResultSyncAsync(
            claim.Id, outbox.JobId, outbox.PayloadSha256, true,
            CancellationToken.None);
        _db.MarkPricingResultSourceFinalized(
            claim.Id, outbox.JobId, outbox.PayloadSha256);
        await inbox.FlushTerminalReceiptsAsync(cloud, CancellationToken.None);

        Assert.Equal((descriptor.Id, true, (string?)null), cloud.TerminalAcks.Single());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "intake")));
    }

    [Fact]
    public async Task Constructor_ReturnsInterruptedPreOutboxClaimForSafeExecutionRetry()
    {
        var source = Workbook();
        var descriptor = Describe(source);
        var cloud = new FakeCloud(source);
        var intake = Path.Combine(_root, "intake");
        var inbox = Inbox(intake);
        await inbox.StageAsync(cloud, descriptor, CancellationToken.None);
        Assert.NotNull(await inbox.TryClaimAsync(CancellationToken.None));

        var recovered = Inbox(intake);
        var retry = Assert.IsType<PricingUploadClaim>(
            await recovered.TryClaimAsync(CancellationToken.None));

        Assert.Equal(descriptor.Id, retry.Id);
        Assert.Empty(cloud.TerminalAcks);
        Assert.True(File.Exists(retry.WorkbookPath));
    }

    [Fact]
    public async Task Stage_ReplaysFetchedAckAfterCrashWithoutDownloadingAgain()
    {
        var source = Workbook();
        var descriptor = Describe(source);
        var cloud = new FakeCloud(source);
        var intake = Path.Combine(_root, "intake");
        var inbox = Inbox(intake);

        await inbox.StageAsync(cloud, descriptor, CancellationToken.None);
        await inbox.StageAsync(cloud, descriptor, CancellationToken.None);

        Assert.Equal(1, cloud.Downloads);
        Assert.Equal(2, cloud.FetchedAcks);
    }

    [Fact]
    public async Task ResultSyncFailure_RetainsWorkbookAndDurableRetryClaim()
    {
        var source = Workbook();
        var descriptor = Describe(source);
        var cloud = new FakeCloud(source);
        var intake = Path.Combine(_root, "intake");
        var inbox = Inbox(intake);
        await inbox.StageAsync(cloud, descriptor, CancellationToken.None);
        var first = Assert.IsType<PricingUploadClaim>(
            await inbox.TryClaimAsync(CancellationToken.None));

        var outbox = _db.StagePricingResultPayload(
            "job-pending", null, first.Id,
            CompletedEmptyPayload(), 0, true);
        await inbox.ReturnForResultSyncRetryAsync(
            first, outbox.JobId, outbox.PayloadSha256, CancellationToken.None);

        Assert.True(File.Exists(first.WorkbookPath));
        Assert.Empty(cloud.TerminalAcks);
        var recovered = Inbox(intake);
        Assert.Null(await recovered.TryClaimAsync(CancellationToken.None));
        Assert.True(File.Exists(first.WorkbookPath));

        _db.MarkPricingResultPayloadAccepted(
            outbox.JobId, outbox.PayloadSha256, 0, "pricing_result_upload_accepted",
            $"{{\"accepted\":true,\"jobId\":\"{outbox.JobId}\",\"recorded\":0}}",
            RemoteCommandTrust.CommandV1KeyId,
            Convert.ToBase64String(new byte[64]));
        await recovered.CompleteAcceptedResultSyncAsync(
            first.Id, outbox.JobId, outbox.PayloadSha256, true,
            CancellationToken.None);
        _db.MarkPricingResultSourceFinalized(
            first.Id, outbox.JobId, outbox.PayloadSha256);
        await recovered.FlushTerminalReceiptsAsync(cloud, CancellationToken.None);
        Assert.Empty(Directory.EnumerateFiles(intake));
    }

    [Fact]
    public async Task FlushTerminal_RetainsReceiptAndRetriesWhenLocalDeletionFails()
    {
        var source = Workbook();
        var descriptor = Describe(source);
        var cloud = new FakeCloud(source);
        var intake = Path.Combine(_root, "intake");
        var rejectOnce = true;
        bool DeleteWithFailure(string path)
        {
            if (rejectOnce && path.EndsWith(".processing.xlsx", StringComparison.Ordinal))
            {
                rejectOnce = false;
                return false;
            }
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        var inbox = Inbox(intake, DeleteWithFailure);
        await inbox.StageAsync(cloud, descriptor, CancellationToken.None);
        var claim = Assert.IsType<PricingUploadClaim>(
            await inbox.TryClaimAsync(CancellationToken.None));
        var outbox = _db.StagePricingResultPayload(
            "job-delete", null, claim.Id,
            CompletedEmptyPayload(), 0, true);
        _db.MarkPricingResultPayloadAccepted(
            outbox.JobId, outbox.PayloadSha256, 0, "pricing_result_upload_accepted",
            $"{{\"accepted\":true,\"jobId\":\"{outbox.JobId}\",\"recorded\":0}}",
            RemoteCommandTrust.CommandV1KeyId,
            Convert.ToBase64String(new byte[64]));
        await inbox.CompleteAcceptedResultSyncAsync(
            claim.Id, outbox.JobId, outbox.PayloadSha256, true,
            CancellationToken.None);
        _db.MarkPricingResultSourceFinalized(
            claim.Id, outbox.JobId, outbox.PayloadSha256);

        await Assert.ThrowsAsync<IOException>(() =>
            inbox.FlushTerminalReceiptsAsync(cloud, CancellationToken.None));

        Assert.Empty(cloud.TerminalAcks);
        Assert.True(File.Exists(claim.WorkbookPath));
        Assert.Single(Directory.EnumerateFiles(intake, "*.receipt.json"));

        await inbox.FlushTerminalReceiptsAsync(cloud, CancellationToken.None);

        Assert.Single(cloud.TerminalAcks);
        Assert.Empty(Directory.EnumerateFiles(intake));
    }

    [Fact]
    public async Task ReconcileTemporaryFiles_RetriesStaleCrashRemnantsExplicitly()
    {
        var intake = Path.Combine(_root, "intake");
        var rejectOnce = true;
        bool DeleteWithFailure(string path)
        {
            if (rejectOnce)
            {
                rejectOnce = false;
                return false;
            }
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        var inbox = new PricingUploadInbox(intake, DeleteWithFailure);
        var remnant = Path.Combine(intake, $"{Guid.NewGuid():D}.receipt.tmp");
        File.WriteAllText(remnant, "opaque-remnant");
        File.SetLastWriteTimeUtc(remnant, DateTime.UtcNow - TimeSpan.FromMinutes(3));

        await Assert.ThrowsAsync<IOException>(() =>
            inbox.ReconcileTemporaryFilesAsync(CancellationToken.None));
        Assert.True(File.Exists(remnant));

        await inbox.ReconcileTemporaryFilesAsync(CancellationToken.None);

        Assert.False(File.Exists(remnant));
    }

    private string Workbook()
    {
        var path = Path.Combine(_root, "operator-source.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Pricing");
        sheet.Cell("A1").Value = "NDC";
        sheet.Cell("B1").Value = "Drug Name";
        sheet.Cell("C1").Value = "Price";
        sheet.Cell("A2").Value = "00000000000";
        sheet.Cell("B2").Value = "Example";
        sheet.Cell("C2").Value = 1.25;
        workbook.SaveAs(path);
        return path;
    }

    private static PricingUploadDescriptor Describe(string path)
    {
        using var stream = File.OpenRead(path);
        return new(
            Guid.NewGuid(),
            "Pricing workbook",
            stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            1);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string CompletedEmptyPayload() => JsonSerializer.Serialize(new
    {
        commandId = (string?)null,
        approvalId = "11111111-1111-4111-8111-111111111111",
        grantDigest = new string('a', 64),
        status = PricingJobStatus.Completed,
        mode = "sql",
        costBasis = PricingApprovalContract.CostPerUnitBasis,
        totalItems = 0,
        completedItems = 0,
        failedItems = 0,
        omittedInvalidItems = 0,
        omittedSelectorObservations = 0,
        items = Array.Empty<object>(),
    });

    private PricingUploadInbox Inbox(
        string path,
        Func<string, bool>? deleteFile = null) =>
        new(path, deleteFile, _db.GetPricingResultOutboxBySource);

    private sealed class FakeCloud(string sourcePath) : IPricingUploadCloudClient
    {
        internal int Downloads { get; private set; }
        internal int FetchedAcks { get; private set; }
        internal List<(Guid Id, bool Consumed, string? Reason)> TerminalAcks { get; } = [];

        public Task<IReadOnlyList<PricingUploadDescriptor>> PollAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PricingUploadDescriptor>>([]);

        public async Task DownloadAsync(
            PricingUploadDescriptor descriptor,
            string temporaryPath,
            CancellationToken ct)
        {
            Downloads++;
            await using var input = File.OpenRead(sourcePath);
            await using var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough | FileOptions.Asynchronous);
            await input.CopyToAsync(output, ct);
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
        }

        public Task AckFetchedAsync(PricingUploadDescriptor descriptor, CancellationToken ct)
        {
            FetchedAcks++;
            return Task.CompletedTask;
        }

        public Task AckLifecycleAsync(
            Guid id,
            bool consumed,
            string? reasonCode,
            CancellationToken ct)
        {
            TerminalAcks.Add((id, consumed, reasonCode));
            return Task.CompletedTask;
        }
    }
}
