using System.Text.Json;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class SeedClientTests
{
    private static SeedResponse MakeResponse(string digest = "digest-1", string phase = "pattern") =>
        new(digest, 1, phase, new[] { "schema" }, null,
            null, Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

    [Fact]
    public async Task PullAsync_PostsToCorrectEndpoint()
    {
        string? capturedPath = null;
        var client = new SeedClient(new FakePostSigner((path, _) =>
        {
            capturedPath = path;
            return JsonSerializer.SerializeToElement(MakeResponse());
        }));

        var req = new SeedRequest("PioneerRx", "pattern", "fp-1", "ver-1", Array.Empty<string>(), null);
        await client.PullAsync(req, CancellationToken.None);

        Assert.Equal("/api/agent/seed/pull", capturedPath);
    }

    [Fact]
    public async Task PullAsync_Returns304_ReturnsNull()
    {
        var client = new SeedClient(new FakePostSigner((_, _) => null));

        var req = new SeedRequest("PioneerRx", "pattern", "fp-1", "ver-1", Array.Empty<string>(), "digest-1");
        var result = await client.PullAsync(req, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PullAsync_DeserializesResponse()
    {
        var expected = MakeResponse("digest-abc", "model");
        var client = new SeedClient(new FakePostSigner((_, _) => JsonSerializer.SerializeToElement(expected)));

        var req = new SeedRequest("PioneerRx", "model", "fp-1", "ver-1", new[] { "t1", "t2" }, null);
        var result = await client.PullAsync(req, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("digest-abc", result!.SeedDigest);
        Assert.Equal("model", result.Phase);
    }

    [Fact]
    public async Task ConfirmAsync_PostsToCorrectEndpoint()
    {
        string? capturedPath = null;
        var client = new SeedClient(new FakePostSigner((path, _) =>
        {
            capturedPath = path;
            return JsonSerializer.SerializeToElement(new { ok = true });
        }));

        await client.ConfirmAsync(new SeedConfirmRequest("d-1", "2026-04-14T00:00:00Z", 5, 2), CancellationToken.None);
        Assert.Equal("/api/agent/seed/confirm", capturedPath);
    }
}

internal sealed class FakePostSigner : IPostSigner
{
    private readonly Func<string, object, JsonElement?> _handler;
    public FakePostSigner(Func<string, object, JsonElement?> handler) => _handler = handler;
    public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct) =>
        Task.FromResult(_handler(path, payload));
}
