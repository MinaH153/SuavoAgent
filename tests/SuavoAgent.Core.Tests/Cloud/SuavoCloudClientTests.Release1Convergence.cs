using System.Net;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class SuavoCloudClientTests
{
    [Fact]
    public async Task Release1InstallUploadUsesExactBodyPathAndHmac()
    {
        const string body =
            "{\"installReceipt\":{},\"installReceiptSignatureBase64Url\":\"" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "\",\"maintenancePublicKeySpkiDerBase64\":\"AQID\"}\n";
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true}"),
        });
        using var client = Release1Client(handler);

        Assert.True(await client.SendRelease1InstallReceiptAsync(
            body,
            CancellationToken.None));

        Assert.Equal("/api/agent/release1/install-receipt", handler.LastPath);
        Assert.Equal(body, handler.LastBody);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
                .ToLowerInvariant(),
            handler.LastContentSha256);
        Assert.NotNull(handler.LastSignature);
        Assert.NotNull(handler.LastNonce);
    }

    [Fact]
    public async Task Release1PreliminaryRequiresExactResponseAndReturnsCommandId()
    {
        const string commandId = "11111111-1111-4111-8111-111111111111";
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"success\":true,\"data\":{{\"commandId\":\"{commandId}\"}}}}"),
        });
        using var client = Release1Client(handler);
        const string body =
            "{\"proof\":{},\"proofSignatureBase64Url\":\"" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "\"}\n";

        Assert.Equal(commandId, await client.SendRelease1PreliminaryAsync(
            body,
            CancellationToken.None));
        Assert.Equal("/api/agent/release1/preliminary", handler.LastPath);
        Assert.Equal(body, handler.LastBody);
    }

    [Fact]
    public async Task Release1AckUsesExactNoErrorBodyAndFinalRequiresHttp200()
    {
        const string commandId = "22222222-2222-4222-8222-222222222222";
        const string ackBody =
            "{\"result\":{\"commandId\":\"22222222-2222-4222-8222-222222222222\",\"inventorySha256\":\"" +
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" +
            "\"},\"status\":\"executed\"}\n";
        var ackHandler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty),
        });
        using (var client = Release1Client(ackHandler))
        {
            Assert.True(await client.AckRelease1ChallengeAsync(
                commandId,
                ackBody,
                CancellationToken.None));
        }
        Assert.Equal($"/api/agent/commands/{commandId}/ack", ackHandler.LastPath);
        Assert.Equal(ackBody, ackHandler.LastBody);
        Assert.DoesNotContain("error", ackHandler.LastBody, StringComparison.Ordinal);

        var non200 = new FixedHandler(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"success\":true}"),
        });
        using var rejected = Release1Client(non200);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            rejected.SendRelease1FinalAsync(
                "{\"attestation\":{},\"attestationSignatureBase64Url\":\"" +
                new string('A', 86) +
                "\",\"installReceiptSignatureBase64Url\":\"" +
                new string('A', 86) + "\"}\n",
                CancellationToken.None));
    }

    [Fact]
    public async Task Release1SuccessResponseRejectsUnknownFields()
    {
        var handler = new FixedHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true,\"extra\":true}"),
        });
        using var client = Release1Client(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SendRelease1InstallReceiptAsync(
                "{\"installReceipt\":{},\"installReceiptSignatureBase64Url\":\"" +
                new string('A', 86) +
                "\",\"maintenancePublicKeySpkiDerBase64\":\"AQID\"}\n",
                CancellationToken.None));
    }

    private static SuavoCloudClient Release1Client(HttpMessageHandler handler) => new(
        new AgentOptions
        {
            ApiKey = "release1-test-secret",
            CloudUrl = "https://suavollc.com",
            StrictOutboundTokenAllowlist = true,
        },
        handler);
}
