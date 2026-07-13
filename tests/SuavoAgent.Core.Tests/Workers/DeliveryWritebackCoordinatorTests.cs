using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Tests.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class DeliveryWritebackCoordinatorTests : IDisposable
{
    private const string PharmacyId = "00000000-0000-4000-8000-000000000001";
    private const string AgentId = "agent-install-1";
    private const string Fingerprint = "machine-fingerprint-1";
    private const string CandidateId = "00000000-0000-4000-8000-000000000002";
    private const string FetchCommandId = "00000000-0000-4000-8000-000000000003";
    private const string HmacSalt = "test-writeback-hmac-key";
    private const string RawRx = "123456";
    private static readonly string RxHash = PhiScrubber.HmacHash(RawRx, HmacSalt);
    private static readonly string EvidenceId = $"rxh-{RxHash[..16]}-1770000000";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-delivery-writeback-coordinator-" + Guid.NewGuid().ToString("N"));
    private readonly AgentStateDb _db;

    public DeliveryWritebackCoordinatorTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new AgentStateDb(Path.Combine(_directory, "state.db"));
    }

    [Fact]
    public async Task ExactCommand_ExecutesOnceCallbacksThenAcksAndPurgesOnComplete()
    {
        var correlations = CreateCompletedCorrelations();
        var ledger = CreateLedger();
        var executor = new FakeExecutor(DeliveryWritebackExecutionOutcome.Completed(
            DeliveryWritebackResultCode.Success));
        var cloud = new FakeCloud();
        var coordinator = CreateCoordinator(correlations, ledger, executor, cloud);
        var command = Command();

        Assert.True(coordinator.Register(command).Accepted);
        await coordinator.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(RawRx, executor.LastRawRx);
        Assert.Equal(2, executor.LastFillNumber);
        Assert.Equal(1, cloud.CallbackCalls);
        Assert.Equal(1, cloud.AckCalls);
        Assert.Equal(DeliveryWritebackLedgerState.Acked, ledger.Get(command.CommandId)!.State);
        Assert.Equal(
            WritebackCorrelationRegistrationCode.RawLookupUnavailable,
            correlations.RegisterDeliveryWriteback(
                Command() with
                {
                    WritebackId = "00000000-0000-4000-8000-000000000020",
                    CommandId = "00000000-0000-4000-8000-000000000021",
                    Transition = "pickup",
                    ProofRecordId = null,
                    ProofDigest = null,
                },
                AgentId,
                Fingerprint).Code);

        Assert.True(coordinator.Register(command).Accepted);
        await coordinator.RetryPendingAsync(CancellationToken.None);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(1, cloud.CallbackCalls);
        Assert.Equal(1, cloud.AckCalls);
    }

    [Fact]
    public async Task CallbackOutage_PersistsTerminalResultAndRestartNeverRepeatsSqlWrite()
    {
        var correlations = CreateCompletedCorrelations();
        var ledger = CreateLedger();
        var executor = new FakeExecutor(DeliveryWritebackExecutionOutcome.Completed(
            DeliveryWritebackResultCode.Success));
        var offlineCloud = new FakeCloud { ReturnReceipt = false };
        var command = Command();
        var first = CreateCoordinator(correlations, ledger, executor, offlineCloud);
        Assert.True(first.Register(command).Accepted);

        await first.RetryPendingAsync(CancellationToken.None);
        Assert.Equal(1, executor.Calls);
        Assert.Equal(0, offlineCloud.AckCalls);
        Assert.Equal(
            DeliveryWritebackLedgerState.ResultPendingCallback,
            ledger.Get(command.CommandId)!.State);

        ledger.Defer(
            command.CommandId,
            "callback_retry_test",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            callbackAttempt: true);
        var onlineCloud = new FakeCloud();
        var restarted = CreateCoordinator(correlations, ledger, executor, onlineCloud);
        await restarted.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(1, onlineCloud.CallbackCalls);
        Assert.Equal(1, onlineCloud.AckCalls);
        Assert.Equal(DeliveryWritebackLedgerState.Acked, ledger.Get(command.CommandId)!.State);
    }

    [Fact]
    public async Task ReceiptOnlyMode_AlwaysReportsManualReviewAndNeverInvokesExecutor()
    {
        var correlations = CreateCompletedCorrelations();
        var ledger = CreateLedger();
        var executor = new FakeExecutor(DeliveryWritebackExecutionOutcome.Completed(
            DeliveryWritebackResultCode.Success));
        var cloud = new FakeCloud();
        var coordinator = CreateCoordinator(
            correlations,
            ledger,
            executor,
            cloud,
            receiptOnly: true);

        Assert.True(coordinator.Register(Command()).Accepted);
        await coordinator.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(0, executor.Calls);
        Assert.Equal(DeliveryWritebackResultCode.ManualReview, cloud.LastResult);
        Assert.Equal(1, cloud.AckCalls);
    }

    [Fact]
    public async Task CorrelationMismatch_FailsClosedToManualReviewWithoutRawLookupOrSqlWrite()
    {
        var correlations = CreateCompletedCorrelations();
        var ledger = CreateLedger();
        var executor = new FakeExecutor(DeliveryWritebackExecutionOutcome.Completed(
            DeliveryWritebackResultCode.Success));
        var cloud = new FakeCloud();
        var coordinator = CreateCoordinator(correlations, ledger, executor, cloud);
        var command = Command() with
        {
            CandidateId = "00000000-0000-4000-8000-000000000099",
        };

        Assert.True(coordinator.Register(command).Accepted);
        await coordinator.RetryPendingAsync(CancellationToken.None);

        Assert.Equal(0, executor.Calls);
        Assert.Equal(DeliveryWritebackResultCode.ManualReview, cloud.LastResult);
        Assert.Equal(DeliveryWritebackLedgerState.Acked, ledger.Get(command.CommandId)!.State);
    }

    [Fact]
    public async Task FiveTransientExecutions_ConvergeToRetryExhaustedWithOneTerminalCallback()
    {
        var correlations = CreateCompletedCorrelations();
        var ledger = CreateLedger();
        var executor = new FakeExecutor(DeliveryWritebackExecutionOutcome.Retry("sql_unavailable"));
        var cloud = new FakeCloud();
        var coordinator = CreateCoordinator(correlations, ledger, executor, cloud);
        var command = Command();
        Assert.True(coordinator.Register(command).Accepted);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await coordinator.RetryPendingAsync(CancellationToken.None);
            if (attempt < 5)
            {
                ledger.Defer(
                    command.CommandId,
                    "execution_retry_test",
                    DateTimeOffset.UtcNow.AddSeconds(-1),
                    callbackAttempt: false);
            }
        }

        Assert.Equal(5, executor.Calls);
        Assert.Equal(DeliveryWritebackResultCode.RetryExhausted, cloud.LastResult);
        Assert.Equal(1, cloud.CallbackCalls);
        Assert.Equal(1, cloud.AckCalls);
        Assert.Equal(DeliveryWritebackLedgerState.Acked, ledger.Get(command.CommandId)!.State);
    }

    private DeliveryWritebackCoordinator CreateCoordinator(
        IRxCorrelationStore correlations,
        IDeliveryWritebackLedger ledger,
        IDeliveryWritebackExecutor executor,
        IDeliveryWritebackCloudTransport cloud,
        bool receiptOnly = false) =>
        new(
            Options(receiptOnly),
            correlations,
            ledger,
            _db,
            executor,
            cloud,
            NullLogger.Instance);

    private RxCorrelationStore CreateCompletedCorrelations()
    {
        var store = new RxCorrelationStore(
            Path.Combine(_directory, "correlations-" + Guid.NewGuid().ToString("N") + ".json"),
            new TestProtector(),
            maxEntries: 32);
        store.UpsertObservation(new RxCorrelationObservation(
            new RxCorrelationKey(PharmacyId, AgentId, RxHash, EvidenceId),
            Fingerprint,
            RawRx,
            FillNumber: 2));
        var pending = store.RegisterApprovedFetch(
            new ApprovedPatientFetchCommand(
                CandidateId,
                RxHash,
                EvidenceId,
                PharmacyId,
                FetchCommandId),
            AgentId,
            Fingerprint).Pending!;
        store.MarkCallbackAccepted(
            pending,
            "00000000-0000-4000-8000-000000000004",
            "00000000-0000-4000-8000-000000000005",
            DateTimeOffset.UtcNow.AddMinutes(30));
        store.MarkCompleted(pending);
        return store;
    }

    private DeliveryWritebackLedger CreateLedger() => new(
        Path.Combine(_directory, "ledger-" + Guid.NewGuid().ToString("N") + ".bin"),
        new TestProtector(),
        trustedReceiptKeys: DeliveryWritebackReceiptTestSigner.TrustedKeys);

    private static AgentOptions Options(bool receiptOnly) => new()
    {
        PharmacyId = PharmacyId,
        AgentId = AgentId,
        MachineFingerprint = Fingerprint,
        HmacSalt = HmacSalt,
        ReceiptOnlyMode = receiptOnly,
    };

    private static AgentDeliveryWritebackCommand Command() => new(
        2,
        "00000000-0000-4000-8000-000000000010",
        CandidateId,
        RxHash,
        EvidenceId,
        PharmacyId,
        "00000000-0000-4000-8000-000000000011",
        "00000000-0000-4000-8000-000000000012",
        "00000000-0000-4000-8000-000000000014",
        "00000000-0000-4000-8000-000000000015",
        new string('b', 64),
        "complete",
        "2026-07-10T12:15:00.000Z",
        "00000000-0000-4000-8000-000000000013");

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class FakeExecutor(DeliveryWritebackExecutionOutcome outcome) : IDeliveryWritebackExecutor
    {
        internal int Calls { get; private set; }
        internal string? LastRawRx { get; private set; }
        internal int LastFillNumber { get; private set; }

        public Task<DeliveryWritebackExecutionOutcome> ExecuteAsync(
            ReadOnlyMemory<char> rawRxNumber,
            int fillNumber,
            string transition,
            DateTimeOffset transitionAt,
            CancellationToken ct)
        {
            Calls++;
            LastRawRx = new string(rawRxNumber.Span);
            LastFillNumber = fillNumber;
            return Task.FromResult(outcome);
        }
    }

    private sealed class FakeCloud : IDeliveryWritebackCloudTransport
    {
        internal bool ReturnReceipt { get; init; } = true;
        internal int CallbackCalls { get; private set; }
        internal int AckCalls { get; private set; }
        internal DeliveryWritebackResultCode? LastResult { get; private set; }

        public Task<DeliveryWritebackCallbackReceipt?> SendCallbackAsync(
            AgentDeliveryWritebackCommand command,
            DeliveryWritebackResultCode resultCode,
            CancellationToken ct)
        {
            CallbackCalls++;
            LastResult = resultCode;
            if (!ReturnReceipt)
                return Task.FromResult<DeliveryWritebackCallbackReceipt?>(null);
            return Task.FromResult<DeliveryWritebackCallbackReceipt?>(
                DeliveryWritebackReceiptTestSigner.Create(
                    command,
                    resultCode,
                    DateTimeOffset.UtcNow));
        }

        public Task<bool> AckAsync(
            AgentDeliveryWritebackCommand command,
            DeliveryWritebackResultCode resultCode,
            CancellationToken ct)
        {
            AckCalls++;
            LastResult = resultCode;
            return Task.FromResult(true);
        }
    }

    private sealed class TestProtector : IRxCorrelationProtector
    {
        private static readonly byte[] Key = SHA256.HashData("coordinator-test-key"u8.ToArray());

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
