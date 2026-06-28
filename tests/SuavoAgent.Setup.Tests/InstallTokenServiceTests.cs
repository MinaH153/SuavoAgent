using System.Net;
using System.Text;
using System.Text.Json;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class InstallTokenServiceTests
{
    // ── Filename parser (pure, no I/O) ──────────────────────────────────────
    [Theory]
    [InlineData("SuavoSetup-sai_0123456789abcdef", "sai_0123456789abcdef")]
    [InlineData("SuavoSetup-sai_0123456789abcdef (1)", "sai_0123456789abcdef")]   // browser dedup
    [InlineData("SuavoSetup-sai_0123456789abcdef (12)", "sai_0123456789abcdef")]
    [InlineData("suavosetup-sai_0123456789abcdef", "sai_0123456789abcdef")]       // case-insensitive prefix
    [InlineData("SuavoSetup", null)]                       // plain, no token → device-code
    [InlineData("SuavoSetup-notsai_token12345", null)]     // wrong token prefix
    [InlineData("SuavoSetup-sai_short", null)]             // < 12 chars
    [InlineData("Installer-sai_0123456789abcdef", null)]   // wrong exe prefix
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseInstallToken(string? name, string? expected)
    {
        Assert.Equal(expected, SetupConfig.ParseInstallToken(name));
    }

    // ── /register exchange ──────────────────────────────────────────────────
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ExchangeAsync_Success_MapsCredentialsAndPostsRegister()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK,
            """{"success":true,"data":{"apiKey":"sagent_live","agentId":"a-1","pharmacyId":"p-1","pharmacyName":"Queen","initialOverrides":[]}}"""));
        using var svc = new InstallTokenService("https://suavollc.com", handler);

        var res = await svc.ExchangeAsync("sai_token123456", "PC-1", "fp-9", "3.77.0", CancellationToken.None);

        Assert.Equal("sagent_live", res.ApiKey);
        Assert.Equal("a-1", res.AgentId);
        Assert.Equal("p-1", res.PharmacyId);
        Assert.Equal("Queen", res.PharmacyName);
        Assert.Equal("/api/agent/register", handler.LastPath);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("sai_token123456", sent.RootElement.GetProperty("installToken").GetString());
        Assert.Equal("0000000000", sent.RootElement.GetProperty("licenseKey").GetString());
        Assert.Equal("fp-9", sent.RootElement.GetProperty("machineFingerprint").GetString());
        Assert.Equal("3.77.0", sent.RootElement.GetProperty("agentVersion").GetString());
    }

    [Fact]
    public async Task ExchangeAsync_ExpiredToken_403_Throws()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.Forbidden, """{"error":"token_expired"}"""));
        using var svc = new InstallTokenService("https://suavollc.com", handler);
        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.ExchangeAsync("sai_token123456", "PC-1", "fp", "3.77.0", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeAsync_SuccessFalse_Throws()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, """{"success":false,"error":"nope"}"""));
        using var svc = new InstallTokenService("https://suavollc.com", handler);
        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.ExchangeAsync("sai_token123456", "PC-1", "fp", "3.77.0", CancellationToken.None));
    }

    [Fact]
    public async Task ExchangeAsync_MalformedBody_Throws()
    {
        var handler = new QueueHandler(Json(HttpStatusCode.OK, "not json"));
        using var svc = new InstallTokenService("https://suavollc.com", handler);
        await Assert.ThrowsAnyAsync<Exception>(
            () => svc.ExchangeAsync("sai_token123456", "PC-1", "fp", "3.77.0", CancellationToken.None));
    }

    [Fact]
    public void Ctor_RejectsNonHttpsCloudUrl()
    {
        Assert.Throws<InvalidOperationException>(
            () => new InstallTokenService("http://insecure.example", new QueueHandler()));
    }

    [Fact]
    public void Result_ToString_RedactsApiKey()
    {
        var r = new InstallTokenExchangeResult("sagent_topsecret", "a-1", "p-1", "Queen");
        Assert.DoesNotContain("sagent_topsecret", r.ToString(), StringComparison.Ordinal);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        public QueueHandler(params HttpResponseMessage[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return _responses.Count > 0
                ? _responses.Dequeue()
                : Json(HttpStatusCode.OK, "{}");
        }
    }
}
