using System.Security.Cryptography;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class DeliveryWritebackLedgerBoundaryTests : IDisposable
{
    private const string PharmacyId = "00000000-0000-4000-8000-000000000001";
    private const string CommandId = "00000000-0000-4000-8000-000000000002";
    private const string WritebackId = "00000000-0000-4000-8000-000000000003";
    private const string CandidateId = "00000000-0000-4000-8000-000000000004";
    private const string OrderId = "00000000-0000-4000-8000-000000000005";
    private const string InboxItemId = "00000000-0000-4000-8000-000000000006";
    private const string PmsReferenceId = "00000000-0000-4000-8000-000000000007";
    private const string ProofRecordId = "00000000-0000-4000-8000-000000000008";
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-writeback-ledger-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Registration_RejectsEveryMalformedStableUuid()
    {
        var mutations = new Func<AgentDeliveryWritebackCommand, AgentDeliveryWritebackCommand>[]
        {
            command => command with { WritebackId = "NOT-A-UUID" },
            command => command with { CandidateId = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA" },
            command => command with { PharmacyId = PharmacyId[..35] },
            command => command with { OrderId = "" },
            command => command with { InboxItemId = "00000000-0000-4000-8000-00000000000x" },
            command => command with { PmsReferenceId = "00000000-0000-4000-8000-00000000000X" },
            command => command with { CommandId = "00000000-0000-4000-8000-0000000000020" },
            command => command with { ProofRecordId = null },
            command => command with { ProofRecordId = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA" },
        };

        foreach (var mutate in mutations)
        {
            var ledger = CreateLedger();
            Assert.Throws<ArgumentException>(() => ledger.Register(mutate(Command())));
        }
    }

    [Fact]
    public void Registration_RejectsEveryMalformedHashAndEvidenceBinding()
    {
        var mutations = new Func<AgentDeliveryWritebackCommand, AgentDeliveryWritebackCommand>[]
        {
            command => command with { SchemaVersion = 1 },
            command => command with { RxHash = Hash[..63] },
            command => command with { RxHash = Hash + "a" },
            command => command with { RxHash = new string('A', 64) },
            command => command with { RxHash = new string('g', 64) },
            command => command with { EvidenceId = "rxh-bbbbbbbbbbbbbbbb-1770000000" },
            command => command with { EvidenceId = "rxh-aaaaaaaaaaaaaaaa-123456789" },
            command => command with { EvidenceId = "rxh-aaaaaaaaaaaaaaaa-12345678901234" },
            command => command with { EvidenceId = "rxh-aaaaaaaaaaaaaaaa-123456789x" },
        };

        foreach (var mutate in mutations)
        {
            var ledger = CreateLedger();
            Assert.Throws<ArgumentException>(() => ledger.Register(mutate(Command())));
        }
    }

    [Fact]
    public void Registration_EnforcesTransitionTimeAndCompletionProofSemantics()
    {
        var invalid = new[]
        {
            Command() with { Transition = "delivered" },
            Command() with { TransitionAt = "2026-07-12 12:00:00" },
            Command() with { TransitionAt = "2026-99-99T12:00:00.000Z" },
            Command() with { ProofDigest = null },
            Command() with { ProofDigest = Hash[..63] },
            Command() with { ProofDigest = new string('A', 64) },
            Command() with { ProofDigest = new string('g', 64) },
            Command() with
            {
                Transition = "pickup",
                ProofRecordId = ProofRecordId,
                ProofDigest = Hash,
            },
        };

        foreach (var command in invalid)
        {
            var ledger = CreateLedger();
            Assert.Throws<ArgumentException>(() => ledger.Register(command));
        }

        var pickup = Command() with
        {
            Transition = "pickup",
            ProofRecordId = null,
            ProofDigest = null,
        };
        Assert.Equal(
            DeliveryWritebackLedgerRegistrationCode.Registered,
            CreateLedger().Register(pickup).Code);
    }

    [Fact]
    public void DueQueryAndDeferralCodesAreStrictlyBounded()
    {
        var ledger = CreateLedger();
        ledger.Register(Command());

        Assert.Throws<ArgumentException>(() =>
            ledger.GetDue("bad-pharmacy", 1, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.GetDue(PharmacyId, 0, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ledger.GetDue(PharmacyId, 33, DateTimeOffset.UtcNow));

        foreach (var code in new[] { "", new string('a', 81), "UPPER", "contains-dash", "contains space" })
        {
            Assert.Throws<ArgumentException>(() =>
                ledger.Defer(CommandId, code, DateTimeOffset.UtcNow, false));
        }

        var valid = ledger.Defer(
            CommandId,
            "sql_retry_1",
            DateTimeOffset.UtcNow.AddMinutes(1),
            false);
        Assert.Equal("sql_retry_1", valid.LastErrorCode);
    }

    [Fact]
    public void StateMachineRejectsOutOfOrderAndConflictingTerminalMutations()
    {
        var ledger = CreateLedger();
        ledger.Register(Command());

        Assert.Throws<InvalidOperationException>(() => ledger.MarkExecuting(CommandId));
        ledger.MarkCorrelationBound(CommandId);
        ledger.MarkExecuting(CommandId);
        ledger.RecordResult(CommandId, DeliveryWritebackResultCode.Success);
        ledger.RecordResult(CommandId, DeliveryWritebackResultCode.Success);
        Assert.Throws<InvalidDataException>(() =>
            ledger.RecordResult(CommandId, DeliveryWritebackResultCode.ManualReview));
        Assert.Throws<InvalidOperationException>(() => ledger.MarkExecuting(CommandId));

        var receipt = DeliveryWritebackReceiptTestSigner.Create(
            Command(),
            DeliveryWritebackResultCode.Success,
            DateTimeOffset.Parse("2026-07-12T12:01:00Z"));
        ledger.MarkReceiptVerified(CommandId, receipt);
        ledger.MarkReceiptVerified(CommandId, receipt);
        ledger.MarkAcked(CommandId);
        ledger.MarkAcked(CommandId);
        Assert.Throws<InvalidOperationException>(() =>
            ledger.Defer(CommandId, "retry", DateTimeOffset.UtcNow, false));
    }

    private DeliveryWritebackLedger CreateLedger() => new(
        Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".bin"),
        new TestProtector(),
        trustedReceiptKeys: DeliveryWritebackReceiptTestSigner.TrustedKeys);

    private static AgentDeliveryWritebackCommand Command() => new(
        2,
        WritebackId,
        CandidateId,
        Hash,
        "rxh-aaaaaaaaaaaaaaaa-1770000000",
        PharmacyId,
        OrderId,
        InboxItemId,
        PmsReferenceId,
        ProofRecordId,
        new string('b', 64),
        "complete",
        "2026-07-12T12:00:00.000Z",
        CommandId);

    private sealed class TestProtector : IRxCorrelationProtector
    {
        private static readonly byte[] Key = SHA256.HashData("writeback-boundary-key"u8.ToArray());

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
            return [.. nonce, .. tag, .. ciphertext];
        }

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
        {
            var nonce = protectedBytes[..12];
            var tag = protectedBytes[12..28];
            var ciphertext = protectedBytes[28..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, entropy);
            return plaintext;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }
}
