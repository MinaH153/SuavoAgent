using System.Security.Cryptography;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PioneerRxPricedWorkbookPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"priced-publisher-{Guid.NewGuid():N}");

    public PioneerRxPricedWorkbookPublisherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ValidWorkbook_IsChunkedAndCommittedWithoutLeakingLocalPath()
    {
        var path = WriteValidWorkbook();
        var expected = await File.ReadAllBytesAsync(path);
        var ipc = new PublishingIpc();
        var publisher = new PioneerRxPricedWorkbookPublisher(
            ipc,
            NullLogger<PioneerRxPricedWorkbookPublisher>.Instance);
        var commandId = Guid.NewGuid().ToString("D");

        var result = await publisher.PublishAsync(
            commandId,
            path,
            CancellationToken.None);

        Assert.True(result.Published, result.Code);
        Assert.Equal(PioneerRxPricedWorkbookPublicationCodes.Published, result.Code);
        Assert.Equal(expected, ipc.PublishedBytes);
        Assert.Equal(
            [
                IpcCommands.PioneerRxPricedWorkbookBegin,
                IpcCommands.PioneerRxPricedWorkbookChunk,
                IpcCommands.PioneerRxPricedWorkbookCommit,
            ],
            ipc.Requests.Select(request => request.Command).Distinct());
        Assert.All(
            ipc.Requests,
            request => Assert.DoesNotContain(
                path,
                JsonSerializer.Serialize(request),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitRejection_FailsPublication()
    {
        var path = WriteValidWorkbook();
        var ipc = new PublishingIpc(rejectCommit: true);
        var publisher = new PioneerRxPricedWorkbookPublisher(
            ipc,
            NullLogger<PioneerRxPricedWorkbookPublisher>.Instance);

        var result = await publisher.PublishAsync(
            Guid.NewGuid().ToString("D"),
            path,
            CancellationToken.None);

        Assert.False(result.Published);
        Assert.Equal(
            PioneerRxPricedWorkbookPublicationCodes.PublicationFailed,
            result.Code);
    }

    private string WriteValidWorkbook()
    {
        var path = Path.Combine(_root, "priced.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Pricing");
        for (var column = 1;
             column <= PioneerRxPricedWorkbookPublicationContract.ExpectedHeaders.Count;
             column++)
            sheet.Cell(1, column).Value =
                PioneerRxPricedWorkbookPublicationContract.ExpectedHeaders[column - 1];
        for (var rank = 1;
             rank <= PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows;
             rank++)
        {
            var row = rank + 1;
            sheet.Cell(row, 1).Value = rank;
            sheet.Cell(row, 2).Value = $"Drug {rank}";
            sheet.Cell(row, 3).Value = "1 mg";
            sheet.Cell(row, 4).SetValue(
                (10_000_000_000L + rank).ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            sheet.Cell(row, 5).Value = rank % 20 == 0
                ? "Needs review"
                : "Supplier";
            if (rank % 20 != 0) sheet.Cell(row, 6).Value = 1m + rank / 100m;
        }
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class PublishingIpc(bool rejectCommit = false) : IIpcCommandClient
    {
        private readonly MemoryStream _uploaded = new();
        private PioneerRxPricedWorkbookBeginRequest? _begin;
        private readonly string _token = Guid.NewGuid().ToString("N");

        public bool IsConnected => true;
        public List<IpcRequest> Requests { get; } = [];
        public byte[] PublishedBytes { get; private set; } = [];

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Requests.Add(request);
            object result = request.Command switch
            {
                IpcCommands.PioneerRxPricedWorkbookBegin => Begin(request),
                IpcCommands.PioneerRxPricedWorkbookChunk => Chunk(request),
                IpcCommands.PioneerRxPricedWorkbookCommit => Commit(request),
                _ => throw new InvalidOperationException("Unexpected IPC command."),
            };
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null));
        }

        private PioneerRxPricedWorkbookBeginResult Begin(IpcRequest request)
        {
            _begin = JsonSerializer.Deserialize<PioneerRxPricedWorkbookBeginRequest>(
                request.Data!.Value)!;
            return new PioneerRxPricedWorkbookBeginResult(
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                _begin.JobId,
                true,
                PioneerRxPricedWorkbookPublicationCodes.UploadReady,
                _token,
                false,
                0,
                PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                _begin.WorkbookSha256,
                _begin.WorkbookBytes);
        }

        private PioneerRxPricedWorkbookChunkResult Chunk(IpcRequest request)
        {
            var chunk = JsonSerializer.Deserialize<PioneerRxPricedWorkbookChunkRequest>(
                request.Data!.Value)!;
            Assert.Equal(_uploaded.Length, chunk.Offset);
            var bytes = Convert.FromBase64String(chunk.ChunkBase64);
            _uploaded.Write(bytes);
            return new PioneerRxPricedWorkbookChunkResult(
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                chunk.JobId,
                true,
                PioneerRxPricedWorkbookPublicationCodes.ChunkAccepted,
                _uploaded.Length);
        }

        private PioneerRxPricedWorkbookCommitResult Commit(IpcRequest request)
        {
            var commit = JsonSerializer.Deserialize<PioneerRxPricedWorkbookCommitRequest>(
                request.Data!.Value)!;
            if (rejectCommit)
                return PioneerRxPricedWorkbookCommitResult.Failed(
                    commit.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.PublicationFailed);

            PublishedBytes = _uploaded.ToArray();
            Assert.Equal(_begin!.WorkbookBytes, PublishedBytes.LongLength);
            Assert.Equal(
                _begin.WorkbookSha256,
                Convert.ToHexString(SHA256.HashData(PublishedBytes)).ToLowerInvariant());
            return new PioneerRxPricedWorkbookCommitResult(
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                commit.JobId,
                true,
                PioneerRxPricedWorkbookPublicationCodes.Published,
                PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                commit.ExpectedSha256,
                commit.ExpectedBytes,
                PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows);
        }
    }
}
