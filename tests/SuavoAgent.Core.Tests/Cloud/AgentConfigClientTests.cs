using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Cloud;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class AgentConfigClientTests
{
    [Fact]
    public async Task FetchAsync_SignsGetConfigWithEmptyBodyHmacContract()
    {
        using var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "success": true,
                      "asOf": "2026-04-30T20:00:00.000Z",
                      "overrides": [
                        {
                          "path": "TemplateLearning.RuleGeneration",
                          "value": true,
                          "scope": "pharmacy",
                          "updatedAt": "2026-04-30T19:59:00.000Z"
                        }
                      ]
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://suavollc.com"),
        };
        var options = new AgentOptions { ApiKey = "agent-secret" };
        var client = new AgentConfigClient(
            http,
            options,
            NullLogger<AgentConfigClient>.Instance);

        var result = await client.FetchAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Single(result.Overrides);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal("/api/agent/config", handler.Request.RequestUri!.AbsolutePath);
        Assert.Null(handler.Request.Content);

        var timestamp = Assert.Single(handler.Request.Headers.GetValues("x-agent-timestamp"));
        var version = Assert.Single(handler.Request.Headers.GetValues("x-agent-auth-version"));
        var nonce = Assert.Single(handler.Request.Headers.GetValues("x-agent-nonce"));
        var contentSha256 = Assert.Single(handler.Request.Headers.GetValues("x-agent-content-sha256"));
        var signature = Assert.Single(handler.Request.Headers.GetValues("x-agent-signature"));
        var apiKey = Assert.Single(handler.Request.Headers.GetValues("x-agent-api-key"));
        Assert.Equal("2", version);
        Assert.Equal("agent-secret", apiKey);
        Assert.Equal(AgentRequestSigner.ComputeBodySha256(string.Empty), contentSha256);
        Assert.Equal(
            new HmacSigner("agent-secret").Sign(
                "GET", "/api/agent/config", timestamp, nonce, contentSha256),
            signature);
    }

    [Fact]
    public async Task FetchAsync_ReturnsNullOnNonSuccessWithoutThrowing()
    {
        using var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://suavollc.com"),
        };
        var client = new AgentConfigClient(
            http,
            new AgentOptions { ApiKey = "agent-secret" },
            NullLogger<AgentConfigClient>.Instance);

        var result = await client.FetchAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal("http_401_empty_error_body", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_TriggersCredentialRecoveryOnAgentNotFound401()
    {
        using var handler = new CapturingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"success":false,"error":"Agent not found"}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://suavollc.com"),
        };
        var recovery = new FakeRecoveryClient(
            new AgentCredentialRecoveryResult(true, "key_rotated", RestartRequired: true));
        var lifetime = new FakeLifetime();
        var coordinator = new CloudAuthRecoveryCoordinator(
            recovery,
            lifetime,
            NullLogger<CloudAuthRecoveryCoordinator>.Instance);
        var client = new AgentConfigClient(
            http,
            new AgentOptions { ApiKey = "agent-secret" },
            NullLogger<AgentConfigClient>.Instance,
            coordinator);

        var result = await client.FetchAsync(CancellationToken.None);

        Assert.Null(result);
        Assert.Equal("http_401_Agent_not_found", client.LastFailureKind);
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(1, lifetime.StopCalls);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class FakeRecoveryClient : IAgentCredentialRecoveryClient
    {
        private readonly AgentCredentialRecoveryResult _result;

        public FakeRecoveryClient(AgentCredentialRecoveryResult result)
        {
            _result = result;
        }

        public int Calls { get; private set; }

        public Task<AgentCredentialRecoveryResult> TryRecoverAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public int StopCalls { get; private set; }
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopCalls++;
        }
    }
}
