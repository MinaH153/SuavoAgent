using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// HTTP + ETag + outcome-classification coverage for
/// <see cref="AgentRulesetClient"/>. Uses a custom
/// <see cref="HttpMessageHandler"/> to inject canned responses and
/// observe request shape.
/// </summary>
public sealed class AgentRulesetClientTests
{
    private const string TestApiKey = "test-api-key-deadbeef";

    private static (AgentRulesetClient Client, FakeHandler Handler) BuildClient()
    {
        var handler = new FakeHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.suavollc.com") };
        var opts = new AgentOptions { ApiKey = TestApiKey, CloudUrl = "https://test.suavollc.com" };
        var client = new AgentRulesetClient(http, opts);
        return (client, handler);
    }

    private static RulesetV1 MakeRuleset(string version = "v1.2", int versionInt = 12)
    {
        return new RulesetV1
        {
            RulesetVersion = version,
            RulesetVersionInt = versionInt,
            KeyId = "test-key",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
    }

    private static string MakeEnvelopeJson(RulesetV1 ruleset, byte[] signature)
    {
        var envelope = new
        {
            ruleset,
            signature = Convert.ToBase64String(signature),
        };
        return JsonSerializer.Serialize(envelope);
    }

    [Fact]
    public async Task FetchAsync_with_200_returns_Updated_with_parsed_bundle()
    {
        var (client, handler) = BuildClient();
        var ruleset = MakeRuleset();
        var sig = new byte[] { 1, 2, 3, 4, 5 };
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MakeEnvelopeJson(ruleset, sig), Encoding.UTF8, "application/json"),
            Headers = { ETag = new EntityTagHeaderValue("\"etag-7\"") },
        };

        var result = await client.FetchAsync(currentETag: null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.Updated, result.Outcome);
        Assert.NotNull(result.Bundle);
        Assert.Equal("v1.2", result.Bundle!.Ruleset.RulesetVersion);
        Assert.Equal(12, result.Bundle.Ruleset.RulesetVersionInt);
        Assert.Equal(sig, result.Bundle.SignatureBytes);
        Assert.Equal("\"etag-7\"", result.Bundle.ETag);
        Assert.Null(client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_304_returns_NotModified()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.NotModified);

        var result = await client.FetchAsync(currentETag: "\"etag-5\"", CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.NotModified, result.Outcome);
        Assert.Null(result.Bundle);
        Assert.Null(client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_401_returns_AuthFailed_with_LastFailureKind()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.AuthFailed, result.Outcome);
        Assert.Null(result.Bundle);
        Assert.Equal("http_401_unauthorized", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_403_returns_AuthFailed()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.Forbidden);

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.AuthFailed, result.Outcome);
        Assert.Equal("http_403_unauthorized", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_500_returns_Transient()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.Transient, result.Outcome);
        Assert.Equal("http_500", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_network_exception_returns_Transient()
    {
        var (client, handler) = BuildClient();
        handler.NextException = new HttpRequestException("simulated DNS failure");

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.Transient, result.Outcome);
        Assert.NotNull(client.LastFailureKind);
        Assert.StartsWith("network_", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_malformed_json_returns_SchemaInvalid()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not valid json", Encoding.UTF8, "application/json"),
        };

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.SchemaInvalid, result.Outcome);
        Assert.Equal("schema_invalid_json", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_missing_signature_returns_SchemaInvalid()
    {
        var (client, handler) = BuildClient();
        var bodyJson = JsonSerializer.Serialize(new { ruleset = MakeRuleset(), signature = "" });
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.SchemaInvalid, result.Outcome);
        Assert.Equal("schema_invalid_missing_fields", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_with_invalid_signature_base64_returns_SchemaInvalid()
    {
        var (client, handler) = BuildClient();
        var bodyJson = JsonSerializer.Serialize(new { ruleset = MakeRuleset(), signature = "@@@not-base64@@@" });
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.SchemaInvalid, result.Outcome);
        Assert.Equal("schema_invalid_signature_base64", client.LastFailureKind);
    }

    // ── Coverage audit H1: JSON `null` literal body. JsonSerializer
    // deserializes "null" to a null object — the null-envelope branch is
    // present in AgentRulesetClient but was previously unreachable from
    // tests (only malformed-JSON and missing-fields were covered). ───────
    [Fact]
    public async Task FetchAsync_with_200_and_json_null_body_returns_schema_invalid()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };

        var result = await client.FetchAsync(null, CancellationToken.None);

        Assert.Equal(RulesetFetchOutcome.SchemaInvalid, result.Outcome);
        Assert.Null(result.Bundle);
        Assert.NotNull(client.LastFailureKind);
        Assert.StartsWith("schema_invalid", client.LastFailureKind);
    }

    [Fact]
    public async Task FetchAsync_sends_required_HMAC_headers()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.NotModified);

        await client.FetchAsync(null, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.True(handler.LastRequest!.Headers.Contains("x-agent-api-key"));
        Assert.True(handler.LastRequest.Headers.Contains("x-agent-timestamp"));
        Assert.True(handler.LastRequest.Headers.Contains("x-agent-signature"));
        Assert.Equal(TestApiKey, handler.LastRequest.Headers.GetValues("x-agent-api-key").Single());
    }

    [Fact]
    public async Task FetchAsync_sends_If_None_Match_when_currentETag_provided()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.NotModified);

        await client.FetchAsync(currentETag: "\"etag-99\"", CancellationToken.None);

        Assert.True(handler.LastRequest!.Headers.Contains("If-None-Match"));
        Assert.Equal("\"etag-99\"", handler.LastRequest.Headers.GetValues("If-None-Match").Single());
    }

    [Fact]
    public async Task FetchAsync_omits_If_None_Match_when_currentETag_null()
    {
        var (client, handler) = BuildClient();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.NotModified);

        await client.FetchAsync(currentETag: null, CancellationToken.None);

        Assert.False(handler.LastRequest!.Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task FetchAsync_cancellation_propagates()
    {
        var (client, handler) = BuildClient();
        handler.DelayBeforeResponse = TimeSpan.FromSeconds(30);
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
        };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => client.FetchAsync(null, cts.Token));
    }

    [Fact]
    public void Constructor_with_null_http_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AgentRulesetClient(http: null!, new AgentOptions { ApiKey = "k" }));
    }

    [Fact]
    public void Constructor_with_null_options_throws()
    {
        var http = new HttpClient();
        Assert.Throws<ArgumentNullException>(
            () => new AgentRulesetClient(http, options: null!));
    }

    [Fact]
    public void Constructor_with_missing_ApiKey_throws()
    {
        var http = new HttpClient();
        Assert.Throws<InvalidOperationException>(
            () => new AgentRulesetClient(http, new AgentOptions { ApiKey = null }));
    }

    // ── Fake HTTP handler ─────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpResponseMessage? NextResponse { get; set; }
        public Exception? NextException { get; set; }
        public TimeSpan DelayBeforeResponse { get; set; } = TimeSpan.Zero;
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (DelayBeforeResponse > TimeSpan.Zero)
            {
                await Task.Delay(DelayBeforeResponse, ct);
            }
            if (NextException is not null)
            {
                throw NextException;
            }
            return NextResponse ?? new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
