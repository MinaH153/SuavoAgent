using System.Net;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class DeviceTokenConfirmationTests
{
    private const string ProvisioningId = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task ConfirmAsync_BindsExactProvisioningIdAndHmac()
    {
        var handler = new RecordingHandler(_ => Json(
            """{"success":true,"status":"confirmed","provisioningId":"11111111-1111-4111-8111-111111111111"}"""));

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(),
            ProvisioningId,
            CancellationToken.None,
            handler,
            NoDelay,
            Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Promoted, outcome);
        Assert.Equal(1, handler.Calls);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(7, body.RootElement.EnumerateObject().Count());
        Assert.Equal("ABCD-2345", body.RootElement.GetProperty("deviceCode").GetString());
        Assert.Equal(ProvisioningId, body.RootElement.GetProperty("provisioningId").GetString());
        Assert.Equal(Signature, body.RootElement.GetProperty("signature").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            body.RootElement.GetProperty("sqlServerCertificateSha256").ValueKind);
        Assert.Equal("2", handler.LastAuthVersion);
        var expected = new AgentRequestSigner(Config().ApiKey).Sign(
            "POST",
            "/api/agent/device-token/confirm",
            handler.LastTimestamp!,
            handler.LastNonce!,
            handler.LastContentSha256!);
        Assert.Equal(expected, handler.LastSignature);
    }

    [Fact]
    public async Task ConfirmAsync_SendsOnlySqlCertificateDigestWhenEnrolled()
    {
        var handler = new RecordingHandler(_ => Json(
            """{"success":true,"status":"confirmed","provisioningId":"11111111-1111-4111-8111-111111111111"}"""));
        var digest = new string('a', 64);

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(),
            ProvisioningId,
            CancellationToken.None,
            handler,
            NoDelay,
            Readiness(digest),
            digest);

        Assert.Equal(AuthorityPromotionOutcome.Promoted, outcome);
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(7, body.RootElement.EnumerateObject().Count());
        Assert.Equal(digest, body.RootElement.GetProperty("sqlServerCertificateSha256").GetString());
        Assert.DoesNotContain("BEGIN CERTIFICATE", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("publicKey", handler.LastBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmAsync_RetriesResponseLossWithSameProvisioningIdentity()
    {
        var handler = new RecordingHandler(call => call == 1
            ? throw new HttpRequestException("response lost after apply")
            : Json(
                """{"success":true,"status":"confirmed","provisioningId":"11111111-1111-4111-8111-111111111111"}"""));

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(),
            ProvisioningId,
            CancellationToken.None,
            handler,
            NoDelay,
            Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Promoted, outcome);
        Assert.Equal(2, handler.Calls);
        Assert.Contains(ProvisioningId, handler.LastBody!, StringComparison.Ordinal);
        Assert.Single(handler.Bodies.Distinct(StringComparer.Ordinal));
        Assert.Equal(2, handler.Nonces.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"success\":true}")]
    [InlineData("{\"success\":true,\"status\":\"confirmed\",\"provisioningId\":\"22222222-2222-4222-8222-222222222222\"}")]
    public async Task ConfirmAsync_RejectsEmptyOrMismatchedAcceptance(string responseBody)
    {
        var handler = new RecordingHandler(_ => Json(responseBody));

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(),
            ProvisioningId,
            CancellationToken.None,
            handler,
            NoDelay,
            Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Unknown, outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "PAIRING_CONFIRMATION_INVALID")]
    [InlineData(HttpStatusCode.Unauthorized, "PAIRING_CONFIRMATION_AUTH_INVALID")]
    [InlineData(HttpStatusCode.NotFound, "PAIRING_CONFIRMATION_NOT_FOUND")]
    [InlineData(HttpStatusCode.Gone, "PAIRING_CONFIRMATION_EXPIRED")]
    [InlineData(HttpStatusCode.PreconditionFailed, "PAIRING_CONFIRMATION_BAA_REQUIRED")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "PAIRING_CONFIRMATION_PROOF_INVALID")]
    public async Task ConfirmAsync_ClassifiesExactDeterministicRejection(
        HttpStatusCode status,
        string code)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                $$"""{"success":false,"status":"rejected","code":"{{code}}","error":"rejected"}""",
                Encoding.UTF8,
                "application/json"),
        });

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(), ProvisioningId, CancellationToken.None, handler, NoDelay, Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Rejected, outcome);
    }

    [Fact]
    public async Task ConfirmAsync_RejectsOversizedChunkedResponseBeforeAllocation()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedContent(new byte[16 * 1024 + 1]),
        });

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(), ProvisioningId, CancellationToken.None, handler, NoDelay, Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Unknown, outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConfirmAsync_RejectsInvalidUtf8Response()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d]),
        });

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(), ProvisioningId, CancellationToken.None, handler, NoDelay, Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Unknown, outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ConfirmAsync_DoesNotFollowOffOriginRedirect()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = Json("{}");
            response.StatusCode = HttpStatusCode.TemporaryRedirect;
            response.Headers.Location = new Uri("https://evil.example/steal-confirmation");
            return response;
        });

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            Config(), ProvisioningId, CancellationToken.None, handler, NoDelay, Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Unknown, outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://user@suavollc.com")]
    [InlineData("https://suavollc.com/evil")]
    [InlineData("https://suavollc.com?next=evil")]
    public async Task ConfirmAsync_RejectsCloudUrlThatIsNotAnExactOrigin(string cloudUrl)
    {
        var config = Config() with { CloudUrl = cloudUrl };
        var handler = new RecordingHandler(_ => throw new InvalidOperationException());

        var outcome = await DeviceTokenConfirmation.ConfirmAsync(
            config, ProvisioningId, CancellationToken.None, handler, NoDelay, Readiness());

        Assert.Equal(AuthorityPromotionOutcome.Rejected, outcome);
        Assert.Equal(0, handler.Calls);
    }

    private static SetupConfig Config() => new(
        PharmacyId: PharmacyId,
        ApiKey: "sagent_test_key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.80.0",
        LearningMode: false,
        AgentId: "11111111-1111-4111-8111-111111111111",
        DeviceCode: "ABCD-2345",
        DeviceKeyId: KeyId,
        DeviceKeyName: "Suavo.Agent.DeviceAuthority.test",
        DeviceFingerprint: Fingerprint,
        DeviceChallenge: Challenge);

    private static string Readiness(string? sqlServerCertificateSha256 = null)
    {
        var fields = new DeviceProvisioningProofFields(
            "ABCD-2345",
            ProvisioningId,
            AgentId,
            PharmacyId,
            Fingerprint,
            KeyId,
            Challenge,
            sqlServerCertificateSha256);
        return JsonSerializer.Serialize(new
        {
            deviceProof = new
            {
                deviceCode = fields.DeviceCode,
                provisioningId = fields.ProvisioningId,
                agentId = fields.AgentId,
                pharmacyId = fields.PharmacyId,
                fingerprint = fields.Fingerprint,
                keyId = fields.KeyId,
                challenge = fields.Challenge,
                sqlServerCertificateSha256 = fields.SqlServerCertificateSha256,
                signature = Signature,
                canonicalDigest = DeviceProvisioningProofCanonical.Digest(fields),
            },
        });
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(
        Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal string? LastBody { get; private set; }
        internal string? LastAuthVersion { get; private set; }
        internal string? LastTimestamp { get; private set; }
        internal string? LastNonce { get; private set; }
        internal string? LastContentSha256 { get; private set; }
        internal string? LastSignature { get; private set; }
        internal List<string> Bodies { get; } = [];
        internal List<string> Nonces { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(LastBody);
            LastAuthVersion = request.Headers.GetValues("x-agent-auth-version").Single();
            LastTimestamp = request.Headers.GetValues("x-agent-timestamp").Single();
            LastNonce = request.Headers.GetValues("x-agent-nonce").Single();
            Nonces.Add(LastNonce);
            LastContentSha256 = request.Headers.GetValues("x-agent-content-sha256").Single();
            LastSignature = request.Headers.GetValues("x-agent-signature").Single();
            return response(Calls);
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

    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string Fingerprint = "machine-fingerprint";
    private const string KeyId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Challenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Signature = Convert.ToBase64String(new byte[64])
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
