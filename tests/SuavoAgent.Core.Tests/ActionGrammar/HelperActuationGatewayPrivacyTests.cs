using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Actuation;

public sealed class HelperActuationGatewayPrivacyTests
{
    [Fact]
    public async Task ChannelException_NeverReturnsExceptionMessageToAuditCaller()
    {
        const string sensitive = @"Jane Doe C:\Patients\rx-1234.txt";
        var gateway = new HelperActuationGateway(
            () => new ThrowingClient(sensitive),
            NullLogger<HelperActuationGateway>.Instance);

        var result = await gateway.ClickByLabelAsync(
            new ClickByLabelRequest(
                Label: "Submit",
                ProcessName: "notepad",
                MatchMode: "exact",
                TimeoutMs: 1000,
                DryRun: true),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("ipc_exception", result.RejectionCode);
        Assert.Equal("Helper actuation channel failed", result.RejectionReason);
        Assert.DoesNotContain(sensitive, result.RejectionReason!, StringComparison.Ordinal);
    }

    private sealed class ThrowingClient(string message) : IIpcCommandClient
    {
        public bool IsConnected => true;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct) =>
            throw new InvalidOperationException(message);
    }
}
