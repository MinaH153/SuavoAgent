using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class HmacSignerTests
{
    private const string Timestamp = "1783728600123";
    private static readonly string Nonce = new('A', 43);
    private static readonly string SecondNonce =
        Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Sign_MatchesCloudCrossRuntimeConformanceVector()
    {
        var signer = new HmacSigner($"sagent_{new string('a', 64)}");
        const string body = "{\"healthy\":true}";
        var digest = AgentRequestSigner.ComputeBodySha256(body);

        Assert.Equal(
            "e8c5c4ebde822d11daf0a40051dde9c30aa8b6f2d6306b664722306d68f68ea0",
            digest);
        Assert.Equal(
            "edbab546a4da49c2e9a3d428d6d7eefc6daa49d3f60f83ac48a764d87453c718",
            signer.Sign(
                "POST",
                "/api/agent/heartbeat?lane=own_fleet&lane=manual",
                Timestamp,
                Nonce,
                digest));
    }

    [Fact]
    public void Sign_BindsEveryExactRequestComponent()
    {
        var signer = new HmacSigner("test-key");
        var digest = AgentRequestSigner.ComputeBodySha256("body");
        var baseline = signer.Sign(
            "POST", "/api/agent/heartbeat?lane=own_fleet", Timestamp, Nonce, digest);

        Assert.Equal(
            baseline,
            signer.Sign("POST", "/api/agent/heartbeat?lane=own_fleet", Timestamp, Nonce, digest));
        Assert.NotEqual(
            baseline,
            signer.Sign("PATCH", "/api/agent/heartbeat?lane=own_fleet", Timestamp, Nonce, digest));
        Assert.NotEqual(
            baseline,
            signer.Sign("POST", "/api/agent/config?lane=own_fleet", Timestamp, Nonce, digest));
        Assert.NotEqual(
            baseline,
            signer.Sign("POST", "/api/agent/heartbeat?own_fleet=lane", Timestamp, Nonce, digest));
        Assert.NotEqual(
            baseline,
            signer.Sign("POST", "/api/agent/heartbeat?lane=own_fleet", Timestamp, SecondNonce, digest));
        Assert.NotEqual(
            baseline,
            signer.Sign(
                "POST",
                "/api/agent/heartbeat?lane=own_fleet",
                Timestamp,
                Nonce,
                AgentRequestSigner.ComputeBodySha256("changed")));
    }

    [Fact]
    public void ApplyHeaders_ProducesStrictV2EnvelopeAndFreshNonce()
    {
        var signer = new HmacSigner("agent-secret");
        using var first = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/agent/heartbeat?lane=own_fleet");
        using var second = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/agent/heartbeat?lane=own_fleet");

        var firstAuth = signer.ApplyHeaders(first, "{}");
        var secondAuth = signer.ApplyHeaders(second, "{}");

        Assert.Equal("2", Assert.Single(first.Headers.GetValues("x-agent-auth-version")));
        Assert.Equal("agent-secret", Assert.Single(first.Headers.GetValues("x-agent-api-key")));
        Assert.Matches("^[0-9]{13}$", firstAuth.Timestamp);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", firstAuth.Nonce);
        Assert.Matches("^[a-f0-9]{64}$", firstAuth.ContentSha256);
        Assert.Matches("^[a-f0-9]{64}$", firstAuth.Signature);
        Assert.NotEqual(firstAuth.Nonce, secondAuth.Nonce);
        Assert.Equal(
            firstAuth.Signature,
            signer.Sign(
                "POST",
                "/api/agent/heartbeat?lane=own_fleet",
                firstAuth.Timestamp,
                firstAuth.Nonce,
                firstAuth.ContentSha256));
    }

    [Fact]
    public void IsWithinReplayWindow_RequiresEpochMilliseconds()
    {
        Assert.True(HmacSigner.IsWithinReplayWindow(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            TimeSpan.FromSeconds(60)));
        Assert.False(HmacSigner.IsWithinReplayWindow(
            DateTimeOffset.UtcNow.ToString("o"),
            TimeSpan.FromSeconds(60)));
        Assert.False(HmacSigner.IsWithinReplayWindow(
            DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds().ToString(),
            TimeSpan.FromSeconds(60)));
    }
}
