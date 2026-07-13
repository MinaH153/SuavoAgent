using System.Net;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class SuavoCloudClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"accepted\":true,\"jobId\":\"job\",\"recorded\":1}")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "{\"accepted\":false,\"terminal\":true,\"code\":\"pricing_result_payload_invalid\",\"error\":\"Pricing result payload is invalid\"}")]
    public async Task PricingResponseVerifier_AcceptsExactSignedSuccessAndErrorBytes(
        HttpStatusCode status,
        string body)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(body),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-Response-Signature", signature);
        using var handler = new RecordingHandler(response);
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        var verified = await client.PostSignedResponseVerifiedAsync(
            "/api/agent/pricing-jobs/job/results",
            new { status = "completed", items = Array.Empty<object>() },
            CancellationToken.None);

        Assert.NotNull(verified);
        Assert.Equal((int)status, verified!.StatusCode);
        Assert.Equal(body, verified.Body);
        Assert.Equal(signature, verified.SignatureBase64);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                .ToLowerInvariant(),
            verified.BodySha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PricingResponseVerifier_RejectsUnsignedOrForgedResponse(bool forged)
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string body = "{\"accepted\":true,\"jobId\":\"job\",\"recorded\":1}";
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (forged)
        {
            response.Headers.Add(
                "X-Response-Signature",
                Convert.ToBase64String(attackerKey.SignData(
                    Encoding.UTF8.GetBytes(body),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        }
        using var handler = new RecordingHandler(response);
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler,
            Convert.ToBase64String(trustedKey.ExportSubjectPublicKeyInfo()));

        Assert.Null(await client.PostSignedResponseVerifiedAsync(
            "/api/agent/pricing-jobs/job/results",
            new { status = "completed", items = Array.Empty<object>() },
            CancellationToken.None));
    }

    [Fact]
    public async Task PricingResponseVerifier_RejectsBodyAboveBoundedReadCeiling()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = new string('x', 16 * 1024 + 1);
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };
        response.Headers.Add(
            "X-Response-Signature",
            Convert.ToBase64String(key.SignData(
                Encoding.UTF8.GetBytes(body),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
        using var handler = new RecordingHandler(response);
        using var client = new SuavoCloudClient(
            new AgentOptions
            {
                ApiKey = "agent-secret",
                CloudUrl = "https://suavollc.com",
            },
            handler,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        Assert.Null(await client.PostSignedResponseVerifiedAsync(
            "/api/agent/pricing-jobs/job/results",
            new { status = "completed", items = Array.Empty<object>() },
            CancellationToken.None));
    }
}
