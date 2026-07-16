using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FlaUI.Core.Definitions;
using PioneerRxSim;
using Serilog;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests.Workflows;

public sealed class PioneerRxTop500ExportBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"suavo-top500-{Guid.NewGuid():N}");

    public PioneerRxTop500ExportBoundaryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Recipe_IsTheExactVideoConfirmedFixedSet()
    {
        Assert.Equal("Rx", PioneerRxTop500ReportRecipe.DrugClass);
        Assert.Equal("Generic", PioneerRxTop500ReportRecipe.BrandGeneric);
        Assert.Equal("No Schedule", PioneerRxTop500ReportRecipe.DeaSchedule);
        Assert.Equal("Removed From Inventory", PioneerRxTop500ReportRecipe.RxTransaction);
        Assert.Equal("Top X Most Dispensed", PioneerRxTop500ReportRecipe.ReportType);
        Assert.Equal(500, PioneerRxTop500ReportRecipe.TopCount);
        Assert.Equal(
        [
            "Completed",
            "Out for Delivery",
            "To Be Put in Bin",
            "Waiting for Central Fill",
            "Waiting for Check",
            "Waiting for Delivery",
            "Waiting for Fill",
            "Waiting for Pick up",
        ], PioneerRxTop500ReportRecipe.IncludedStatuses);
    }

    [Fact]
    public void Surface_UsesEmbeddedRxTransactionSearchAndToolbarNavigation()
    {
        Assert.Equal("Rx Transaction Search", PioneerRxTop500ReportSurface.SurfaceHeader);
        Assert.Equal("Find Rx", PioneerRxTop500ReportSurface.DirectOpenReport);
        Assert.Equal("Search", PioneerRxTop500ReportSurface.GlobalSearchMenu);
        Assert.Equal("Rx Binoculars", PioneerRxTop500ReportSurface.OpenReportMenu);
        Assert.Equal("Rx", PioneerRxTop500ReportSurface.RxTab);
        Assert.Equal("Dispensed Item", PioneerRxTop500ReportSurface.DispensedItemTab);
        Assert.Equal("Reports", PioneerRxTop500ReportSurface.ReportsMenu);
        Assert.Equal("Top X Most Dispensed", PioneerRxTop500ReportSurface.ReportEntry);
        Assert.Equal("Report Parameters", PioneerRxTop500ReportSurface.ParametersTitle);
        Assert.Equal("View - F12", PioneerRxTop500ReportSurface.ViewButtonName);
        Assert.Equal("Top X Most Dispensed Report", PioneerRxTop500ReportSurface.ViewerTitle);
        Assert.Equal("1/18", PioneerRxTop500ReportSurface.ViewerFirstPage);
        Assert.Equal("Excel", PioneerRxTop500ReportSurface.ExcelButtonName);
        Assert.Equal(
            [ControlType.MenuItem, ControlType.Button, ControlType.SplitButton],
            PioneerRxTop500ExportWorkflow.SearchControlTypes);
        Assert.Equal(
            ["Find Rx", "Rx Binoculars"],
            PioneerRxTop500ExportWorkflow.DirectReportOpenNames);
    }

    [Fact]
    public void ExcelSaveAs_AllowsOnlyTheAttachedPioneerRxProcess()
    {
        Assert.True(PioneerRxTop500ExportWorkflow.IsTrustedSaveAsDialogProcess(440, 440));
        Assert.False(PioneerRxTop500ExportWorkflow.IsTrustedSaveAsDialogProcess(441, 440));
        Assert.False(PioneerRxTop500ExportWorkflow.IsTrustedSaveAsDialogProcess(0, 440));
        Assert.False(PioneerRxTop500ExportWorkflow.IsTrustedSaveAsDialogProcess(440, 0));
    }

    [Fact]
    public void ExistingViewer_ResetAllowsOnlySamePidTopLevelWindowIncludingMinimized()
    {
        Assert.True(PioneerRxTop500ExportWorkflow.IsSafeExistingReportViewerToClose(
            440, 440, ControlType.Window));
        Assert.False(PioneerRxTop500ExportWorkflow.IsSafeExistingReportViewerToClose(
            441, 440, ControlType.Window));
        Assert.False(PioneerRxTop500ExportWorkflow.IsSafeExistingReportViewerToClose(
            440, 440, ControlType.Pane));
    }

    [Fact]
    public void Simulator_ProvidesAutoSaveSamePidAndForeignPidExportModes()
    {
        Assert.Equal(SimVariant.Faithful, SimOptions.ParseVariant("faithful"));
        Assert.Equal(SimVariant.Top500SaveAs, SimOptions.ParseVariant("top500-save-as"));
        Assert.Equal(
            SimVariant.Top500ForeignSaveAs,
            SimOptions.ParseVariant("top500-foreign-save-as"));
    }

    [Fact]
    public void SaveAs_UsesUniquePerAttemptManagedPathForDurableRetry()
    {
        var jobId = Guid.NewGuid().ToString("D");
        var first = PioneerRxTop500ExportWorkflow.BuildUniqueSavePath(_root, jobId);
        var retry = PioneerRxTop500ExportWorkflow.BuildUniqueSavePath(_root, jobId);

        Assert.NotEqual(first, retry);
        Assert.Equal(Path.GetFullPath(_root), Path.GetDirectoryName(first));
        Assert.StartsWith(
            $"SuavoAgent-Top500-{Guid.Parse(jobId):N}-",
            Path.GetFileName(first),
            StringComparison.Ordinal);
        Assert.EndsWith(".xlsx", first, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RxTabRecipe_EachIndividualFailureHaltsBeforeLaterMutations(int failingStep)
    {
        var calls = new List<int>();
        Func<bool> Step(int index) => () =>
        {
            calls.Add(index);
            return index != failingStep;
        };

        var applied = PioneerRxTop500ExportWorkflow.ApplyRxTabRecipeFailClosed(
            Step(0),
            Step(1),
            Step(2),
            Step(3),
            Step(4));

        Assert.False(applied);
        Assert.Equal(Enumerable.Range(0, failingStep + 1), calls);
    }

    [Fact]
    public async Task Watcher_IgnoresBaseline_AndAcceptsOnlyStableWorkbookPackage()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "Downloads")).FullName;
        WriteMinimalXlsx(Path.Combine(downloads, "existing.xlsx"));
        var watcher = new StableXlsxExportWatcher(
            downloads,
            pollInterval: TimeSpan.FromMilliseconds(5),
            stabilityInterval: TimeSpan.FromMilliseconds(10));
        Assert.True(watcher.TryCaptureBaseline(out var baseline));
        Assert.NotNull(baseline);

        var started = DateTimeOffset.UtcNow;
        var newPath = Path.Combine(downloads, "new-export.xlsx");
        WriteMinimalXlsx(newPath);
        var result = await watcher.WaitAsync(
            baseline!,
            started,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.InvalidStableFileObserved);
        Assert.NotNull(result.Export);
        Assert.Equal(Path.GetFullPath(newPath), result.Export!.FullPath);
        Assert.Equal(64, result.Export.Sha256.Length);
        Assert.True(result.Export.Length > 0);
    }

    [Fact]
    public async Task Watcher_RejectsRenamedNonWorkbookZip()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "InvalidDownloads")).FullName;
        var watcher = new StableXlsxExportWatcher(
            downloads,
            pollInterval: TimeSpan.FromMilliseconds(5),
            stabilityInterval: TimeSpan.FromMilliseconds(10));
        Assert.True(watcher.TryCaptureBaseline(out var baseline));

        var started = DateTimeOffset.UtcNow;
        using (var archive = ZipFile.Open(
                   Path.Combine(downloads, "not-a-workbook.xlsx"),
                   ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            writer.Write("synthetic");
        }

        var result = await watcher.WaitAsync(
            baseline!,
            started,
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InvalidStableFileObserved);
        Assert.Null(result.Export);
    }

    [Fact]
    public void Watcher_BoundsCompressedAndUncompressedWorkbookShapeBeforeParsing()
    {
        Assert.True(StableXlsxExportWatcher.IsCandidateLengthAllowed(16 * 1024 * 1024));
        Assert.False(StableXlsxExportWatcher.IsCandidateLengthAllowed(16 * 1024 * 1024 + 1));
        Assert.True(StableXlsxExportWatcher.ArchiveShapeIsBounded(
            2,
            [1024, 2048]));
        Assert.False(StableXlsxExportWatcher.ArchiveShapeIsBounded(
            2,
            [64L * 1024 * 1024, 1]));
        Assert.False(StableXlsxExportWatcher.ArchiveShapeIsBounded(
            2049,
            [1]));
    }

    [Fact]
    public async Task ArtifactStore_StagesWorkbook_AndExposesOnlyOpaqueToken()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "Source")).FullName;
        var documents = Directory.CreateDirectory(Path.Combine(_root, "Documents")).FullName;
        var sourcePath = Path.Combine(downloads, "PioneerRx export.xlsx");
        WriteMinimalXlsx(sourcePath);
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var source = new StableXlsxExport(
            sourcePath,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes))
                .ToLowerInvariant(),
            sourceBytes.LongLength);
        var store = new PioneerRxTop500ArtifactStore(documents);

        var published = await store.PublishAsync(
            source,
            new DateOnly(2026, 7, 15),
            CancellationToken.None);

        Assert.NotNull(published);
        Assert.Matches("^[0-9a-f]{32}$", published!.Token);
        Assert.Equal(source.Sha256, published.Sha256);
        Assert.False(File.Exists(sourcePath));
        Assert.True(store.TryResolveToken(published.Token, out var resolved));
        Assert.NotNull(resolved);
        Assert.StartsWith(
            Path.Combine(documents, "SuavoAgent Reports"),
            resolved!,
            StringComparison.Ordinal);

        var receipt = new PioneerRxTop500ExportResult(
            PioneerRxTop500ExportRequest.CurrentContractVersion,
            Guid.NewGuid().ToString("D"),
            true,
            PioneerRxTop500ExportCodes.ExportReady,
            null,
            published.Token,
            PioneerRxTop500ReportRecipe.RawArtifactLabel,
            published.Sha256,
            published.Length,
            "01/01/2026",
            "07/15/2026",
            500);
        var json = JsonSerializer.Serialize(receipt);
        Assert.Contains("\"artifactToken\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("localWorkbookPath", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_ExposesTruthfulGeneratingAndExportingBoundaries()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_root, "ProgressDownloads")).FullName;
        var documents = Directory.CreateDirectory(Path.Combine(_root, "ProgressDocuments")).FullName;
        var logger = new LoggerConfiguration().CreateLogger();
        var sink = new RecordingProgressSink();
        using var engine = new PioneerRxUiaEngine(logger);
        var workflow = new PioneerRxTop500ExportWorkflow(
            engine,
            new ActuationGate(new ActuationConfig { Enabled = true, DryRun = false }, logger),
            logger,
            new StableXlsxExportWatcher(downloads),
            new PioneerRxTop500ArtifactStore(documents),
            progressSink: sink);
        var jobId = Guid.NewGuid().ToString("D");

        workflow.ReportProgress(
            jobId,
            PioneerRxTop500ExportStages.GeneratingReportSequence,
            PioneerRxTop500ExportStages.GeneratingReport);
        workflow.ReportProgress(
            jobId,
            PioneerRxTop500ExportStages.ExportingReportSequence,
            PioneerRxTop500ExportStages.ExportingReport);

        Assert.Collection(
            sink.Events,
            progress =>
            {
                Assert.Equal(2, progress.Sequence);
                Assert.Equal("generating_report", progress.Stage);
                Assert.Equal(jobId, progress.JobId);
            },
            progress =>
            {
                Assert.Equal(3, progress.Sequence);
                Assert.Equal("exporting_report", progress.Stage);
                Assert.Equal(jobId, progress.JobId);
            });
    }

    [Fact]
    public async Task ArtifactStore_ReadsVerifiedWorkbookInFrameSafeChunks()
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "ChunkSource")).FullName;
        var documents = Directory.CreateDirectory(Path.Combine(_root, "ChunkDocuments")).FullName;
        var sourcePath = Path.Combine(sourceDirectory, "large-export.xlsx");
        WriteLargeXlsx(sourcePath);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        var sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var store = new PioneerRxTop500ArtifactStore(documents);
        var published = await store.PublishAsync(
            new StableXlsxExport(sourcePath, sha256, bytes.LongLength),
            new DateOnly(2026, 7, 15),
            CancellationToken.None);
        Assert.NotNull(published);

        await using var reconstructed = new MemoryStream();
        long offset = 0;
        var chunks = 0;
        while (offset < bytes.LongLength)
        {
            var chunk = await store.ReadAsync(
                new PioneerRxTop500ArtifactReadRequest(
                    PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
                    Guid.NewGuid().ToString("D"),
                    published!.Token,
                    sha256,
                    bytes.LongLength,
                    offset),
                CancellationToken.None);
            Assert.NotNull(chunk);
            Assert.InRange(chunk!.Bytes.Length, 1, 24 * 1024);
            Assert.Equal(offset, chunk.Offset);
            await reconstructed.WriteAsync(chunk.Bytes);
            offset = chunk.NextOffset;
            chunks++;
        }

        Assert.True(chunks > 2);
        Assert.True(reconstructed.ToArray().SequenceEqual(bytes));
    }

    [Fact]
    public async Task ArtifactStore_RemovesOnlyExpiredManagedRawArtifacts()
    {
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "CleanupSource")).FullName;
        var documents = Directory.CreateDirectory(
            Path.Combine(_root, "CleanupDocuments")).FullName;
        var sourcePath = Path.Combine(sourceDirectory, "raw.xlsx");
        WriteMinimalXlsx(sourcePath);
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        var sha = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var store = new PioneerRxTop500ArtifactStore(documents);
        var published = await store.PublishAsync(
            new StableXlsxExport(sourcePath, sha, bytes.LongLength),
            new DateOnly(2026, 7, 15),
            CancellationToken.None);
        Assert.NotNull(published);
        Assert.True(store.TryResolveToken(published!.Token, out var rawPath));
        Assert.NotNull(rawPath);
        File.SetLastWriteTimeUtc(rawPath!, DateTime.UtcNow.AddDays(-2));

        var finalPriced = Path.Combine(
            Path.GetDirectoryName(rawPath!)!,
            $"SuavoAgent-Top-500-Priced-{Guid.NewGuid():D}.xlsx");
        await File.WriteAllTextAsync(finalPriced, "keep");
        File.SetLastWriteTimeUtc(finalPriced, DateTime.UtcNow.AddDays(-2));

        Assert.True(store.TryPrepare());
        Assert.False(File.Exists(rawPath));
        Assert.True(File.Exists(finalPriced));
    }

    private static void WriteMinimalXlsx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        WriteEntry(
            archive,
            "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
    }

    private static void WriteLargeXlsx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(
            archive,
            "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        WriteEntry(
            archive,
            "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>");
        using var payload = archive.CreateEntry(
            "xl/media/synthetic.bin",
            CompressionLevel.NoCompression).Open();
        payload.Write(System.Security.Cryptography.RandomNumberGenerator.GetBytes(96 * 1024));
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class RecordingProgressSink : IPioneerRxTop500ExportProgressSink
    {
        public List<PioneerRxTop500ExportProgress> Events { get; } = [];

        public void Report(PioneerRxTop500ExportProgress progress) => Events.Add(progress);
    }
}
