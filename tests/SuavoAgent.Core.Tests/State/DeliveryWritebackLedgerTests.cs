using System.Security.Cryptography;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class DeliveryWritebackLedgerTests : IDisposable
{
    private const string PharmacyId = "00000000-0000-4000-8000-000000000001";
    private const string CommandId = "00000000-0000-4000-8000-000000000002";
    private const string WritebackId = "00000000-0000-4000-8000-000000000003";
    private const string CandidateId = "00000000-0000-4000-8000-000000000004";
    private const string OrderId = "00000000-0000-4000-8000-000000000005";
    private const string InboxItemId = "00000000-0000-4000-8000-000000000006";
    private static readonly string RxHash = new('a', 64);
    private static readonly string EvidenceId = $"rxh-{RxHash[..16]}-1770000000";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-delivery-writeback-ledger-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Registration_IsEncryptedRestartSafeAndPersistsEveryCommandField()
    {
        var path = Path.Combine(_directory, "ledger.bin");
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z"));
        var ledger = CreateLedger(path, time);
        var command = Command();

        var registration = ledger.Register(command);
        Assert.Equal(DeliveryWritebackLedgerRegistrationCode.Registered, registration.Code);
        var disk = File.ReadAllBytes(path);
        Assert.Equal(-1, disk.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(CommandId)));
        Assert.Equal(-1, disk.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(RxHash)));

        var restarted = CreateLedger(path, time);
        var item = restarted.Get(CommandId);
        Assert.NotNull(item);
        Assert.Equal(command, item!.Command);
        Assert.Equal(DeliveryWritebackLedgerState.Registered, item.State);
        Assert.False(item.CorrelationBound);
        Assert.Null(item.ResultCode);
        Assert.Equal(0, item.ExecutionAttempts);
        Assert.Equal(0, item.CallbackAttempts);
    }

    [Fact]
    public void Registration_IsIdempotentButRejectsCommandAndWritebackConflicts()
    {
        var ledger = CreateLedger(Path.Combine(_directory, "conflicts.bin"));
        var command = Command();

        Assert.Equal(
            DeliveryWritebackLedgerRegistrationCode.Registered,
            ledger.Register(command).Code);
        Assert.Equal(
            DeliveryWritebackLedgerRegistrationCode.Idempotent,
            ledger.Register(command).Code);
        Assert.Equal(
            DeliveryWritebackLedgerRegistrationCode.CommandConflict,
            ledger.Register(command with
            {
                OrderId = "00000000-0000-4000-8000-000000000007",
            }).Code);
        Assert.Equal(
            DeliveryWritebackLedgerRegistrationCode.WritebackConflict,
            ledger.Register(command with
            {
                CommandId = "00000000-0000-4000-8000-000000000008",
            }).Code);
        Assert.Equal(
            DeliveryWritebackLedgerRegistrationCode.WritebackConflict,
            ledger.Register(command with
            {
                CommandId = "00000000-0000-4000-8000-000000000009",
                WritebackId = "00000000-0000-4000-8000-000000000010",
            }).Code);
    }

    [Fact]
    public void TerminalResultSurvivesRestartAndAckRequiresExactAuthenticatedReceipt()
    {
        var path = Path.Combine(_directory, "terminal.bin");
        var ledger = CreateLedger(path);
        var command = Command();
        ledger.Register(command);
        Assert.Throws<InvalidOperationException>(() => ledger.MarkAcked(CommandId));

        ledger.MarkCorrelationBound(CommandId);
        var executing = ledger.MarkExecuting(CommandId);
        Assert.Equal(1, executing.ExecutionAttempts);
        ledger.RecordResult(CommandId, DeliveryWritebackResultCode.Success);

        var restarted = CreateLedger(path);
        var pending = restarted.Get(CommandId);
        Assert.NotNull(pending);
        Assert.Equal(DeliveryWritebackLedgerState.ResultPendingCallback, pending!.State);
        Assert.Equal(DeliveryWritebackResultCode.Success, pending.ResultCode);
        Assert.Equal(1, pending.ExecutionAttempts);
        Assert.Throws<InvalidDataException>(() => restarted.MarkReceiptVerified(
            CommandId,
            Receipt() with { OrderId = "00000000-0000-4000-8000-000000000011" }));
        Assert.Throws<InvalidDataException>(() => restarted.MarkReceiptVerified(
            CommandId,
            Receipt() with
            {
                Proof = Receipt().Proof with { CanonicalBodySha256 = new string('0', 64) },
            }));

        var verified = restarted.MarkReceiptVerified(CommandId, Receipt());
        Assert.Equal(DeliveryWritebackLedgerState.ReceiptVerified, verified.State);
        var untrustedRestart = new DeliveryWritebackLedger(
            path,
            new TestProtector(),
            trustedReceiptKeys: new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Throws<InvalidDataException>(() => untrustedRestart.Get(CommandId));
        var acked = restarted.MarkAcked(CommandId);
        Assert.Equal(DeliveryWritebackLedgerState.Acked, acked.State);
        Assert.Empty(restarted.GetDue(
            PharmacyId,
            8,
            DateTimeOffset.Parse("2026-07-10T13:00:00Z")));
    }

    [Fact]
    public void CallbackDeferralPersistsAttemptsWithoutChangingTerminalResult()
    {
        var path = Path.Combine(_directory, "defer.bin");
        var ledger = CreateLedger(path);
        ledger.Register(Command());
        ledger.RecordResult(CommandId, DeliveryWritebackResultCode.ManualReview);
        var retryAt = DateTimeOffset.Parse("2026-07-10T12:01:00Z");

        var deferred = ledger.Defer(
            CommandId,
            "callback_receipt_unverified",
            retryAt,
            callbackAttempt: true);
        Assert.Equal(DeliveryWritebackLedgerState.ResultPendingCallback, deferred.State);
        Assert.Equal(DeliveryWritebackResultCode.ManualReview, deferred.ResultCode);
        Assert.Equal(1, deferred.CallbackAttempts);
        Assert.Equal(retryAt, deferred.NextRetryAt);
        Assert.Empty(ledger.GetDue(PharmacyId, 8, retryAt.AddMilliseconds(-1)));
        Assert.Single(ledger.GetDue(PharmacyId, 8, retryAt));

        var restarted = CreateLedger(path);
        Assert.Equal(1, restarted.Get(CommandId)!.CallbackAttempts);
    }

    [Fact]
    public void TamperedLedgerFailsClosed()
    {
        var path = Path.Combine(_directory, "tampered.bin");
        var ledger = CreateLedger(path);
        ledger.Register(Command());
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x7f;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => ledger.Get(CommandId));
    }

    [Fact]
    public void SignedReceiptProof_VerifiesCanonicalBodyAndPinnedKey()
    {
        var receipt = Receipt();
        var proof = receipt.Proof;
        var body = System.Text.Encoding.UTF8.GetBytes(proof.CanonicalBodyJson);

        Assert.Equal(
            proof.CanonicalBodySha256,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
        Assert.True(proof.Verify(receipt, DeliveryWritebackReceiptTestSigner.TrustedKeys));
    }

    private static AgentDeliveryWritebackCommand Command() => new(
        2,
        WritebackId,
        CandidateId,
        RxHash,
        EvidenceId,
        PharmacyId,
        OrderId,
        InboxItemId,
        "00000000-0000-4000-8000-000000000020",
        "00000000-0000-4000-8000-000000000021",
        new string('b', 64),
        "complete",
        "2026-07-10T12:15:00.000Z",
        CommandId);

    private static DeliveryWritebackCallbackReceipt Receipt() =>
        DeliveryWritebackReceiptTestSigner.Create(
            Command(),
            DeliveryWritebackResultCode.Success,
            DateTimeOffset.Parse("2026-07-10T12:16:00Z"));

    private static DeliveryWritebackLedger CreateLedger(
        string path,
        TimeProvider? time = null) =>
        new(
            path,
            new TestProtector(),
            time ?? new MutableTimeProvider(DateTimeOffset.Parse("2026-07-10T12:00:00Z")),
            trustedReceiptKeys: DeliveryWritebackReceiptTestSigner.TrustedKeys);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class TestProtector : IRxCorrelationProtector
    {
        private static readonly byte[] Key = SHA256.HashData("delivery-ledger-test-key"u8.ToArray());

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(Key, tagSizeInBytes: 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, entropy);
            return [.. nonce, .. tag, .. ciphertext];
        }

        public byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
        {
            if (protectedBytes.Length < 29) throw new CryptographicException("invalid test ciphertext");
            var nonce = protectedBytes[..12];
            var tag = protectedBytes[12..28];
            var ciphertext = protectedBytes[28..];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(Key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, entropy);
            return plaintext;
        }
    }
}
