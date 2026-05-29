using System.Net;
using System.Text;
using System.Text.Json;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class DeviceCodeServiceTests
{
    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CreateAsync_PostsFingerprintAndParsesCode()
    {
        var handler = new QueueHandler(Json(
            """{"deviceCode":"ABCD-2345","userCode":"ABCD-2345","verificationUrl":"https://suavollc.com/admin/agents/pair?code=ABCD-2345","expiresIn":900,"pollInterval":5}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        var res = await svc.CreateAsync("fp-123", "3.15.0", CancellationToken.None);

        Assert.Equal("ABCD-2345", res.DeviceCode);
        Assert.Equal(900, res.ExpiresInSeconds);
        Assert.Equal(5, res.PollIntervalSeconds);
        Assert.Equal("/api/agent/device-code", handler.LastPath);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("fp-123", sent.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal("3.15.0", sent.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task PollAsync_Pending_ThenAuthorized_ReturnsKey()
    {
        var handler = new QueueHandler(
            Json("""{"status":"pending"}"""),
            Json("""{"status":"authorized","apiKey":"sagent_live","agentId":"a-1","pharmacyId":"p-1","pharmacyName":"Queen"}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        var first = await svc.PollAsync("ABCD-2345", CancellationToken.None);
        Assert.True(first.IsPending);
        Assert.False(first.IsTerminal);
        Assert.Null(first.ApiKey);

        var second = await svc.PollAsync("ABCD-2345", CancellationToken.None);
        Assert.True(second.IsAuthorized);
        Assert.True(second.IsTerminal);
        Assert.Equal("sagent_live", second.ApiKey);
        Assert.Equal("a-1", second.AgentId);
        Assert.Equal("/api/agent/device-token", handler.LastPath);
    }

    [Fact]
    public async Task PollAsync_Expired_IsTerminalWithNoKey()
    {
        var handler = new QueueHandler(Json("""{"status":"expired"}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        var res = await svc.PollAsync("ABCD-2345", CancellationToken.None);
        Assert.Equal("expired", res.Status);
        Assert.True(res.IsTerminal);
        Assert.Null(res.ApiKey);
    }

    [Fact]
    public void Ctor_RejectsNonHttpsCloudUrl()
    {
        Assert.Throws<InvalidOperationException>(
            () => new DeviceCodeService("http://insecure.example", new QueueHandler()));
    }

    [Fact]
    public void PollResult_ToString_RedactsApiKey()
    {
        var r = new DeviceCodePollResult("authorized", "sagent_topsecret", "a-1", "p-1", "Queen");
        Assert.DoesNotContain("sagent_topsecret", r.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pending", false, false)]
    [InlineData("authorized", true, true)]
    [InlineData("expired", true, false)]
    [InlineData("denied", true, false)]
    [InlineData("weird-gateway-blob", false, false)] // unknown -> keep polling
    public void PollResult_Terminality(string status, bool expectTerminal, bool expectAuthorized)
    {
        var r = new DeviceCodePollResult(status);
        Assert.Equal(expectTerminal, r.IsTerminal);
        Assert.Equal(expectAuthorized, r.IsAuthorized);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        public QueueHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"pending"}""", Encoding.UTF8, "application/json"),
                };
        }
    }
}
