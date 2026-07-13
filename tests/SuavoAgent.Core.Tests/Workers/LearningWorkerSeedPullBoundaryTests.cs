using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class LearningWorkerTests
{
    private const string SeedCommandId = "11111111-1111-4111-8111-111111111111";
    private static readonly string SeedDeviceKeyId = new('b', 64);
    private static readonly string SeedSourceDigest = new('c', 64);

    [Theory]
    [InlineData("pattern", 'd', false)]
    [InlineData("model", 'e', true)]
    public async Task PullSeeds_ValidDeviceBoundEnvelopeAppliesAndPersistsReceipt(
        string phase,
        char digestCharacter,
        bool withCorrelations)
    {
        var sessionId = $"learning-pull-{phase}";
        MoveSessionTo(sessionId, phase);
        _db.UpsertObservedProcess(
            sessionId, "PioneerPharmacy.exe", "/opt/pms/PioneerPharmacy.exe",
            isPmsCandidate: true);
        var digest = new string(digestCharacter, 64);
        var response = AuthorizedResponse(
            CreateFakeSeedResponse(digest, phase, withCorrelations));
        var postSigner = new SeedResponsePostSigner(
            (_, _) => JsonSerializer.SerializeToElement(response));
        var seedClient = new SeedClient(postSigner);
        var authority = new SeedAuthoritySigner();
        using var services = new ServiceCollection()
            .AddSingleton<IDeviceAuthoritySigner>(authority)
            .BuildServiceProvider();
        var worker = CreateWorker(seedClient, services);
        SetField(worker, "_sessionId", sessionId);

        await InvokeTaskAsync(worker, "PullSeedsAsync", phase, CancellationToken.None);

        var request = Assert.IsType<SeedRequest>(Assert.Single(postSigner.Payloads));
        Assert.Equal("/api/agent/seed/pull", Assert.Single(postSigner.Paths));
        Assert.Equal("PioneerRx", request.AdapterType);
        Assert.Equal(phase, request.Phase);
        Assert.Equal(digest, Field<string>(worker, "_activeSeedDigest"));
        Assert.Equal(digest, Field<string>(worker, "_lastSeedDigest"));
        var applied = _db.GetLatestAppliedSeed(sessionId, phase);
        Assert.NotNull(applied);
        Assert.Equal(digest, applied!.SeedDigest);
        var receipt = _db.GetSeedApplicationReceipt(digest);
        Assert.NotNull(receipt);
        Assert.Equal(SeedCommandId, receipt!.Signed.Receipt.CommandId);
        Assert.Equal(SeedDeviceKeyId, receipt.Signed.KeyId);
        Assert.Equal(1, authority.SeedReceiptCount);
    }

    [Fact]
    public async Task PullSeeds_DifferentDeviceAuthorityKeyRejectsBeforeLocalApply()
    {
        const string sessionId = "learning-pull-wrong-key";
        MoveSessionTo(sessionId, "pattern");
        var response = AuthorizedResponse(
            CreateFakeSeedResponse(new string('f', 64), "pattern")) with
        {
            DeviceKeyId = new string('a', 64),
        };
        var seedClient = new SeedClient(new SeedResponsePostSigner(
            (_, _) => JsonSerializer.SerializeToElement(response)));
        using var services = new ServiceCollection()
            .AddSingleton<IDeviceAuthoritySigner>(new SeedAuthoritySigner())
            .BuildServiceProvider();
        var worker = CreateWorker(seedClient, services);
        SetField(worker, "_sessionId", sessionId);

        await InvokeTaskAsync(worker, "PullSeedsAsync", "pattern", CancellationToken.None);

        Assert.Null(Field<string>(worker, "_activeSeedDigest"));
        Assert.Null(_db.GetLatestAppliedSeed(sessionId, "pattern"));
        Assert.Null(_db.GetSeedApplicationReceipt(response.SeedDigest));
    }

    [Fact]
    public async Task PullSeeds_CloudNoChangeLeavesCurrentBindingUntouched()
    {
        const string sessionId = "learning-pull-no-change";
        MoveSessionTo(sessionId, "pattern");
        var seedClient = new SeedClient(new SeedResponsePostSigner((_, _) => null));
        using var services = new ServiceCollection()
            .AddSingleton<IDeviceAuthoritySigner>(new SeedAuthoritySigner())
            .BuildServiceProvider();
        var worker = CreateWorker(seedClient, services);
        SetField(worker, "_sessionId", sessionId);
        SetField(worker, "_lastSeedDigest", new string('9', 64));

        await InvokeTaskAsync(worker, "PullSeedsAsync", "pattern", CancellationToken.None);

        Assert.Null(Field<string>(worker, "_activeSeedDigest"));
        Assert.Equal(new string('9', 64), Field<string>(worker, "_lastSeedDigest"));
    }

    private void MoveSessionTo(string sessionId, string phase)
    {
        _db.CreateLearningSession(sessionId, _options.PharmacyId!);
        if (phase is "pattern" or "model")
            _db.UpdateLearningPhase(sessionId, "pattern");
        if (phase == "model")
            _db.UpdateLearningPhase(sessionId, "model");
    }

    private static SeedResponse AuthorizedResponse(SeedResponse response) => response with
    {
        CommandId = SeedCommandId,
        DeviceKeyId = SeedDeviceKeyId,
        SourceManifestDigest = SeedSourceDigest,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
    };

    private sealed class SeedResponsePostSigner : IPostSigner
    {
        private readonly Func<string, object, JsonElement?> _response;
        internal List<string> Paths { get; } = [];
        internal List<object> Payloads { get; } = [];

        internal SeedResponsePostSigner(Func<string, object, JsonElement?> response) =>
            _response = response;

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct) => Respond(path, payload);

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => Respond(path, payload);

        private Task<JsonElement?> Respond(string path, object payload)
        {
            Paths.Add(path);
            Payloads.Add(payload);
            return Task.FromResult(_response(path, payload));
        }
    }

    private sealed class SeedAuthoritySigner : IDeviceAuthoritySigner
    {
        internal int SeedReceiptCount { get; private set; }
        public string KeyId => SeedDeviceKeyId;

        public SignedDeviceReceipt<SeedApplicationDeviceReceipt> Sign(
            SeedApplicationDeviceReceipt receipt)
        {
            SeedReceiptCount++;
            return new(
                receipt, KeyId, "seed-signature", new string('8', 64));
        }

        public SignedDeviceReceipt<PomActivationDeviceReceipt> Sign(
            PomActivationDeviceReceipt receipt) => throw new NotSupportedException();

        public SignedDeviceReceipt<RxSourceDeviceReceipt> Sign(
            RxSourceDeviceReceipt receipt) => throw new NotSupportedException();

        public SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Sign(
            AutonomyEvidenceDeviceReceipt receipt) => throw new NotSupportedException();

        public SignedDeviceProvisioningProof SignProvisioningProof(
            DeviceProvisioningProofPayload proof) => throw new NotSupportedException();

        public SignedDeviceProbationHealth SignProbationHealth(
            DeviceProbationHealthFields health) => throw new NotSupportedException();

        public void Dispose() { }
    }
}
