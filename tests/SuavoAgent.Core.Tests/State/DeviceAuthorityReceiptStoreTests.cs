using System.Text.Json;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class DeviceAuthorityReceiptStoreTests : IDisposable
{
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";
    private const string PomId = "44444444-4444-4444-8444-444444444444";
    private const string ApprovedBy = "55555555-5555-4555-8555-555555555555";
    private const string SessionId = "learn-device-authority-20260710120000";
    private static readonly string TemplateDigest = new('a', 64);

    private readonly AgentStateDb _db = new(":memory:");
    private readonly InMemoryDeviceAttestationKeyProvider _keys = new();
    private readonly AgentOptions _options = new()
    {
        AgentId = AgentId,
        PharmacyId = PharmacyId,
        MachineFingerprint = "device-authority-state-test",
    };

    public DeviceAuthorityReceiptStoreTests()
    {
        using var pending = _keys.OpenOrCreate(_options.MachineFingerprint!);
        _keys.CommitPending(_options.MachineFingerprint!, pending.Enrollment.KeyId);
    }

    public void Dispose()
    {
        _keys.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void PomReceipt_CommitsBeforeNetworkAndExactRetryReturnsSameSignedBytes()
    {
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        var command = Command();
        var terminal = _db.CompletePomApproval(
            command,
            succeeded: true,
            "pom_approval_activated");
        var ledger = _db.GetPomApprovalLedger(CommandId)!;

        var first = _db.GetOrCreatePomDeviceReceipt(
            command, terminal, ledger, _options, signer);
        var retry = _db.GetOrCreatePomDeviceReceipt(
            command, terminal, ledger, _options, signer);

        Assert.Equal(1, first.Signed.Receipt.Counter);
        Assert.Equal(first.Signed, retry.Signed);
        Assert.False(first.Accepted);
        _db.MarkPomDeviceReceiptAccepted(
            CommandId,
            "66666666-6666-4666-8666-666666666666");
        var accepted = _db.GetOrCreatePomDeviceReceipt(
            command, terminal, ledger, _options, signer);
        Assert.True(accepted.Accepted);
        Assert.Equal(
            "66666666-6666-4666-8666-666666666666",
            accepted.SourceBindingId);
    }

    [Fact]
    public void PomReceipt_SameCommandWithDifferentDeviceKeyFailsClosed()
    {
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        var command = Command();
        var terminal = _db.CompletePomApproval(
            command,
            succeeded: true,
            "pom_approval_activated");
        var ledger = _db.GetPomApprovalLedger(CommandId)!;
        _db.GetOrCreatePomDeviceReceipt(command, terminal, ledger, _options, signer);

        using var otherKeys = new InMemoryDeviceAttestationKeyProvider();
        using (var pending = otherKeys.OpenOrCreate(_options.MachineFingerprint!))
            otherKeys.CommitPending(_options.MachineFingerprint!, pending.Enrollment.KeyId);
        using var otherSigner = new DeviceAuthoritySigner(_options, otherKeys);

        Assert.Throws<InvalidOperationException>(() => _db.GetOrCreatePomDeviceReceipt(
            command, terminal, ledger, _options, otherSigner));
    }

    [Fact]
    public void RxReceipt_ExactRetryIsStableAndNewBatchAdvancesCounter()
    {
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        var binding = new AgentStateDb.CloudLearnedSourceBinding(
            "66666666-6666-4666-8666-666666666666",
            PomId,
            SessionId,
            new string('b', 64),
            TemplateDigest);
        var firstDigest = new string('c', 64);
        var secondDigest = new string('d', 64);

        var first = _db.GetOrCreateRxDeviceReceipt(
            firstDigest, binding, _options, signer);
        var retry = _db.GetOrCreateRxDeviceReceipt(
            firstDigest, binding, _options, signer);
        var second = _db.GetOrCreateRxDeviceReceipt(
            secondDigest, binding, _options, signer);

        Assert.Equal(first.Signed, retry.Signed);
        Assert.Equal(1, first.Signed.Receipt.Counter);
        Assert.Equal(2, second.Signed.Receipt.Counter);
        _db.MarkRxDeviceReceiptAccepted(firstDigest);
        Assert.True(_db.GetOrCreateRxDeviceReceipt(
            firstDigest, binding, _options, signer).Accepted);
    }

    [Fact]
    public void SeedReceipt_CommitsExactDeviceBoundBytesBeforeCloudConfirmation()
    {
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        var response = SeedResponseFor(CommandId, signer.KeyId, new string('c', 64));

        var first = _db.GetOrCreateSeedApplicationReceipt(
            response, _options, SessionId, 5, 2, signer);
        var retry = _db.GetOrCreateSeedApplicationReceipt(
            response, _options, SessionId, 99, 99, signer);

        Assert.Equal(first.Signed, retry.Signed);
        Assert.Equal(1, first.Signed.Receipt.Counter);
        Assert.Equal(5, first.Signed.Receipt.CorrelationsApplied);
        Assert.Equal(2, first.Signed.Receipt.CorrelationsSkipped);
        Assert.False(first.Accepted);

        _db.MarkSeedApplicationReceiptAccepted(CommandId);
        var accepted = _db.GetSeedApplicationReceipt(response.SeedDigest);
        Assert.NotNull(accepted);
        Assert.True(accepted!.Accepted);
        Assert.Equal(first.Signed, accepted.Signed);

        var divergent = response with { SourceManifestDigest = new string('d', 64) };
        Assert.Throws<InvalidOperationException>(() =>
            _db.GetOrCreateSeedApplicationReceipt(
                divergent, _options, SessionId, 5, 2, signer));
    }

    [Fact]
    public void SeedReceipt_NewIssuedCommandAdvancesDeviceReceiptCounter()
    {
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        var first = _db.GetOrCreateSeedApplicationReceipt(
            SeedResponseFor(CommandId, signer.KeyId, new string('c', 64)),
            _options, SessionId, 1, 0, signer);
        var second = _db.GetOrCreateSeedApplicationReceipt(
            SeedResponseFor(
                "73333333-3333-4333-8333-333333333333",
                signer.KeyId,
                new string('d', 64)),
            _options, SessionId, 0, 1, signer);

        Assert.Equal(1, first.Signed.Receipt.Counter);
        Assert.Equal(2, second.Signed.Receipt.Counter);
    }

    private static PomApprovalCommand Command()
    {
        var data = JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1,
            pomId = PomId,
            sessionId = SessionId,
            approvedModelDigest = new string('b', 64),
            approvedTemplateDigest = TemplateDigest,
            approvedBy = ApprovedBy,
            commandId = CommandId,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        });
        Assert.True(PomApprovalCommandContract.TryParse(data, out var command, out _));
        return command!;
    }

    private static SeedResponse SeedResponseFor(
        string commandId,
        string deviceKeyId,
        string sourceManifestDigest) => new(
        new string('e', 64),
        1770000000,
        "model",
        Array.Empty<string>(),
        null,
        null,
        Array.Empty<SeedQueryShape>(),
        Array.Empty<SeedStatusMapping>(),
        null,
        CommandId: commandId,
        DeviceKeyId: deviceKeyId,
        SourceManifestDigest: sourceManifestDigest,
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
}
