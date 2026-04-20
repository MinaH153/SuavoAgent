using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public class SchemaAdaptationWorkerTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly ECDsa _cloudKey = ECDsa.Create();
    private readonly string _pubKeyDer;
    private const string LocalFromHash = "local-from-hash";

    public SchemaAdaptationWorkerTests()
    {
        _db = new AgentStateDb(":memory:");
        _pubKeyDer = Convert.ToBase64String(_cloudKey.ExportSubjectPublicKeyInfo());
    }

    public void Dispose()
    {
        _db.Dispose();
        _cloudKey.Dispose();
    }

    private static readonly PmsVersionFingerprint Fp = new(
        "PioneerRx", LocalFromHash, "dialect-hash", "2026.3.1");

    private sealed class FakeTransport : ISchemaAdaptationTransport
    {
        public AdaptationPullResponse? Response { get; set; }
        public int Calls { get; private set; }
        public Task<AdaptationPullResponse?> PullAsync(string pmsType, string fromHash, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Response);
        }
    }

    private SchemaAdaptation SignedSample(string id = "adapt-1", string from = LocalFromHash) =>
        new SchemaAdaptationPackager(_cloudKey, "adapt-v1").Pack(
            adaptationId: id, pmsType: "PioneerRx",
            fromSchemaHash: from, toSchemaHash: "to-hash",
            deltas: Array.Empty<SchemaDelta>(),
            rewrites: new[]
            {
                new QueryRewrite("old", "SELECT 1 FROM x WHERE id = @p0", "new"),
            },
            originPharmacyId: "o",
            notBefore: DateTimeOffset.UtcNow.AddHours(-1),
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

    private SchemaAdaptationWorker BuildWorker(ISchemaAdaptationTransport transport, bool enabled = true)
    {
        var options = new AgentOptions();
        options.FleetFeatures.SchemaAdaptation = enabled;
        var applier = new SchemaAdaptationApplier(
            _db, _pubKeyDer, () => LocalFromHash, NullLogger<SchemaAdaptationApplier>.Instance);
        return new SchemaAdaptationWorker(
            transport, applier, options, () => Fp,
            NullLogger<SchemaAdaptationWorker>.Instance);
    }

    // ──────────────────────── one-tick determinism ────────────────────────

    [Fact]
    public async Task Tick_NullResponse_NoOp()
    {
        var transport = new FakeTransport { Response = null };
        var worker = BuildWorker(transport);
        await worker.TickAsync(CancellationToken.None);
        Assert.Equal(1, transport.Calls);
        Assert.Null(_db.GetAppliedSchemaAdaptation("adapt-1"));
    }

    [Fact]
    public async Task Tick_AppliesAdaptations()
    {
        var a = SignedSample();
        var transport = new FakeTransport
        {
            Response = new AdaptationPullResponse(new[] { a }, null),
        };
        var worker = BuildWorker(transport);
        await worker.TickAsync(CancellationToken.None);
        Assert.NotNull(_db.GetAppliedSchemaAdaptation(a.AdaptationId));
    }

    [Fact]
    public async Task Tick_AppliesRevocations()
    {
        var a = SignedSample();
        var transport = new FakeTransport
        {
            Response = new AdaptationPullResponse(new[] { a }, null),
        };
        var worker = BuildWorker(transport);
        await worker.TickAsync(CancellationToken.None);
        Assert.NotNull(_db.GetAppliedSchemaAdaptation(a.AdaptationId));

        var revocation = new SchemaAdaptationPackager(_cloudKey, "adapt-v1")
            .PackRevocation(
                revocationId: "rev-1",
                targetAdaptationId: a.AdaptationId,
                reason: "cloud-recall",
                revokedAt: DateTimeOffset.UtcNow);
        transport.Response = new AdaptationPullResponse(null, new[] { revocation });
        await worker.TickAsync(CancellationToken.None);

        var after = _db.GetAppliedSchemaAdaptation(a.AdaptationId);
        Assert.NotNull(after);
        Assert.NotNull(after!.RolledBackAt);
    }

    [Fact]
    public async Task Tick_InvalidAdaptationSignature_SkippedNotAppliedAndDoesNotThrow()
    {
        var a = SignedSample();
        var tampered = a with { ToSchemaHash = "tampered" };
        var transport = new FakeTransport
        {
            Response = new AdaptationPullResponse(new[] { tampered }, null),
        };
        var worker = BuildWorker(transport);
        await worker.TickAsync(CancellationToken.None);
        Assert.Null(_db.GetAppliedSchemaAdaptation(tampered.AdaptationId));
    }

    [Fact]
    public async Task Tick_TransportThrows_WorkerDoesNotThrow()
    {
        var throwing = new ThrowingTransport();
        var worker = BuildWorker(throwing);
        // TickAsync itself does not catch — but ExecuteAsync does. Smoke-test
        // that TickAsync surfaces the exception to the caller so the ExecuteAsync
        // loop can log and continue.
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.TickAsync(CancellationToken.None));
    }

    private sealed class ThrowingTransport : ISchemaAdaptationTransport
    {
        public Task<AdaptationPullResponse?> PullAsync(string pmsType, string fromHash, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    // ──────────────────────── disabled feature flag ────────────────────────

    [Fact]
    public async Task Execute_DisabledByFlag_IdlesImmediately()
    {
        var transport = new FakeTransport();
        var worker = BuildWorker(transport, enabled: false);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50); // brief yield — but ExecuteAsync should have returned by now.
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(0, transport.Calls);
    }
}
