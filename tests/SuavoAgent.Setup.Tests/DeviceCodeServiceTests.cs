using System.Net;
using System.Text;
using System.Text.Json;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class DeviceCodeServiceTests
{
    private const string AuthorizedResponse =
        """{"status":"authorized","apiKey":"sagent_live","agentId":"a-1","pharmacyId":"p-1","pharmacyName":"Queen","verticalConfig":{"vertical":"default","complianceMode":"none","systemConnector":"none","connectorLabel":"your system","redactionProfileId":"none","framing":{"productNoun":"SuavoAgent","systemNoun":"your system","businessNoun":"business","idLabel":"License ID"},"compliance":{"baaRequired":false,"consentCopyId":"terms-v1"}},"verticalConfigSignature":"signed-envelope","verticalConfigKeyId":"vertical-v1"}""";

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CreateAsync_PostsFingerprintAndParsesCode()
    {
        var handler = new QueueHandler(Json(
            $$"""{"deviceCode":"ABCD-2345","userCode":"ABCD-2345","deviceSecret":"agent-only-secret","deviceChallenge":"{{new string('A', 43)}}","verificationUrl":"https://suavollc.com/pharmacy/agent/pair?code=ABCD-2345","expiresIn":900,"pollInterval":5}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        var res = await svc.CreateAsync("fp-123", "3.15.0", CancellationToken.None);

        Assert.Equal("ABCD-2345", res.DeviceCode);
        Assert.Equal(900, res.ExpiresInSeconds);
        Assert.Equal(5, res.PollIntervalSeconds);
        Assert.Equal(64, res.DeviceKeyId.Length);
        Assert.False(string.IsNullOrWhiteSpace(res.DeviceKeyName));
        Assert.Equal(64, res.MaintenanceKeyId.Length);
        Assert.Equal(new string('A', 43), res.DeviceChallenge);
        Assert.Equal("/api/agent/device-code", handler.LastPath);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("fp-123", sent.RootElement.GetProperty("fingerprint").GetString());
        Assert.Equal("3.15.0", sent.RootElement.GetProperty("version").GetString());
        Assert.Equal("ES256", sent.RootElement.GetProperty("deviceKey").GetProperty("algorithm").GetString());
        Assert.Equal(64, sent.RootElement.GetProperty("deviceKey").GetProperty("keyId").GetString()!.Length);
        Assert.False(string.IsNullOrWhiteSpace(
            sent.RootElement.GetProperty("deviceKey").GetProperty("publicKeySpki").GetString()));
        var maintenance = sent.RootElement.GetProperty("maintenanceKey");
        Assert.Equal("ES256", maintenance.GetProperty("algorithm").GetString());
        Assert.Equal(res.MaintenanceKeyId, maintenance.GetProperty("keyId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(
            maintenance.GetProperty("publicKeySpki").GetString()));
        Assert.Equal(86, maintenance.GetProperty("proof").GetString()!.Length);
    }

    [Fact]
    public async Task CreateAsync_RejectsChunkedResponseBeyondBound()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedContent(new byte[64 * 1024 + 1]),
        };
        var handler = new QueueHandler(response);
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        await Assert.ThrowsAsync<DeviceCodeTransientException>(() =>
            svc.CreateAsync("fp-oversized", "3.15.0", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PollAsync_RejectsChunkedResponseBeyondBound()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedContent(new byte[512 * 1024 + 1]),
        };
        var handler = new QueueHandler(response);
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        await Assert.ThrowsAsync<DeviceCodeTransientException>(() =>
            svc.PollAsync("ABCD-2345", "secret", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PollAsync_RejectsInvalidUtf8AndDuplicateJsonProperties()
    {
        var invalidUtf8 = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d]),
        };
        var duplicate = Json("""{"status":"pending","status":"authorized"}""");
        var handler = new QueueHandler(invalidUtf8, duplicate);
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        await Assert.ThrowsAsync<DeviceCodeTransientException>(() =>
            svc.PollAsync("ABCD-2345", "secret", CancellationToken.None));
        await Assert.ThrowsAsync<DeviceCodeTransientException>(() =>
            svc.PollAsync("ABCD-2345", "secret", CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_DoesNotFollowOffOriginRedirect()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("https://evil.example/steal-enrollment");
        var handler = new QueueHandler(
            redirect,
            Json("""{"deviceCode":"SHOULD-NOT-LOAD"}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            svc.CreateAsync("fp-redirect", "3.15.0", CancellationToken.None));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CreateAsync_RejectsPhishingVerificationUrl()
    {
        var handler = new QueueHandler(Json(
            $$"""{"deviceCode":"ABCD-2345","deviceSecret":"agent-only-secret","deviceChallenge":"{{new string('A', 43)}}","verificationUrl":"https://evil.example/pharmacy/agent/pair?code=ABCD-2345","expiresIn":900,"pollInterval":5}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        await Assert.ThrowsAsync<DeviceCodeTransientException>(() =>
            svc.CreateAsync("fp-phishing", "3.15.0", CancellationToken.None));
    }

    [Fact]
    public async Task PollAsync_Pending_ThenAuthorized_ReturnsKey()
    {
        var handler = new QueueHandler(
            Json("""{"status":"pending"}"""),
            Json(AuthorizedResponse));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        var first = await svc.PollAsync("ABCD-2345", "agent-only-secret", CancellationToken.None);
        Assert.True(first.IsPending);
        Assert.False(first.IsTerminal);
        Assert.Null(first.ApiKey);

        var second = await svc.PollAsync("ABCD-2345", "agent-only-secret", CancellationToken.None);
        Assert.True(second.IsAuthorized);
        Assert.True(second.IsTerminal);
        Assert.Equal("sagent_live", second.ApiKey);
        Assert.Equal("a-1", second.AgentId);
        Assert.Equal("/api/agent/device-token", handler.LastPath);
    }

    [Fact]
    public async Task PollAsync_AuthorizedWithoutSignedVerticalProfile_IsRejected()
    {
        var handler = new QueueHandler(Json(
            """{"status":"authorized","apiKey":"sagent_live","agentId":"a-1","pharmacyId":"p-1"}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        await Assert.ThrowsAsync<DeviceCodeTransientException>(() =>
            svc.PollAsync("ABCD-2345", "agent-only-secret", CancellationToken.None));
    }

    [Fact]
    public async Task PollAsync_Expired_IsTerminalWithNoKey()
    {
        var handler = new QueueHandler(Json("""{"status":"expired"}"""));
        using var svc = new DeviceCodeService("https://suavollc.com", handler);

        var res = await svc.PollAsync("ABCD-2345", "agent-only-secret", CancellationToken.None);
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
        public int Calls { get; private set; }

        public QueueHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
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

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
