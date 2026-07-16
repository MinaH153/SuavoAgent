using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using PioneerRxSim;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests.Workflows;

public sealed class PioneerRxWorkbookPublicationTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"priced-publication-{Guid.NewGuid():N}");

    public PioneerRxWorkbookPublicationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ChunkedUpload_PublishesExactWorkbookWithoutReturningAPath()
    {
        var documents = Directory.CreateDirectory(Path.Combine(_root, "Documents")).FullName;
        var source = WritePricedWorkbook("priced.xlsx");
        var bytes = await File.ReadAllBytesAsync(source);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var jobId = Guid.NewGuid().ToString("D");
        using var store = new PioneerRxPricedWorkbookStore(documents);

        var begin = await store.BeginAsync(
            new PioneerRxPricedWorkbookBeginRequest(1, jobId, sha, bytes.LongLength),
            CancellationToken.None);
        Assert.True(begin.Success);
        Assert.False(begin.Published);
        Assert.Matches("^[0-9a-f]{32}$", begin.UploadToken!);

        long offset = 0;
        while (offset < bytes.LongLength)
        {
            var count = (int)Math.Min(4096, bytes.LongLength - offset);
            var chunk = await store.AppendAsync(
                new PioneerRxPricedWorkbookChunkRequest(
                    1,
                    jobId,
                    begin.UploadToken!,
                    sha,
                    bytes.LongLength,
                    offset,
                    Convert.ToBase64String(bytes, (int)offset, count)),
                CancellationToken.None);
            Assert.True(chunk.Success, chunk.Code);
            offset = chunk.NextOffset;
        }

        var commit = await store.CommitAsync(
            new PioneerRxPricedWorkbookCommitRequest(
                1,
                jobId,
                begin.UploadToken!,
                sha,
                bytes.LongLength),
            CancellationToken.None);

        Assert.True(commit.Success, commit.Code);
        Assert.Equal(500, commit.DataRows);
        Assert.True(store.TryResolvePublishedPath(jobId, out var publishedPath));
        Assert.True(PioneerRxPricedWorkbookSchemaValidator.IsExact(publishedPath!));
        var serialized = JsonSerializer.Serialize(commit);
        Assert.DoesNotContain(documents, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("path", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentBegin_ReusesExactSession_AndDoesNotDeleteActiveUpload()
    {
        var documents = Directory.CreateDirectory(Path.Combine(_root, "ConcurrentDocuments")).FullName;
        var source = WritePricedWorkbook("concurrent.xlsx");
        var bytes = await File.ReadAllBytesAsync(source);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var jobId = Guid.NewGuid().ToString("D");
        var request = new PioneerRxPricedWorkbookBeginRequest(1, jobId, sha, bytes.LongLength);
        using var store = new PioneerRxPricedWorkbookStore(documents);

        var results = await Task.WhenAll(
            store.BeginAsync(request, CancellationToken.None),
            store.BeginAsync(request, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Success, result.Code));
        Assert.Equal(results[0].UploadToken, results[1].UploadToken);
        var firstChunk = await store.AppendAsync(
            new PioneerRxPricedWorkbookChunkRequest(
                1,
                jobId,
                results[0].UploadToken!,
                sha,
                bytes.LongLength,
                0,
                Convert.ToBase64String(bytes, 0, 1024)),
            CancellationToken.None);
        Assert.True(firstChunk.Success, firstChunk.Code);

        var collision = await store.BeginAsync(
            request with { WorkbookSha256 = new string('0', 64) },
            CancellationToken.None);
        Assert.False(collision.Success);
        Assert.Equal(PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable, collision.Code);
        var resumed = await store.BeginAsync(request, CancellationToken.None);
        Assert.True(resumed.Success);
        Assert.Equal(1024, resumed.NextOffset);
    }

    [Fact]
    public async Task FormulaWorkbook_IsRejectedBeforePublication()
    {
        var documents = Directory.CreateDirectory(Path.Combine(_root, "FormulaDocuments")).FullName;
        var source = WritePricedWorkbook("formula-source.xlsx");
        var formulaPath = Path.Combine(_root, "formula.xlsx");
        using (var workbook = new XLWorkbook(source))
        {
            workbook.Worksheet("Pricing").Cell(2, 6).FormulaA1 = "=1+1";
            workbook.SaveAs(formulaPath);
        }
        var bytes = await File.ReadAllBytesAsync(formulaPath);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var jobId = Guid.NewGuid().ToString("D");
        using var store = new PioneerRxPricedWorkbookStore(documents);
        var begin = await store.BeginAsync(
            new PioneerRxPricedWorkbookBeginRequest(1, jobId, sha, bytes.LongLength),
            CancellationToken.None);
        Assert.True(begin.Success);
        await UploadAllAsync(store, begin, bytes, sha, jobId);

        var commit = await store.CommitAsync(
            new PioneerRxPricedWorkbookCommitRequest(
                1, jobId, begin.UploadToken!, sha, bytes.LongLength),
            CancellationToken.None);

        Assert.False(commit.Success);
        Assert.Equal(PioneerRxPricedWorkbookPublicationCodes.SchemaMismatch, commit.Code);
        Assert.False(store.TryResolvePublishedPath(jobId, out _));
    }

    [Fact]
    public async Task SemanticWatcher_IgnoresConcurrentUnrelatedWorkbook_WithoutDeletingIt()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        var watcher = new StableXlsxExportWatcher(
            downloads,
            pollInterval: TimeSpan.FromMilliseconds(5),
            stabilityInterval: TimeSpan.FromMilliseconds(10));
        Assert.True(watcher.TryCaptureBaseline(out var baseline));
        var started = DateTimeOffset.UtcNow;
        var unrelated = Path.Combine(downloads, "unrelated-budget.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.AddWorksheet("Budget").Cell("A1").Value = "unrelated";
            workbook.SaveAs(unrelated);
        }
        var wait = watcher.WaitAsync(
            baseline!,
            started,
            TimeSpan.FromSeconds(4),
            CancellationToken.None,
            export => PioneerRxTop500ExportWorkbookValidator.IsExact(
                export.FullPath,
                new DateOnly(2026, 7, 15)));
        await Task.Delay(75);
        var actual = SyntheticTop500XlsxWriter.Write(downloads, Now);

        var result = await wait;

        Assert.True(result.Success);
        Assert.Equal(Path.GetFullPath(actual), result.Export!.FullPath);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Begin_RemovesExpiredOrphanPartialButPreservesFinalWorkbook()
    {
        var documents = Directory.CreateDirectory(
            Path.Combine(_root, "CleanupDocuments")).FullName;
        var reports = Directory.CreateDirectory(
            Path.Combine(documents, "SuavoAgent Reports")).FullName;
        var orphanJobId = Guid.NewGuid().ToString("D");
        var orphan = Path.Combine(
            reports,
            $".SuavoAgent-Top-500-Priced-{orphanJobId}-{new string('a', 32)}.partial.xlsx");
        await File.WriteAllTextAsync(orphan, "orphan");
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
        var final = Path.Combine(
            reports,
            $"SuavoAgent-Top-500-Priced-{orphanJobId}.xlsx");
        await File.WriteAllTextAsync(final, "keep");
        File.SetLastWriteTimeUtc(final, DateTime.UtcNow.AddDays(-2));
        using var store = new PioneerRxPricedWorkbookStore(documents);

        var begin = await store.BeginAsync(
            new PioneerRxPricedWorkbookBeginRequest(
                1,
                Guid.NewGuid().ToString("D"),
                new string('0', 64),
                10),
            CancellationToken.None);

        Assert.True(begin.Success, begin.Code);
        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task Begin_ExpiresAbandonedLiveSessionAndStartsFreshUpload()
    {
        var documents = Directory.CreateDirectory(
            Path.Combine(_root, "SessionExpiryDocuments")).FullName;
        var clock = new MutableTimeProvider(Now);
        using var store = new PioneerRxPricedWorkbookStore(documents, clock);
        var request = new PioneerRxPricedWorkbookBeginRequest(
            1,
            Guid.NewGuid().ToString("D"),
            new string('0', 64),
            10);

        var first = await store.BeginAsync(request, CancellationToken.None);
        Assert.True(first.Success, first.Code);
        clock.Advance(TimeSpan.FromHours(2));
        var restarted = await store.BeginAsync(request, CancellationToken.None);

        Assert.True(restarted.Success, restarted.Code);
        Assert.NotEqual(first.UploadToken, restarted.UploadToken);
        Assert.Equal(0, restarted.NextOffset);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(documents, "SuavoAgent Reports"),
            "*.partial.xlsx",
            SearchOption.TopDirectoryOnly));
    }

    private async Task UploadAllAsync(
        PioneerRxPricedWorkbookStore store,
        PioneerRxPricedWorkbookBeginResult begin,
        byte[] bytes,
        string sha,
        string jobId)
    {
        long offset = begin.NextOffset;
        while (offset < bytes.LongLength)
        {
            var count = (int)Math.Min(4096, bytes.LongLength - offset);
            var chunk = await store.AppendAsync(
                new PioneerRxPricedWorkbookChunkRequest(
                    1,
                    jobId,
                    begin.UploadToken!,
                    sha,
                    bytes.LongLength,
                    offset,
                    Convert.ToBase64String(bytes, (int)offset, count)),
                CancellationToken.None);
            Assert.True(chunk.Success, chunk.Code);
            offset = chunk.NextOffset;
        }
    }

    private string WritePricedWorkbook(string name)
    {
        var path = Path.Combine(_root, name);
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Pricing");
        var headers = PioneerRxPricedWorkbookPublicationContract.ExpectedHeaders;
        for (var column = 1; column <= headers.Count; column++)
            sheet.Cell(1, column).Value = headers[column - 1];
        for (var rank = 1; rank <= 500; rank++)
        {
            var row = rank + 1;
            sheet.Cell(row, 1).Value = rank;
            sheet.Cell(row, 2).Value = $"Drug {rank}";
            sheet.Cell(row, 3).Value = "1 mg";
            sheet.Cell(row, 4).SetValue((10_000_000_000L + rank).ToString());
            sheet.Cell(row, 5).Value = rank % 10 == 0 ? "Needs review" : "Supplier";
            if (rank % 10 != 0) sheet.Cell(row, 6).Value = 1m + rank / 100m;
        }
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan duration) => _now += duration;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
