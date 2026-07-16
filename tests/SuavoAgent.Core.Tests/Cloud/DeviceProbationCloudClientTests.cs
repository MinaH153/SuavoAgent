using System.Net;
using System.Text;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class DeviceProbationCloudClientTests
{
    [Fact]
    public async Task SendHealthAsync_SignsEveryPhysicalCallWithFreshExactRequestNonce()
    {
        using var handler = new RecordingHandler();
        var apiKey = $"sagent_{new string('a', 64)}";
        using var client = new DeviceProbationCloudClient(
            new AgentOptions
            {
                ApiKey = apiKey,
                CloudUrl = "https://suavollc.com",
            },
            handler);
        var health = new DeviceProbationHealthFields(
            "ABCD-2345",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "33333333-3333-4333-8333-333333333333",
            "machine.fp-1",
            "3.92.2",
            new string('b', 64),
            new string('A', 43),
            HelperAttached: false,
            IpcConnected: false,
            ActuationReady: false,
            SqlConnected: true,
            SchemaCanaryGreen: true,
            PmsCode: "pms_schema_canary",
            SqlServerCertificateSha256: new string('c', 64),
            ObservedAtUtc: "2026-07-13T08:00:00.0000000Z",
            ChallengeCounter: 1);
        var signed = new SignedDeviceProbationHealth(
            health,
            new string('S', 86),
            new string('c', 64));

        Assert.Equal(
            DeviceProbationHealthSendOutcome.Accepted,
            await client.SendHealthAsync(signed, CancellationToken.None));
        Assert.Equal(
            DeviceProbationHealthSendOutcome.Accepted,
            await client.SendHealthAsync(signed, CancellationToken.None));

        Assert.Equal(2, handler.Attempts.Count);
        Assert.Equal(
            handler.Attempts[0].Body,
            handler.Attempts[1].Body);
        Assert.NotEqual(
            handler.Attempts[0].Nonce,
            handler.Attempts[1].Nonce);
        Assert.All(handler.Attempts, attempt =>
        {
            Assert.Equal(HttpMethod.Post, attempt.Method);
            Assert.Equal("/api/agent/device-token/probation-health", attempt.PathAndQuery);
            Assert.Equal("2", attempt.AuthVersion);
            Assert.Equal(apiKey, attempt.ApiKey);
            Assert.Equal(
                AgentRequestSigner.ComputeBodySha256(attempt.Body),
                attempt.ContentSha256);
            Assert.Equal(
                new AgentRequestSigner(apiKey).Sign(
                    "POST",
                    attempt.PathAndQuery,
                    attempt.Timestamp,
                    attempt.Nonce,
                    attempt.ContentSha256),
                attempt.Signature);
        });
    }

    [Theory]
    [InlineData("PROBATION_HEALTH_OBSERVATION_STALE", "RefreshObservation")]
    [InlineData("PROBATION_HEALTH_EXPIRED", "CredentialExpired")]
    [InlineData("PROBATION_HEALTH_REJECTED", "RetryExact")]
    public async Task SendHealthAsync_MapsOnlyDefinitiveServerCodesToStateChanges(
        string code,
        string expected)
    {
        using var handler = new FixedResponseHandler(
            HttpStatusCode.UnprocessableEntity,
            $$"""{"success":false,"code":"{{code}}"}""");
        using var client = new DeviceProbationCloudClient(
            new AgentOptions
            {
                ApiKey = "probation-test-key",
                CloudUrl = "https://suavollc.com",
            },
            handler);
        var health = new DeviceProbationHealthFields(
            "ABCD-2345",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "33333333-3333-4333-8333-333333333333",
            "machine.fp-1",
            "3.92.2",
            new string('b', 64),
            new string('A', 43),
            HelperAttached: false,
            IpcConnected: false,
            ActuationReady: false,
            SqlConnected: true,
            SchemaCanaryGreen: true,
            PmsCode: "pms_schema_canary",
            SqlServerCertificateSha256: new string('c', 64),
            ObservedAtUtc: "2026-07-13T08:00:00.0000000Z",
            ChallengeCounter: 1);

        var outcome = await client.SendHealthAsync(
            new SignedDeviceProbationHealth(
                health,
                new string('S', 86),
                new string('d', 64)),
            CancellationToken.None);

        Assert.Equal(expected, outcome.ToString());
    }

    private sealed record Attempt(
        HttpMethod Method,
        string PathAndQuery,
        string Body,
        string AuthVersion,
        string ApiKey,
        string Timestamp,
        string Nonce,
        string ContentSha256,
        string Signature);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<Attempt> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Attempts.Add(new(
                request.Method,
                request.RequestUri!.PathAndQuery,
                body,
                request.Headers.GetValues("x-agent-auth-version").Single(),
                request.Headers.GetValues("x-agent-api-key").Single(),
                request.Headers.GetValues("x-agent-timestamp").Single(),
                request.Headers.GetValues("x-agent-nonce").Single(),
                request.Headers.GetValues("x-agent-content-sha256").Single(),
                request.Headers.GetValues("x-agent-signature").Single()));
            var provisioningId =
                "11111111-1111-4111-8111-111111111111";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"success":true,"status":"probation_healthy","provisioningId":"{{provisioningId}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class FixedResponseHandler(
        HttpStatusCode statusCode,
        string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    "application/json"),
            });
    }
}
