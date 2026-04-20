using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// CloudSchemaAdaptationUploader is tested against a fake IPostSigner so we
/// can exercise every response shape without a live HTTP stack. This keeps
/// the uploader contract unambiguous: success vs. already-stored vs.
/// cloud-rejection vs. transport-failed all have distinct outcomes.
/// </summary>
public class CloudSchemaAdaptationUploaderTests
{
    private static SchemaAdaptation Sample()
    {
        using var ecdsa = ECDsa.Create();
        var packager = new SchemaAdaptationPackager(ecdsa, "adapt-v1");
        return packager.Pack(
            adaptationId: "adapt-123",
            pmsType: "PioneerRx",
            fromSchemaHash: "from", toSchemaHash: "to",
            deltas: Array.Empty<SchemaDelta>(),
            rewrites: new[]
            {
                new QueryRewrite("old", "SELECT 1 FROM x WHERE id = @p0", "new"),
            },
            originPharmacyId: "o",
            notBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));
    }

    private sealed class FakeSigner : IPostSigner
    {
        public JsonElement? Response { get; set; }
        public Func<JsonElement?>? ResponseFactory { get; set; }
        public Exception? Throws { get; set; }
        public int Calls { get; private set; }
        public string? LastPath { get; private set; }

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            Calls++;
            LastPath = path;
            if (Throws is not null) throw Throws;
            return Task.FromResult(ResponseFactory is not null ? ResponseFactory() : Response);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct)
            => PostSignedAsync(path, payload, ct);
    }

    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Upload_Success_ReturnsUploaded()
    {
        var signer = new FakeSigner { Response = J("{\"success\":true}") };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.Uploaded, result.Outcome);
        Assert.Equal("/api/agent/schema-adaptation", signer.LastPath);
    }

    [Fact]
    public async Task Upload_AlreadyStored_ReturnsAlreadyStored()
    {
        var signer = new FakeSigner
        {
            Response = J("{\"success\":true,\"alreadyStored\":true}"),
        };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.AlreadyStored, result.Outcome);
    }

    [Fact]
    public async Task Upload_Rejected_SurfaceErrorDetail()
    {
        var signer = new FakeSigner
        {
            Response = J("{\"success\":false,\"error\":\"Invalid JSON\"}"),
        };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.Rejected, result.Outcome);
        Assert.Equal("Invalid JSON", result.Detail);
    }

    [Fact]
    public async Task Upload_RejectedNoErrorField_ReturnsUnspecified()
    {
        var signer = new FakeSigner { Response = J("{\"success\":false}") };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.Rejected, result.Outcome);
        Assert.Equal("unspecified", result.Detail);
    }

    [Fact]
    public async Task Upload_NullResponseBody_ReturnsTransportFailed()
    {
        var signer = new FakeSigner { Response = null };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.TransportFailed, result.Outcome);
    }

    [Fact]
    public async Task Upload_HttpRequestException_ReturnsTransportFailed()
    {
        var signer = new FakeSigner { Throws = new HttpRequestException("connection refused") };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.TransportFailed, result.Outcome);
        Assert.Contains("connection refused", result.Detail);
    }

    [Fact]
    public async Task Upload_TaskCanceled_ReturnsTransportFailed()
    {
        var signer = new FakeSigner { Throws = new TaskCanceledException("timeout") };
        var uploader = new CloudSchemaAdaptationUploader(signer);

        var result = await uploader.UploadAsync(Sample(), CancellationToken.None);

        Assert.Equal(SchemaAdaptationUploadOutcome.TransportFailed, result.Outcome);
    }
}
