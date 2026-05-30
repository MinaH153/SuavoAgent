using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

/// <summary>
/// Pins that <see cref="DiscoveryClient.FindAsync"/> classifies every failure
/// path into a PHI-safe <see cref="DiscoveryFailure"/> instead of an opaque
/// null, so the cloud can show the real reason instead of "see agent logs".
/// See [[decision-agent-bridge-watch-local-scrub-derivatives]].
/// </summary>
public class DiscoveryClientTests
{
    private static readonly FileDiscoverySpec Spec = PharmacyPresets.NdcPricingList();

    private static DiscoveryClient NewClient() =>
        new(NullLogger<DiscoveryClient>.Instance);

    [Fact]
    public async Task FindAsync_HelperReturnsResult_Succeeds()
    {
        var payload = new FileDiscoveryResult(null, System.Array.Empty<CandidateRanking>(), FileDiscoveryResolution.NotFound);
        var ipc = new StubIpc
        {
            OnSend = req => new IpcResponse(
                req.Id, IpcStatus.Ok, IpcCommands.FindFile,
                JsonSerializer.SerializeToElement(payload), null),
        };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Result);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public async Task FindAsync_IpcSendThrows_ClassifiesIpcUnavailable()
    {
        var ipc = new StubIpc { ThrowOnSend = new System.IO.IOException("pipe broken") };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DiscoveryFailureReason.IpcUnavailable, outcome.Failure!.Reason);
    }

    [Fact]
    public async Task FindAsync_Cancellation_Propagates()
    {
        var ipc = new StubIpc { ThrowOnSend = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None));
    }

    [Fact]
    public async Task FindAsync_NullResponse_ClassifiesHelperTimeout()
    {
        var ipc = new StubIpc { OnSend = _ => null };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DiscoveryFailureReason.HelperTimeout, outcome.Failure!.Reason);
    }

    [Fact]
    public async Task FindAsync_ResponseIdMismatch_ClassifiesIpcDesync_AndSuspectsVersion()
    {
        var ipc = new StubIpc
        {
            OnSend = _ => new IpcResponse(
                "a-different-id", IpcStatus.Ok, IpcCommands.FindFile,
                JsonSerializer.SerializeToElement(new { }), null),
        };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DiscoveryFailureReason.IpcDesync, outcome.Failure!.Reason);
        Assert.True(outcome.Failure.HelperVersionSuspect);
    }

    [Fact]
    public async Task FindAsync_NonOkStatus_ClassifiesHelperError_WithStatusAndCode()
    {
        var ipc = new StubIpc
        {
            OnSend = req => new IpcResponse(
                req.Id, IpcStatus.InternalError, IpcCommands.FindFile, null,
                new IpcError("find_failed", "boom on box", Retryable: false, AttemptCount: 1)),
        };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DiscoveryFailureReason.HelperError, outcome.Failure!.Reason);
        Assert.Equal(IpcStatus.InternalError, outcome.Failure.IpcStatus);
        Assert.Equal("find_failed", outcome.Failure.ErrorCode);
    }

    [Fact]
    public async Task FindAsync_OkButNoData_ClassifiesNoData()
    {
        var ipc = new StubIpc
        {
            OnSend = req => new IpcResponse(req.Id, IpcStatus.Ok, IpcCommands.FindFile, null, null),
        };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DiscoveryFailureReason.NoData, outcome.Failure!.Reason);
    }

    [Fact]
    public async Task FindAsync_UndeserializableData_ClassifiesDeserializeError_AndSuspectsVersion()
    {
        var ipc = new StubIpc
        {
            // A bare JSON number cannot deserialize into the FileDiscoveryResult record.
            OnSend = req => new IpcResponse(
                req.Id, IpcStatus.Ok, IpcCommands.FindFile,
                JsonSerializer.SerializeToElement(12345), null),
        };

        var outcome = await NewClient().FindAsync(ipc, "job-1", Spec, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(DiscoveryFailureReason.DeserializeError, outcome.Failure!.Reason);
        Assert.True(outcome.Failure.HelperVersionSuspect);
    }

    [Theory]
    [InlineData(DiscoveryFailureReason.IpcUnavailable, "ipc_unavailable")]
    [InlineData(DiscoveryFailureReason.HelperTimeout, "helper_timeout")]
    [InlineData(DiscoveryFailureReason.IpcDesync, "ipc_desync")]
    [InlineData(DiscoveryFailureReason.HelperError, "helper_error")]
    [InlineData(DiscoveryFailureReason.NoData, "no_data")]
    [InlineData(DiscoveryFailureReason.DeserializeError, "deserialize_error")]
    public void DiscoveryFailure_ReasonCode_IsStableSnakeCase(DiscoveryFailureReason reason, string expected)
    {
        Assert.Equal(expected, new DiscoveryFailure(reason).ReasonCode);
        Assert.False(string.IsNullOrWhiteSpace(new DiscoveryFailure(reason).HumanMessage));
    }

    [Fact]
    public void ToAckResult_CarriesSafeScalarsOnly_NoRawHelperText()
    {
        var failure = new DiscoveryFailure(
            DiscoveryFailureReason.HelperError,
            IpcStatus: IpcStatus.InternalError,
            ErrorCode: "find_failed",
            HelperVersionSuspect: false);

        var json = JsonSerializer.Serialize(failure.ToAckResult());

        Assert.Contains("\"reason\":\"helper_error\"", json);
        Assert.Contains("\"ipcStatus\":500", json);
        Assert.Contains("\"errorCode\":\"find_failed\"", json);
        Assert.Contains("\"outcome\":\"failed\"", json);
        Assert.Contains("\"stage\":\"discovery\"", json);
        // Never ship the Helper's free-text message or anything message-shaped.
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("boom", json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubIpc : IIpcCommandClient
    {
        public Func<IpcRequest, IpcResponse?>? OnSend { get; set; }
        public Exception? ThrowOnSend { get; set; }

        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }
            return Task.FromResult(OnSend?.Invoke(request));
        }
    }
}
