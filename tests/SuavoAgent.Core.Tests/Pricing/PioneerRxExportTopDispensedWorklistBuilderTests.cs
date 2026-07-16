using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PioneerRxSim;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PioneerRxExportTopDispensedWorklistBuilderTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"top500-builder-{Guid.NewGuid():N}");

    public PioneerRxExportTopDispensedWorklistBuilderTests() =>
        Directory.CreateDirectory(_root);

    [Fact]
    public async Task BuildAsync_UsesDedicatedExportThenBoundedArtifactReads()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(_root, "fixture")).FullName;
        var workbookPath = SyntheticTop500XlsxWriter.Write(fixture, Now);
        var bytes = await File.ReadAllBytesAsync(workbookPath);
        var ipc = new ExportArtifactIpc(bytes, Now);
        var builder = new PioneerRxExportTopDispensedWorklistBuilder(
            ipc,
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            NullLogger<PioneerRxExportTopDispensedWorklistBuilder>.Instance,
            _root,
            new FixedTimeProvider(Now));
        var commandId = Guid.NewGuid().ToString("D");
        var progress = new List<PioneerRxTop500ExportProgress>();

        var result = await builder.BuildAsync(
            commandId,
            (update, _) =>
            {
                progress.Add(update);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.Ok, result.ErrorCode);
        Assert.Equal(500, result.ItemCount);
        Assert.NotNull(result.WorkbookPath);
        Assert.StartsWith(_root, result.WorkbookPath!, StringComparison.Ordinal);
        Assert.Equal(IpcCommands.PioneerRxTop500Export, ipc.Commands[0]);
        Assert.All(
            ipc.Commands.Skip(1),
            command => Assert.Equal(IpcCommands.PioneerRxTop500ReadArtifact, command));
        Assert.True(ipc.Commands.Count > 2, "The fixture must exercise multiple bounded chunks.");
        Assert.DoesNotContain(
            ipc.SerializedResponses,
            response => response.Contains(workbookPath, StringComparison.Ordinal));
        Assert.DoesNotContain(
            ipc.SerializedResponses,
            response => response.Contains("localWorkbookPath", StringComparison.Ordinal));
        Assert.Collection(
            progress,
            update => Assert.Equal("generating_report", update.Stage),
            update => Assert.Equal("exporting_report", update.Stage));
    }

    [Fact]
    public async Task BuildAsync_RejectsArtifactWhoseReceiptDigestDoesNotMatchBytes()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(_root, "tamper-fixture")).FullName;
        var workbookPath = SyntheticTop500XlsxWriter.Write(fixture, Now);
        var bytes = await File.ReadAllBytesAsync(workbookPath);
        var ipc = new ExportArtifactIpc(bytes, Now, receiptSha256: new string('0', 64));
        var builder = new PioneerRxExportTopDispensedWorklistBuilder(
            ipc,
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            NullLogger<PioneerRxExportTopDispensedWorklistBuilder>.Instance,
            _root,
            new FixedTimeProvider(Now));

        var result = await builder.BuildAsync(
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("pricing_worklist_validation_failed", result.ErrorCode);
    }

    [Theory]
    [InlineData(PioneerRxTop500ExportCodes.ActuationGateClosed, "pricing_report_permission_blocked", ActuationRejectionCodes.GateDisabled)]
    [InlineData(PioneerRxTop500ExportCodes.PioneerRxUnavailable, "pricing_pioneerrx_not_open", null)]
    [InlineData(PioneerRxTop500ExportCodes.ReportNavigationUnavailable, "pricing_report_open_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.ReportWindowUnavailable, "pricing_report_open_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.FilterSurfaceUnavailable, "pricing_report_filters_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.FilterVerificationFailed, "pricing_report_filters_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.ReportViewUnavailable, "pricing_report_generation_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.ExportControlUnavailable, "pricing_report_export_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.ExportTimedOut, "pricing_report_export_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.ExportSaveDialogUntrusted, "pricing_report_save_dialog_blocked", PioneerRxTop500ExportCodes.ExportSaveDialogUntrusted)]
    [InlineData(PioneerRxTop500ExportCodes.ExportSaveDialogInvalid, "pricing_report_save_dialog_blocked", PioneerRxTop500ExportCodes.ExportSaveDialogInvalid)]
    [InlineData(PioneerRxTop500ExportCodes.ExportDirectoryUnavailable, "pricing_report_storage_unavailable", null)]
    [InlineData(PioneerRxTop500ExportCodes.ExportInvalid, "pricing_report_validation_failed", null)]
    [InlineData(PioneerRxTop500ExportCodes.Cancelled, "pricing_report_cancelled", null)]
    [InlineData(PioneerRxTop500ExportCodes.UnexpectedFailure, "pricing_report_generation_failed", null)]
    public async Task BuildAsync_MapsExactHelperFailureToFiniteUserSafeCode(
        string helperCode,
        string expectedCode,
        string? blockerCode)
    {
        var ipc = new ExportArtifactIpc(
            [1],
            Now,
            exportFailureCode: helperCode,
            exportBlockerCode: blockerCode);
        var builder = new PioneerRxExportTopDispensedWorklistBuilder(
            ipc,
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            NullLogger<PioneerRxExportTopDispensedWorklistBuilder>.Instance,
            _root,
            new FixedTimeProvider(Now));

        var result = await builder.BuildAsync(
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Single(ipc.Commands);
    }

    [Fact]
    public async Task BuildAsync_RejectsFailureReceiptWithFreeFormBlocker()
    {
        var ipc = new ExportArtifactIpc(
            [1],
            Now,
            exportFailureCode: PioneerRxTop500ExportCodes.ActuationGateClosed,
            exportBlockerCode: "open C:\\Users\\patient\\report.xlsx");
        var builder = new PioneerRxExportTopDispensedWorklistBuilder(
            ipc,
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            NullLogger<PioneerRxExportTopDispensedWorklistBuilder>.Instance,
            _root,
            new FixedTimeProvider(Now));

        var result = await builder.BuildAsync(
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("pricing_worklist_validation_failed", result.ErrorCode);
    }

    [Fact]
    public async Task BuildAsync_UsesReceiptRunDateWhenRunCrossesMidnight()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(_root, "midnight-fixture")).FullName;
        var workbookPath = SyntheticTop500XlsxWriter.Write(fixture, Now);
        var bytes = await File.ReadAllBytesAsync(workbookPath);
        var ipc = new ExportArtifactIpc(bytes, Now);
        var builder = new PioneerRxExportTopDispensedWorklistBuilder(
            ipc,
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            NullLogger<PioneerRxExportTopDispensedWorklistBuilder>.Instance,
            _root,
            new MidnightAdvancingTimeProvider(Now));

        var result = await builder.BuildAsync(
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        Assert.True(result.Ok, result.ErrorCode);
        Assert.Equal(500, result.ItemCount);
    }

    [Fact]
    public async Task BuildAsync_RemovesInvalidProtectedCacheAndRegenerates()
    {
        var fixture = Directory.CreateDirectory(Path.Combine(_root, "cache-fixture")).FullName;
        var workbookPath = SyntheticTop500XlsxWriter.Write(fixture, Now);
        var bytes = await File.ReadAllBytesAsync(workbookPath);
        var ipc = new ExportArtifactIpc(bytes, Now);
        var commandId = Guid.NewGuid().ToString("D");
        var generated = Directory.CreateDirectory(
            Path.Combine(_root, "pricing", "generated")).FullName;
        var cachedPath = Path.Combine(generated, $"{commandId}.xlsx");
        await File.WriteAllTextAsync(cachedPath, "corrupt");
        var builder = new PioneerRxExportTopDispensedWorklistBuilder(
            ipc,
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            NullLogger<PioneerRxExportTopDispensedWorklistBuilder>.Instance,
            _root,
            new FixedTimeProvider(Now));

        var result = await builder.BuildAsync(commandId, CancellationToken.None);

        Assert.True(result.Ok, result.ErrorCode);
        Assert.Equal(IpcCommands.PioneerRxTop500Export, ipc.Commands[0]);
        Assert.True(new FileInfo(cachedPath).Length > "corrupt".Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class ExportArtifactIpc : IIpcCommandClient
    {
        private const int TestChunkBytes = 4 * 1024;
        private readonly byte[] _workbook;
        private readonly DateTimeOffset _now;
        private readonly string _receiptSha256;
        private readonly string? _exportFailureCode;
        private readonly string? _exportBlockerCode;

        public ExportArtifactIpc(
            byte[] workbook,
            DateTimeOffset now,
            string? receiptSha256 = null,
            string? exportFailureCode = null,
            string? exportBlockerCode = null)
        {
            _workbook = workbook;
            _now = now;
            _receiptSha256 = receiptSha256 ??
                Convert.ToHexString(SHA256.HashData(workbook)).ToLowerInvariant();
            _exportFailureCode = exportFailureCode;
            _exportBlockerCode = exportBlockerCode;
        }

        public List<string> Commands { get; } = [];
        public List<string> SerializedResponses { get; } = [];
        public bool IsConnected => true;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Commands.Add(request.Command);
            IpcResponse response = request.Command switch
            {
                IpcCommands.PioneerRxTop500Export => Export(request),
                IpcCommands.PioneerRxTop500ReadArtifact => Read(request),
                _ => new IpcResponse(
                    request.Id,
                    IpcStatus.BadRequest,
                    request.Command,
                    null,
                    new IpcError("unexpected", "unexpected", false, 1)),
            };
            SerializedResponses.Add(JsonSerializer.Serialize(response));
            return Task.FromResult<IpcResponse?>(response);
        }

        private IpcResponse Export(IpcRequest request)
        {
            var payload = JsonSerializer.Deserialize<PioneerRxTop500ExportRequest>(
                request.Data!.Value)!;
            var runDate = DateOnly.FromDateTime(_now.DateTime);
            if (_exportFailureCode is not null)
            {
                var failure = PioneerRxTop500ExportResult.Failed(
                    payload.JobId,
                    _exportFailureCode,
                    PioneerRxTop500ReportRecipe.StartFor(runDate),
                    runDate,
                    _exportBlockerCode);
                return new IpcResponse(
                    request.Id,
                    IpcStatus.Ok,
                    request.Command,
                    JsonSerializer.SerializeToElement(failure),
                    null);
            }
            var result = new PioneerRxTop500ExportResult(
                PioneerRxTop500ExportRequest.CurrentContractVersion,
                payload.JobId,
                true,
                PioneerRxTop500ExportCodes.ExportReady,
                null,
                new string('a', 32),
                PioneerRxTop500ReportRecipe.RawArtifactLabel,
                _receiptSha256,
                _workbook.LongLength,
                PioneerRxTop500ReportRecipe.FormatDate(
                    PioneerRxTop500ReportRecipe.StartFor(runDate)),
                PioneerRxTop500ReportRecipe.FormatDate(runDate),
                PioneerRxTop500ReportRecipe.TopCount);
            return new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null);
        }

        private IpcResponse Read(IpcRequest request)
        {
            var payload = JsonSerializer.Deserialize<PioneerRxTop500ArtifactReadRequest>(
                request.Data!.Value)!;
            var count = (int)Math.Min(TestChunkBytes, _workbook.LongLength - payload.Offset);
            var chunk = _workbook.AsSpan((int)payload.Offset, count).ToArray();
            var next = payload.Offset + count;
            var result = new PioneerRxTop500ArtifactReadResult(
                PioneerRxTop500ArtifactReadRequest.CurrentContractVersion,
                payload.JobId,
                true,
                PioneerRxTop500ArtifactReadCodes.Ready,
                Convert.ToBase64String(chunk),
                payload.Offset,
                next,
                next == _workbook.LongLength,
                payload.ExpectedSha256,
                _workbook.LongLength);
            return new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class MidnightAdvancingTimeProvider(DateTimeOffset beforeMidnight)
        : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) == 1
                ? beforeMidnight
                : beforeMidnight.AddDays(1);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
