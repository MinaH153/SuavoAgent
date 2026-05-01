using System.Net;
using System.Text;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class SuavoCloudClientTests
{
    [Fact]
    public async Task PostSignedAsync_IncludesSanitizedCloudReasonOnAuthFailure()
    {
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            new FixedHandler(
                new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"success":false,"error":"Agent not found"}""",
                        Encoding.UTF8,
                        "application/json"),
                }));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostSignedAsync("/api/agent/heartbeat", new { ok = true }, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Contains("reason=Agent not found", ex.Message);
    }

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FixedHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }
}
