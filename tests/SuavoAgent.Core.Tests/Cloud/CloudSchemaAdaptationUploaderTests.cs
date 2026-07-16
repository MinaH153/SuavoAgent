using System.Security.Cryptography;
using System.Text.Json;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class CloudSchemaAdaptationUploaderTests
{
    [Fact]
    public async Task Upload_RetiredChannelRejectsWithoutNetwork()
    {
        var signer = new CountingSigner();
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.Rejected, result.Outcome);
        Assert.Equal("schema_adaptation_upload_retired", result.Detail);
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task Upload_CancellationRemainsObservable()
    {
        var uploader = new CloudSchemaAdaptationUploader(new CountingSigner());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            uploader.UploadAsync(Sample(), cancellation.Token));
    }

    private static SchemaAdaptation Sample()
    {
        using var ecdsa = ECDsa.Create();
        return new SchemaAdaptationPackager(ecdsa, "adapt-v1").Pack(
            adaptationId: "adapt-123",
            pmsType: "PioneerRx",
            fromSchemaHash: "from",
            toSchemaHash: "to",
            deltas: Array.Empty<SchemaDelta>(),
            rewrites:
            [
                new QueryRewrite(
                    "old",
                    "SELECT 1 FROM x WHERE id = @p0",
                    "new"),
            ],
            originPharmacyId: "opaque-origin",
            notBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));
    }

    private sealed class CountingSigner : IPostSigner
    {
        internal int Calls { get; private set; }

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<JsonElement?>(null);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<JsonElement?>(null);
        }
    }
}
