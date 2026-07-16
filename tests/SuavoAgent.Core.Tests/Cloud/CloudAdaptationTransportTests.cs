using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class CloudAdaptationTransportTests
{
    [Fact]
    public async Task Pull_RetiredChannelFailsExplicitlyBeforeNetwork()
    {
        var client = new NeverCalledSigner();
        var transport = new CloudAdaptationTransport(client, "opaque-public-key");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transport.PullAsync("PioneerRx", "schema-hash", CancellationToken.None));

        Assert.Equal("schema_adaptation_distribution_retired", exception.Message);
        Assert.Equal(0, client.Calls);
    }

    private sealed class NeverCalledSigner : IPostSigner
    {
        internal int Calls { get; private set; }

        public Task<System.Text.Json.JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<System.Text.Json.JsonElement?>(null);
        }

        public Task<System.Text.Json.JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<System.Text.Json.JsonElement?>(null);
        }
    }
}
